using System.Diagnostics;

namespace Procione;

/// <summary>
/// Le azioni. Regola di questa classe: <b>l'operativita' che esiste gia' negli script si CHIAMA,
/// non si riscrive</b>.
///
/// Gli script di <c>scripts/</c> non sono comandi qualsiasi: sono sedimentazione di incidenti
/// (il proxy che inoltra a un IP vecchio, il tunnel stantio, il task che dice "registrato" senza
/// esserlo). Riprodurli in C# significherebbe farli divergere il giorno in cui uno dei due viene
/// corretto — e la copia sbagliata sarebbe quella dentro lo strumento che l'operatore usa per
/// prima. Qui si aggiungono soltanto due cose che gli script non hanno: i <b>guardrail</b>
/// (rifiutare cio' che viola la regola 2) e la <b>verifica</b> dell'esito.
/// </summary>
internal static class Actions
{
    // =============================================================================================
    //  Avvio
    // =============================================================================================

    /// <summary>Bring-up completo. E' bringup.ps1: idempotente, non fallisce mai in modo bloccante.</summary>
    public static int UpAll(bool forza)
    {
        var (kind, compose) = Probes.LayoutQuick();
        if (compose && !forza)
        {
            Ui.Error("l'assetto Docker Compose e' attivo: il bring-up kind aprirebbe un SECONDO guscio sulla 5199.");
            Ui.Info("`procione ferma compose` prima, oppure --forza se sai cosa stai facendo (regola 2).");
            return 2;
        }
        Ui.Title("Bring-up (scripts/bringup.ps1)");
        return Proc.Script("bringup.ps1");
    }

    /// <summary>Avvia il guscio come fa il profilo `procione-main` di .claude/launch.json.</summary>
    public static int UpShell(bool forza)
    {
        if (Probes.ListeningPorts().Contains(Platform.ShellPort) && !forza)
        {
            Ui.Error($"la porta {Platform.ShellPort} e' gia' occupata: c'e' gia' un guscio.");
            Ui.Info("mai due guscii insieme (l'incidente del 2026-07-20: istanza di worktree con master key");
            Ui.Info("segnaposto che intercetta l'utente e gli rompe il login).");
            Ui.Info("`procione ferma guscio` per chiudere quello attuale, poi riprova.");
            return 2;
        }
        var (_, compose) = Probes.LayoutQuick();
        if (compose && !forza)
        {
            Ui.Error("il guscio gira gia' come container Compose: un secondo guscio violerebbe la regola 2.");
            return 2;
        }
        if (!File.Exists(Platform.ShellProject))
        {
            Ui.Error($"progetto non trovato: {Platform.ShellProject}");
            return 2;
        }

        // Ambiente = Development, non Production: con `dotnet run` non pubblicato, in Production gli
        // static web assets non vengono agganciati e la UI resta senza blazor.web.js ne' CSS (404).
        // E' la stessa scelta, con la stessa motivazione, del profilo procione-main.
        var log = Platform.ShellLog;
        var cmd = string.Join(" ",
            $"& '{Platform.Script("ensure-trading-portforward.ps1")}';",
            "$tok = Join-Path $env:USERPROFILE '.procione/telegram.token';",
            "if (Test-Path $tok) { $env:TELEGRAM_BOT_TOKEN = (Get-Content $tok -Raw).Trim() }",
            "else { Write-Host 'Telegram : nessun token in ~/.procione/telegram.token, le notifiche falliranno.' };",
            "$env:ASPNETCORE_ENVIRONMENT='Development';",
            $"$env:ASPNETCORE_URLS='http://localhost:{Platform.ShellPort}';",
            $"dotnet run --project '{Platform.ShellProject}' --no-launch-profile -c Release 2>&1 |",
            $"Tee-Object -FilePath '{log}'");

        Ui.Title("Avvio del guscio");
        Ui.Info($"repo   : {Platform.RepoRoot}");
        Ui.Info($"log    : {log}   (`procione log guscio`)");
        if (!Proc.Detach(cmd)) return 2;

        // Il verdetto e' la VERIFICA, non l'assenza di errori all'avvio: `dotnet run` compila prima
        // di ascoltare, quindi si concede tempo vero.
        Ui.Info("compilazione e avvio in corso (fino a 3 minuti al primo giro)...");
        if (WaitFor(() => Probes.ListeningPorts().Contains(Platform.ShellPort), 180))
        {
            Ui.Good($"guscio in ascolto su http://localhost:{Platform.ShellPort}");
            return 0;
        }
        Ui.Warn($"la porta {Platform.ShellPort} non risulta ancora in ascolto: guarda `procione log guscio`.");
        return 1;
    }

    public static int UpCluster()
    {
        Ui.Title("Creazione del cluster kind (scripts/k8s-bootstrap.ps1)");
        Ui.Info("prerequisito una-tantum: dopo, i Secret vanno popolati con gli script dedicati.");
        return Proc.Script("k8s-bootstrap.ps1");
    }

