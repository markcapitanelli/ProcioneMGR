using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace Procione;

/// <summary>
/// Le sonde. Tutte in SOLA LETTURA: questa classe non ripara nulla, guarda soltanto — cosi'
/// `procione stato` si puo' lanciare a occhi chiusi, anche mentre la piattaforma opera.
///
/// Il principio che le governa tutte, imparato pagando: <b>il verdetto e' la risposta, non lo
/// stato dichiarato</b>. Il proxy dell'API server "running" non significa che l'API risponda
/// (l'IP del nodo cambia a ogni riavvio di Docker); un port-forward "in ascolto" non significa
/// che porti da qualche parte (il pod puo' essere stato sostituito); un pod "Running" non
/// significa che il tunnel verso di lui sia ancora quello giusto. Ogni sonda qui sotto chiude il
/// cerchio fino al dato osservabile.
/// </summary>
internal static class Probes
{
    private static readonly HttpClient Http = MakeClient(false);

    /// Client separato per il solo <c>/livez</c> del kube-apiserver: il certificato del cluster
    /// kind e' autofirmato. La deroga vale per QUESTA sonda e per nessun'altra.
    private static readonly HttpClient HttpSelfSigned = MakeClient(true);

    private static HttpClient MakeClient(bool accettaCertificatoAutofirmato)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        if (accettaCertificatoAutofirmato)
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    // =============================================================================================
    //  Rilevazione
    // =============================================================================================

