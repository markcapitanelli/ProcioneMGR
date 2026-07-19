using System.Globalization;
using System.Text.Json;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Sentiment.Metrics;

/// <summary>
/// Fear &amp; Greed Index di alternative.me — API pubblica gratuita SENZA chiave
/// (https://api.alternative.me/fng/), un punto al giorno 0 (extreme fear) - 100 (extreme greed).
/// Fonte scelta dalla ricerca 2026-07: l'indice NON predice i ritorni giornalieri (reagisce ai
/// prezzi), ma gli ESTREMI (≤20 / ≥80) hanno valore contrarian documentato su orizzonti
/// multi-settimana — per questo alimenta i flag Extremes del composite, non un segnale diretto.
/// Termini d'uso: attribuzione obbligatoria (link ad alternative.me, presente in /sentiment).
/// Lo storico completo è scaricabile con limit=0 (~2500 punti): backfill una tantum via
/// <see cref="IBackfillableMetricSource"/>, poi limit=7 per tick.
/// </summary>
public sealed class FearGreedClient(IHttpClientFactory httpClientFactory) : IBackfillableMetricSource
{
    private const string ApiUrl = "https://api.alternative.me/fng/";

    public string Name => SentimentMetricSources.FearGreed;

    public Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct)
        => FetchAsync(limit: 7, ct);

    public Task<IReadOnlyList<SentimentMetricSample>> FetchFullHistoryAsync(CancellationToken ct)
        => FetchAsync(limit: 0, ct);

    private async Task<IReadOnlyList<SentimentMetricSample>> FetchAsync(int limit, CancellationToken ct)
    {
        // CreateClient per chiamata (vedi RssNewsSource): rotazione anti-DNS-stale.
        var httpClient = httpClientFactory.CreateClient("SentimentFearGreed");
        var json = await httpClient.GetStringAsync($"{ApiUrl}?limit={limit}", ct);
        return ParseFng(json);
    }

    /// <summary>Parsing puro della risposta /fng/ — testabile senza rete (fixture reale nei test).</summary>
    public static IReadOnlyList<SentimentMetricSample> ParseFng(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var samples = new List<SentimentMetricSample>();
        foreach (var item in data.EnumerateArray())
        {
            // Valori come STRINGHE nell'API; un elemento malformato si salta, non fa fallire la fonte.
            if (!item.TryGetProperty("value", out var valueEl) ||
                !decimal.TryParse(valueEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ||
                !item.TryGetProperty("timestamp", out var tsEl) ||
                !long.TryParse(tsEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                continue;
            }

            samples.Add(new SentimentMetricSample(
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime,
                SentimentMetrics.FearGreedIndex,
                Symbol: string.Empty, // indice di mercato, non per-simbolo
                Math.Clamp(value, 0m, 100m)));
        }
        return samples;
    }
}