    public static int UpCompose(bool conMotore, bool forza)
    {
        var (kind, _) = Probes.LayoutQuick();
        if (kind && !forza)
        {
            Ui.Error("il cluster kind e' attivo: accendere Compose adesso mette due assetti sullo stesso dominio.");
            Ui.Info("regola 2, un solo scrittore. Spegni prima i workload del cluster, oppure --forza.");
            return 2;
        }
        var argv = new List<string> { "compose", "-f", Platform.ComposeFile };
        if (conMotore) { argv.Add("--profile"); argv.Add("engine"); }
        argv.Add("up"); argv.Add("-d");
        Ui.Title("docker compose up -d" + (conMotore ? " (profilo engine)" : ""));
        return Proc.Inherit("docker", argv);
    }

    // =============================================================================================
    //  Arresto
    // =============================================================================================

    public static int DownShell()
    {
        Ui.Title("Arresto del guscio");
        var pids = OwningPids(Platform.ShellPort);
        if (pids.Count == 0) { Ui.Info($"nessun processo in ascolto su {Platform.ShellPort}: gia' fermo."); return 0; }

        Kill(pids);
        if (WaitFor(() => !Probes.ListeningPorts().Contains(Platform.ShellPort), 15))
        {
            Ui.Good($"porta {Platform.ShellPort} libera (verificato).");
            return 0;
        }
        Ui.Error($"la porta {Platform.ShellPort} risulta ancora occupata.");
        return 1;
    }

    public static int DownTunnels()
    {
        Ui.Title("Chiusura dei tunnel");
        int[] porte = [Platform.IngestionPort, Platform.EngineGrpcPort, Platform.EngineHealthPort];
        var pids = porte.SelectMany(OwningPids).Distinct().ToList();
        if (pids.Count == 0) Ui.Info("nessun port-forward attivo.");
        else Kill(pids);

        // I marcatori vanno via con i tunnel: lasciarli farebbe credere alla prossima sonda che un
        // tunnel inesistente stia servendo un pod.
        foreach (var m in new[] { Platform.TradingTunnelMarker, Platform.IngestionTunnelMarker })
            try { if (File.Exists(m)) File.Delete(m); } catch { }

        var rimaste = porte.Where(Probes.ListeningPorts().Contains).ToList();
        if (rimaste.Count == 0) { Ui.Good("tutte le porte dei tunnel sono libere (verificato)."); return 0; }
        Ui.Warn($"ancora in ascolto: {string.Join(", ", rimaste)}");
        return 1;
    }

    public static int DownCompose(bool conVolumi)
    {
        if (conVolumi && !Ui.ConfirmWord(
                "-v cancella i VOLUMI: il Postgres di Compose e tutto il suo contenuto spariscono.", "cancella"))
        {
            Ui.Info("annullato.");
            return 1;
        }
        var argv = new List<string> { "compose", "-p", Platform.ComposeProject, "-f", Platform.ComposeFile, "down" };
        if (conVolumi) argv.Add("-v");
        Ui.Title("docker compose down");
        return Proc.Inherit("docker", argv);
    }

    /// <summary>Spegne tutto cio' che sta sull'host, lasciando in piedi il cluster.</summary>
    public static int DownAll()
    {
        var esito = DownShell();
        esito |= DownTunnels();
        Ui.Info("il cluster kind resta acceso: e' il core caldo, riparte da solo e opera senza il guscio.");
        Ui.Info("per spegnere anche quello: `procione cluster distruggi` (distruttivo) oppure ferma Docker Desktop.");
        return esito == 0 ? 0 : 1;
    }

    // =============================================================================================
    //  Riparazioni
    // =============================================================================================

    public static int RepairAll()
    {
        Ui.Title("Riparazione completa (scripts/bringup.ps1)");
        return Proc.Script("bringup.ps1");
    }

    /// <summary>
    /// Ricrea il proxy dell'API server. Stessa ricetta di bringup.ps1 §2 — se una delle due cambia,
    /// deve cambiare anche l'altra: il socat punta al NOME DNS del nodo, MAI al suo IP, perche'
    /// Docker riassegna gli indirizzi della rete kind a ogni avvio.
    /// </summary>
    public static int RepairProxy()
    {
        Ui.Title("Riparazione del proxy dell'API server");

        var nodo = Proc.Capture("docker", ["inspect", Platform.KindNodeContainer, "--format", "{{.Name}}"], 10000);
        if (!nodo.Ok)
        {
            Ui.Error($"nodo '{Platform.KindNodeContainer}' non trovato: il cluster non esiste.");
            Ui.Info("`procione avvia cluster` e' il prerequisito.");
            return 2;
        }

        Proc.Capture("docker", ["rm", "-f", Platform.ApiProxyContainer], 20000);
        var run = Proc.Capture("docker",
        [
            "run", "-d", "--name", Platform.ApiProxyContainer,
            "--network", "kind", "--restart", "unless-stopped",
            "-p", $"127.0.0.1:{Platform.ApiProxyPort}:6443",
            "alpine/socat",
            "tcp-listen:6443,fork,reuseaddr",
            $"tcp-connect:{Platform.KindNodeContainer}:6443",
        ], 60000);
        if (!run.Ok) { Ui.Error($"docker run fallito: {run.FirstLine}"); return 2; }
        Ui.Info($"container {Platform.ApiProxyContainer} ricreato verso {Platform.KindNodeContainer}:6443.");

        RepairContext(silenzioso: true);

        // Il verdetto e' la RISPOSTA dell'API server attraverso il proxy: "container running" e' gia'
        // stato verde due volte mentre il cluster era irraggiungibile (2026-08-04, 2026-08-11).
        Ui.Info("verifico che l'API server risponda attraverso il proxy...");
        if (WaitFor(() => Proc.Kubectl(["get", "--raw", "/livez"], 8000).Ok, 45))
        {
            Ui.Good($"l'API server risponde attraverso 127.0.0.1:{Platform.ApiProxyPort} (verificato).");
            return 0;
        }
        Ui.Error("il proxy e' su ma l'API server non risponde ancora: il nodo sta ancora partendo?");
        Ui.Info("`procione stato` fra un minuto; se persiste, guarda `docker logs " + Platform.KindNodeContainer + "`.");
        return 1;
    }

