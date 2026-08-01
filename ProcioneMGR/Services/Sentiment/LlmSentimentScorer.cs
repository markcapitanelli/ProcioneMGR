using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcioneMGR.Services.Llm;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// <see cref="ISentimentScorer"/> basato sul layer LLM multi-provider (PRD-AI-MULTIPROVIDER Fase B):
/// chiede al provider ATTIVO (<see cref="ILlmClient"/> = DelegatingLlmClient) un punteggio in
/// [-1,+1] per titolo+sommario. Ogni chiamata passa dal <see cref="ILlmCallGuard"/> condiviso
/// (path "sentiment", metriche separate): se il provider è giù il breaker del layer sospende anche
/// questo percorso — coerente col principio "il breaker è del layer, non del provider".
///
/// <para><b>Mai un'eccezione verso il chiamante</b>: qualunque esito non-Ok (chiave assente,
/// breaker aperto, errore, risposta non interpretabile) ripiega in silenzio operoso sul
/// <see cref="KeywordSentimentScorer"/> — il punteggio arriva comunque, e il log dice da dove.
/// La sync delle notizie non deve MAI fermarsi per un problema del canale AI.</para>
///
/// <para><b>Costo dichiarato</b> (principio §1.4 del PRD): il percorso vivo scora solo le notizie
/// NUOVE del giro di sync (≈ decine di titoli l'ora, prompt corti); il replay storico del pannello
/// di confronto usa <see cref="ScoreBatchAsync"/> (N titoli in UNA chiamata) proprio per non
/// moltiplicare le chiamate. Il free tier NVIDIA (16 richieste concorrenti) non è mai un vincolo:
/// le chiamate qui sono sequenziali.</para>
/// </summary>
public sealed class LlmSentimentScorer(
    ILlmClient llm,
    ILlmCallGuard guard,
    KeywordSentimentScorer fallback,
    ILogger<LlmSentimentScorer> logger) : ISentimentScorer
{
    /// <summary>Etichetta metrica del guard: separa i conteggi dal path advisory/veto.</summary>
    internal const string GuardPath = "sentiment";

    /// <summary>Notizie per chiamata nel percorso batch: abbastanza da tagliare i costi di un ordine di grandezza, abbastanza poche da non degradare la qualità del giudizio.</summary>
    internal const int BatchSize = 20;

    private const string SystemPromptSingle = """
        Sei un classificatore di sentiment per notizie di mercato (crypto e macro-finanza).
        Valuta l'impatto ATTESO della notizia sul prezzo dell'asset di cui parla.
        Rispondi ESCLUSIVAMENTE con un numero decimale fra -1.0 e 1.0 (punto come separatore):
        -1.0 = fortemente ribassista, 0 = neutra o non determinabile, 1.0 = fortemente rialzista.
        Nessun testo, nessuna spiegazione: solo il numero.
        """;

    private const string SystemPromptBatch = """
        Sei un classificatore di sentiment per notizie di mercato (crypto e macro-finanza).
        Ricevi un elenco numerato di notizie. Per ciascuna valuta l'impatto ATTESO sul prezzo
        dell'asset di cui parla: -1.0 = fortemente ribassista, 0 = neutra o non determinabile,
        1.0 = fortemente rialzista.
        Rispondi ESCLUSIVAMENTE con un array JSON di numeri decimali (punto come separatore),
        nello stesso ordine dell'elenco, UN numero per notizia, lunghezza esattamente uguale al
        numero di notizie. Nessun testo attorno: solo l'array JSON.
        """;

    public async Task<decimal> ScoreAsync(string title, string? summary, CancellationToken ct = default)
    {
        var result = await guard.ExecuteAsync(GuardPath,
            token => llm.CompleteAsync(SystemPromptSingle, BuildItemText(title, summary), token),
            timeout: TimeSpan.FromSeconds(30), ct: ct);

        if (result.Outcome != LlmCallOutcome.Ok)
        {
            logger.LogDebug("Sentiment LLM non disponibile ({Cause}): ripiego sul lessico per '{Title}'.",
                result.Cause, Truncate(title, 60));
            return fallback.Score(title, summary);
        }

        if (TryParseScore(result.Text!, out var score))
        {
            return score;
        }

        logger.LogWarning("Sentiment LLM: risposta non interpretabile ('{Raw}'): ripiego sul lessico per '{Title}'.",
            Truncate(result.Text!, 80), Truncate(title, 60));
        return fallback.Score(title, summary);
    }

    /// <summary>
    /// Scora N notizie in UNA chiamata per batch (per il replay storico del pannello di confronto:
    /// 150 titoli = 8 chiamate invece di 150). Per ogni batch che fallisce o torna disallineato si
    /// ripiega sul lessico PER QUEL BATCH — il risultato ha sempre la stessa lunghezza dell'input.
    /// Restituisce anche quanti punteggi vengono davvero dall'LLM (il pannello lo dichiara).
    /// </summary>
    public async Task<(IReadOnlyList<decimal> Scores, int FromLlm)> ScoreBatchAsync(
        IReadOnlyList<(string Title, string? Summary)> items, CancellationToken ct = default)
    {
        var scores = new decimal[items.Count];
        var fromLlm = 0;

        for (var start = 0; start < items.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = items.Skip(start).Take(BatchSize).ToList();

            var sb = new StringBuilder();
            for (var i = 0; i < batch.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {BuildItemText(batch[i].Title, batch[i].Summary)}");
            }

            var result = await guard.ExecuteAsync(GuardPath,
                token => llm.CompleteAsync(SystemPromptBatch, sb.ToString(), token),
                timeout: TimeSpan.FromSeconds(90), ct: ct);

            var parsed = result.Outcome == LlmCallOutcome.Ok ? TryParseScoreArray(result.Text!, batch.Count) : null;
            for (var i = 0; i < batch.Count; i++)
            {
                if (parsed is not null)
                {
                    scores[start + i] = parsed[i];
                    fromLlm++;
                }
                else
                {
                    scores[start + i] = fallback.Score(batch[i].Title, batch[i].Summary);
                }
            }

            if (parsed is null)
            {
                logger.LogWarning("Sentiment LLM batch {From}-{To}: {Reason} — ripiego sul lessico per il batch.",
                    start + 1, start + batch.Count,
                    result.Outcome == LlmCallOutcome.Ok ? "risposta non interpretabile o disallineata" : result.Cause);
            }
        }

        return (scores, fromLlm);
    }

    /// <summary>Titolo + sommario troncato: il prompt resta corto e il costo prevedibile.</summary>
    private static string BuildItemText(string title, string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? title : $"{title} — {Truncate(summary, 300)}";

    /// <summary>
    /// Estrae il primo numero decimale dalla risposta (tollera testo attorno e la virgola come
    /// separatore — i modelli rispondono in italiano) e lo blocca in [-1,+1].
    /// </summary>
    internal static bool TryParseScore(string raw, out decimal score)
    {
        score = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var match = System.Text.RegularExpressions.Regex.Match(raw, @"-?\d+(?:[.,]\d+)?");
        if (!match.Success) return false;

        if (!decimal.TryParse(match.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        score = Math.Clamp(value, -1m, 1m);
        return true;
    }

    /// <summary>
    /// Estrae dall'output l'array JSON di numeri e pretende ESATTAMENTE la lunghezza attesa: un
    /// array più corto o più lungo è disallineato (non si sa più quale punteggio è di chi) e vale
    /// come fallimento del batch — mai un'assegnazione "a scalare" silenziosamente sbagliata.
    /// </summary>
    internal static IReadOnlyList<decimal>? TryParseScoreArray(string raw, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var values = new List<decimal>(expectedCount);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Number) return null;
                values.Add(Math.Clamp(el.GetDecimal(), -1m, 1m));
            }
            return values.Count == expectedCount ? values : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
