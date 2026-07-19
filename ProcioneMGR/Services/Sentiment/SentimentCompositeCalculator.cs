using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Calcolo PURO dello <see cref="SentimentSnapshot"/> dai punti metrici e dai punteggi news:
/// niente DB, niente clock implicito — completamente testabile. Regole:
/// - z-score = (ultimo − media baseline) / σ baseline; con meno di <see cref="MinBaselineObservations"/>
///   osservazioni o σ=0 lo z è null (meglio nessun numero che un numero rumoroso);
/// - il contributo di uno z al composite è z/2 clampato in [-1,+1] (z=±2, la soglia "estremo"
///   di default, satura il contributo);
/// - i pesi sono RINORMALIZZATI sui soli componenti disponibili: una fonte giù non distorce
///   il composite, lo restringe;
/// - l'open interest è contesto di ampiezza (flag), MAI parte del composite: dice "quanto è
///   grossa la scommessa", non in che direzione.
/// </summary>
public static class SentimentCompositeCalculator
{
    /// <summary>Sotto questa numerosità del baseline gli z-score non si calcolano.</summary>
    public const int MinBaselineObservations = 10;

    public static SentimentSnapshot Compute(
        SentimentOptions options,
        DateTime nowUtc,
        IReadOnlyList<SentimentMetricPoint> metricsInBaselineWindow,
        IReadOnlyDictionary<string, double> newsScore24hBySymbol,
        double? marketNewsScore24h,
        IReadOnlyList<string> baseSymbols)
    {
        var snapshot = new SentimentSnapshot { ComputedAtUtc = nowUtc, NewsScore24h = marketNewsScore24h };

        // --- Fear & Greed (mercato intero) ---
        var fng = metricsInBaselineWindow
            .Where(p => p.Metric == SentimentMetrics.FearGreedIndex)
            .OrderBy(p => p.TimestampUtc)
            .ToList();
        double? fngComponent = null;
        if (fng.Count > 0)
        {
            var latest = fng[^1];
            var value = (double)latest.Value;
            snapshot.FearGreedValue = value;
            snapshot.FearGreedLabel = FearGreedLabel(value);
            fngComponent = (value - 50.0) / 50.0; // 0-100 → [-1,+1]

            var weekAgo = latest.TimestampUtc.AddDays(-7);
            var reference = fng.LastOrDefault(p => p.TimestampUtc <= weekAgo);
            if (reference is not null)
            {
                snapshot.FearGreedDelta7d = value - (double)reference.Value;
            }

            if (value <= options.FearGreedExtremeLow)
            {
                snapshot.Extremes.Add($"Fear & Greed {value:F0} (extreme fear): mood da capitolazione, storicamente zona contrarian di accumulo.");
            }
            else if (value >= options.FearGreedExtremeHigh)
            {
                snapshot.Extremes.Add($"Fear & Greed {value:F0} (extreme greed): euforia, storicamente zona contrarian di rischio correzione.");
            }
        }

        // --- Per simbolo ---
        foreach (var symbol in baseSymbols)
        {
            var s = new SymbolSentiment
            {
                Symbol = symbol,
                NewsScore24h = newsScore24hBySymbol.TryGetValue(symbol, out var ns) ? ns : null,
            };

            var funding = Series(metricsInBaselineWindow, SentimentMetrics.FundingRate, symbol);
            if (funding.Count > 0) s.FundingPercent = (double)funding[^1].Value;
            s.FundingZ = ZScore(funding);

            var globalLs = Series(metricsInBaselineWindow, SentimentMetrics.GlobalLongShortRatio, symbol);
            if (globalLs.Count > 0) s.GlobalLongShortRatio = (double)globalLs[^1].Value;
            s.GlobalLongShortZ = ZScore(globalLs);
            s.TopTraderLongShortZ = ZScore(Series(metricsInBaselineWindow, SentimentMetrics.TopTraderLongShortRatio, symbol));
            s.TakerZ = ZScore(Series(metricsInBaselineWindow, SentimentMetrics.TakerBuySellRatio, symbol));

            var oiValue = Series(metricsInBaselineWindow, SentimentMetrics.OpenInterestValue, symbol);
            s.OiChange24hPercent = Change24hPercent(oiValue);
            var oiZ = ZScore(oiValue);

            // Composite del simbolo: news + F&G (di mercato) + derivati, pesi rinormalizzati.
            var longShortZ = MeanOfAvailable(s.GlobalLongShortZ, s.TopTraderLongShortZ);
            s.Composite = WeightedComposite(options,
                news: s.NewsScore24h,
                fearGreed: fngComponent,
                funding: ZContribution(s.FundingZ),
                longShort: ZContribution(longShortZ),
                taker: ZContribution(s.TakerZ));

            AddZExtreme(s.Extremes, options, s.FundingZ, symbol,
                positivo: "funding molto sopra il suo baseline (long affollati: rischio long squeeze)",
                negativo: "funding molto sotto il suo baseline (short affollati: rischio short squeeze)");
            AddZExtreme(s.Extremes, options, s.GlobalLongShortZ, symbol,
                positivo: "posizionamento long degli account a un estremo storico",
                negativo: "posizionamento short degli account a un estremo storico");
            AddZExtreme(s.Extremes, options, s.TopTraderLongShortZ, symbol,
                positivo: "top trader sbilanciati long a un estremo",
                negativo: "top trader sbilanciati short a un estremo");
            AddZExtreme(s.Extremes, options, s.TakerZ, symbol,
                positivo: "pressione taker in acquisto estrema",
                negativo: "pressione taker in vendita estrema");
            AddZExtreme(s.Extremes, options, oiZ, symbol,
                positivo: "open interest in forte espansione (leva che si accumula)",
                negativo: "open interest in forte contrazione (deleveraging)");

            snapshot.Symbols.Add(s);
            snapshot.Extremes.AddRange(s.Extremes);
        }

        // --- Composite di mercato: news di mercato + F&G + media dei contributi derivati per simbolo ---
        var avgFunding = MeanOfAvailable(snapshot.Symbols.Select(x => ZContribution(x.FundingZ)).ToArray());
        var avgLongShort = MeanOfAvailable(snapshot.Symbols
            .Select(x => ZContribution(MeanOfAvailable(x.GlobalLongShortZ, x.TopTraderLongShortZ))).ToArray());
        var avgTaker = MeanOfAvailable(snapshot.Symbols.Select(x => ZContribution(x.TakerZ)).ToArray());
        snapshot.CompositeScore = WeightedComposite(options,
            news: marketNewsScore24h,
            fearGreed: fngComponent,
            funding: avgFunding,
            longShort: avgLongShort,
            taker: avgTaker);

        return snapshot;
    }