    public static int RepairContext(bool silenzioso = false)
    {
        var url = $"https://127.0.0.1:{Platform.ApiProxyPort}";
        var r = Proc.Capture("kubectl", ["config", "set-cluster", Platform.KubeContext, $"--server={url}"], 10000);
        if (!silenzioso)
        {
            if (r.Ok) Ui.Good($"kubectl ora punta al proxy ({url}).");
            else Ui.Error($"set-cluster fallito: {r.FirstLine}");
        }
        return r.Ok ? 0 : 2;
    }

    public static int RepairTunnels()
    {
        Ui.Title("Riparazione dei tunnel (scripts/ensure-trading-portforward.ps1)");
        return Proc.Script("ensure-trading-portforward.ps1");
    }

    // =============================================================================================
    //  Riavvii
    // =============================================================================================

    /// <summary>
    /// Riavvia un servizio in-cluster e RIFA' il tunnel. I due passi sono inseparabili: il rollout
    /// sostituisce il pod, e un port-forward verso un pod che non c'e' piu' resta in ascolto
    /// mentendo — la pagina /trading si svuota mentre il motore opera regolarmente.
    /// </summary>
    public static int Restart(string componente, bool confermato)
    {
        var srv = Platform.Services.FirstOrDefault(s => s.Label == componente);
        if (srv.Label is null)
        {
            Ui.Error($"componente sconosciuto: '{componente}'. Attesi: {string.Join(", ", Platform.Services.Select(s => s.Label))}.");
            return 2;
        }

        if (componente == "motore" && !confermato &&
            !Ui.Confirm("Il motore ha posizioni e corsie vive. Riavviarlo davvero?"))
        {
            Ui.Info("annullato.");
            return 1;
        }

        Ui.Title($"Riavvio di {srv.Deploy}");
        var r = Proc.KubectlInherit(["-n", srv.Ns, "rollout", "restart", $"deploy/{srv.Deploy}"]);
        if (r != 0) return r;

        Proc.KubectlInherit(["-n", srv.Ns, "rollout", "status", $"deploy/{srv.Deploy}", "--timeout=180s"]);

        Ui.Info("il pod e' cambiato: rifaccio i tunnel, altrimenti resterebbero stantii.");
        return Proc.Script("ensure-trading-portforward.ps1");
    }

    public static int RestartShell()
    {
        var e = DownShell();
        if (e != 0) return e;
        return UpShell(forza: false);
    }

    // =============================================================================================
    //  Log
    // =============================================================================================

    public static int Logs(string componente, int righe, bool segui)
    {
        switch (componente)
        {
            case "guscio":
                if (!File.Exists(Platform.ShellLog))
                {
                    Ui.Warn($"nessun log in {Platform.ShellLog}.");
                    Ui.Info("il monolite non scrive su file: il log esiste solo se il guscio e' stato avviato");
                    Ui.Info("con `procione avvia guscio`. Altrimenti l'output e' nella finestra che lo esegue.");
                    return 1;
                }
                return TailFile(Platform.ShellLog, righe, segui);

            case "bringup":
                return File.Exists(Platform.BringUpLog)
                    ? TailFile(Platform.BringUpLog, righe, segui)
                    : Say($"nessun log di bring-up in {Platform.BringUpLog} (mai eseguito su questa macchina?).");

            case "watchdog":
                if (!File.Exists(Platform.WatchdogState)) return Say("il watchdog non ha ancora scritto il suo stato.");
                Ui.Title("Ultimo stato noto al watchdog");
                Console.WriteLine(File.ReadAllText(Platform.WatchdogState).Trim());
                return 0;

            case "supervisore" or "servizio":
                // E' il log che prima non esisteva: l'output dei lavori automatici finiva nella
                // finestra PowerShell che si chiudeva subito dopo, e nessuno lo vedeva mai.
                return File.Exists(Platform.SupervisorLog)
                    ? TailFile(Platform.SupervisorLog, righe, segui)
                    : Say("il supervisore non ha ancora scritto nulla (`procione servizio` per accenderlo).");

            case "compose":
            case "osservabilita":
                var progetto = componente == "compose" ? Platform.ComposeProject : Platform.ObservabilityProject;
                var docker = new List<string> { "compose", "-p", progetto, "logs", "--tail", righe.ToString() };
                if (segui) docker.Add("-f");
                return Proc.Inherit("docker", docker);

            default:
                var srv = Platform.Services.FirstOrDefault(s => s.Label == componente);
                if (srv.Label is null)
                {
                    Ui.Error($"componente sconosciuto: '{componente}'.");
                    Ui.Info("attesi: guscio, motore, ingestion, ml, supervisore, bringup, watchdog, compose, osservabilita.");
                    return 2;
                }
                var argv = new List<string> { "-n", srv.Ns, "logs", $"deploy/{srv.Deploy}", $"--tail={righe}" };
                if (segui) argv.Add("-f");
                return Proc.KubectlInherit(argv);
        }
    }

