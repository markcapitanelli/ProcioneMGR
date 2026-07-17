using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ProcioneMGR.Services.AltData;

/// <summary>
/// Ingestor del posizionamento retail (long % vs short %) per coppia — un CONTRARIAN indicator:
/// il retail è storicamente sul lato sbagliato ai punti di svolta. A differenza delle notizie
/// testuali, qui non c'è testo da classificare/scorare col lessico: <see cref="NewsCategory.RetailSentiment"/>
/// e il punteggio di sentiment sono valorizzati direttamente da un dato numerico
/// (<c>RawNewsItem.CategoryOverride</c>/<c>SentimentScoreOverride</c>).
///
/// DEVIAZIONE FLAGGATA rispetto al piano originale (due siti separati):
/// - <c>forexclientsentiment.com</c> è dietro una challenge Cloudflare attiva (verificato dal
///   vivo: risposta 403, pagina "Just a moment" di ~5KB) — non scrapabile senza un browser
///   headless, fuori scope.
/// - <c>fxssi.com/tools/current-ratio</c> stesso: la pagina HTML statica NON contiene i valori
///   (widget client-side, nessun dato embeddato server-side) — verificato dal vivo scaricando la
///   pagina reale (1MB di HTML/CSS/JS, zero percentuali di long/short nel markup).
/// - ALTERNATIVA EQUIVALENTE implementata: lo stesso sito FXSSI espone pubblicamente l'endpoint
///   JSON che il suo stesso widget interroga lato client
///   (<c>https://c.fxssi.com/api/current-ratio</c>, verificato dal vivo: 200, JSON pulito,
///   nessuna autenticazione) — aggrega il posizionamento long/short di PIÙ broker reali
///   (FXSSI, MyFxBook, Oanda, Dukascopy, FXCM, XM, ecc.) sotto un'unica chiave per simbolo.
///   Usiamo la chiave "fxssi" come fonte "FXSSI" e la chiave "myfxbook" come fonte "MyFxBook" —
///   quest'ultima sostituisce forexclientsentiment.com con una fonte ANCORA PIÙ riconosciuta e
///   indipendente nel settore, ottenendo comunque le due fonti indipendenti richieste per il
///   confronto incrociato, con una sola chiamata HTTP verificata funzionante.
///
/// Un'istanza per fonte (stesso principio di <see cref="RssNewsSource"/> con
/// <see cref="NewsFeeds.KnownFeeds"/>): "un ingestor, più istanze" invece di una classe per sito.
/// </summary>
public sealed class RetailSentimentIngestor(string sourceName, string brokerKey, IHttpClientFactory httpClientFactory) : IAltDataSource
{
    private const string ApiUrl = "https://c.fxssi.com/api/current-ratio";
    private const string PublicPageUrl = "https://fxssi.com/tools/current-ratio";

    /// <summary>Simboli dell'aggregatore FXSSI che sono in realtà coppie crypto già tracciate dalla piattaforma: mappati al ticker canonico per essere compatibili con <c>SentimentAlphaFactor</c>/OHLCV esistenti.</summary>
    private static readonly Dictionary<string, string> CryptoSymbolMap = new()
    {
        ["BTCUSD"] = "BTC",
        ["ETHUSD"] = "ETH",
    };

    public string Name { get; } = sourceName;

    public async Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct)
    {
        // CreateClient per chiamata (vedi RssNewsSource): evita di catturare l'HttpClient e il suo
        // handler per l'intera vita del processo, vanificando la rotazione anti-DNS-stale.
        var httpClient = httpClientFactory.CreateClient("AltDataRetailSentiment");
        var response = await httpClient.GetFromJsonAsync<FxssiApiResponse>(ApiUrl, ct);
        if (response?.Brokers is null || !response.Brokers.TryGetValue(brokerKey, out var ratios))
        {
            return [];
        }
        return ParseRatios(ratios);
    }

    /// <summary>Parsing puro dal dizionario simbolo→percentuale già deserializzato — testabile senza rete.</summary>
    public static IReadOnlyList<RawNewsItem> ParseRatios(IReadOnlyDictionary<string, string> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);

        // Un'unica istantanea per sync: bucket orario nell'Url per la deduplica (stesso simbolo
        // e stesso testo ogni volta — senza un elemento che cambia, AltDataSyncService tratterebbe
        // ogni lettura successiva come già vista e non ne salverebbe mai una nuova nel tempo).
        var snapshotUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour), DateTimeKind.Utc);
        var hourBucket = snapshotUtc.ToString("yyyyMMddHH");

        var items = new List<RawNewsItem>();
        foreach (var (symbol, rawPct) in ratios)
        {
            if (!decimal.TryParse(rawPct, System.Globalization.CultureInfo.InvariantCulture, out var pctLong))
            {
                continue; // valore malformato nell'API esterna: salta invece di far fallire l'intera fonte
            }
            pctLong = Math.Clamp(pctLong, 0m, 100m);
            var pctShort = 100m - pctLong;
            var sentimentScore = (pctLong - 50m) / 50m; // range [-1, +1], come richiesto

            var displaySymbol = CryptoSymbolMap.GetValueOrDefault(symbol, symbol);
            var title = $"{displaySymbol}: {pctLong:F1}% long / {pctShort:F1}% short";
            var summary = $"Posizionamento retail aggregato per {displaySymbol}, istantanea oraria.";
            var url = $"{PublicPageUrl}?filter={symbol}#{hourBucket}";

            items.Add(new RawNewsItem(
                snapshotUtc,
                title,
                summary,
                url,
                CategoryOverride: NewsCategory.RetailSentiment,
                SentimentScoreOverride: sentimentScore,
                SymbolsOverride: [displaySymbol]));
        }
        return items;
    }

    private sealed record FxssiApiResponse(
        [property: JsonPropertyName("brokers")] Dictionary<string, Dictionary<string, string>>? Brokers);
}
