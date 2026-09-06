using System.Text.Json;

namespace Procione;

/// <summary>
/// `procione` — la plancia di comando della piattaforma.
///
/// Senza argomenti apre la plancia interattiva. Con argomenti fa una cosa sola e se ne va, cosi'
/// sta anche dentro uno script (`procione stato` esce 0/1/2 a seconda della gravita').
///
/// I nomi dei comandi sono in italiano perche' sono interfaccia utente; i tipi e i membri di questo
/// progetto sono in inglese, come ovunque nel repository. Gli equivalenti inglesi piu' ovvi
/// (status, up, down, logs, fix, doctor) sono accettati come sinonimi: nessuno deve ricordarsi in
/// che lingua stava pensando.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        Ui.Init();
        try
        {
            return await Dispatch([.. argv]);
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message.Trim());
            return 3;
        }
    }

    private static async Task<int> Dispatch(List<string> args)
    {
        if (args.Count == 0) return await Dashboard.RunAsync(soloLettura: false);

        var comando = args[0].ToLowerInvariant();
        args.RemoveAt(0);

        // Gli argomenti GREZZI, prima che le opzioni comuni vengano tolte: `esegui` e `strumenti`
        // li girano a un altro programma, e li' `-v` e' un'opzione DI QUELLO, non nostra. Toglierla
        // qui cambierebbe in silenzio il comando che l'utente ha scritto.
        var grezzi = args.ToArray();

        // Opzioni comuni, tolte dalla lista prima di leggere gli argomenti posizionali.
        var json = Flag(args, "--json");
        var forza = Flag(args, "--forza", "--force");
        var si = Flag(args, "--si", "--yes", "-y");
        var purga = Flag(args, "--purga", "--purge");
        var segui = Flag(args, "-f", "--segui", "--follow");
        var conMotore = Flag(args, "--motore", "--engine");
        var conVolumi = Flag(args, "-v", "--volumi");
        var produzione = Flag(args, "--produzione", "--production");
        var righe = int.TryParse(Opt(args, "-n") ?? Opt(args, "--righe"), out var n) ? n : 80;
        var ogni = int.TryParse(Opt(args, "--ogni") ?? Opt(args, "--every"), out var o) ? o : (int?)null;

        switch (comando)
        {
            case "stato" or "status" or "st":
                return await Stato(json);

            case "guarda" or "watch":
                return await Dashboard.RunAsync(soloLettura: true, ogni);

            case "plancia" or "menu":
                return await Dashboard.RunAsync(soloLettura: false, ogni);

            case "avvia" or "up" or "su":
                return (Arg(args) ?? "tutto") switch
                {
                    "tutto" or "all" => Actions.UpAll(forza),
                    // --produzione e' la vecchia `procione postgres`: il guscio come lo avvia
                    // run-postgres.ps1, in ambiente Production. Dietro un flag perche' e' l'altra
                    // via, non quella normale.
                    "guscio" or "shell" or "ui" => produzione ? Actions.Postgres() : Actions.UpShell(forza),
                    "cluster" or "kind" => Machine.Cluster("avvia", si),
                    "docker" => Machine.Docker("avvia", si),
                    "db" or "database" or "postgres" => Machine.Database("avvia", si),
                    "compose" => Actions.UpCompose(conMotore, forza),
                    "osservabilita" or "obs" => Actions.Observability("su", false),
                    "argocd" => Actions.Argocd("su"),
                    var altro => Sconosciuto(altro, "tutto, guscio, cluster, docker, db, compose, osservabilita, argocd"),
                };

            case "ferma" or "down" or "giu":
                return (Arg(args) ?? "tutto") switch
                {
                    "tutto" or "all" => Actions.DownAll(),
                    "guscio" or "shell" or "ui" => Actions.DownShell(),
                    "tunnel" or "tunnels" => Actions.DownTunnels(),
                    "cluster" or "kind" => Machine.Cluster("ferma", si),
                    "docker" => Machine.Docker("ferma", si),
                    "db" or "database" or "postgres" => Machine.Database("ferma", si),
                    "supervisore" or "servizio" => Actions.ServizioFerma(),
                    "compose" => Actions.DownCompose(conVolumi),
                    "osservabilita" or "obs" => Actions.Observability("giu", purga),
                    "argocd" => Actions.Argocd("giu"),
                    var altro => Sconosciuto(altro, "tutto, guscio, tunnel, cluster, docker, db, supervisore, compose, osservabilita, argocd"),
                };

            case "riavvia" or "restart":
                var che = Arg(args);
                if (che is null)
                    return Sconosciuto("(niente)", "guscio, motore, ingestion, ml, cluster, db, supervisore");
                return che switch
                {
                    "guscio" or "shell" => Actions.RestartShell(),
                    "cluster" or "kind" => Machine.Cluster("riavvia", si),
                    "db" or "database" or "postgres" => Machine.Database("riavvia", si),
                    "supervisore" or "servizio" => Actions.ServizioRiavvia(),
                    _ => Actions.Restart(che, si),
                };

            case "ripara" or "fix":
                return (Arg(args) ?? "tutto") switch
                {
                    "tutto" or "all" => Actions.RepairAll(),
                    "proxy" => Actions.RepairProxy(),
                    "tunnel" or "tunnels" => Actions.RepairTunnels(),
                    "contesto" or "context" => Actions.RepairContext(),
                    var altro => Sconosciuto(altro, "tutto, proxy, tunnel, contesto"),
                };

            case "log" or "logs":
                return Actions.Logs(Arg(args) ?? "guscio", righe, segui);

            case "dottore" or "doctor":
                return Actions.Doctor();

            // --- il supervisore: le automazioni, dentro questo programma ---------------------------

            case "servizio" or "supervisore" or "service":
                return (Arg(args) ?? "avvia") switch
                {
                    "avvia" or "su" or "start" => await Actions.Servizio(
                        Flag(args, "--muto", "--silenzioso", "--quiet"),
                        conIcona: !Flag(args, "--senza-icona", "--no-icon")),
                    "ferma" or "giu" or "stop" => Actions.ServizioFerma(),
                    "riavvia" or "restart" => Actions.ServizioRiavvia(),
                    "stato" or "status" => await Actions.Lavoro(null, null),
                    var altro => Sconosciuto(altro, "avvia, ferma, riavvia, stato"),
                };

            case "lavoro" or "lavori" or "job":
                // Due letture separate e in ordine: annidarle in una sola chiamata lascerebbe
                // l'ordine degli argomenti alla regola di valutazione del linguaggio.
                var quale = Arg(args);
                var cheFare = Arg(args);
                return await Actions.Lavoro(quale, cheFare);

            case "veglia" or "watchdog":
                return Actions.Watchdog();

            case "corsie" or "lanes":
                // Argomenti GREZZI: `--modalita paper` appartiene allo strumento, non a noi.
                return Actions.Lanes(grezzi);

            case "attivita" or "tasks":
                return (Arg(args) ?? "stato") switch
                {
                    // `--si` vale qui e non su `cluster distruggi`, che pretende una parola
                    // DIGITATA: la differenza e' voluta. Una migrazione e' reversibile
                    // (`procione attivita migra` di nuovo, o `rimuovi`); un cluster distrutto no.
                    "migra" or "migrate" => await Tasks.MigrateAsync(si),
                    "registra" or "register" => Tasks.RegisterSupervisor(),
                    "rimuovi" or "remove" => await Tasks.RemoveAllAsync(si),
                    _ => await Stato(false), // lo stato delle attivita' e' gia' una sezione del quadro
                };

            case "backup":
                return Actions.Backup(Flag(args, "--verifica", "--verify"));

            case "segreti" or "secrets":
                return Actions.Secrets(Arg(args));

            // --- cio' che sta SOTTO la piattaforma: Docker, il database, le porte -----------------

            case "db" or "database" or "postgres" or "pg":
                // `postgres` significa il DATABASE, che e' quello che la parola vuol dire. Fino al
                // 2026-09-06 avviava invece il GUSCIO (run-postgres.ps1, ambiente Production): un
                // comando che fa una cosa diversa dal proprio nome e' una trappola, e per giunta
                // qui la cosa diversa era piu' invasiva. Quella resta, dietro un flag esplicito:
                // `procione avvia guscio --produzione`.
                return Machine.Database(Arg(args), si);

            case "docker":
                return Machine.Docker(Arg(args), si);

            case "porte" or "ports":
                return Machine.Ports();

            case "icona" or "tray":
                return await Actions.Icona(int.TryParse(Opt(args, "--per"), out var sec) ? sec : null);

            case "esegui" or "run":
                return Actions.Run(grezzi.FirstOrDefault(), [.. grezzi.Skip(1)]);

            case "strumenti" or "tools":
                return Actions.Tools(grezzi.FirstOrDefault(), [.. grezzi.Skip(1)]);

            case "argocd":
                var sottoArgocd = Arg(args);
                // La revisione GREZZA: e' un riferimento git, e i riferimenti git distinguono le
                // maiuscole. Il sottocomando invece resta insensibile, come tutti gli altri.
                return Actions.Argocd(sottoArgocd, ArgGrezzo(args));

            case "osservabilita" or "obs":
                return Actions.Observability(Arg(args), purga);

            case "immagini" or "images":
                return Actions.Images([.. args]);

            case "smoke":
                return Actions.Smoke();

            case "apri" or "open":
                return Actions.Open(Arg(args));

            case "cluster":
                return (Arg(args) ?? "") switch
                {
                    "crea" or "create" => Actions.UpCluster(),
                    "distruggi" or "destroy" => Actions.DestroyCluster(),
                    // Fra «crea» e «distruggi» non c'era niente, e il rimedio che la plancia
                    // stampava per un nodo fermo era un comando docker da battere altrove.
                    var altro => Machine.Cluster(altro, si),
                };

            case "aiuto" or "help" or "-h" or "--help" or "/?":
                Aiuto();
                return 0;

            default:
                Ui.Error($"comando sconosciuto: '{comando}'.");
                Aiuto();
                return 2;
        }
    }

    // =============================================================================================

    private static async Task<int> Stato(bool json)
    {
        var s = await Probes.RunAsync();

        if (json)
        {
            // Forma stabile e piatta: serve a chi mette `procione stato --json` in un altro script,
            // non a chi legge.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                istante = s.Taken,
                assetto = s.Layout.ToString().ToLowerInvariant(),
                verdetto = s.Worst.ToString().ToLowerInvariant(),
                uscita = s.ExitCode,
                controlli = s.Checks.Select(c => new
                {
                    gruppo = c.Group,
                    nome = c.Name,
                    livello = c.Level.ToString().ToLowerInvariant(),
                    dettaglio = c.Detail,
                    rimedio = c.Fix,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return s.ExitCode;
        }

        Ui.Print(Ui.Render(s, conRimedi: true));
        return s.ExitCode;
    }

    private static int Sconosciuto(string dato, string attesi)
    {
        Ui.Error($"argomento non riconosciuto: '{dato}'.");
        Ui.Info($"attesi: {attesi}.");
        return 2;
    }

    // --- lettura degli argomenti -----------------------------------------------------------------

    /// <summary>Il prossimo argomento posizionale, normalizzato: e' una parola-chiave nostra.</summary>
    private static string? Arg(List<string> args) => ArgGrezzo(args)?.ToLowerInvariant();

    /// <summary>
    /// Il prossimo argomento posizionale COM'E' STATO SCRITTO.
    ///
    /// Serve per i valori che vengono girati a qualcun altro: un riferimento git e' sensibile alle
    /// maiuscole, e <c>procione argocd ripunta claude/Risk-Free-Zero</c> minuscolizzato ripunta
    /// TUTTE le Application su un ref che non esiste — ArgoCD va in ComparisonError e smette di
    /// sincronizzare, senza che nessuno abbia visto il valore cambiare.
    /// </summary>
    private static string? ArgGrezzo(List<string> args)
    {
        var primo = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (primo is not null) args.Remove(primo);
        return primo;
    }

    private static bool Flag(List<string> args, params string[] nomi)
    {
        var trovato = false;
        foreach (var nome in nomi)
            while (args.RemoveAll(a => string.Equals(a, nome, StringComparison.OrdinalIgnoreCase)) > 0)
                trovato = true;
        return trovato;
    }

    /// <summary>Legge <c>--opzione valore</c> e toglie entrambi dalla lista.</summary>
    private static string? Opt(List<string> args, string nome)
    {
        var i = args.FindIndex(a => string.Equals(a, nome, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Count) return null;
        var valore = args[i + 1];
        args.RemoveRange(i, 2);
        return valore;
    }

    // =============================================================================================

    private static void Aiuto()
    {
        Ui.Line("""

  procione — plancia di comando della piattaforma ProcioneMGR

    Senza argomenti apre la plancia interattiva (quadro che si aggiorna + comandi a un tasto).
""", ConsoleColor.Cyan);

        Sezione("Guardare", [
            ("procione stato [--json]",       "un quadro completo e via. Esce 0 se tutto e' a posto, 1 con avvisi, 2 con guasti"),
            ("procione guarda [--ogni 10]",   "lo stesso quadro, che si ridisegna da solo"),
            ("procione dottore",              "i PREREQUISITI della macchina: strumenti, segreti, configurazione"),
            ("procione log <cosa> [-n 200] [-f]", "guscio, motore, ingestion, ml, supervisore, bringup, watchdog, compose"),
            ("procione porte",                "chi occupa le porte della piattaforma, con nome e percorso del processo"),
        ]);

        Sezione("Le automazioni (dentro questo programma)", [
            ("procione servizio",             "accende il supervisore e l'ICONA accanto all'orologio. Nessuna finestra"),
            ("procione servizio ferma",       "lo ferma (serve anche per ricompilare la plancia: l'eseguibile e' in uso)"),
            ("procione servizio riavvia",     "lo rimette in piedi staccato da questa finestra, senza aspettare il logon"),
            ("procione icona [--per 30]",     "prova SOLO l'icona e dice se la shell l'ha accettata; non tocca nulla"),
            ("procione lavoro",               "i lavori: cadenza, ultimo esito, prossima scadenza"),
            ("procione lavoro <nome> ora",    "esegui un lavoro adesso, fuori cadenza (veglia, backup, avvio)"),
            ("procione lavoro <nome> accendi|spegni", "acceso/spento e' una preferenza, sopravvive al riavvio"),
            ("procione attivita migra [--si]", "DA TRE MECCANISMI A UNO: toglie i task vecchi e registra solo la plancia"),
            ("procione attivita rimuovi [--si]", "toglie ogni avvio automatico: si torna a lanciare tutto a mano"),
        ]);

        Sezione("Accendere e spegnere", [
            ("procione avvia",                "bring-up completo (scripts/bringup.ps1): Docker, proxy, cluster, tunnel, guscio"),
            ("procione avvia guscio",         "solo il guscio, come il profilo procione-main; rifiuta se la 5199 e' occupata"),
            ("procione avvia guscio --produzione", "come run-postgres.ps1: ambiente Production (era `procione postgres`)"),
            ("procione avvia compose [--motore]", "assetto Docker Compose; rifiuta se kind e' vivo (regola 2)"),
            ("procione ferma [guscio|tunnel|cluster|docker|db|supervisore|tutto]", "spegne. `tutto` lascia su il cluster: e' il core caldo"),
            ("procione riavvia <motore|ingestion|ml|guscio|cluster|db|supervisore>", "rollout + tunnel rifatto; il motore chiede conferma"),
        ]);

        Sezione("Cio' che sta sotto la piattaforma", [
            ("procione docker [stato|avvia|ferma]", "Docker Desktop, con l'attesa vera del demone (al boot: minuti)"),
            ("procione db [stato|avvia|ferma|riavvia]", "il servizio Windows di PostgreSQL. Il verdetto e' la porta, non il comando"),
            ("procione cluster avvia|ferma|riavvia", "il nodo kind SENZA distruggerlo: riavvia, verifica il proxy, rifa' i tunnel"),
            ("procione cluster crea|distruggi", "gli estremi: creazione una-tantum, e distruzione con conferma digitata"),
        ]);

        Sezione("Riparare", [
            ("procione ripara",         "rilancia il bring-up: idempotente, sistema quel che trova rotto"),
            ("procione ripara proxy",   "ricrea kind-apiproxy verso il NOME del nodo e VERIFICA che l'API risponda"),
            ("procione ripara tunnel",  "rifa' i port-forward 18080/18092/18093 se sono stantii"),
            ("procione ripara contesto","riporta kubectl sul proxy 127.0.0.1:16443"),
        ]);

        Sezione("Manutenzione", [
            ("procione backup [--verifica]", "pg_dump adesso, oppure controlla i dump esistenti"),
            ("procione corsie stato",        "quali corsie stanno girando, e in che modalita'"),
            ("procione corsie avvia tutte --modalita paper", "le avvia (Live NO: si passa da /trading)"),
            ("procione veglia",              "un giro di watchdog.ps1 adesso, sotto i tuoi occhi"),
            ("procione segreti [quale]",     "i Secret del cluster: tutti (da appsettings), postgres, trading, ui"),
            ("procione immagini [target]",   "build locale delle immagini e import nel nodo kind"),
            ("procione smoke",               "le cinque asserzioni end-to-end contro il cluster"),
            ("procione argocd [su|giu|installa|ripunta <rev>]", "ArgoCD sta spento di proposito: accendilo solo quando serve"),
            ("procione osservabilita su|giu","Grafana, Prometheus, Loki, Tempo"),
            ("procione apri [rotta]",        "apre la UI nel browser"),
        ]);

        Sezione("Tutto il resto", [
            ("procione esegui",              "elenca i 19 script di scripts/"),
            ("procione esegui <nome> [arg]", "lancia uno script qualsiasi coi suoi argomenti, dalla plancia"),
            ("procione strumenti",           "i programmi di tools/: DbBackup, FuturesVerify, SpotVerify, ..."),
            ("procione strumenti <nome> [arg]", "li lancia con la connection string gia' impostata"),
        ]);

        Ui.Line("""
  Note
    · La plancia non riscrive gli script di scripts/: li chiama. Una sola verita' operativa.
    · Le automazioni girano DENTRO la plancia, con l'output catturato: nessuna finestra PowerShell
      nasce piu' da sola. `procione attivita migra` fa il passaggio, una volta sola.
    · Il supervisore mostra un'icona accanto all'orologio: colore = verdetto, clic destro = menu,
      doppio clic = plancia. Le finestre le apre solo quando sei TU a scegliere una voce.
    · $env:PROCIONE_REPO forza quale repository comandare (utile lavorando dai worktree).
""", ConsoleColor.DarkGray);
    }

    private static void Sezione(string titolo, (string Comando, string Cosa)[] voci)
    {
        Ui.Line($"  {titolo}", ConsoleColor.Yellow);
        var largh = voci.Max(v => v.Comando.Length);
        foreach (var (cmd, cosa) in voci)
        {
            Ui.Write("    " + cmd.PadRight(largh + 2), ConsoleColor.White);
            Ui.Line(cosa, ConsoleColor.DarkGray);
        }
        Console.WriteLine();
    }
}
