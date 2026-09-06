using System.Diagnostics;

namespace Procione;

/// <summary>
/// I pezzi che stanno SOTTO la piattaforma: Docker Desktop, il servizio Windows di PostgreSQL, e
/// l'accensione del cluster.
///
/// PERCHE' ESISTONO QUI (2026-09-06, revisione su richiesta del proprietario). Non appartengono a
/// ProcioneMGR — sono di Windows e di Docker — ma senza di loro non parte niente, e finora la
/// plancia sapeva soltanto DIRE che erano giu'. I rimedi che stampava mandavano fuori da se':
/// «avvia Docker Desktop», «services.msc → postgresql-x64-18», «docker start
/// procionemgr-dev-control-plane». Un pannello di comando che per le tre cose piu' basilari
/// rimanda altrove non e' un pannello di comando: e' un cartello.
///
/// Tutte e tre le azioni di ARRESTO chiedono conferma, e non e' prudenza generica: fermare uno
/// qualunque di questi tre pezzi ferma il motore di trading, che ha posizioni aperte. La conferma
/// e' il punto in cui l'operatore dichiara di saperlo.
/// </summary>
internal static class Machine
{
    // =============================================================================================
    //  Docker Desktop
    // =============================================================================================

    /// <summary>Cosa dice Docker di se': il demone risponde? e Docker Desktop com'e' messo?</summary>
    private static (bool DemoneVivo, string Dettaglio) StatoDocker()
    {
        // Il verdetto e' `docker info`, cioe' il DEMONE che risponde: `docker desktop status` puo'
        // dire «running» mentre il motore Linux non e' ancora salito, ed e' proprio la finestra in
        // cui tutto il resto fallisce senza spiegazioni.
        var info = Proc.Capture("docker", ["info", "--format", "{{.ServerVersion}}"], 20000);
        if (info.Ok && info.Out.Length > 0) return (true, $"demone pronto (server {info.Out})");

        var desktop = Proc.Capture("docker", ["desktop", "status"], 15000);
        var riga = desktop.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault(r => r.TrimStart().StartsWith("Status", StringComparison.OrdinalIgnoreCase));
        var stato = riga?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
        return (false, stato is null ? "il demone non risponde" : $"il demone non risponde (Docker Desktop: {stato})");
    }

    public static int Docker(string? sub, bool si)
    {
        switch (sub)
        {
            case null or "stato" or "status":
            {
                var (vivo, dettaglio) = StatoDocker();
                if (vivo) Ui.Good($"Docker: {dettaglio}");
                else Ui.Error($"Docker: {dettaglio}");
                return vivo ? 0 : 2;
            }

            case "avvia" or "su" or "start":
            {
                if (StatoDocker().DemoneVivo) { Ui.Good("Docker e' gia' pronto."); return 0; }

                Ui.Title("Avvio di Docker Desktop");
                // `docker desktop start` c'e' dalla 4.37 e ASPETTA; sulle versioni precedenti non
                // esiste, e allora si lancia l'eseguibile. Si prova la via pulita per prima.
                var r = Proc.Capture("docker", ["desktop", "start"], 300000);
                if (!r.Ok)
                {
                    if (!File.Exists(Platform.DockerDesktopExe))
                    {
                        Ui.Error($"«docker desktop start» non disponibile e {Platform.DockerDesktopExe} non c'e'.");
                        return 2;
                    }
                    Ui.Info("«docker desktop start» non disponibile: lancio l'applicazione.");
                    try { Process.Start(new ProcessStartInfo(Platform.DockerDesktopExe) { UseShellExecute = true }); }
                    catch (Exception ex) { Ui.Error($"avvio fallito: {ex.Message.Trim()}"); return 2; }
                }

                // Il verdetto e' la VERIFICA. Al boot Docker impiega minuti: e' la stessa attesa
                // che bringup.ps1 concede al primo passo, e per la stessa ragione.
                Ui.Info("attendo che il demone risponda (fino a 5 minuti)...");
                if (Attendi(() => StatoDocker().DemoneVivo, 300))
                {
                    Ui.Good("Docker pronto (verificato).");
                    return 0;
                }
                Ui.Error("Docker Desktop e' stato avviato ma il demone non risponde ancora.");
                return 1;
            }

            case "ferma" or "giu" or "stop":
            {
                Ui.Warn("fermare Docker ferma il CLUSTER: il motore di trading smette di operare,");
                Ui.Info("i tunnel muoiono e il guscio resta senza motore. Il guscio in se' sopravvive.");
                if (!si && !Ui.Confirm("Fermare Docker Desktop?")) { Ui.Info("annullato."); return 1; }

                Ui.Title("Arresto di Docker Desktop");
                var r = Proc.Capture("docker", ["desktop", "stop"], 180000);
                if (!r.Ok && r.Text.Length > 0) Ui.Info(r.FirstLine);

                if (Attendi(() => !StatoDocker().DemoneVivo, 120))
                {
                    Ui.Good("Docker fermo (verificato).");
                    return 0;
                }
                Ui.Warn("il demone risponde ancora: Docker Desktop non si e' fermato.");
                return 1;
            }

            default:
                Ui.Error($"argomento non riconosciuto: '{sub}'. Attesi: stato, avvia, ferma.");
                return 2;
        }
    }

