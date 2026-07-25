using System.Text.Json;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.AltData;

/// <summary>Ritorno medio del simbolo di riferimento sui tre orizzonti, per un gruppo di notizie/eventi.</summary>
public sealed record ImpactStats(int Observations, double AvgReturn1h, double AvgReturn4h, double AvgReturn24h);

public sealed record CategoryImpact(string Category, ImpactStats Stats);

public sealed record SourceImpact(string Source, ImpactStats Stats);

/// <summary>
/// Confronto incrociato fra due fonti indipendenti di sentiment retail sullo STESSO simbolo nella
/// STESSA ora: quando concordano (entrambe fortemente long o fortemente short), il ritorno medio
/// del simbolo di riferimento differisce da quando divergono?
/// </summary>
public sealed record RetailSentimentAgreement(
    string Symbol,
    int MatchedSnapshots,
    int AgreementCount,
    ImpactStats WhenBothLong,
    ImpactStats WhenBothShort,
    ImpactStats WhenDisagree);

public sealed record NewsImpactReport(
    string ReferenceSymbol,
    IReadOnlyList<CategoryImpact> ByCategory,
    IReadOnlyList<SourceImpact> BySource,
    IReadOnlyList<RetailSentimentAgreement> RetailSentimentCrossSource);

public interface INewsImpactAnalyzer
{
    /// <summary>
    /// Misura il movimento di prezzo del simbolo di riferimento nelle finestre [t,t+1h], [t,t+4h],
    /// [t,t+24h] a partire dal timestamp di ciascuna notizia/evento, e aggrega per categoria e per
    /// fonte. <paramref name="referenceCandles"/> deve essere ordinato cronologicamente.
    /// </summary>
    NewsImpactReport Analyze(string referenceSymbol, IReadOnlyList<AltDataPoint> news, IReadOnlyList<OhlcvData> referenceCandles);

    /// <summary>
    /// [T2.7] Event-study RIGOROSO sugli stessi eventi alt-data: abnormal return contro una baseline
    /// per-evento, finestra pre-evento (anticipazione/leakage) e p-value placebo su date casuali —
    /// il rigore che le medie semplici di <see cref="Analyze"/> non hanno. Filtra prima per
    /// categoria/fonte se vuoi studiare un sottoinsieme; le finestre sono in barre di
    /// <paramref name="referenceCandles"/>.
    /// </summary>
    Analysis.EventStudyResult StudyRigorous(
        IReadOnlyList<AltDataPoint> news, IReadOnlyList<OhlcvData> referenceCandles, Analysis.EventStudyConfig? config = null);
}

/// <summary>
/// DECISIONE ARCHITETTURALE (Fase D.2): la piattaforma ingerisce OHLCV solo per crypto
/// (Binance/Bitget) — non esiste uno storico prezzi per le coppie forex (EURUSD ecc.) di cui
/// parlano le fonti macro/calendario/sentiment retail. Misurare l'impatto "sul proprio strumento"
/// richiederebbe OHLCV forex, fuori scope. Si misura quindi l'impatto di OGNI notizia/evento
/// (qualunque sia lo strumento nominale di cui parla) sul movimento di un SIMBOLO CRYPTO DI
/// RIFERIMENTO scelto dall'utente (es. BTC/USDT) — una domanda empirica legittima e ben nota
/// in letteratura ("risk-on/risk-off": le decisioni Fed/ECB e il sentiment macro muovono anche
/// gli asset di rischio come le crypto, non solo il proprio strumento diretto). Se in futuro la
/// piattaforma ingerisse anche OHLCV forex, lo stesso analyzer funzionerebbe passando quella
/// serie come <c>referenceCandles</c> — nessun cambiamento di codice necessario.
/// </summary>
public sealed class NewsImpactAnalyzer : INewsImpactAnalyzer
{
    private static readonly TimeSpan Horizon1h = TimeSpan.FromHours(1);
    private static readonly TimeSpan Horizon4h = TimeSpan.FromHours(4);
    private static readonly TimeSpan Horizon24h = TimeSpan.FromHours(24);

    public NewsImpactReport Analyze(string referenceSymbol, IReadOnlyList<AltDataPoint> news, IReadOnlyList<OhlcvData> referenceCandles)
    {
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(referenceCandles);

        var scored = news
            .Select(n => (Item: n, Returns: ForwardReturns(referenceCandles, n.TimestampUtc)))
            .ToList();

        var byCategory = scored
            .GroupBy(x => x.Item.Category)
            .Select(g => new CategoryImpact(g.Key, BuildStats(g.Select(x => x.Returns))))
            .OrderByDescending(c => c.Stats.Observations)
            .ToList();

        var bySource = scored
            .GroupBy(x => x.Item.Source)
            .Select(g => new SourceImpact(g.Key, BuildStats(g.Select(x => x.Returns))))
            .OrderByDescending(s => s.Stats.Observations)
            .ToList();

        var crossSource = BuildRetailSentimentAgreement(news, referenceCandles);

        return new NewsImpactReport(referenceSymbol, byCategory, bySource, crossSource);
    }

    /// <inheritdoc />
    public Analysis.EventStudyResult StudyRigorous(
        IReadOnlyList<AltDataPoint> news, IReadOnlyList<OhlcvData> referenceCandles, Analysis.EventStudyConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(referenceCandles);
        return Analysis.EventStudy.Run(referenceCandles, news.Select(n => n.TimestampUtc).ToList(), config);
    }

