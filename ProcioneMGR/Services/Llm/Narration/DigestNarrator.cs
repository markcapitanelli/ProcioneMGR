using System.Text;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Llm.Narration;

public interface IDigestNarrator
{
    /// <summary>
    /// Un paragrafo in italiano che riassume la giornata della flotta. <c>null</c> = nessuna
    /// narrazione (AI spenta, senza chiave, breaker aperto, budget esaurito, errore): il digest
    /// esce ESATTAMENTE come uscirebbe senza questa funzione.
    /// </summary>
    Task<string?> NarrateAsync(DigestData data, CancellationToken ct = default);
}

/// <summary>
/// [G9] La narrativa di sintesi in cima al digest giornaliero.
///
/// <para><b>Additiva per costruzione</b>: il digest strutturato resta quello di prima, riga per
/// riga. Questa aggiunge un paragrafo SOPRA, e la sua assenza non è un guasto — non si notifica,
/// non si ritenta, non si dichiara. Il dead-man's-switch del digest (se non arriva, la piattaforma
/// è muta) non deve dipendere da un provider AI.</para>
///
/// <para><b>Il vincolo che conta</b>: il paragrafo non deve contraddire i numeri che stanno sotto.
/// Non si può verificare a macchina in generale, ma si può togliere l'occasione: il prompt riceve
/// le stesse righe che finiranno nel messaggio, e chiede esplicitamente di non introdurre numeri
/// che non ci sono. Il testo esce SOPRA i dati, non al loro posto, così il lettore ha sempre la
/// fonte accanto alla sintesi.</para>
/// </summary>
public sealed class DigestNarrator(
    ILlmClient llm,
    ILlmCallGuard guard,
    ILogger<DigestNarrator> logger) : IDigestNarrator
{
    /// <summary>Etichetta del path per metriche, breaker e budget (AF1).</summary>
    public const string GuardPath = "digest";

    /// <summary>Tetto di lunghezza: un digest si legge sul telefono appena svegli.</summary>
    public const int MaxChars = 600;

    private const string SystemPrompt = """
        Riassumi in ITALIANO, in UN SOLO paragrafo di 2-4 frasi, la giornata di una piattaforma di
        trading automatico a partire dai dati che ricevi.

        Regole assolute:
        - usa SOLO i fatti che ti vengono dati; non introdurre numeri, simboli o eventi che non ci sono;
        - se i dati dicono poco, dillo ("giornata tranquilla, nessuna decisione della flotta") invece
          di riempire il vuoto;
        - niente consigli operativi, niente previsioni, niente esortazioni;
        - niente titoli, niente elenchi, niente markdown: solo il paragrafo.
        """;

    public async Task<string?> NarrateAsync(DigestData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var result = await guard.ExecuteAsync(GuardPath,
            token => llm.CompleteAsync(SystemPrompt, BuildPrompt(data), token), ct: ct);
        if (result.Outcome != LlmCallOutcome.Ok)
        {
            logger.LogDebug("Narrativa del digest non prodotta ({Cause}): il digest esce senza.", result.Cause);
            return null;
        }

        var text = Clean(result.Text);
        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogDebug("Narrativa del digest vuota: il digest esce senza.");
            return null;
        }
        return text;
    }

    /// <summary>
    /// Il prompt: le STESSE righe che il lettore troverà sotto. Puro e ispezionabile dai test.
    /// </summary>
    public static string BuildPrompt(DigestData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sb = new StringBuilder();

        sb.AppendLine("CORSIE");
        if (data.Lanes.Count == 0) sb.AppendLine("  (nessuna corsia leggibile)");
        foreach (var l in data.Lanes) sb.AppendLine("  " + l);

        if (data.Attention.Count > 0)
        {
            sb.AppendLine("DA GUARDARE");
            foreach (var a in data.Attention) sb.AppendLine("  " + a);
        }

        sb.AppendLine("FLOTTA (ultime 24h)");
        if (data.FleetDecisions.Count == 0) sb.AppendLine("  nessuna decisione");
        foreach (var d in data.FleetDecisions) sb.AppendLine("  " + d);

        if (data.AiUsage is not null) sb.AppendLine("CONSUMO AI: " + data.AiUsage);
        if (data.Carry is not null) sb.AppendLine("CARRY: " + data.Carry);
        foreach (var h in data.Heartbeats) sb.AppendLine("HEARTBEAT " + h);

        return sb.ToString();
    }

    /// <summary>
    /// Ripulisce la risposta: via il markdown accidentale, una riga sola, e taglio duro alla
    /// lunghezza massima. Un modello prolisso non deve poter allungare un messaggio che si legge
    /// sul telefono. Pubblico per i test.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = raw.Replace("```", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("*", "").Replace("#", "");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (text.Length <= MaxChars) return text;

        // Taglio all'ultimo confine di parola prima del tetto, con l'ellissi a dichiarare il taglio.
        var cut = text[..MaxChars];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > MaxChars / 2) cut = cut[..lastSpace];
        return cut.TrimEnd('.', ',', ';', ' ') + "…";
    }
}