    public static async Task<Snapshot> RunAsync()
    {
        var checks = new List<Check>();
        var listening = ListeningPorts();

        // --- Docker ------------------------------------------------------------------------------
        var docker = await Proc.CaptureAsync("docker", ["info", "--format", "{{.ServerVersion}}"], 20000);
        var dockerOk = docker.Ok && docker.Out.Length > 0;
        checks.Add(dockerOk
            ? new Check("fondamenta", "Docker", Level.Ok, $"demone pronto (server {docker.Out})")
            : new Check("fondamenta", "Docker", Level.Down,
                docker.Code == Proc.Failed ? "eseguibile 'docker' non nel PATH" : Explain(docker),
                "avvia Docker Desktop (al boot impiega minuti); senza, tutto il resto e' inutile"));

        List<Container> containers = dockerOk ? await ContainersAsync() : [];

        var composeUi = containers.FirstOrDefault(c =>
            c.Project == Platform.ComposeProject && c.Service == "ui" && c.State == "running");
        var kindNode = containers.FirstOrDefault(c => c.Name == Platform.KindNodeContainer);
        var kindUp = kindNode is { State: "running" };
        var composeUp = composeUi is not null;

        var layout = Verdicts.Which(dockerOk, kindUp, composeUp);

        checks.Add(layout switch
        {
            Layout.Kind => new Check("fondamenta", "Assetto", Level.Ok, "kind — cluster locale (Compose spento)"),
            Layout.Compose => new Check("fondamenta", "Assetto", Level.Ok,
                $"Docker Compose — {containers.Count(c => c.Project == Platform.ComposeProject && c.State == "running")} servizi attivi"),
            Layout.Both => new Check("fondamenta", "Assetto", Level.Down,
                "kind E Compose attivi INSIEME: due motori possibili sullo stesso dominio (regola 2)",
                "spegni uno dei due: `procione ferma compose` oppure ferma i workload del cluster"),
            Layout.None => new Check("fondamenta", "Assetto", Level.Warn, "nessuno: la piattaforma e' spenta",
                "`procione avvia` rimette in piedi l'assetto kind"),
            _ => new Check("fondamenta", "Assetto", Level.NotApplicable, "indeterminabile senza Docker"),
        });

        // --- Sonde indipendenti, tutte insieme ---------------------------------------------------
        // La plancia deve rispondere in un paio di secondi ANCHE quando meta' piattaforma e' morta:
        // in serie, cinque timeout da 6s farebbero mezzo minuto di attesa muta.
        var tProxy = dockerOk
            ? GetAsync(HttpSelfSigned, $"https://127.0.0.1:{Platform.ApiProxyPort}/livez")
            : Task.FromResult((Ok: false, Status: 0, Body: "", Error: "Docker giu'"));
        var tShell = GetAsync(Http, $"http://localhost:{Platform.ShellPort}/health");
        var tEngine = GetAsync(Http, $"http://localhost:{Platform.EngineHealthPort}/health");
        var tIngest = GetAsync(Http, $"http://localhost:{Platform.IngestionPort}/health");
        var tGrafana = GetAsync(Http, $"http://localhost:{Platform.GrafanaPort}/api/health");
        var tPostgres = TcpAsync("localhost", Platform.PostgresPort);
        var tKubeconfig = Proc.CaptureAsync("kubectl", ["config", "view", "-o", "json"], 10000);
        var tNodes = kindUp
            ? Proc.KubectlAsync(["get", "nodes", "--no-headers"])
            : Task.FromResult(new ExecResult(Proc.NotStarted, "", "cluster non in esecuzione"));
        var tPods = kindUp
            ? Proc.KubectlAsync(["get", "pods", "-A", "-o", PodJsonPath], 15000)
            : Task.FromResult(new ExecResult(Proc.NotStarted, "", "cluster non in esecuzione"));
        var tTasks = ScheduledTasksAsync();

        await Task.WhenAll(tProxy, tShell, tEngine, tIngest, tGrafana, tPostgres,
                           tKubeconfig, tNodes, tPods, tTasks);

        var proxy = await tProxy;

        // L'ESITO di `kubectl get pods`, non solo il suo output. Una lista vuota da un kubectl che
        // non ha risposto e' indistinguibile da «non ci sono pod», e le due cose portano a due
        // quadri opposti: e' successo due volte (2026-08-04 e 2026-08-11, il proxy che inoltrava a
        // un IP vecchio) che il nodo risultasse su e ogni kubectl morisse in TLS handshake timeout.
        // Senza questa distinzione il quadro accende tre rossi falsi («nessun pod nel namespace —
        // il Deployment e' stato applicato?», che manda a cercare nel posto sbagliato) e un VERDE
        // su ArgoCD mai misurato, perche' contare su una lista vuota da' zero.
        var rPods = await tPods;
        var pods = Parsing.Pods(rPods.Out);
        var podsNoti = rPods.Ok;

        // =========================================================================================
        //  Cluster
        // =========================================================================================
        if (kindNode is null && composeUp)
        {
            checks.Add(new Check("cluster kind", "Cluster", Level.NotApplicable,
                "non presente: la piattaforma gira su Docker Compose"));
        }
        else
        {
            // --- Nodo ---
            if (kindNode is null)
                checks.Add(new Check("cluster kind", "Nodo", Level.Down,
                    $"container '{Platform.KindNodeContainer}' assente: il cluster non esiste",
                    "`procione avvia cluster` (k8s-bootstrap.ps1) — e' un prerequisito una-tantum"));
            else if (!kindUp)
                checks.Add(new Check("cluster kind", "Nodo", Level.Down,
                    $"container fermo ({kindNode.Status})",
                    $"docker start {Platform.KindNodeContainer}"));
            else
            {
                var stato = Parsing.NodeStatus((await tNodes).Out);
                checks.Add(stato == "Ready"
                    ? new Check("cluster kind", "Nodo", Level.Ok,
                        $"Ready — container su da {Parsing.ContainerUptime(kindNode.Status)}")
                    : new Check("cluster kind", "Nodo", Level.Down,
                        stato is not null ? $"stato '{stato}'" : $"kubectl non risponde ({Explain(await tNodes)})",
                        "`procione ripara proxy` se e' l'API server a non rispondere"));
            }

            // --- Proxy dell'API server ---
            // Il verdetto e' la RISPOSTA attraverso il proxy: "container running" e' successo due
            // volte con il socat che inoltrava all'IP che il nodo aveva PRIMA del riavvio di Docker
            // (2026-08-04 e 2026-08-11, un'ora di TLS handshake timeout con tutto "sano").
            var proxyContainer = containers.FirstOrDefault(c => c.Name == Platform.ApiProxyContainer);
            checks.Add(proxy.Ok
                ? new Check("cluster kind", "Proxy API", Level.Ok,
                    $"l'API server risponde attraverso 127.0.0.1:{Platform.ApiProxyPort} (/livez 200)")
                : new Check("cluster kind", "Proxy API", Level.Down,
                    proxyContainer is null
                        ? $"container '{Platform.ApiProxyContainer}' assente"
                        : proxyContainer.State == "running"
                            ? $"container su ma l'API NON risponde ({proxy.Error}) — inoltra a un IP vecchio?"
                            : $"container fermo ({proxyContainer.Status})",
                    "`procione ripara proxy` (lo ricrea puntando al NOME DNS del nodo, mai all'IP)"));

            // --- Contesto kubectl ---
            var server = Parsing.KubeServer((await tKubeconfig).Out, Platform.KubeContext);
            var atteso = $"https://127.0.0.1:{Platform.ApiProxyPort}";
            checks.Add(server is null
                ? new Check("cluster kind", "Contesto", Level.Warn,
                    $"cluster '{Platform.KubeContext}' assente dal kubeconfig", "`procione ripara contesto`")
                : server == atteso
                    ? new Check("cluster kind", "Contesto", Level.Ok, $"kubectl punta al proxy ({server})")
                    : new Check("cluster kind", "Contesto", Level.Warn,
                        $"kubectl punta a {server}, non al proxy: e' la porta che Windows riserva dopo un riavvio",
                        "`procione ripara contesto`"));

            // --- ArgoCD (spento di proposito) ---
            if (kindUp && podsNoti)
            {
                var argo = pods.Count(p => p.Ns == Platform.ArgocdNamespace);
                checks.Add(new Check("cluster kind", "ArgoCD", Level.Ok, argo == 0
                    ? "spento — scelta del 2026-08-05: 7 pod per un lavoro che nessuna Application fa in automatico"
                    : $"{argo} pod attivi (`procione argocd giu` per spegnerlo quando hai finito)"));
            }
            else if (kindUp)
            {
                checks.Add(new Check("cluster kind", "ArgoCD", Level.NotApplicable,
                    "indeterminabile: kubectl non elenca i pod"));
            }
        }

        // =========================================================================================
        //  Servizi in cluster
        // =========================================================================================
        if (kindUp)
        {
            foreach (var (ns, deploy, label) in Platform.Services)
            {
                var p = pods.Where(x => x.Ns == ns).OrderByDescending(x => x.Created).FirstOrDefault();
                checks.Add(!podsNoti
                    // Non si sa: dirlo, invece di dedurre «nessun pod» da una risposta mai
                    // arrivata. Il rimedio punta al guasto vero, che sta un piano piu' sotto.
                    ? new Check("servizi in cluster", label, Level.Warn,
                        $"stato ignoto: kubectl non elenca i pod ({Explain(rPods)})",
                        "`procione ripara proxy` — quasi sempre e' l'API server irraggiungibile")
                    : p is null
                    ? new Check("servizi in cluster", label, Level.Down, $"nessun pod nel namespace {ns}",
                        $"kubectl -n {ns} get deploy — il Deployment e' stato applicato?")
                    : p is { Phase: "Running", Ready: true }
                        ? new Check("servizi in cluster", label, Level.Ok,
                            $"{p.Name}  su da {Ui.Age(DateTimeOffset.UtcNow - p.Created)}  ({p.Restarts} riavvii)")
                        : new Check("servizi in cluster", label, Level.Down,
                            $"{p.Name}  fase {p.Phase}, pronto={p.Ready}, {p.Restarts} riavvii",
                            $"`procione log {label}` per vedere perche'"));
            }
        }

        // =========================================================================================
        //  Tunnel (port-forward)
        // =========================================================================================
        if (layout == Layout.Compose)
        {
            checks.Add(new Check("tunnel", "Port-forward", Level.NotApplicable,
                "non previsti: su Compose i servizi si parlano sulla rete del progetto"));
        }
        else
        {
            // `kindUp && podsNoti` e non il solo `kindUp`: senza l'elenco dei pod il confronto col
            // marcatore non si puo' fare, e dedurre «nessun pod Running a cui puntare» da una
            // risposta mai arrivata sposterebbe l'attenzione dal guasto vero (l'API server).
            checks.Add(Verdicts.Tunnel("motore", [Platform.EngineGrpcPort, Platform.EngineHealthPort],
                ReadMarker(Platform.TradingTunnelMarker),
                Verdicts.TunnelPod(pods, Platform.TradingNamespace),
                listening, kindUp && podsNoti, "/trading e il comando del motore via gRPC"));

            checks.Add(Verdicts.Tunnel("ingestion", [Platform.IngestionPort],
                ReadMarker(Platform.IngestionTunnelMarker),
                Verdicts.TunnelPod(pods, Platform.IngestionNamespace),
                listening, kindUp && podsNoti, "il pulsante «Sync now» di /market/watchlist"));
        }

        // =========================================================================================
        //  Servizi raggiungibili dall'host
        // =========================================================================================
        var shell = await tShell;
        var (nProc, exePath, pid) = ShellProcesses();
        var dove = exePath is null ? "" : $" — {Path.GetDirectoryName(exePath)} (pid {pid})";
        if (shell.Ok)
            checks.Add(nProc > 1
                // Due guscii vivi e' l'incidente del 2026-07-20 in agguato: un'istanza di worktree
                // con la master key segnaposto che intercetta l'utente e gli rompe il login.
                ? new Check("in ascolto", "Guscio", Level.Warn,
                    $"/health 200 ma ci sono {nProc} processi ProcioneMGR vivi{dove}",
                    "mai due guscii insieme: chiudi quello di troppo (`procione ferma guscio` e riavvia)")
                : new Check("in ascolto", "Guscio", Level.Ok,
                    $"/health 200 su :{Platform.ShellPort}{(composeUp ? " — container compose 'ui'" : dove)}"));
        else if (listening.Contains(Platform.ShellPort))
            checks.Add(new Check("in ascolto", "Guscio", Level.Warn,
                $"porta {Platform.ShellPort} in ascolto ma /health non risponde ({shell.Error}) — in avvio?",
                "attendi qualche secondo; se resta cosi', `procione log guscio`"));
        else
            checks.Add(new Check("in ascolto", "Guscio", Level.Down, $"nulla in ascolto su :{Platform.ShellPort}",
                "`procione avvia guscio`"));

        var engine = await tEngine;
        if (layout == Layout.Compose)
        {
            var tr = containers.FirstOrDefault(c => c.Project == Platform.ComposeProject && c.Service == "trading");
            checks.Add(tr is null
                ? new Check("in ascolto", "Motore", Level.NotApplicable,
                    "profilo 'engine' non attivo: il motore gira dentro il guscio")
                : tr.State == "running"
                    ? new Check("in ascolto", "Motore", Level.Ok, $"container {tr.Name} ({tr.Status})")
                    : new Check("in ascolto", "Motore", Level.Down, $"container {tr.Name} fermo ({tr.Status})",
                        "docker compose --profile engine up -d"));
        }
        else
        {
            // MAI la 18092 come sonda: e' gRPC h2c e a un GET HTTP/1.x risponde 400 SEMPRE. Il
            // watchdog ci ha creduto per mesi e non poteva vedere il motore sano (2026-08-11).
            checks.Add(engine.Ok
                ? new Check("in ascolto", "Motore", Level.Ok, $"/health 200 su :{Platform.EngineHealthPort} (porta health, non la gRPC)")
                : new Check("in ascolto", "Motore", Level.Down,
                    $"/health non risponde su :{Platform.EngineHealthPort} ({engine.Error})",
                    "quasi sempre e' il tunnel: `procione ripara tunnel`"));
        }

        var ingest = await tIngest;
        if (layout == Layout.Compose)
            checks.Add(new Check("in ascolto", "Ingestion", Level.NotApplicable, "in-process nel guscio su Compose"));
        else
            checks.Add(ingest.Ok
                ? new Check("in ascolto", "Ingestion", Level.Ok, $"/health 200 — {Parsing.HeartbeatAge(ingest.Body)}")
                : new Check("in ascolto", "Ingestion", Level.Warn,
                    $"/health non risponde su :{Platform.IngestionPort} ({ingest.Error}) — «Sync now» fallira'",
                    "`procione ripara tunnel`"));

        var pgOk = await tPostgres;
        if (layout == Layout.Compose)
        {
            var pg = containers.FirstOrDefault(c => c.Project == Platform.ComposeProject && c.Service == "postgres");
            checks.Add(pg is { State: "running" }
                ? new Check("in ascolto", "Postgres", Level.Ok, $"container {pg.Name} ({pg.Status}) — porta non pubblicata, di proposito")
                : new Check("in ascolto", "Postgres", Level.Down, "container postgres non in esecuzione",
                    "docker compose up -d postgres"));
        }
        else
            checks.Add(pgOk
                ? new Check("in ascolto", "Postgres", Level.Ok, $"TCP :{Platform.PostgresPort} accetta connessioni")
                : new Check("in ascolto", "Postgres", Level.Down, $"TCP :{Platform.PostgresPort} non risponde",
                    "e' un servizio Windows nativo: services.msc → postgresql-x64-18"));

        // Observability: si mostra solo se e' stata accesa almeno una volta, altrimenti e' rumore.
        if (containers.Any(c => c.Project == Platform.ObservabilityProject))
        {
            var grafana = await tGrafana;
            var suSu = containers.Count(c => c.Project == Platform.ObservabilityProject && c.State == "running");
            checks.Add(grafana.Ok
                ? new Check("in ascolto", "Grafana", Level.Ok, $"http://localhost:{Platform.GrafanaPort} — {suSu} container attivi")
                : new Check("in ascolto", "Grafana", Level.NotApplicable,
                    suSu == 0 ? "stack observability presente ma spento" : $"{suSu} container su, Grafana non risponde ancora"));
        }

        // =========================================================================================
        //  Supervisore — le automazioni, ora dentro questo programma
        // =========================================================================================
        var adesso = DateTimeOffset.Now;
        var supervisore = Supervisor.ReadState();
        var vivo = Supervisor.IsAlive(supervisore, adesso);
        var lettura = await tTasks;
        var tasks = lettura.Tasks;
        var plancia = Tasks.Active(tasks, Platform.SupervisorTask) || File.Exists(Tasks.StartupPlancia);
        var accesi = Prefs.Read();

        // Quali lavori stanno girando ancora dalle vecchie attivita' di Windows. Serve a non
        // dichiarare «fermo» cio' che sta semplicemente girando altrove: su una macchina non
        // migrata sarebbe falso, e un rosso falso e' peggio di nessun rosso.
        //
        // `Active` e non `Exists`: un'attivita' DISABILITATA esiste ma non parte, e contarla come
        // copertura declasserebbe un guasto ad avviso affermando un fatto mai misurato. Se la
        // lettura e' fallita non si conta NULLA come coperto: nel dubbio si dichiara scoperto.
        var daTask = !lettura.Ok ? [] : Jobs.Legacy
            .Where(l => Tasks.Active(tasks, l.Task) ||
                        (l.Job == "avvio" && File.Exists(Platform.StartupShortcut)))
            .Select(l => l.Job)
            .ToList();

        checks.Add(Verdicts.Supervisore(supervisore, vivo, supervisore?.Pid == Environment.ProcessId,
                                        plancia, vivo ? [] : daTask, adesso));

        foreach (var job in Jobs.All)
            checks.Add(Verdicts.Job(job, supervisore?.Jobs.FirstOrDefault(j => j.Name == job.Name),
                                    vivo, adesso, copertoDaTask: daTask.Contains(job.Name),
                                    acceso: Prefs.IsEnabled(job, accesi)));

        // =========================================================================================
        //  Automazioni di Windows
        // =========================================================================================
        if (!lettura.Ok)
        {
            checks.Add(new Check("automazioni", "Attivita'", Level.Warn,
                "il Task Scheduler non ha risposto: non so quali automazioni siano registrate",
                "riprova; `Get-ScheduledTask` da PowerShell dice cosa non va"));
        }
        else
        {
            // Si mostra solo cio' che ESISTE. Dopo la migrazione la sezione si riduce a una riga —
            // l'attivita' della plancia — e i tre task vecchi spariscono dal quadro invece di
            // restare come tre gialli permanenti su qualcosa che si e' deciso di non avere piu'.
            foreach (var (task, _, era) in Jobs.Legacy)
            {
                var t = tasks.FirstOrDefault(x => x.Name == task);
                if (t is null || t.State == "ASSENTE") continue;
                checks.Add(Verdicts.LegacyTask(task, era, t.State,
                    t.LastRun.Length > 0 ? $"ultima {t.LastRun}" : "mai eseguita",
                    Parsing.TaskResultIsFine(t.LastResult), Parsing.TaskResultLabel(t.LastResult),
                    // Doppione solo se un supervisore gira DAVVERO: fra la migrazione e il logon
                    // successivo il task vecchio e' l'unica sorveglianza rimasta.
                    supervisorePrende: vivo,
                    planciaRegistrata: plancia));
            }

            var suo = tasks.FirstOrDefault(x => x.Name == Platform.SupervisorTask);
            if (suo is not null && suo.State != "ASSENTE")
                checks.Add(PlanciaCheck(suo, vivo));
            else if (File.Exists(Tasks.StartupPlancia))
                checks.Add(new Check("automazioni", "Plancia", Level.Ok,
                    "avvio automatico da Esecuzione automatica (ripiego senza privilegi)"));
            else if (daTask.Count == 0)
                // Solo quando NON c'e' nemmeno un meccanismo vecchio: altrimenti l'avvio automatico
                // esiste, e' solo fuori di qui — e a dirlo ci pensano gia' le righe qui sopra.
                checks.Add(new Check("automazioni", "Plancia", Level.Warn,
                    "nessun avvio automatico: dopo un riavvio le automazioni restano ferme finche' non apri la plancia",
                    "`procione attivita migra`"));
        }

        checks.Add(BackupCheck(adesso));

        return new Snapshot
        {
            Taken = DateTimeOffset.Now,
            Layout = layout,
            Checks = checks,
        };
    }

