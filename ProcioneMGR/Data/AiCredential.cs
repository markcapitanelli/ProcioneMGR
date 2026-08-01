namespace ProcioneMGR.Data;

/// <summary>
/// Chiave API di un provider AI, cifrata a riposo (AES-256-GCM via converter — stesso pattern di
/// <see cref="ExchangeCredential"/>). Una riga per provider, a livello di PIATTAFORMA e non
/// per-utente: il layer AI (supervisione, e gli usi futuri) è un servizio della piattaforma, come
/// i worker che lo eseguono. La variabile d'ambiente resta il fallback per chi non vuole la
/// chiave a database (vedi <c>AiKeyStore</c>: DB prima, env poi).
/// </summary>
public class AiCredential
{
    public int Id { get; set; }

    /// <summary>"Anthropic" | "Nvidia" | domani altri: stringa e non enum, un provider nuovo non deve richiedere una migrazione.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Cifrata a riposo dal converter.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}
