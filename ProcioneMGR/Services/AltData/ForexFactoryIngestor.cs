using System.Globalization;
using HtmlAgilityPack;

namespace ProcioneMGR.Services.AltData;

/// <summary>
/// Ingestor del calendario economico di ForexFactory (Fase D.2). ForexFactory non ha un feed RSS
/// pubblico (<c>/rss</c> risponde 403) — verificato dal vivo che <c>/calendar</c> è invece HTML
/// server-renderizzato con uno User-Agent da browser realistico (senza, il sito risponde comunque
/// con la pagina ma non è mai stato verificato un blocco Cloudflare attivo in questo scraping:
/// la pagina contiene le righe evento reali, non una challenge page).
///
/// LIMITAZIONE DOCUMENTATA: i valori "Actual/Forecast/Previous" NON sono presenti nell'HTML
/// statico — verificato dal vivo, tutte le celle <c>calendar__actual</c> risultano vuote nella
/// risposta server: ForexFactory li popola via JavaScript/AJAX dopo il caricamento pagina.
/// Riprodurli richiederebbe un browser headless (fuori scope, dipendenza pesante e fragile) o
/// l'endpoint AJAX interno del sito (non documentato, più a rischio di rottura silenziosa di uno
/// scraping HTML già di per sé fragile). Questo ingestor estrae quindi solo i campi presenti
/// staticamente: orario, valuta, titolo evento, livello di impatto (High/Medium/Low/Holiday) — già
/// sufficienti per costruire la categoria <see cref="NewsCategory.EconomicCalendar"/> e misurarne
/// l'impatto storico sul prezzo (<c>NewsImpactAnalyzer</c>), che è l'obiettivo primario.
///
/// LIMITAZIONE FUSO ORARIO: l'orario testuale (es. "2:15pm") è quello mostrato dalla pagina a un
/// visitatore anonimo (default del sito, tipicamente Eastern Time) — non c'è conversione a UTC
/// esplicita perché il fuso di default non è documentato/garantito stabile. Accettabile per
/// un'analisi di impatto a granularità oraria/giornaliera, non per un timing al minuto.
/// </summary>
public sealed class ForexFactoryIngestor(HttpClient httpClient) : IAltDataSource
{
    public string Name => "ForexFactory";

    private const string CalendarUrl = "https://www.forexfactory.com/calendar";

    public async Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct)
    {
        var html = await httpClient.GetStringAsync(CalendarUrl, ct);
        return ParseCalendar(html);
    }

    /// <summary>Parsing puro da HTML già scaricato — separato da <see cref="FetchLatestAsync"/> per essere testabile senza rete (stesso pattern di <c>RssNewsSource.ParseFeed</c>).</summary>
    public static IReadOnlyList<RawNewsItem> ParseCalendar(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = new List<RawNewsItem>();
        var rows = doc.DocumentNode.SelectNodes("//tr[contains(concat(' ', normalize-space(@class), ' '), ' calendar__row ')]");
        if (rows is null) return items;

        DateTime? currentDay = null;
        foreach (var row in rows)
        {
            var dayDateline = row.Attributes["data-day-dateline"]?.Value;
            if (dayDateline is not null && long.TryParse(dayDateline, out var unixSeconds))
            {
                currentDay = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Date;
            }

            var eventId = row.Attributes["data-event-id"]?.Value;
            if (eventId is null)
            {
                continue; // riga separatore (day-breaker) o non-evento, niente da estrarre
            }

            var title = row.SelectSingleNode(".//span[contains(@class,'calendar__event-title')]")?.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var currency = row.SelectSingleNode(".//td[contains(@class,'calendar__currency')]")?.InnerText.Trim() ?? "";
            var impactClass = row.SelectSingleNode(".//td[contains(@class,'calendar__impact')]//span")?.GetAttributeValue("class", "") ?? "";
            var impact = ExtractImpact(impactClass);
            var timeText = row.SelectSingleNode(".//td[contains(@class,'calendar__time')]")?.InnerText.Trim();
            var scheduledUtc = CombineDayAndTime(currentDay, timeText);

            var summary = string.IsNullOrEmpty(currency) ? $"Impatto: {impact}" : $"{currency} — Impatto: {impact}";
            var symbols = string.IsNullOrEmpty(currency) ? [] : new[] { currency };

            // URL univoco per evento (fragment sull'id) — necessario per la deduplica di
            // AltDataSyncService: la pagina calendario è UNA sola per l'intera sync, quindi senza
            // un frammento distintivo tutti gli eventi condividerebbero lo stesso Url e solo il
            // primo verrebbe inserito.
            items.Add(new RawNewsItem(
                scheduledUtc,
                title!,
                summary,
                $"{CalendarUrl}#detail={eventId}",
                CategoryOverride: NewsCategory.EconomicCalendar,
                SentimentScoreOverride: null,
                SymbolsOverride: symbols));
        }
        return items;
    }

    private static string ExtractImpact(string impactIconClass)
    {
        if (impactIconClass.Contains("impact-red")) return "High";
        if (impactIconClass.Contains("impact-ora")) return "Medium";
        if (impactIconClass.Contains("impact-yel")) return "Low";
        if (impactIconClass.Contains("impact-gra")) return "Holiday";
        return "Unknown";
    }

    private static DateTime CombineDayAndTime(DateTime? day, string? timeText)
    {
        var baseDay = day ?? DateTime.UtcNow.Date;
        if (!string.IsNullOrWhiteSpace(timeText) &&
            DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
        {
            return DateTime.SpecifyKind(baseDay.Add(parsedTime.TimeOfDay), DateTimeKind.Utc);
        }
        return DateTime.SpecifyKind(baseDay, DateTimeKind.Utc);
    }
}