    // =============================================================================================
    //  Sonde singole
    // =============================================================================================

    /// <summary>
    /// La freschezza dei dump. Qui si LEGGE il disco e basta: il giudizio sta in
    /// <see cref="Verdicts.Backup"/>, dove si puo' provare — la soglia delle 36 ore sorveglia
    /// esattamente la cosa che il 2026-08-17 e' morta in silenzio per sei notti.
    /// </summary>
    private static Check BackupCheck(DateTimeOffset adesso)
    {
        try
        {
            if (!Directory.Exists(Platform.BackupDir))
                return Verdicts.Backup(false, 0, null, 0, adesso, SogliaBackup);

            var dumps = new DirectoryInfo(Platform.BackupDir)
                .GetFiles("procionemgr-*.dump")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            return dumps.Count == 0
                ? Verdicts.Backup(true, 0, null, 0, adesso, SogliaBackup)
                : Verdicts.Backup(true, dumps.Count, new DateTimeOffset(dumps[0].LastWriteTime),
                                  dumps[0].Length, adesso, SogliaBackup);
        }
        catch (Exception ex)
        {
            return new Check("automazioni", "Backup", Level.Warn, $"cartella illeggibile: {ex.Message.Trim()}");
        }
    }

    /// Il backup e' notturno: oltre 36 ore almeno un giro e' saltato.
    private static readonly TimeSpan SogliaBackup = TimeSpan.FromHours(36);