    /// <summary>
    /// `tail` (e `tail -f`) su un file che qualcun altro sta SCRIVENDO.
    ///
    /// FileShare.ReadWrite non e' un dettaglio: il log del guscio lo tiene aperto Tee-Object, e
    /// un'apertura in lettura esclusiva — quella di File.ReadLines — fallisce con "il processo non
    /// puo' accedere al file". Cioe' il log sarebbe leggibile solo a guscio spento: esattamente
    /// quando non serve.
    /// </summary>
    private static int TailFile(string path, int righe, bool segui)
    {
        righe = Math.Max(1, righe);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);

            var coda = new Queue<string>(righe);
            string? riga;
            while ((riga = sr.ReadLine()) is not null)
            {
                if (coda.Count == righe) coda.Dequeue();
                coda.Enqueue(riga);
            }
            foreach (var r in coda) Console.WriteLine(r);

            if (!segui) return 0;

            Ui.Info("— in ascolto sul file, Ctrl+C per uscire —");
            while (true)
            {
                var nuova = sr.ReadLine();
                if (nuova is null) { Thread.Sleep(400); continue; }
                Console.WriteLine(nuova);
            }
        }
        catch (Exception ex) { Ui.Error($"lettura fallita: {ex.Message.Trim()}"); return 2; }
    }

    // =============================================================================================
    //  Deleghe agli script
    // =============================================================================================

    public static int Backup(bool verifica) =>
        verifica ? Proc.Script("db-backup.ps1", "-Verify") : Proc.Script("db-backup.ps1");

    // =============================================================================================
    //  Supervisore
    // =============================================================================================

    /// <summary>
    /// Accende il supervisore residente in QUESTO processo, e non torna finche' non lo si ferma.
    /// </summary>
    public static async Task<int> Servizio(bool muto)
    {
        if (muto)
        {
            // Prima cosa, prima di qualunque stampa: la finestra che l'host ci ha dato non deve
            // restare a vista. E' l'intero motivo per cui il supervisore vive qui dentro.
            Ui.HideConsoleWindow();
            Ui.Colors = false;
        }

        using var supervisore = Supervisor.TryAcquire();
        if (supervisore is null)
        {
            var altro = Supervisor.ReadState();
            Ui.Warn($"c'e' gia' un supervisore vivo{(altro is null ? "" : $" (pid {altro.Pid})")}: non ne parte un secondo.");
            Ui.Info("regola 2, un solo scrittore: due supervisori farebbero due backup nella stessa notte.");
            Ui.Info("`procione servizio ferma` per fermare quello attuale, `procione stato` per vederlo.");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (!muto)
        {
            Ui.Title("Supervisore");
            Ui.Info("gli script girano qui dentro, con l'output catturato: nessuna finestra nasce mai.");
            Ui.Info("Ctrl+C per fermarlo. `procione stato` lo vede anche da un'altra finestra.");
        }

        await supervisore.RunAsync(muto, cts.Token);
        return 0;
    }

    public static int ServizioFerma()
    {
        var stato = Supervisor.ReadState();
        if (!Supervisor.IsAlive(stato, DateTimeOffset.Now))
        {
            Ui.Info("nessun supervisore attivo.");
            return 0;
        }
        if (!Supervisor.RequestStop())
        {
            Ui.Warn($"il supervisore (pid {stato!.Pid}) non risponde al segnale di arresto.");
            Ui.Info("se e' incastrato: termina il processo a mano, lo stato in %TEMP% resta coerente.");
            return 1;
        }

        // Il verdetto e' la VERIFICA, non l'invio del segnale.
        if (WaitFor(() => !Supervisor.IsAlive(Supervisor.ReadState(), DateTimeOffset.Now), 20))
        {
            Ui.Good("supervisore fermato (verificato).");
            return 0;
        }
        Ui.Warn("segnale inviato ma il supervisore risulta ancora vivo: sta finendo un lavoro lungo?");
        return 1;
    }

    /// <summary>I lavori: elencarli, accenderli, spegnerli, farne partire uno adesso.</summary>
    public static async Task<int> Lavoro(string? nome, string? cosa)
    {
        var adesso = DateTimeOffset.Now;
        var stato = Supervisor.ReadState();
        var vivo = Supervisor.IsAlive(stato, adesso);

        if (nome is null)
        {
            // Anche qui serve sapere cosa sta ancora girando dal Task Scheduler: senza, un lavoro
            // che gira regolarmente fuori dalla plancia risulterebbe «fermo».
            var lettura = vivo ? new Tasks.TaskRead(true, []) : await Tasks.ReadAsync(Jobs.Legacy.Select(l => l.Task));
            var daTask = !lettura.Ok
                ? []
                : Jobs.Legacy.Where(l => Tasks.Active(lettura.Tasks, l.Task)).Select(l => l.Job).ToList();
            var accesi = Prefs.Read();

            Ui.Title("Lavori del supervisore");
            foreach (var job in Jobs.All)
            {
                var c = Verdicts.Job(job, stato?.Jobs.FirstOrDefault(j => j.Name == job.Name), vivo, adesso,
                                     copertoDaTask: daTask.Contains(job.Name),
                                     acceso: Prefs.IsEnabled(job, accesi));
                Ui.Write("  " + Ui.Glyph(c.Level) + " ", Ui.Color(c.Level));
                Ui.Write(job.Name.PadRight(10), ConsoleColor.White);
                Ui.Line(c.Detail, ConsoleColor.Gray);
                // Solo lo script: il «cosa fa» sta gia' nel dettaglio del verdetto quando serve, e
                // ripeterlo raddoppia ogni riga senza aggiungere niente.
                Ui.Info($"{"",12}scripts/{job.Script} {string.Join(' ', job.Args)}".TrimEnd());
            }
            Ui.Info("");
            Ui.Info("procione lavoro <nome> ora|accendi|spegni");
            return 0;
        }

        var lavoro = Jobs.Find(nome);
        if (lavoro is null)
        {
            Ui.Error($"lavoro sconosciuto: '{nome}'. Attesi: {string.Join(", ", Jobs.All.Select(j => j.Name))}.");
            return 2;
        }

        switch (cosa)
        {
            case "accendi" or "on":
                return Prefs.Set(lavoro.Name, true) ? Say0($"«{lavoro.Name}» acceso.") : 2;

            case "spegni" or "off":
                return Prefs.Set(lavoro.Name, false) ? Say0($"«{lavoro.Name}» spento.") : 2;

            case null or "ora" or "now" or "esegui":
                // Esecuzione ESPLICITA, fuori cadenza. Non serve possedere il mutex: e' l'operatore
                // che l'ha chiesta, e la vede scorrere. Il supervisore residente, se c'e', continua
                // per conto suo — e ricalcolera' la prossima scadenza dal proprio ultimo giro.
                Ui.Title($"{lavoro.Name}: {lavoro.Script} {string.Join(' ', lavoro.Args)}".TrimEnd());
                if (vivo) Ui.Info("(un supervisore e' attivo: questo giro e' in piu', non al posto suo)");
                return Proc.Script(lavoro.Script, lavoro.Args);

            default:
                Ui.Error($"argomento non riconosciuto: '{cosa}'. Attesi: ora, accendi, spegni.");
                return 2;
        }
    }

    private static int Say0(string testo) { Ui.Good(testo); return 0; }

    public static int Argocd(string? sub, string? revisione = null) => sub switch
    {
        "su" or "up" => Proc.Script("argocd-toggle.ps1", "-Up"),
        "giu" or "down" => Proc.Script("argocd-toggle.ps1", "-Down"),
        "installa" or "bootstrap" => Proc.Script("k8s-argocd-bootstrap.ps1"),
        "ripunta" or "retarget" => revisione is null
            ? Say("uso: procione argocd ripunta <branch|tag|sha>   (`master` per tornare normale)")
            : Proc.Script("k8s-argocd-retarget.ps1", "-TargetRevision", revisione),
        _ => Proc.Script("argocd-toggle.ps1"),
    };

    // =============================================================================================
    //  Strumenti (tools/)
    // =============================================================================================

    /// <summary>
    /// I cinque programmi di <c>tools/</c>: verifiche vive, backup, caccia alle strategie.
    ///
    /// Chiedono tutti la connection string in una variabile d'ambiente, che finora andava
    /// impostata a mano prima di lanciarli. Qui si legge da <c>appsettings.json</c> del repo e si
    /// passa al figlio — la stessa fonte che usa il guscio, quindi non ci sono due verita'.
    /// </summary>
    public static int Tools(string? nome, string[] argomenti)
    {
        (string Nome, string Cosa, string Uso)[] elenco =
        [
            ("DbBackup",      "backup/verify/list/restore del database", "backup | verify [file] | list [dir] | restore [file]"),
            ("FuturesVerify", "verifica LIVE dei Futures, solo lettura (testnet/demo)", "nessun argomento"),
            ("SpotVerify",    "verifica LIVE dello spot Bitget, solo lettura", "nessun argomento"),
            ("PlatformExpand","inventario e misure sulla piattaforma", "stats | ... (vedi il sorgente)"),
            ("StrategyHunter","caccia alle strategie", "ingest | discover | validate | probe | save"),
        ];

        if (nome is null)
        {
            Ui.Title("Strumenti (tools/)");
            foreach (var (n, cosa, uso) in elenco)
            {
                Ui.Write($"    {n,-15}", ConsoleColor.White);
                Ui.Line(cosa, ConsoleColor.DarkGray);
                Ui.Info($"{"",15}{uso}");
            }
            Ui.Info("");
            Ui.Info("uso: procione strumenti <nome> [argomenti...]");
            Ui.Warn("StrategyHunter e le fasi di scrittura di PlatformExpand pretendono il GUSCIO FERMO");
            Ui.Info("(regola 2: un solo scrittore sulla stessa serie).");
            return 0;
        }

        var trovato = elenco.FirstOrDefault(e => string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase)).Nome;
        if (trovato is null)
        {
            Ui.Error($"strumento sconosciuto: '{nome}'. Attesi: {string.Join(", ", elenco.Select(e => e.Nome))}.");
            return 2;
        }

        // --- i due percorsi che NON passano da qui -------------------------------------------------
        // Non e' prudenza generica: sono le uniche due azioni irreversibili dell'intera cartella, e
        // una plancia serve a rendere le cose FACILI. Renderle facili e' esattamente cio' che non
        // va fatto con un ordine di mercato vero e con un ripristino che sovrascrive il database.
        if (argomenti.Any(a => a.Equals("--place-min-order", StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Error("--place-min-order piazza un ordine di mercato REALE (~5 USDT) sulle credenziali salvate.");
            Ui.Info("la plancia non lo esegue: lancialo a mano, deliberatamente, sapendo su quale conto sei.");
            Ui.Info($"    dotnet run --project tools/SpotVerify -c Release -- --place-min-order");
            return 2;
        }
        if (trovato == "DbBackup" && argomenti.FirstOrDefault()?.Equals("restore", StringComparison.OrdinalIgnoreCase) == true)
        {
            Ui.Error("`restore` sovrascrive il database corrente (pg_restore --clean) e chiede conferma su stdin.");
            Ui.Info("la plancia non lo esegue: lancialo a mano, con il terminale davanti.");
            Ui.Info($"    dotnet run --project tools/DbBackup -c Release -- restore <file>");
            return 2;
        }

        var progetto = Path.Combine(Platform.RepoRoot, "tools", trovato, $"{trovato}.csproj");
        if (!File.Exists(progetto)) { Ui.Error($"progetto non trovato: {progetto}"); return 2; }

        var conn = ConnectionString();
        if (conn is null)
            Ui.Warn("connection string non trovata in appsettings.json: lo strumento potrebbe rifiutarsi di partire.");
        else
            Environment.SetEnvironmentVariable("ConnectionStrings__PostgresConnection", conn);

        // DbBackup, senza BACKUP_DIR, ripiega sul suo default storico (ProgettoP\backup): sarebbe
        // una SECONDA verita' sui dump, diversa da quella di `procione backup`, di `procione stato`
        // e del lavoro notturno — e `backup` depositerebbe dentro l'albero del repository un file
        // che contiene la master key cifrata e le credenziali exchange.
        if (trovato == "DbBackup" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BACKUP_DIR")))
            Environment.SetEnvironmentVariable("BACKUP_DIR", Platform.BackupDir);

        if (trovato is "StrategyHunter" or "PlatformExpand" &&
            Probes.ListeningPorts().Contains(Platform.ShellPort))
        {
            Ui.Warn($"il guscio e' vivo su :{Platform.ShellPort}, e questo strumento scrive sulle stesse serie.");
            if (!Ui.Confirm("Procedere lo stesso? (regola 2: un solo scrittore)")) { Ui.Info("annullato."); return 1; }
        }

        Ui.Title($"{trovato} {string.Join(' ', argomenti)}".TrimEnd());
        List<string> argv = ["run", "--project", progetto, "-c", "Release", "--"];
        argv.AddRange(argomenti);
        return Proc.Inherit("dotnet", argv);
    }

    /// <summary>La connection string, letta dallo stesso appsettings.json che usa il guscio.</summary>
    private static string? ConnectionString()
    {
        try
        {
            if (!File.Exists(Platform.AppSettings)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Platform.AppSettings));
            return doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                   cs.TryGetProperty("PostgresConnection", out var pg)
                ? pg.GetString()
                : null;
        }
        catch { return null; }
    }

    public static int Observability(string? sub, bool purga) => sub switch
    {
        "su" or "up" => Proc.Script("observability-up.ps1"),
        "giu" or "down" => purga ? Proc.Script("observability-down.ps1", "-Purge") : Proc.Script("observability-down.ps1"),
        _ => Say("uso: procione osservabilita su|giu [--purga]"),
    };

    public static int Images(string[] target)
    {
        Ui.Info("build e import nel nodo kind. NON riavvia i pod: il rollout e' una decisione separata.");
        if (target.Length == 0) return Proc.Script("build-images-local.ps1");

        // I target si passano come valori CONSECUTIVI dopo -Targets, non come stringa separata da
        // virgole: con `powershell -File` la virgola non viene spezzata, e `[string[]]$Targets`
        // riceverebbe un unico elemento «a,b» che lo script rifiuta con «Target sconosciuto».
        List<string> argv = ["-Targets", .. target];
        return Proc.Script("build-images-local.ps1", [.. argv]);
    }

    public static int Smoke() => Proc.Script("e2e-smoke.ps1", "-Context", Platform.KubeContext);

    /// <summary>
    /// I Secret del cluster. Sono tre script separati piu' un wrapper, ed e' l'unico gruppo che
    /// finora non aveva un modo di essere lanciato dalla plancia — cioe' l'unico per cui bisognava
    /// ancora ricordarsi il nome del file.
    /// </summary>
    public static int Secrets(string? quale)
    {
        switch (quale)
        {
            case "postgres": return Proc.Script("k8s-postgres-secret.ps1");
            case "trading": return Proc.Script("k8s-trading-secret.ps1");
            case "ui": return Proc.Script("k8s-ui-secret.ps1");

            case "da-appsettings" or "appsettings":
                Ui.Title("Riscrittura dei Secret da ProcioneMGR/appsettings.json");
                Ui.Info("dopo una rotazione: rilegge master key, segreto gRPC e connection string e li riscrive.");
                Ui.Info("i pod NON rileggono i Secret da soli: serve `procione riavvia motore` (e gli altri).");
                return Proc.Script("update-k8s-secrets-from-appsettings.ps1");

            case null or "tutti" or "all":
                Ui.Title("Tutti i Secret, dalla configurazione viva");
                Ui.Info("e' la strada consigliata: un solo posto da cui leggere i valori, nessuno da digitare.");
                return Proc.Script("update-k8s-secrets-from-appsettings.ps1");

            default:
                Ui.Error($"argomento non riconosciuto: '{quale}'.");
                Ui.Info("attesi: tutti, postgres, trading, ui, da-appsettings.");
                return 2;
        }
    }

    /// <summary>
    /// Il guscio come lo avvia <c>run-postgres.ps1</c>: ambiente Production, tunnel garantiti.
    ///
    /// Si avvisa PRIMA, invece di lasciar fallire: con il cluster giu' quello script muore su una
    /// riga di stderr di kubectl ($ErrorActionPreference='Stop' lo trasforma in errore terminante),
    /// e l'utente si ritrova una finestra che si chiude senza spiegazioni.
    /// </summary>
    public static int Postgres()
    {
        var (kind, _) = Probes.LayoutQuick();
        if (!kind)
        {
            Ui.Warn("il cluster kind non e' in esecuzione: run-postgres.ps1 muore appena interroga kubectl.");
            Ui.Info("($ErrorActionPreference='Stop' piu' lo stderr di kubectl: e' un difetto noto dello script.)");
            Ui.Info("usa `procione avvia guscio`, che fa la stessa cosa senza dipendere dal cluster.");
            return 2;
        }
        Ui.Title("Avvio del guscio (scripts/run-postgres.ps1)");
        return Proc.Script("run-postgres.ps1");
    }

    /// <summary>Un giro di veglia adesso, sotto gli occhi di chi guarda.</summary>
    public static int Watchdog() => Proc.Script("watchdog.ps1");

    /// <summary>
    /// Lo sportello per tutto il resto: qualunque script di <c>scripts/</c>, con i suoi argomenti.
    ///
    /// Esiste perche' la promessa «un solo programma» non regge se il ventesimo script, quello che
    /// serve una volta l'anno, richiede comunque di ricordarsi percorso ed estensione. Il nome si
    /// risolve DENTRO scripts/ e non altrove: non e' una scorciatoia per eseguire file arbitrari.
    /// </summary>
    public static int Run(string? nome, string[] argomenti)
    {
        var disponibili = Scripts();
        if (nome is null)
        {
            Ui.Title($"Script disponibili ({disponibili.Count})");
            foreach (var s in disponibili) Ui.Info(s);
            Ui.Info("");
            Ui.Info("uso: procione esegui <nome> [argomenti...]   (l'estensione .ps1 si puo' omettere)");
            return 0;
        }

        var file = nome.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? nome : nome + ".ps1";
        var trovato = disponibili.FirstOrDefault(s => string.Equals(s, file, StringComparison.OrdinalIgnoreCase));
        if (trovato is null)
        {
            Ui.Error($"nessuno script '{file}' in scripts/.");
            var simili = disponibili.Where(s => s.Contains(nome, StringComparison.OrdinalIgnoreCase)).ToList();
            if (simili.Count > 0) Ui.Info("forse: " + string.Join(", ", simili));
            return 2;
        }

        Ui.Title($"scripts/{trovato} {string.Join(' ', argomenti)}".TrimEnd());
        return Proc.Script(trovato, argomenti);
    }

    private static List<string> Scripts()
    {
        try
        {
            return [.. new DirectoryInfo(Path.Combine(Platform.RepoRoot, "scripts"))
                .GetFiles("*.ps1").Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)];
        }
        catch { return []; }
    }

    public static int DestroyCluster()
    {
        Ui.Title("Distruzione del cluster kind");
        Ui.Warn("il cluster contiene i Secret (master key compresa) e va poi ricostruito da zero:");
        Ui.Info("k8s-bootstrap.ps1, i tre script dei Secret, build-images-local.ps1, il deploy dei manifesti.");
        Ui.Info("I DATI non sono nel cluster (Postgres e' un servizio Windows): quelli restano.");
        if (!Ui.ConfirmWord($"Distruggere '{Platform.KindNodeContainer}'?", "distruggi"))
        {
            Ui.Info("annullato.");
            return 1;
        }
        return Proc.Script("k8s-teardown.ps1");
    }

    public static int Open(string? rotta)
    {
        var url = $"http://localhost:{Platform.ShellPort}/" + (rotta ?? "").TrimStart('/');
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Ui.Good($"aperto {url}");
            return 0;
        }
        catch (Exception ex) { Ui.Error($"apertura fallita: {ex.Message.Trim()}"); return 2; }
    }

    // =============================================================================================
    //  Dottore
    // =============================================================================================

    /// <summary>
    /// I PREREQUISITI, non lo stato: cosa manca sulla macchina perche' la piattaforma possa anche
    /// solo essere avviata. E' la domanda che viene prima di `stato`, e ha risposte diverse.
    /// </summary>
    public static int Doctor()
    {
        Ui.Title("Repository");
        Ui.Info($"radice   : {Platform.RepoRoot}");
        Ui.Info($"dedotta  : {Platform.RepoRootOrigin}");
        Ui.Info("override : $env:PROCIONE_REPO (utile dai worktree, per comandare sempre il repo principale)");

        Ui.Title("Strumenti");
        var mancanti = 0;
        // `kind` e' OPZIONALE e su questa macchina non c'e' apposta: le immagini si importano con
        // `docker save | ctr images import`, che non lo usa. Segnalarlo come prerequisito mancante
        // insegnerebbe a ignorare i gialli del dottore, che e' il modo di renderlo inutile.
        foreach (var (exe, args, perche, opzionale) in new[]
                 {
                     ("docker",  new[] { "--version" },              "tutto: cluster, Compose, immagini", false),
                     ("kubectl", new[] { "version", "--client" },    "cluster e tunnel", false),
                     ("dotnet",  new[] { "--version" },              "compilare e avviare il guscio", false),
                     ("git",     new[] { "--version" },              "aggiornamenti del repo", false),
                     ("kind",    new[] { "--version" },              "creare/distruggere il cluster", true),
                 })
        {
            var r = Proc.Capture(exe, args, 20000);
            if (r.Ok) Ui.Good($"{exe,-8} {r.FirstLine.Trim()}");
            else if (opzionale) Ui.Info($"{exe,-8} assente (opzionale) — servirebbe per: {perche}");
            else { Ui.Warn($"{exe,-8} non disponibile — serve per: {perche}"); mancanti++; }
        }

        var pgDump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                  "PostgreSQL", "18", "bin", "pg_dump.exe");
        if (File.Exists(pgDump)) Ui.Good($"pg_dump  {pgDump}");
        else { Ui.Warn($"pg_dump  non trovato in {pgDump} — i backup non possono girare"); mancanti++; }

        Ui.Title("Segreti e configurazione");
        if (File.Exists(Platform.AppSettings))
        {
            // Si controlla la FORMA, mai il valore: la master key non si stampa e non si logga.
            var testo = File.ReadAllText(Platform.AppSettings);
            var segnaposto = testo.Contains("__NUOVA_PASSWORD_PG__") || testo.Contains("__MASTER_KEY__");
            if (segnaposto) { Ui.Error("appsettings.json contiene ancora dei segnaposto: completa la rotazione."); mancanti++; }
            else Ui.Good("appsettings.json presente, nessun segnaposto residuo");
        }
        else { Ui.Error($"appsettings.json assente ({Platform.AppSettings}): il guscio non partira'."); mancanti++; }

        if (File.Exists(Platform.TelegramTokenFile)) Ui.Good("token Telegram in ~/.procione/telegram.token");
        else Ui.Warn("nessun token in ~/.procione/telegram.token: le notifiche del guscio falliranno in silenzio");

        foreach (var v in new[] { "TELEGRAM_BOT_TOKEN", "TELEGRAM_CHAT_ID" })
        {
            var presente = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.Machine) ??
                Environment.GetEnvironmentVariable(v));
            if (presente) Ui.Good($"{v} impostata (il watchdog puo' avvisare)");
            else Ui.Warn($"{v} assente: il watchdog logga soltanto, non notifica");
        }

        var env = Path.Combine(Platform.RepoRoot, ".env");
        if (File.Exists(env)) Ui.Good(".env presente (assetto Docker Compose disponibile)");
        else Ui.Info(".env assente: l'assetto Docker Compose non e' configurato (copia .env.example)");

        Ui.Title("Verdetto");
        if (mancanti == 0) Ui.Good("nessun prerequisito mancante.");
        else Ui.Warn($"{mancanti} prerequisiti da sistemare (sopra, in giallo o rosso).");
        return mancanti == 0 ? 0 : 1;
    }

    // =============================================================================================
    //  Utilita'
    // =============================================================================================

    private static int Say(string testo) { Ui.Info(testo); return 1; }

    /// <summary>PID dei processi in ascolto su una porta.</summary>
    private static List<int> OwningPids(int porta)
    {
        var r = Proc.Ps($"Get-NetTCPConnection -State Listen -LocalPort {porta} -ErrorAction SilentlyContinue | " +
                        "Select-Object -ExpandProperty OwningProcess -Unique", 15000);
        return r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0)
                    .Where(v => v > 0).Distinct().ToList();
    }

    private static void Kill(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var nome = p.ProcessName;
                p.Kill(entireProcessTree: true);
                Ui.Info($"terminato {nome} (pid {pid})");
            }
            catch (Exception ex) { Ui.Warn($"pid {pid} non terminato: {ex.Message.Trim()}"); }
        }
    }

    /// <summary>Aspetta che una condizione diventi vera. Il verdetto e' la verifica, mai l'assenza di errori.</summary>
    private static bool WaitFor(Func<bool> condizione, int secondi)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(secondi);
        while (DateTime.UtcNow < scadenza)
        {
            try { if (condizione()) return true; } catch { }
            Thread.Sleep(700);
        }
        try { return condizione(); } catch { return false; }
    }
}