    // =============================================================================================
    //  PostgreSQL — il servizio di Windows
    // =============================================================================================

    /// Gli stati di un servizio Windows, nei NUMERI. Mai la parola: `sc query` e `Get-Service`
    /// stampano un testo LOCALIZZATO, ed e' la stessa trappola gia' pagata leggendo
    /// `schtasks /Query /V` su una macchina in italiano.
    private static string DescriviStato(int stato) => stato switch
    {
        1 => "fermo",
        2 => "in avvio",
        3 => "in arresto",
        4 => "in esecuzione",
        5 => "in ripresa",
        6 => "in pausa",
        7 => "in pausa",
        _ => $"stato {stato}",
    };

    /// <summary>
    /// Il servizio di PostgreSQL: nome vero, stato numerico, tipo di avvio.
    ///
    /// Il nome si CERCA (`postgresql*`) invece di essere dato per scontato: la versione fa parte
    /// del nome (`postgresql-x64-18`), e un aggiornamento maggiore lo cambierebbe lasciando la
    /// plancia a cercare un servizio che non esiste piu' — e a dichiararlo assente.
    /// </summary>
    private static (string Nome, int Stato, string Avvio)? Servizio()
    {
        var r = Proc.Ps(
            "$s = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($s) { $s.Name + '|' + [int]$s.Status + '|' + [string]$s.StartType }", 20000);
        if (!r.Ok) return null;
        var c = r.Out.Trim().Split('|');
        if (c.Length < 2 || c[0].Length == 0 || !int.TryParse(c[1], out var stato)) return null;
        return (c[0], stato, c.Length > 2 ? c[2] : "");
    }

    public static int Database(string? sub, bool si)
    {
        var servizio = Servizio();
        if (servizio is null && sub is not ("avvia" or "su" or "start"))
        {
            Ui.Error($"nessun servizio 'postgresql*' su questa macchina (atteso: {Platform.PostgresService}).");
            Ui.Info("sull'assetto Docker Compose il database e' un container: `procione log compose`.");
            return 2;
        }

        switch (sub)
        {
            case null or "stato" or "status":
            {
                var (nome, stato, avvio) = servizio!.Value;
                var porta = Net.ListeningPorts().Contains(Platform.PostgresPort);
                var descr = $"{nome}: {DescriviStato(stato)}, avvio {avvio.ToLowerInvariant()}";
                // Servizio «in esecuzione» e porta chiusa e' un guasto vero e non teorico: succede
                // quando Postgres parte e muore sul recupero. Il verdetto e' la porta.
                if (stato == 4 && porta) Ui.Good($"{descr} — TCP :{Platform.PostgresPort} accetta connessioni");
                else if (stato == 4) Ui.Warn($"{descr} — ma la porta {Platform.PostgresPort} NON accetta connessioni");
                else Ui.Error(descr);
                return stato == 4 && porta ? 0 : 2;
            }

            case "avvia" or "su" or "start":
            {
                var nome = servizio?.Nome ?? Platform.PostgresService;
                if (servizio is { Stato: 4 } && Net.ListeningPorts().Contains(Platform.PostgresPort))
                {
                    Ui.Good("PostgreSQL e' gia' in esecuzione.");
                    return 0;
                }
                Ui.Title($"Avvio del servizio {nome}");
                var r = Proc.Ps($"Start-Service -Name '{Apici(nome)}' -ErrorAction Stop", 60000);
                if (!r.Ok) Ui.Info(Elevazione(r));

                // Il verdetto e' la porta, non l'esito del comando: un servizio «avviato» che non
                // ascolta e' esattamente il caso che il quadro deve saper distinguere.
                if (Attendi(() => Net.ListeningPorts().Contains(Platform.PostgresPort), 60))
                {
                    Ui.Good($"PostgreSQL in ascolto su :{Platform.PostgresPort} (verificato).");
                    return 0;
                }
                Ui.Error($"il servizio non ascolta su :{Platform.PostgresPort}.");
                return 2;
            }

            case "ferma" or "giu" or "stop":
            {
                var (nome, _, _) = servizio!.Value;
                Ui.Warn("PostgreSQL e' il database della piattaforma: fermarlo mentre il motore opera");
                Ui.Info("interrompe scritture in corso (trade, candele, ledger). Ferma prima guscio e motore.");
                if (!si && !Ui.ConfirmWord($"Fermare «{nome}»?", "ferma")) { Ui.Info("annullato."); return 1; }

                Ui.Title($"Arresto del servizio {nome}");
                var r = Proc.Ps($"Stop-Service -Name '{Apici(nome)}' -Force -ErrorAction Stop", 60000);
                if (!r.Ok) Ui.Info(Elevazione(r));

                if (Attendi(() => !Net.ListeningPorts().Contains(Platform.PostgresPort), 60))
                {
                    Ui.Good("PostgreSQL fermo (verificato).");
                    return 0;
                }
                Ui.Error($"la porta {Platform.PostgresPort} risponde ancora.");
                return 2;
            }

            case "riavvia" or "restart":
            {
                var e = Database("ferma", si);
                return e != 0 ? e : Database("avvia", si);
            }

            default:
                Ui.Error($"argomento non riconosciuto: '{sub}'. Attesi: stato, avvia, ferma, riavvia.");
                return 2;
        }
    }

