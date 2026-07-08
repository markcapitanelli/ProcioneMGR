using System.Text.RegularExpressions;

namespace ProcioneMGR.Services.AltData;

/// <summary>
/// Categoria di impatto di una notizia/evento. Regulatory/Security/Institutional/CentralBanks/
/// Macro sono derivate per keyword dal testo (vedi <see cref="NewsImpactClassifier.Classify"/>).
/// EconomicCalendar e RetailSentiment sono invece categorie STRUTTURALI: non derivano da
/// classificazione testuale ma sono assegnate direttamente dal rispettivo ingestor
/// (<c>ForexFactoryIngestor</c>, <c>RetailSentimentIngestor</c>) tramite
/// <c>RawNewsItem.CategoryOverride</c>, perché la natura del dato è diversa da una notizia
/// testuale (un evento datato con impatto atteso; un numero di posizionamento retail).
/// </summary>
public enum NewsCategory
{
    Regulatory,
    Security,
    Institutional,
    CentralBanks,
    Macro,
    EconomicCalendar,
    RetailSentiment,
    Other,
}

/// <summary>
/// Classificazione per parola chiave (word-boundary, non semplice substring — "ban" non deve
/// far scattare un falso positivo su "banana", "sol" su "absolute"): filtro leggero PRIMA di
/// un'eventuale chiamata LLM di sentiment, per concentrare il costo/rumore sulle notizie che
/// la letteratura conferma muovere davvero il mercato.
/// </summary>
public static class NewsImpactClassifier
{
    private static readonly string[] RegulatoryKeywords =
    [
        "sec", "cftc", "regulator", "regulation", "regulatory", "lawsuit", "etf", "ban",
        "legislation", "compliance", "subpoena", "sue", "sues", "court", "ruling",
    ];

    private static readonly string[] SecurityKeywords =
    [
        "hack", "hacked", "hacker", "exploit", "exploited", "breach", "stolen", "theft",
        "vulnerability", "rug pull", "scam", "phishing", "drained",
    ];

    private static readonly string[] InstitutionalKeywords =
    [
        "blackrock", "institutional", "inflow", "inflows", "adoption", "custody",
        "pension", "treasury", "fidelity", "grayscale",
    ];

    /// <summary>Banche centrali e le loro decisioni/dichiarazioni: letteratura event-study consolidata sull'impatto diretto su FX/risk asset.</summary>
    private static readonly string[] CentralBanksKeywords =
    [
        "fed", "fomc", "federal reserve", "ecb", "boe", "boj", "rba", "rbnz", "snb", "pboc",
        "central bank", "rate decision", "rate hike", "rate cut", "interest rate", "powell",
        "lagarde", "bailey", "ueda", "quantitative easing", "quantitative tightening", "hawkish",
        "dovish",
    ];

    /// <summary>Dati macroeconomici: pubblicazioni periodiche con riscontro storico misurabile su FX/risk asset.</summary>
    private static readonly string[] MacroKeywords =
    [
        "gdp", "cpi", "inflation", "nfp", "non-farm payrolls", "nonfarm payrolls", "unemployment",
        "pmi", "retail sales", "trade balance", "recession", "stimulus", "tariff", "tariffs",
        "treasury yield", "yields", "jobless claims", "consumer confidence", "ppi",
    ];

    private static readonly Dictionary<string, string[]> SymbolAliases = new()
    {
        ["BTC"] = ["bitcoin", "btc"],
        ["ETH"] = ["ethereum", "eth", "ether"],
        ["SOL"] = ["solana", "sol"],
        ["BNB"] = ["bnb"],
        ["XRP"] = ["ripple", "xrp"],
        ["DOGE"] = ["dogecoin", "doge"],
        ["ADA"] = ["cardano"],
        // Major forex pairs: nessun OHLCV corrispondente nella piattaforma (solo crypto via
        // Binance/Bitget), ma utili per tag/filtro in UI e per un futuro collegamento diretto.
        ["EURUSD"] = ["eur/usd", "eurusd", "euro"],
        ["GBPUSD"] = ["gbp/usd", "gbpusd", "cable", "pound sterling", "sterling"],
        ["USDJPY"] = ["usd/jpy", "usdjpy", "japanese yen"],
        ["USDCHF"] = ["usd/chf", "usdchf", "swiss franc"],
        ["AUDUSD"] = ["aud/usd", "audusd", "aussie"],
        ["USDCAD"] = ["usd/cad", "usdcad", "loonie"],
        ["NZDUSD"] = ["nzd/usd", "nzdusd", "kiwi"],
        ["DXY"] = ["dxy", "dollar index"],
    };

    /// <summary>
    /// Punteggio per numero di keyword trovate per categoria (non "prima categoria che matcha"):
    /// una singola parola ambigua non deve sovrastare un segnale più specifico con più riscontri
    /// (es. "BlackRock ... ETF inflows" è due segnali istituzionali contro un solo "etf"
    /// regolatorio). A parità di punteggio vince l'ordine di dichiarazione (LINQ OrderBy è stabile),
    /// che riflette l'impatto di mercato più diretto secondo la letteratura event-study.
    /// </summary>
    public static NewsCategory Classify(string title, string? summary)
    {
        var text = Combine(title, summary);
        var scored = new (NewsCategory Category, int Count)[]
        {
            (NewsCategory.Regulatory, CountMatches(text, RegulatoryKeywords)),
            (NewsCategory.Security, CountMatches(text, SecurityKeywords)),
            (NewsCategory.CentralBanks, CountMatches(text, CentralBanksKeywords)),
            (NewsCategory.Macro, CountMatches(text, MacroKeywords)),
            (NewsCategory.Institutional, CountMatches(text, InstitutionalKeywords)),
        };

        var best = scored.OrderByDescending(s => s.Count).First();
        return best.Count > 0 ? best.Category : NewsCategory.Other;
    }

    public static IReadOnlyList<string> DetectSymbols(string title, string? summary)
    {
        var text = Combine(title, summary);
        var found = new List<string>();
        foreach (var (symbol, aliases) in SymbolAliases)
        {
            if (ContainsAnyWord(text, aliases))
            {
                found.Add(symbol);
            }
        }
        return found;
    }

    private static string Combine(string title, string? summary) => $"{title} {summary}".ToLowerInvariant();

    private static bool ContainsAnyWord(string text, string[] keywords) =>
        keywords.Any(k => Regex.IsMatch(text, $@"\b{Regex.Escape(k)}\b"));

    private static int CountMatches(string text, string[] keywords) =>
        keywords.Count(k => Regex.IsMatch(text, $@"\b{Regex.Escape(k)}\b"));
}