    /// <summary>Etichetta alternative.me-style dal valore 0-100.</summary>
    public static string FearGreedLabel(double value) => value switch
    {
        <= 25 => "Extreme Fear",
        <= 45 => "Fear",
        <= 55 => "Neutral",
        <= 75 => "Greed",
        _ => "Extreme Greed",
    };

    private static List<SentimentMetricPoint> Series(IReadOnlyList<SentimentMetricPoint> metrics, string metric, string symbol)
        => metrics.Where(p => p.Metric == metric && p.Symbol == symbol).OrderBy(p => p.TimestampUtc).ToList();

    /// <summary>z dell'ULTIMO punto vs l'intera serie nella finestra; null se serie corta o piatta.</summary>
    public static double? ZScore(IReadOnlyList<SentimentMetricPoint> series)
    {
        if (series.Count < MinBaselineObservations) return null;
        var values = series.Select(p => (double)p.Value).ToArray();
        var mean = values.Average();
        var std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length);
        // Tolleranza RELATIVA, non double.Epsilon: una serie piatta produce σ~1e-18 di puro rumore
        // floating-point, e dividerci darebbe z giganteschi privi di significato.
        if (std <= 1e-9 * Math.Max(1.0, Math.Abs(mean))) return null;
        return (values[^1] - mean) / std;
    }

    private static double? Change24hPercent(IReadOnlyList<SentimentMetricPoint> series)
    {
        if (series.Count == 0) return null;
        var latest = series[^1];
        var dayAgo = latest.TimestampUtc.AddHours(-24);
        var reference = series.LastOrDefault(p => p.TimestampUtc <= dayAgo);
        if (reference is null || reference.Value == 0m) return null;
        return (double)((latest.Value - reference.Value) / reference.Value * 100m);
    }

    /// <summary>Contributo al composite di uno z: z/2 clampato (z=±2 satura a ±1); null se z assente.</summary>
    private static double? ZContribution(double? z) => z is null ? null : Math.Clamp(z.Value / 2.0, -1.0, 1.0);

    private static double? MeanOfAvailable(params double?[] values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    /// <summary>Media pesata dei componenti DISPONIBILI, con pesi rinormalizzati; 0 se nessun componente.</summary>
    private static double WeightedComposite(SentimentOptions options, double? news, double? fearGreed, double? funding, double? longShort, double? taker)
    {
        var terms = new List<(double Weight, double Value)>();
        if (news is not null) terms.Add((options.WeightNews, Math.Clamp(news.Value, -1.0, 1.0)));
        if (fearGreed is not null) terms.Add((options.WeightFearGreed, fearGreed.Value));
        if (funding is not null) terms.Add((options.WeightFunding, funding.Value));
        if (longShort is not null) terms.Add((options.WeightLongShort, longShort.Value));
        if (taker is not null) terms.Add((options.WeightTaker, taker.Value));

        var totalWeight = terms.Sum(t => t.Weight);
        if (totalWeight <= 0) return 0.0;
        return Math.Clamp(terms.Sum(t => t.Weight * t.Value) / totalWeight, -1.0, 1.0);
    }

    private static void AddZExtreme(List<string> extremes, SentimentOptions options, double? z, string symbol, string positivo, string negativo)
    {
        if (z is null || Math.Abs(z.Value) < options.ExtremeZScore) return;
        extremes.Add($"{symbol}: {(z.Value > 0 ? positivo : negativo)} (z={z.Value:+0.0;-0.0}).");
    }
}