    private static string Apici(string s) => s.Replace("'", "''");

    /// <summary>Il messaggio giusto quando un comando su un servizio fallisce: quasi sempre e' l'elevazione.</summary>
    private static string Elevazione(ExecResult r) =>
        r.Text.Contains("access", StringComparison.OrdinalIgnoreCase) ||
        r.Text.Contains("accesso", StringComparison.OrdinalIgnoreCase) ||
        r.Text.Contains("PermissionDenied", StringComparison.OrdinalIgnoreCase)
            ? "serve una shell ELEVATA per comandare un servizio di Windows."
            : r.FirstLine;

    // =============================================================================================
    //  Il cluster: spegnerlo e riaccenderlo, senza distruggerlo
    // =============================================================================================

    /// <summary>
    /// Accende e spegne il nodo kind.
    ///
    /// Fino a oggi la plancia sapeva soltanto CREARE e DISTRUGGERE un cluster: fra i due estremi
    /// non c'era niente, e il rimedio stampato per un nodo fermo era `docker start
    /// procionemgr-dev-control-plane` — cioe' un comando docker, da battere fuori di qui.
    ///
    /// Riaccendere non e' `docker start` e basta, ed e' il motivo per cui merita un comando: al
    /// riavvio Docker riassegna gli indirizzi della rete kind, quindi il proxy dell'API server puo'
    /// restare a inoltrare verso un IP che non esiste piu' (2026-08-04 e 2026-08-11, un'ora di TLS
    /// handshake timeout con tutto «running»), e i port-forward puntano a pod che sono ripartiti.
    /// Qui i tre passi stanno insieme, nell'ordine giusto, e ognuno si VERIFICA.
    /// </summary>
    public static int Cluster(string? sub, bool si)
    {
        switch (sub)
        {
            case "ferma" or "giu" or "stop":
            {
                Ui.Warn("fermare il cluster ferma il MOTORE: le corsie smettono di operare e le");
                Ui.Info("posizioni aperte restano tali, senza nessuno che le sorvegli.");
                if (!si && !Ui.Confirm($"Fermare «{Platform.KindNodeContainer}»?")) { Ui.Info("annullato."); return 1; }

                Ui.Title("Arresto del cluster");
                var r = Proc.Capture("docker", ["stop", Platform.KindNodeContainer], 180000);
                if (!r.Ok) { Ui.Error($"docker stop fallito: {r.FirstLine}"); return 2; }

                // I tunnel restano in ascolto verso un cluster che non c'e' piu': lasciarli sarebbe
                // esattamente il «tunnel che non porta da nessuna parte» che il quadro segnala.
                Actions.DownTunnels();
                Ui.Good("cluster fermo. `procione cluster avvia` lo rimette in piedi (dati e Secret restano).");
                return 0;
            }

            case "avvia" or "su" or "start":
            {
                Ui.Title("Avvio del cluster");
                var r = Proc.Capture("docker", ["start", Platform.KindNodeContainer], 180000);
                if (!r.Ok)
                {
                    Ui.Error($"docker start fallito: {r.FirstLine}");
                    Ui.Info("se il container non esiste, il cluster va CREATO: `procione cluster crea`.");
                    return 2;
                }

                Ui.Info("attendo che l'API server risponda attraverso il proxy (fino a 3 minuti)...");
                if (!Attendi(() => Proc.Kubectl(["get", "--raw", "/livez"], 8000).Ok, 180))
                {
                    // Il caso previsto, non un'eccezione: Docker ha riassegnato gli IP e il socat
                    // punta a quello vecchio. RepairProxy lo ricrea sul NOME DNS e verifica.
                    Ui.Warn("l'API server non risponde: il proxy inoltra a un indirizzo vecchio. Lo rifaccio.");
                    if (Actions.RepairProxy() != 0) return 1;
                }
                else Ui.Good("l'API server risponde attraverso il proxy.");

                Ui.Info("rifaccio i tunnel: i pod sono ripartiti, quelli vecchi sarebbero stantii.");
                Actions.RepairTunnels();
                return 0;
            }

            case "riavvia" or "restart":
            {
                var e = Cluster("ferma", si);
                return e != 0 ? e : Cluster("avvia", si);
            }

            default:
                Ui.Error($"argomento non riconosciuto: '{sub}'.");
                Ui.Info("attesi: avvia, ferma, riavvia, crea, distruggi.");
                return 2;
        }
    }

