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

    /// <summary>Chiave: il ruolo dell'host ("shell" | "engine").</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Ultimo battito (UTC). La riga non si cancella mai: si aggiorna.</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>Versione informativa dell'assembly che batte, per la diagnostica dei deploy.</summary>
    public string Version { get; set; } = string.Empty;
}