    /// <summary>
    /// L'attivita' che avvia la plancia al logon, giudicata anche sul suo ESITO.
    ///
    /// Guardare solo lo stato («pronta») la lascerebbe verde anche quando fallisce a ogni logon —
    /// per esempio perche' punta a un eseguibile che non c'e' piu' (0x8007002E) — e in quel caso il
    /// rimedio suggerito dalla riga del supervisore («l'attivita' c'e' gia': basta rientrare»)
    /// manderebbe a rifare proprio la cosa che non funziona. E' la stessa asimmetria che
    /// <see cref="Verdicts.LegacyTask"/> evita di proposito.
    ///
    /// L'esito conta pero' solo quando il supervisore NON batte: mentre gira, l'ultimo codice e'
    /// «in esecuzione» o quello lasciato dall'arresto precedente, e non prova niente.
    /// </summary>
    private static Check PlanciaCheck(TaskInfo t, bool supervisoreVivo)
    {
        var quando = t.LastRun.Length > 0 ? $", ultima {t.LastRun}" : "";

        if (t.State == "Disabled")
            return new Check("automazioni", "Plancia", Level.Warn,
                $"DISABILITATA: al prossimo riavvio nessuna automazione ripartira'{quando}",
                $"Enable-ScheduledTask -TaskName '{Platform.SupervisorTask}'");

        if (!supervisoreVivo && !Parsing.TaskResultIsFine(t.LastResult))
            return new Check("automazioni", "Plancia", Level.Down,
                $"FALLISCE all'avvio: ultimo esito {Parsing.TaskResultLabel(t.LastResult)}{quando}",
                "l'eseguibile registrato non c'e' piu' o non parte: `procione attivita registra` lo riscrive");

        return new Check("automazioni", "Plancia", Level.Ok,
            $"{t.State.ToLowerInvariant()}, al logon avvia il supervisore{quando}");
    }

