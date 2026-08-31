using System.Text.Json;

namespace Procione;

/// <summary>
/// Un lavoro periodico del supervisore: cosa lanciare, quando, e per quanto lo si aspetta.
///
/// Sono gli stessi script che finora giravano dal Task Scheduler di Windows, con gli stessi
/// argomenti e la stessa cadenza — cambia CHI li chiama. Non c'e' nessuna automazione nuova qui
/// dentro: aggiungerne una di nascosto, dentro un cambiamento che si presenta come «unificazione»,
/// sarebbe il modo migliore per non farla notare a nessuno.
/// </summary>
/// <param name="Timeout">
/// Tetto di attesa. Non e' un dettaglio: uno script appeso senza tetto blocca il supervisore per
/// sempre, e un supervisore fermo e' indistinguibile — dal di fuori — da uno che non trova nulla
/// di rotto. Scaduto il tempo si uccide l'albero dei processi e lo si registra come fallimento.
/// </param>
internal sealed record Job(
    string Name,
    string What,
    Schedule When,
    string Script,
    string[] Args,
    TimeSpan Timeout,
    bool EnabledByDefault = true,
    bool SeedFromBackupDir = false);

/// <summary>Lo stato di un lavoro fra un'esecuzione e l'altra.</summary>
internal sealed class JobState
{
    public string Name { get; set; } = "";
    public DateTimeOffset? LastRun { get; set; }
    public double LastElapsedSeconds { get; set; }

    /// Codice d'uscita dell'ultima esecuzione. Puo' essere uno dei codici sintetici di
    /// <see cref="Proc"/> (timeout, non avviato): sono guasti diversi e vanno distinti.
    public int LastCode { get; set; }

    public string LastSummary { get; set; } = "";

    /// <summary>
    /// Da quando e' in esecuzione, se lo e'. Si scrive PRIMA di lanciare lo script e si azzera
    /// alla fine.
    ///
    /// Serve a riconoscere l'esecuzione INTERROTTA: se il supervisore viene ucciso a meta' di un
    /// pg_dump (riavvio di Windows, finestra chiusa, tetto scaduto sul processo padre) non passa
    /// mai da <c>Registra</c>, e senza questo campo il giro dopo troverebbe soltanto un dump
    /// troncato sul disco — piu' recente di ogni altro, quindi preso per buono, con il recupero
    /// dell'occorrenza persa abbandonato in silenzio.
    /// </summary>
    public DateTimeOffset? RunningSince { get; set; }

    /// Quanti fallimenti di fila. Un fallimento isolato capita (il cluster stava ripartendo); tre
    /// di fila sono un guasto, e la differenza deve vedersi nel quadro.
    public int ConsecutiveFailures { get; set; }

    public bool Enabled { get; set; } = true;

    /// Vero se non e' mai stato eseguito e non se ne sa nulla.
    ///
    /// [JsonIgnore] non e' cosmetica: una proprieta' CALCOLATA che finisce nel file serializzato
    /// diventa una chiave inventata, che al giro dopo qualcuno prova a rileggere come se fosse un
    /// dato. E' la stessa trappola gia' pagata sui POCO di configurazione, dove una get-only
    /// compariva in appsettings.json e nessun guardiano se ne accorgeva.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Mai => LastRun is null;
}