    // =============================================================================================
    //  Le porte
    // =============================================================================================

    /// <summary>
    /// Chi occupa le porte della piattaforma, con nome ed eseguibile del processo.
    ///
    /// E' la domanda che l'operatore si fa per prima quando qualcosa non parte («chi ha la 5199?»),
    /// e finora la risposta stava fuori dalla plancia, in un `netstat -ano` seguito da un
    /// `tasklist`. Qui la mappa porta → PID si chiede al sistema DENTRO il processo
    /// (<see cref="Net"/>), quindi risponde anche quando la macchina e' satura.
    /// </summary>
    public static int Ports()
    {
        Ui.Title("Porte della piattaforma");
        var inAscolto = Net.ListeningPorts();

        foreach (var (porta, chi) in Platform.KnownPorts)
        {
            if (!inAscolto.Contains(porta))
            {
                Ui.Write($"    {porta,-6}", ConsoleColor.White);
                Ui.Line($"{chi,-26} libera", ConsoleColor.DarkGray);
                continue;
            }

            var pids = Net.OwningPids(porta);
            var descrizione = pids is null
                ? "in ascolto — proprietario non leggibile"
                : pids.Count == 0
                    ? "in ascolto — proprietario sconosciuto"
                    : string.Join(", ", pids.Select(Descrivi));

            Ui.Write($"    {porta,-6}", ConsoleColor.White);
            Ui.Write($"{chi,-26} ", ConsoleColor.Gray);
            Ui.Line(descrizione, ConsoleColor.Cyan);
        }

        Ui.Info("");
        Ui.Info("`procione ferma guscio` / `procione ferma tunnel` liberano quelle della piattaforma.");
        return 0;
    }

    private static string Descrivi(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? percorso = null;
            try { percorso = p.MainModule?.FileName; } catch { /* processo di un altro utente */ }
            // Il PERCORSO, non solo il nome: su questa macchina convivono repo principale e
            // worktree, e «ProcioneMGR.exe» da solo non dice quale dei due si e' preso la porta —
            // che e' precisamente la domanda dell'incidente del 2026-07-20.
            return percorso is null ? $"{p.ProcessName} (pid {pid})" : $"{p.ProcessName} (pid {pid}) — {percorso}";
        }
        catch { return $"pid {pid} (gia' uscito)"; }
    }

    // =============================================================================================

    /// <summary>Aspetta che una condizione diventi vera. Il verdetto e' la verifica, mai il comando.</summary>
    private static bool Attendi(Func<bool> condizione, int secondi)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(secondi);
        while (DateTime.UtcNow < scadenza)
        {
            try { if (condizione()) return true; } catch { }
            Thread.Sleep(1000);
        }
        try { return condizione(); } catch { return false; }
    }
}