    // =============================================================================================
    //  Utilita'
    // =============================================================================================

    private const string PodJsonPath =
        "jsonpath={range .items[*]}{.metadata.namespace}{\"|\"}{.metadata.name}{\"|\"}{.status.phase}{\"|\"}" +
        "{.status.containerStatuses[0].restartCount}{\"|\"}{.status.containerStatuses[0].ready}{\"|\"}" +
        "{.metadata.creationTimestamp}{\"\\n\"}{end}";

    /// <summary>
    /// Solo la domanda «quale assetto e' vivo?», per i guardrail delle azioni: una chiamata a
    /// docker invece della rilevazione completa, perche' rifiutare un comando dev'essere immediato.
    /// </summary>
    public static (bool Kind, bool Compose) LayoutQuick()
    {
        var c = ContainersAsync().GetAwaiter().GetResult();
        return (c.Any(x => x.Name == Platform.KindNodeContainer && x.State == "running"),
                c.Any(x => x.Project == Platform.ComposeProject && x.Service == "ui" && x.State == "running"));
    }

    private static async Task<List<Container>> ContainersAsync()
    {
        // `docker ps -a`: anche i fermi. Un container che ESISTE ma e' fermo e' una diagnosi
        // diversa da un container che non c'e', e i due rimedi non si somigliano.
        const string formato = "{{.Names}}\t{{.State}}\t{{.Status}}\t{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.service\"}}";
        var r = await Proc.CaptureAsync("docker", ["ps", "-a", "--format", formato], 15000);
        return Parsing.Containers(r.Out);
    }

