namespace ProcioneMGR.Data;

/// <summary>
/// [AF5.1] Battito di vita di un host, una riga per processo. Il guscio scrive la SUA riga, il
/// motore la SUA: ogni scrittore ha esattamente una riga, quindi la regola "ogni scrittore ha
/// esattamente un host" vale a grana di riga — nessuna contesa, nessun lock.
///
/// Il punto: se muore il motore, il guscio se ne accorge dagli errori gRPC ma nessuno lo DICE; se
/// muore il guscio, il motore continua a tradare senza occhi e nessuno se ne accorge affatto. Ogni
/// host legge la riga ALTRUI e dichiara la stantiezza (vedi HeartbeatMonitorWorker). Il caso
/// "muoiono entrambi" non è coperto da qui per costruzione: per quello esiste il watchdog esterno
/// (scripts/watchdog.ps1) e l'assenza del digest giornaliero.
/// </summary>
public class HostHeartbeat
{
    public const string ShellRole = "shell";
    public const string EngineRole = "engine";

    /// <summary>
    /// [2026-08-15] Timbro del CICLO di sync OHLCV, scritto da MarketDataSyncWorker a fine ciclo
    /// (ovunque giri: pod ingestion o monolite). Non è il battito di un host ma di un LAVORO — è
    /// il dato che mancava nell'incidente del 2026-08-14, quando «l'ultimo giro è delle 22:44» non
    /// era scritto da nessuna parte e 122 serie sono rimaste ferme 6 ore in silenzio.
    /// HeartbeatMonitorWorker NON sorveglia questo ruolo (guarda solo shell/engine): i giudici
    /// sono la pagina /market/watchlist e la guardia di freschezza.
    /// </summary>
    public const string IngestionSyncRole = "ingestion-sync";

    /// <summary>
    /// [K8, 2026-08-31] Timbro del CICLO del carry, scritto da <c>CarryWorker</c> a fine
    /// valutazione. Come <see cref="IngestionSyncRole"/> non è un host ma un LAVORO, e per la
    /// stessa ragione: il carry vive nel pod del motore e il suo guardiano nel guscio, quindi il
    /// testimone in-process (<c>CarryWorker.LastEvaluationUtc</c>) non era osservabile da chi
    /// doveva giudicarlo — <c>Fleet:CarrySilenceAlertHours</c> era una manopola amministrabile che
    /// non poteva scattare mai, sull'unica classe con edge positivo misurato.
    /// <c>HeartbeatMonitorWorker</c> NON sorveglia questo ruolo: il giudice è il guardiano della
    /// flotta, che ha la sua soglia e il suo anti-raffica.
    /// </summary>
    public const string CarryRole = "carry";

    /// <summary>Chiave: il ruolo dell'host ("shell" | "engine") o del lavoro ("ingestion-sync").</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Ultimo battito (UTC). La riga non si cancella mai: si aggiorna.</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>Versione informativa dell'assembly che batte, per la diagnostica dei deploy.</summary>
    public string Version { get; set; } = string.Empty;
}