    private static List<RetailSentimentAgreement> BuildRetailSentimentAgreement(
        IReadOnlyList<AltDataPoint> news, IReadOnlyList<OhlcvData> candles)
    {
        var retailCategory = nameof(NewsCategory.RetailSentiment);
        var readings = news
            .Where(n => n.Category == retailCategory && n.SentimentScore.HasValue)
            .Select(n => (Symbol: FirstSymbol(n), Hour: TruncateToHour(n.TimestampUtc), n.Source, Score: n.SentimentScore!.Value))
            .Where(r => r.Symbol is not null)
            .ToList();

        // Coppie (FXSSI, MyFxBook) sullo stesso simbolo nella stessa ora — le uniche due fonti di
        // sentiment retail registrate (vedi RetailSentimentIngestor); se in futuro se ne
        // aggiungessero altre, qui andrebbero scelte esplicitamente le due da confrontare.
        var bySymbol = readings
            .GroupBy(r => r.Symbol!)
            .Select(g =>
            {
                var matches = new List<(DateTime Hour, decimal ScoreA, decimal ScoreB)>();
                foreach (var hourGroup in g.GroupBy(r => r.Hour))
                {
                    var fxssi = hourGroup.FirstOrDefault(r => r.Source == "FXSSI");
                    var myfxbook = hourGroup.FirstOrDefault(r => r.Source == "MyFxBook");
                    if (fxssi.Symbol is not null && myfxbook.Symbol is not null)
                    {
                        matches.Add((hourGroup.Key, fxssi.Score, myfxbook.Score));
                    }
                }
                return (Symbol: g.Key, Matches: matches);
            })
            .Where(x => x.Matches.Count > 0)
            .ToList();

        var result = new List<RetailSentimentAgreement>();
        foreach (var (symbol, matches) in bySymbol)
        {
            const decimal strongThreshold = 0.4m; // corrisponde a >70% o <30% long, come richiesto
            var bothLong = matches.Where(m => m.ScoreA > strongThreshold && m.ScoreB > strongThreshold).ToList();
            var bothShort = matches.Where(m => m.ScoreA < -strongThreshold && m.ScoreB < -strongThreshold).ToList();
            var agreeing = bothLong.Count + bothShort.Count;
            var disagreeing = matches.Where(m => !bothLong.Contains(m) && !bothShort.Contains(m)).ToList();

            result.Add(new RetailSentimentAgreement(
                symbol,
                matches.Count,
                agreeing,
                BuildStats(bothLong.Select(m => ForwardReturns(candles, m.Hour))),
                BuildStats(bothShort.Select(m => ForwardReturns(candles, m.Hour))),
                BuildStats(disagreeing.Select(m => ForwardReturns(candles, m.Hour)))));
        }
        return result.OrderByDescending(r => r.MatchedSnapshots).ToList();
    }

    private static (double? R1h, double? R4h, double? R24h) ForwardReturns(IReadOnlyList<OhlcvData> candles, DateTime fromUtc) =>
        (ForwardReturn(candles, fromUtc, Horizon1h), ForwardReturn(candles, fromUtc, Horizon4h), ForwardReturn(candles, fromUtc, Horizon24h));

    /// <summary>Rendimento del simbolo di riferimento fra la prima candela disponibile ≥ <paramref name="fromUtc"/> e la prima ≥ <paramref name="fromUtc"/>+<paramref name="horizon"/>. Null se una delle due è fuori dal range caricato.</summary>
    private static double? ForwardReturn(IReadOnlyList<OhlcvData> candles, DateTime fromUtc, TimeSpan horizon)
    {
        var entryIdx = FirstIndexAtOrAfter(candles, fromUtc);
        if (entryIdx is null) return null;
        var entryPrice = candles[entryIdx.Value].Close;
        if (entryPrice <= 0m) return null;

        var exitIdx = FirstIndexAtOrAfter(candles, fromUtc + horizon);
        if (exitIdx is null) return null;
        var exitPrice = candles[exitIdx.Value].Close;

        return (double)((exitPrice - entryPrice) / entryPrice);
    }

    /// <summary>Ricerca binaria: indice della prima candela con TimestampUtc &gt;= t, o null se t è oltre l'ultima candela. Richiede candles ordinate crescenti.</summary>
    private static int? FirstIndexAtOrAfter(IReadOnlyList<OhlcvData> candles, DateTime t)
    {
        int lo = 0, hi = candles.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (candles[mid].TimestampUtc < t) lo = mid + 1; else hi = mid;
        }
        return lo < candles.Count ? lo : null;
    }

    private static ImpactStats BuildStats(IEnumerable<(double? R1h, double? R4h, double? R24h)> group)
    {
        var list = group as IReadOnlyCollection<(double? R1h, double? R4h, double? R24h)> ?? group.ToList();

        double Avg(Func<(double? R1h, double? R4h, double? R24h), double?> selector)
        {
            var vals = list.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return vals.Length > 0 ? vals.Average() : 0.0;
        }

        return new ImpactStats(list.Count, Avg(x => x.R1h), Avg(x => x.R4h), Avg(x => x.R24h));
    }

    private static string? FirstSymbol(AltDataPoint point)
    {
        try
        {
            var symbols = JsonSerializer.Deserialize<List<string>>(point.SymbolsJson);
            return symbols?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime TruncateToHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
}
