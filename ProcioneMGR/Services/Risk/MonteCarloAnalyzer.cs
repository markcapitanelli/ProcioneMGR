namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Montecarlo Analysis "evoluta" (Trombetta, cap. 8): ricombina casualmente la lista dei
/// trade per stimare la distribuzione dei draw down possibili, oltre a quello storico.
///
/// Rispetto alla Montecarlo classica aggiunge tre leve:
///  1. costi extra per appesantire la curva (stress dei costi fissi/slippage);
///  2. rumore casuale proporzionale al singolo trade (generalizza i risultati);
///  3. ricombinazione di un SOTTOINSIEME dei trade (distribuzione del valore atteso).
///
/// L'output chiave e' il draw down al 95esimo percentile della distribuzione: e' il livello
/// di guardia consigliato per lo spegnimento del sistema (tipicamente 1.5-2.5 volte il max
/// draw down storico). Deterministico a parita' di <see cref="MonteCarloConfig.Seed"/>.
/// </summary>
public sealed class MonteCarloAnalyzer
{
    public MonteCarloResult Run(IReadOnlyList<decimal> tradePnls, MonteCarloConfig config)
    {
        ArgumentNullException.ThrowIfNull(tradePnls);
        ArgumentNullException.ThrowIfNull(config);
        if (config.NumberOfShuffles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "NumberOfShuffles deve essere >= 1.");
        }
        if (config.OperationsPercent is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "OperationsPercent deve essere in (0, 100].");
        }

        if (tradePnls.Count == 0)
        {
            return new MonteCarloResult(); // nessun trade: nessuna distribuzione da stimare
        }

        var rng = config.Seed.HasValue ? new Random(config.Seed.Value) : new Random();

        // 1) Costi extra (round turn: 2 * costo) e 2) rumore proporzionale al trade.
        var baseline = new decimal[tradePnls.Count];
        for (var i = 0; i < tradePnls.Count; i++)
        {
            var pnl = tradePnls[i] - 2m * config.ExtraCostPerTrade;
            if (config.NoisePercent > 0m)
            {
                // factor casuale in [-1, 1]: aggiunge/toglie fino a NoisePercent% del valore nominale.
                var factor = (decimal)(rng.NextDouble() * 2.0 - 1.0);
                pnl += factor * (config.NoisePercent / 100m) * Math.Abs(tradePnls[i]);
            }
            baseline[i] = pnl;
        }

        var originalEquity = CumulativeEquity(baseline);
        var originalMaxDd = MaxDrawdown(originalEquity);

        // 3) Ricombinazioni: campiona una frazione dei trade e li rimescola.
        var sampleSize = Math.Max(1, (int)Math.Round(baseline.Length * config.OperationsPercent / 100m));
        var maxDrawdowns = new List<decimal>(config.NumberOfShuffles);
        decimal worstDd = originalMaxDd, bestDd = originalMaxDd;
        IReadOnlyList<decimal> worstEquity = originalEquity, bestEquity = originalEquity;

        for (var s = 0; s < config.NumberOfShuffles; s++)
        {
            var shuffled = config.SamplingMode == MonteCarloSamplingMode.StationaryBlock
                ? StationaryBlockSample(baseline, sampleSize, Math.Max(1, config.MeanBlockLength), rng)
                : SampleWithoutReplacement(baseline, sampleSize, rng);
            var equity = CumulativeEquity(shuffled);
            var dd = MaxDrawdown(equity);
            maxDrawdowns.Add(dd);
            if (dd > worstDd) { worstDd = dd; worstEquity = equity; }
            if (dd < bestDd) { bestDd = dd; bestEquity = equity; }
        }

        maxDrawdowns.Sort(); // crescente: l'ultimo 5% contiene i draw down peggiori.
        var dd95 = Optimization.TradeStatistics.Percentile(maxDrawdowns, 0.95m);

        return new MonteCarloResult
        {
            OriginalEquity = originalEquity,
            OriginalMaxDrawdown = originalMaxDd,
            WorstEquity = worstEquity,
            BestEquity = bestEquity,
            WorstMaxDrawdown = worstDd,
            BestMaxDrawdown = bestDd,
            MaxDrawdown95 = dd95,
            RiskFactor95 = originalMaxDd == 0m ? 0m : dd95 / originalMaxDd,
            RiskFactorWorst = originalMaxDd == 0m ? 0m : worstDd / originalMaxDd,
            SortedMaxDrawdowns = maxDrawdowns,
        };
    }

    /// <summary>
    /// [T1.5] Stationary block bootstrap: campiona CON reinserimento blocchi contigui di lunghezza
    /// geometrica media <paramref name="meanBlockLength"/>, con wrap-around a fine serie. A ogni
    /// passo il blocco prosegue con probabilità 1-1/L o ne inizia uno nuovo in un punto casuale.
    /// </summary>
    private static decimal[] StationaryBlockSample(decimal[] source, int size, int meanBlockLength, Random rng)
    {
        var result = new decimal[size];
        var pNewBlock = 1.0 / meanBlockLength;
        var idx = rng.Next(source.Length);
        for (var i = 0; i < size; i++)
        {
            result[i] = source[idx];
            idx = rng.NextDouble() < pNewBlock ? rng.Next(source.Length) : (idx + 1) % source.Length;
        }
        return result;
    }

    private static decimal[] SampleWithoutReplacement(decimal[] source, int size, Random rng)
    {
        // Fisher-Yates parziale: i primi "size" elementi di una permutazione casuale.
        var indices = new int[source.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        for (var i = 0; i < size; i++)
        {
            var j = rng.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var result = new decimal[size];
        for (var i = 0; i < size; i++) result[i] = source[indices[i]];
        return result;
    }

    private static decimal[] CumulativeEquity(IReadOnlyList<decimal> pnls)
    {
        var equity = new decimal[pnls.Count];
        decimal cum = 0m;
        for (var i = 0; i < pnls.Count; i++)
        {
            cum += pnls[i];
            equity[i] = cum;
        }
        return equity;
    }

    /// <summary>Max draw down monetario (valore positivo) di un'equity cumulata che parte da 0.</summary>
    private static decimal MaxDrawdown(IReadOnlyList<decimal> equity)
    {
        decimal peak = 0m, maxDd = 0m;
        foreach (var e in equity)
        {
            if (e > peak) peak = e;
            var dd = peak - e;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }
}

/// <summary>Come vengono ricombinati i trade a ogni shuffle.</summary>
public enum MonteCarloSamplingMode
{
    /// <summary>Permutazione iid (storico): ogni trade è indipendente. DISTRUGGE l'autocorrelazione.</summary>
    IidShuffle,

    /// <summary>
    /// [T1.5] Stationary block bootstrap (Politis–Romano): blocchi contigui di lunghezza geometrica
    /// media <see cref="MonteCarloConfig.MeanBlockLength"/>, con reinserimento e wrap-around.
    /// Preserva le SEQUENZE di trade (serie vincenti/perdenti consecutive) — cioè esattamente la
    /// struttura che produce i drawdown profondi e che lo shuffle iid spezza, sottostimandoli.
    /// </summary>
    StationaryBlock,
}

/// <summary>Parametri della Montecarlo Analysis evoluta.</summary>
public sealed record MonteCarloConfig
{
    /// <summary>Costo extra per lato imputato a ogni trade (applicato x2, round turn).</summary>
    public decimal ExtraCostPerTrade { get; init; }

    /// <summary>Rumore casuale max, in % del valore nominale del singolo trade (0 = disattivo).</summary>
    public decimal NoisePercent { get; init; }

    /// <summary>Percentuale dei trade da ricombinare a ogni shuffle (100 = tutti).</summary>
    public decimal OperationsPercent { get; init; } = 100m;

    public int NumberOfShuffles { get; init; } = 100;

    /// <summary>Seed per risultati riproducibili (null = casuale).</summary>
    public int? Seed { get; init; }

    /// <summary>Default <see cref="MonteCarloSamplingMode.IidShuffle"/> = comportamento storico invariato.</summary>
    public MonteCarloSamplingMode SamplingMode { get; init; } = MonteCarloSamplingMode.IidShuffle;

    /// <summary>Lunghezza media dei blocchi nel modo <see cref="MonteCarloSamplingMode.StationaryBlock"/>.</summary>
    public int MeanBlockLength { get; init; } = 10;
}

/// <summary>Esito della Montecarlo Analysis evoluta.</summary>
public sealed record MonteCarloResult
{
    public IReadOnlyList<decimal> OriginalEquity { get; init; } = [];
    public decimal OriginalMaxDrawdown { get; init; }

    public IReadOnlyList<decimal> WorstEquity { get; init; } = [];
    public IReadOnlyList<decimal> BestEquity { get; init; } = [];
    public decimal WorstMaxDrawdown { get; init; }
    public decimal BestMaxDrawdown { get; init; }

    /// <summary>95esimo percentile dei max draw down: livello di guardia consigliato.</summary>
    public decimal MaxDrawdown95 { get; init; }

    /// <summary>MaxDrawdown95 / draw down storico (atteso tipicamente tra 1.5 e 2.5).</summary>
    public decimal RiskFactor95 { get; init; }
    public decimal RiskFactorWorst { get; init; }

    /// <summary>Distribuzione ordinata (crescente) dei max draw down delle ricombinazioni.</summary>
    public IReadOnlyList<decimal> SortedMaxDrawdowns { get; init; } = [];
}