/// <summary>
/// Lo stato del supervisore, come lo trova CHIUNQUE apra la plancia.
///
/// Vive in un file perche' deve essere leggibile da un altro processo: il supervisore puo' girare
/// invisibile dal logon, e `procione stato` — lanciato mezz'ora dopo da un'altra finestra — deve
/// poter dire se le automazioni stanno davvero girando. Il campo che risponde a quella domanda e'
/// <see cref="Heartbeat"/>, non <see cref="Pid"/>: un PID si riusa, e un processo puo' essere vivo
/// e piantato. Il battito e' l'unica prova che il ciclo gira.
/// </summary>
internal sealed class SupervisorState
{
    public int Pid { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Heartbeat { get; set; }

    /// Su quale repository sta operando: su questa macchina convivono il repo principale e i
    /// worktree, e un supervisore che comanda il worktree sbagliato e' l'incidente del 2026-08-17
    /// (sei notti di backup perse verso un appsettings.json stantio).
    public string Repo { get; set; } = "";

    public List<JobState> Jobs { get; set; } = [];
}

/// <summary>
/// La tabella dei lavori. Un solo posto in cui leggere «cosa gira da solo, e quando».
/// </summary>
internal static class Jobs
{
    /// <summary>
    /// I lavori del supervisore, con la cadenza che avevano come attivita' pianificate.
    ///
    /// · veglia — era «ProcioneMGR Watchdog», trigger ogni PT5M, ExecutionTimeLimit PT4M.
    /// · backup — era «ProcioneMGR Backup DB», trigger giornaliero alle 03:30, -KeepDays 14.
    /// · avvio  — era «ProcioneMGR BringUp», al logon. SPENTO di default: un bring-up completo
    ///            dura minuti e tocca cluster e tunnel, e non e' cio' che ci si aspetta aprendo
    ///            una console per guardare uno stato.
    /// </summary>
    public static IReadOnlyList<Job> All =>
    [
        new("veglia",
            "dead-man switch: guscio, motore, Postgres, freschezza dei backup",
            Schedule.Ogni(TimeSpan.FromMinutes(5)),
            "watchdog.ps1", [],
            TimeSpan.FromMinutes(4)),

        new("backup",
            "pg_dump del database, con potatura dei dump piu' vecchi di 14 giorni",
            Schedule.Alle(3, 30),
            "db-backup.ps1", ["-Destination", Platform.BackupDir, "-KeepDays", "14"],
            TimeSpan.FromMinutes(60),
            SeedFromBackupDir: true),

        // [K5 2026-08-31] Il tetto passa da 15 a 30 minuti. Non e' generosita': e' il margine
        // misurato. Nella notte del 30/08 il bring-up ha impiegato 10m29s, di cui 7m15s nel passo
        // che si dichiara «fino a 5 minuti» (+45% sul proprio budget); il 31/08, dopo un riavvio
        // della macchina, 7m30s. Bastavano quattro minuti di lentezza in piu' — Docker che parte
        // piano, il cluster che fatica — perche' il tetto uccidesse il bring-up PRIMA del passo
        // che avvia il guscio. In quel caso il guscio non parte affatto e nulla lo rilancia: il
        // watchdog manda un messaggio e si ferma li'. Un tetto tarato sul caso buono trasforma una
        // lentezza in un'indisponibilita'.
        new("avvio",
            "bring-up completo della piattaforma all'accensione del supervisore",
            Schedule.SoloAllAvvio(),
            "bringup.ps1", [],
            TimeSpan.FromMinutes(30),
            EnabledByDefault: false),

        // [K2+K3, PRD autonomia-piena 2026-08-31] I due piani che non si aggiornavano da soli.
        //
        // Il motore aveva gia' il suo lavoro (`deploy`); il guscio e la plancia no, e una
        // correzione mergiata con la CI verde restava fuori da entrambi per GIORNI — finche'
        // qualcuno non riavviava il PC. Misura del 2026-08-30: guscio indietro di 7 commit,
        // plancia di 13, e la plancia stantia si e' appesa sullo stesso pipe dell'incidente del
        // 28/08 con dentro il binario il fix che lo impediva.
        //
        // Cadenza 20', sfalsata dai 30' del deploy: i due lavori toccano lo stesso repository con
        // `git pull --ff-only`, e sovrapporli a ogni giro sarebbe cercarsi una gara. Se capita
        // comunque, il pull fallisce, il giro salta e si riprova — nessuno dei due forza niente.
        //
        // Il tetto e' generoso come quello di `avvio` perche' il caso peggiore CONTIENE un
        // bring-up completo: fermare il guscio, ricompilarlo (~3m36s misurati) e rimettere i
        // port-forward.
        new("piani",
            "guscio e plancia allineati a master: aggiorna in finestra di quiete",
            Schedule.Ogni(TimeSpan.FromMinutes(20)),
            "sync-piani.ps1", [],
            TimeSpan.FromMinutes(30)),

        // [2026-08-25, sera] L'ECCEZIONE DICHIARATA alla regola scritta in testa a questo file
        // («nessuna automazione nuova qui dentro»): questa E' un'automazione nuova, e c'e' per
        // decisione esplicita del proprietario — «il sync del trading da ora in poi deve
        // diventare automatico» — che rovescia il «sync SEMPRE MANUALE» di trading-app.yaml.
        // Il freno che resta: -IfNewCommit agisce SOLO quando master e' avanzato, cioe' dopo
        // una merge (un atto umano); a parita' di commit il giro costa un git fetch. ArgoCD
        // non puo' farlo al posto nostro: non raggiunge il repo privato dal 2026-08-05.
        new("deploy",
            "sync automatico del motore: master avanzato -> build locale + import + apply",
            Schedule.Ogni(TimeSpan.FromMinutes(30)),
            "deploy-trading.ps1", ["-IfNewCommit"],
            TimeSpan.FromMinutes(25)),
    ];

