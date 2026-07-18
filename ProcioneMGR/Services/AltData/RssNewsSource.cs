using System.ServiceModel.Syndication;
using System.Xml;

namespace ProcioneMGR.Services.AltData;

/// <summary>
/// Fonti RSS note: gratuite, senza chiave API, senza rate limit pratico — a differenza di
/// CryptoPanic (piano gratuito chiuso ad aprile 2026) o di provider a pagamento come
/// CryptoCompare. Editorialmente affidabili: CoinDesk/Cointelegraph/The Block/Decrypt sono le
/// fonti più citate negli studi di event-study sull'impatto di notizie regolatorie/ETF sui
/// mercati crypto.
///
/// FXStreet (Fase D.2, forex/macro) è anch'essa un normale feed RSS 2.0 — verificato dal vivo
/// (200, <c>text/xml</c>, item validi). DELIBERATO: niente "FxStreetRssIngestor" dedicato,
/// solo due voci in più qui — <see cref="RssNewsSource"/> già gestisce qualunque feed RSS/Atom,
/// e la distinzione Macro/CentralBanks è fatta dal classificatore per KEYWORD sul contenuto
/// (stesso principio delle notizie crypto), non da una classe per fonte. Una classe wrapper che
/// non aggiunge comportamento sarebbe duplicazione, non riuso. "FXStreet-CentralBanks" è il feed
/// di categoria dedicato del sito (<c>/rss/news/central-banks</c>) — dà una seconda fonte
/// mirata oltre al feed generale, utile perché il classificatore da solo non garantisce recall
/// completo sulle notizie di banche centrali.
/// </summary>
public static class NewsFeeds
{
    public static readonly IReadOnlyDictionary<string, string> KnownFeeds = new Dictionary<string, string>
    {
        ["CoinDesk"] = "https://www.coindesk.com/arc/outboundfeeds/rss",
        ["Cointelegraph"] = "https://cointelegraph.com/rss",
        ["TheBlock"] = "https://www.theblock.co/rss.xml",
        ["Decrypt"] = "https://decrypt.co/feed",
        ["FXStreet"] = "https://www.fxstreet.com/rss",
        ["FXStreet-CentralBanks"] = "https://www.fxstreet.com/rss/news/central-banks",
    };
}

/// <summary>Implementazione di <see cref="IAltDataSource"/> per un singolo feed RSS/Atom.</summary>
public sealed class RssNewsSource(string name, string feedUrl, IHttpClientFactory httpClientFactory) : IAltDataSource
{
    public string Name { get; } = name;

    public async Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct)
    {
        // CreateClient per chiamata, non un HttpClient catturato una volta a startup: cosi' la
        // rotazione periodica dell'handler di IHttpClientFactory (contro il DNS stale) funziona
        // davvero anche per una fonte che vive nel processo per settimane.
        var httpClient = httpClientFactory.CreateClient("AltDataRss");
        await using var stream = await httpClient.GetStreamAsync(feedUrl, ct);
        using var reader = XmlReader.Create(stream);
        var feed = SyndicationFeed.Load(reader);
        return ParseFeed(feed);
    }

    /// <summary>Estrazione pura dal feed già parsato — separata da <see cref="FetchLatestAsync"/> per essere testabile senza rete.</summary>
    public static IReadOnlyList<RawNewsItem> ParseFeed(SyndicationFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var items = new List<RawNewsItem>();
        foreach (var item in feed.Items)
        {
            var published = item.PublishDate != default
                ? item.PublishDate.UtcDateTime
                : item.LastUpdatedTime != default
                    ? item.LastUpdatedTime.UtcDateTime
                    : DateTime.UtcNow;

            var url = item.Links.FirstOrDefault()?.Uri?.ToString();
            var summary = item.Summary?.Text;
            var title = item.Title?.Text ?? string.Empty;

            items.Add(new RawNewsItem(DateTime.SpecifyKind(published, DateTimeKind.Utc), title, summary, url));
        }
        return items;
    }
}