    private static string? ReadMarker(string path)
    {
        try { return File.Exists(path) ? Parsing.Marker(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static Task<Tasks.TaskRead> ScheduledTasksAsync() =>
        Tasks.ReadAsync(Jobs.Legacy.Select(l => l.Task).Append(Platform.SupervisorTask));

    /// <summary>Quanti guscii sono vivi e da quale cartella girano.</summary>
    private static (int Count, string? Path, int Pid) ShellProcesses()
    {
        try
        {
            var ps = Process.GetProcessesByName("ProcioneMGR");
            if (ps.Length == 0) return (0, null, 0);
            string? path = null;
            try { path = ps[0].MainModule?.FileName; } catch { /* processo di un altro utente */ }
            return (ps.Length, path, ps[0].Id);
        }
        catch { return (0, null, 0); }
    }

    public static HashSet<int> ListeningPorts()
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet(); }
        catch { return []; }
    }

    private static async Task<(bool Ok, int Status, string Body, string Error)> GetAsync(HttpClient c, string url)
    {
        try
        {
            using var r = await c.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            return ((int)r.StatusCode == 200, (int)r.StatusCode, body.Trim(),
                    (int)r.StatusCode == 200 ? "" : $"HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, 0, "", RootMessage(ex));
        }
    }

    private static async Task<bool> TcpAsync(string host, int port, int timeoutMs = 3000)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return c.Connected;
        }
        catch { return false; }
    }

    private static string RootMessage(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message.Trim();
    }

    private static string Explain(ExecResult r) => r.Code switch
    {
        Proc.TimedOut => r.Err,
        Proc.Failed => r.Err,
        Proc.NotStarted => r.Err,
        _ => r.Text.Length > 0 ? r.FirstLine : $"uscita {r.Code}",
    };
}
