namespace Procione;

/// <summary>
/// I nomi, le porte e i percorsi della piattaforma, in un solo posto.
///
/// Ogni valore qui dentro esiste GIA' dentro uno script di <c>scripts/</c> o in un manifest di
/// <c>infra/k8s/</c>: questa classe non ne inventa nessuno, li raccoglie. Se uno cambia laggiu',
/// cambia qui — e il commento accanto dice dove guardare per verificarlo.
/// </summary>
internal static class Platform
{
    // --- Cluster kind (scripts/k8s-common.ps1, scripts/bringup.ps1) ---------------------------
    public const string KubeContext = "kind-procionemgr-dev";
    public const string KindNodeContainer = "procionemgr-dev-control-plane";

    /// Proxy socat che ripubblica l'API server: Windows RISERVA la porta originale dopo un
    /// riavvio e Docker non ripristina il binding. Vedi bringup.ps1 §2.
    public const string ApiProxyContainer = "kind-apiproxy";
    public const int ApiProxyPort = 16443;

    // --- Assetto Docker Compose (docker-compose.yml: `name: procionemgr`) ---------------------
    public const string ComposeProject = "procionemgr";
    public const string ObservabilityProject = "procionemgr-observability";

    // --- Porte dell'host ----------------------------------------------------------------------
    /// Il guscio. Una sola istanza, sempre: due guscii sulla stessa porta sono l'incidente del
    /// 2026-07-20 (istanza di un worktree con master key segnaposto che intercetta l'utente).
    public const int ShellPort = 5199;

    /// Tunnel verso l'ingestion in-cluster: serve al pulsante «Sync now» di /market/watchlist.
    public const int IngestionPort = 18080;

    /// Tunnel verso il motore: 18092 e' gRPC h2c (a un GET HTTP/1.x risponde SEMPRE 400 — non
    /// usarla mai come sonda di salute), 18093 e' la porta health HTTP. Vedi
    /// ensure-trading-portforward.ps1 e la correzione del watchdog del 2026-08-11.
    public const int EngineGrpcPort = 18092;
    public const int EngineHealthPort = 18093;

    public const int PostgresPort = 5432;
    public const int GrafanaPort = 3000;

    // --- Namespace e workload -----------------------------------------------------------------
    public const string TradingNamespace = "procionemgr-trading";
    public const string IngestionNamespace = "procionemgr-ingestion";
    public const string MlNamespace = "procionemgr-ml";
    public const string ArgocdNamespace = "argocd";

    /// I tre servizi in-cluster: namespace, nome del Deployment, etichetta per l'operatore.
    public static readonly (string Ns, string Deploy, string Label)[] Services =
    [
        (TradingNamespace,   "procionemgr-trading",   "motore"),
        (IngestionNamespace, "procionemgr-ingestion", "ingestion"),
        (MlNamespace,        "procionemgr-ml",        "ml"),
    ];

    // --- File di stato lasciati dagli script ---------------------------------------------------
    // Sono in %TEMP% perche' sono stato di macchina, non configurazione: non si versionano.
    public static string TradingTunnelMarker => Path.Combine(Path.GetTempPath(), "procionemgr-trading-portforward.pod");
    public static string IngestionTunnelMarker => Path.Combine(Path.GetTempPath(), "procionemgr-ingestion-portforward.pod");
    public static string BringUpLog => Path.Combine(Path.GetTempPath(), "procionemgr-bringup.log");
    public static string WatchdogState => Path.Combine(Path.GetTempPath(), "procionemgr-watchdog-state.json");

    /// Log del guscio quando lo avvia questa plancia. Il monolite non scrive su file (nessun
    /// Serilog): senza questo tee, `procione log guscio` non avrebbe nulla da mostrare.
    public static string ShellLog => Path.Combine(Path.GetTempPath(), "procionemgr-guscio.log");

    /// Stato del supervisore residente. E' in %TEMP% perche' e' stato di macchina — chi gira, da
    /// quando, con che esito — e non sopravvive di proposito a un riavvio.
    public static string SupervisorState => Path.Combine(Path.GetTempPath(), "procionemgr-supervisore.json");

    public static string SupervisorLog => Path.Combine(Path.GetTempPath(), "procionemgr-supervisore.log");

    // --- Automazione: una sola attivita' pianificata, che avvia QUESTA plancia -------------------

    /// <summary>
    /// L'unica attivita' pianificata prevista: avvia il supervisore al logon, e basta.
    ///
    /// Prima erano tre meccanismi diversi e tutti fuori dalla plancia — due task del Task Scheduler
    /// piu' un .cmd nella cartella Esecuzione automatica — e ognuno apriva la sua finestra
    /// PowerShell davanti a quello che si stava facendo. Ora c'e' un programma solo, e le
    /// automazioni sono lavori suoi.
    /// </summary>
    public const string SupervisorTask = "ProcioneMGR Plancia";

