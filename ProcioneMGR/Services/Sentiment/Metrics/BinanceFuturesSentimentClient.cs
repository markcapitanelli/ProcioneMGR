using System.Globalization;
using System.Text.Json;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Sentiment.Metrics;

/// <summary>
/// Dati pubblici di posizionamento dai futures USDS-M di Binance — API senza chiave
/// (limite IP 1000 req/5min; questa fonte ne usa ~5 per simbolo ogni tick da 30 min):
/// global long/short account ratio, top-trader long/short position ratio, taker buy/sell
/// volume ratio, open interest, funding rate. È POSIZIONAMENTO REALE sul venue dove la
/// piattaforma trada: la fonte con il riscontro più solido della ricerca 2026-07 — funding e
/// positioning estremi sono segnali contrarian di squeeze/reversal documentati.
///
/// ATTENZIONE STORICO: Binance conserva SOLO gli ultimi 30 giorni di queste serie — i buchi sono
/// irrecuperabili. Per questo il worker di raccolta è default ON e ogni fetch prende limit=48
/// punti orari (recupera fino a 48h di downtime; oltre, il buco resta e i baseline lo tollerano).
///
/// FALLBACK GEO designato (non implementato): gli endpoint sono market data pubblico e l'Italia
/// non è bloccata (il repo chiama già fapi.binance.com in produzione — GetFundingRateAsync — e la
/// raggiungibilità è stata riverificata dal vivo il 2026-07-19 catturando le fixture dei test).
/// Se comparisse un 451/403 persistente, la degradazione è pulita (fonte rossa in /sentiment,
/// composite rinormalizzato sui componenti restanti) e il rimedio additivo è una fonte
/// "BitgetFutures" sugli endpoint pubblici V2 mix (open interest, funding, position ratio):
/// nuova ISentimentMetricSource, zero modifiche a schema e orchestrazione.
/// </summary>
public sealed class BinanceFuturesSentimentClient(
    IReadOnlyList<string> markets,
    IHttpClientFactory httpClientFactory) : ISentimentMetricSource
{
    private const string BaseUrl = "https://fapi.binance.com";
    private const int PointsPerFetch = 48; // 48 punti orari: copre fino a 48h di downtime

    public string Name => SentimentMetricSources.BinanceFutures;

    public async Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct)
    {
        var httpClient = httpClientFactory.CreateClient("SentimentBinanceFutures");
        var samples = new List<SentimentMetricSample>();

        foreach (var market in markets)
        {
            ct.ThrowIfCancellationRequested();
            var baseSymbol = ToBaseTicker(market);

            samples.AddRange(ParseRatioSeries(
                await GetAsync(httpClient, $"/futures/data/globalLongShortAccountRatio?symbol={market}&period=1h&limit={PointsPerFetch}", ct),
                "longShortRatio", SentimentMetrics.GlobalLongShortRatio, baseSymbol));
            samples.AddRange(ParseRatioSeries(
                await GetAsync(httpClient, $"/futures/data/topLongShortPositionRatio?symbol={market}&period=1h&limit={PointsPerFetch}", ct),
                "longShortRatio", SentimentMetrics.TopTraderLongShortRatio, baseSymbol));
            samples.AddRange(ParseRatioSeries(
                await GetAsync(httpClient, $"/futures/data/takerlongshortRatio?symbol={market}&period=1h&limit={PointsPerFetch}", ct),
                "buySellRatio", SentimentMetrics.TakerBuySellRatio, baseSymbol));
            samples.AddRange(ParseOpenInterestSeries(
                await GetAsync(httpClient, $"/futures/data/openInterestHist?symbol={market}&period=1h&limit={PointsPerFetch}", ct),
                baseSymbol));
            samples.AddRange(ParseFundingRates(
                await GetAsync(httpClient, $"/fapi/v1/fundingRate?symbol={market}&limit={PointsPerFetch}", ct),
                baseSymbol));
        }
        return samples;
    }

    private static async Task<string> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(BaseUrl + path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>"BTCUSDT" → "BTC": ticker base, compatibile con SymbolsJson delle news e l'OHLCV.</summary>
    public static string ToBaseTicker(string market)
        => market.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? market[..^4].ToUpperInvariant() : market.ToUpperInvariant();

    /// <summary>
    /// Parsing puro delle serie "ratio" (globalLongShortAccountRatio / topLongShortPositionRatio /
    /// takerlongshortRatio): array di oggetti con il campo ratio come stringa + timestamp in ms.
    /// Un elemento malformato si salta, non fa fallire la fonte.
    /// </summary>
    public static IReadOnlyList<SentimentMetricSample> ParseRatioSeries(string json, string ratioField, string metric, string baseSymbol)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var samples = new List<SentimentMetricSample>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty(ratioField, out var ratioEl) ||
                !decimal.TryParse(ratioEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ratio) ||
                !item.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetInt64(out var unixMs))
            {
                continue;
            }
            samples.Add(new SentimentMetricSample(
                DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime, metric, baseSymbol, ratio));
        }
        return samples;
    }

    /// <summary>Parsing puro di openInterestHist: due metriche per punto (contratti e valore USDT).</summary>
    public static IReadOnlyList<SentimentMetricSample> ParseOpenInterestSeries(string json, string baseSymbol)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var samples = new List<SentimentMetricSample>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetInt64(out var unixMs))
            {
                continue;
            }
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

            if (item.TryGetProperty("sumOpenInterest", out var oiEl) &&
                decimal.TryParse(oiEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var oi))
            {
                samples.Add(new SentimentMetricSample(ts, SentimentMetrics.OpenInterest, baseSymbol, oi));
            }
            if (item.TryGetProperty("sumOpenInterestValue", out var oivEl) &&
                decimal.TryParse(oivEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var oiv))
            {
                samples.Add(new SentimentMetricSample(ts, SentimentMetrics.OpenInterestValue, baseSymbol, oiv));
            }
        }
        return samples;
    }

    /// <summary>Parsing puro di /fapi/v1/fundingRate: funding in PERCENTO (×100, convenzione piattaforma).</summary>
    public static IReadOnlyList<SentimentMetricSample> ParseFundingRates(string json, string baseSymbol)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var samples = new List<SentimentMetricSample>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("fundingRate", out var frEl) ||
                !decimal.TryParse(frEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) ||
                !item.TryGetProperty("fundingTime", out var tsEl) || !tsEl.TryGetInt64(out var unixMs))
            {
                continue;
            }
            samples.Add(new SentimentMetricSample(
                DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime,
                SentimentMetrics.FundingRate, baseSymbol, rate * 100m));
        }
        return samples;
    }
}