    public static Job? Find(string nome) =>
        All.FirstOrDefault(j => string.Equals(j.Name, nome, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Le attivita' pianificate di Windows che il supervisore SOSTITUISCE.
    ///
    /// Restano elencate qui dopo la migrazione, e non e' un residuo: finche' una di queste esiste
    /// ancora, la stessa cosa gira due volte — una dal Task Scheduler (con la sua finestra
    /// PowerShell che salta davanti a quello che stai facendo) e una dal supervisore. E' la
    /// regola 2 applicata alle automazioni: un solo scrittore. Il quadro deve dirlo.
    /// </summary>
    public static readonly (string Task, string Job, string Era)[] Legacy =
    [
        ("ProcioneMGR Watchdog",  "veglia", "watchdog.ps1 ogni 5 minuti"),
        ("ProcioneMGR Backup DB", "backup", "db-backup.ps1 alle 03:30"),
        ("ProcioneMGR BringUp",   "avvio",  "bringup.ps1 al logon"),
    ];
}

/// <summary>
/// Quali lavori sono accesi. E' l'unica cosa del supervisore che sopravvive al riavvio, quindi non
/// sta in %TEMP% con lo stato: sta accanto al token Telegram, nella cartella della plancia.
///
/// Vive in un file SUO, e non dentro lo stato del supervisore, per una ragione pratica: cosi'
/// `procione lavoro avvio spegni` funziona anche mentre il supervisore gira — che riprende il file
/// a ogni giro — invece di essere sovrascritto dal battito successivo.
/// </summary>
internal static class Prefs
{
    /// <summary>
    /// Le preferenze salvate, oppure <c>null</c> se il file non si e' potuto leggere.
    ///
    /// La distinzione fra «non c'e' nessuna preferenza» e «non sono riuscito a leggerla» qui non e'
    /// pedanteria: confondere le due cose significa tornare ai default, e il default di un lavoro
    /// spento a mano e' ACCESO. Un file momentaneamente occupato rimetterebbe in moto, in silenzio,
    /// qualcosa che l'operatore aveva fermato.
    /// </summary>
    public static Dictionary<string, bool>? Read()
    {
        if (!File.Exists(Platform.SupervisorPrefs)) return [];
        var testo = Files.ReadShared(Platform.SupervisorPrefs);
        if (testo is null) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, bool>>(testo) ?? []; }
        catch (JsonException) { return []; }   // file corrotto: i default sono la scelta migliore
    }

    /// <summary>Vero se il lavoro e' acceso: la preferenza salvata, altrimenti il default della tabella.</summary>
    public static bool IsEnabled(Job job, IReadOnlyDictionary<string, bool>? salvate = null)
    {
        var mappa = salvate ?? Read();
        return mappa is not null && mappa.TryGetValue(job.Name, out var v) ? v : job.EnabledByDefault;
    }

    public static bool Set(string nome, bool acceso)
    {
        var tutte = Read();
        if (tutte is null)
        {
            Ui.Error("preferenze non leggibili adesso: non le sovrascrivo, riprova fra un istante.");
            return false;
        }
        tutte[nome] = acceso;
        if (Files.WriteAtomic(Platform.SupervisorPrefs,
                              JsonSerializer.Serialize(tutte, new JsonSerializerOptions { WriteIndented = true })))
            return true;

        Ui.Error($"preferenze non salvate ({Platform.SupervisorPrefs}).");
        return false;
    }
}