    /// Il .cmd che bringup.ps1 -Register deposita quando non ha i privilegi per registrare il task
    /// (ripiego documentato nello script). E' il terzo meccanismo da ritirare nella migrazione.
    public static string StartupShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "ProcioneMGR-BringUp.cmd");

    /// Preferenze del supervisore (quali lavori sono accesi). NON in %TEMP%: e' configurazione, e
    /// deve sopravvivere al riavvio. Sta accanto al token Telegram, nella cartella della plancia.
    public static string SupervisorPrefs =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".procione", "lavori.json");

    /// <summary>L'eseguibile della plancia, quello che l'attivita' pianificata deve lanciare.</summary>
    public static string? SelfExe => Environment.ProcessPath;

    /// <summary>Marcatore che riconosce un worktree: gli si taglia via tutto cio' che segue.</summary>
    private const string SegnoWorktree = @"\.claude\worktrees\";

    /// <summary>Vero se il percorso vive dentro un worktree usa-e-getta.</summary>
    public static bool InWorktree(string? percorso) =>
        percorso is not null && percorso.Contains(SegnoWorktree, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La radice del repository PRINCIPALE, anche quando si sta girando da un worktree.
    ///
    /// E' la stessa funzione di <c>Get-MainRepoRoot</c> in <c>db-backup.ps1</c>, e per la stessa
    /// ragione, pagata il 2026-08-17: un'attivita' pianificata registrata standoci dentro un
    /// worktree punta alla copia che vive li'. Quella copia sparisce con <c>git worktree remove</c>,
    /// e il suo <c>appsettings.json</c> — che e' gitignorato, quindi fotografato alla nascita del
    /// worktree e mai piu' aggiornato — invecchia in silenzio. Sei notti di backup perse.
    ///
    /// Un worktree e' uno scratch, non una fonte di verita': ne' per la configurazione, ne' per gli
    /// eseguibili che qualcuno lancera' fra sei mesi.
    /// </summary>
    public static string MainRepoRoot
    {
        get
        {
            var i = RepoRoot.IndexOf(SegnoWorktree, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? RepoRoot : RepoRoot[..i];
        }
    }

    /// Dove db-backup.ps1 deposita i dump (fuori dal repo, di proposito).
    public static string BackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ProcioneMGR-Backup");

    // --- Radice del repository -----------------------------------------------------------------

    /// <summary>Cartella del repo su cui la plancia agisce.</summary>
    public static string RepoRoot { get; }

    /// <summary>Come e' stata trovata: si stampa in <c>procione dottore</c>, perche' su questa
    /// macchina convivono il repo principale e i worktree e sapere QUALE si sta comandando e'
    /// meta' della diagnosi.</summary>
    public static string RepoRootOrigin { get; }

    static Platform()
    {
        var esplicito = Environment.GetEnvironmentVariable("PROCIONE_REPO");
        if (Find(esplicito) is { } daEnv)
        {
            RepoRoot = daEnv;
            RepoRootOrigin = "variabile d'ambiente PROCIONE_REPO";
            return;
        }
        // Dalla posizione dell'eseguibile: e' il caso normale (tools/Procione/bin/... dentro il repo).
        if (Find(AppContext.BaseDirectory) is { } daExe)
        {
            RepoRoot = daExe;
            RepoRootOrigin = "posizione dell'eseguibile";
            return;
        }
        if (Find(Directory.GetCurrentDirectory()) is { } daCwd)
        {
            RepoRoot = daCwd;
            RepoRootOrigin = "cartella corrente";
            return;
        }
        RepoRoot = Directory.GetCurrentDirectory();
        RepoRootOrigin = "RIPIEGO: nessun ProcioneMGR.sln trovato risalendo le cartelle";
    }

    /// Risale le cartelle finche' non trova ProcioneMGR.sln.
    private static string? Find(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { /* percorso illeggibile: si prova la sorgente successiva */ }
        return null;
    }

    public static string Script(string nome) => Path.Combine(RepoRoot, "scripts", nome);
    public static string ComposeFile => Path.Combine(RepoRoot, "docker-compose.yml");
    public static string ObservabilityCompose => Path.Combine(RepoRoot, "infra", "observability", "docker-compose.yml");
    public static string AppSettings => Path.Combine(RepoRoot, "ProcioneMGR", "appsettings.json");
    public static string ShellProject => Path.Combine(RepoRoot, "ProcioneMGR", "ProcioneMGR.csproj");

    /// Il token del bot Telegram vive in un solo posto, lo stesso che usa .claude/launch.json.
    public static string TelegramTokenFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".procione", "telegram.token");
}
