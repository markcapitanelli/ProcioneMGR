using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Validation;

/// <summary>
/// Verdetto del giudice del gemello nullo: la distribuzione dello Sharpe su N mercati nulli
/// (<see cref="NullTwinGenerator"/>) e la posizione del risultato REALE dentro di essa.
/// <c>Passed</c> = il reale supera il quantile richiesto (default 99°): tutto il resto è
/// selezione, non edge.
/// </summary>
public sealed record NullTwinVerdict
{
    /// <summary>Gemelli il cui backtest è andato a buon fine (i falliti sono esclusi dalla distribuzione).</summary>
    public required int ValidTwins { get; init; }
    public required decimal RealSharpe { get; init; }
    public required decimal Median { get; init; }
    public required decimal P90 { get; init; }
    public required decimal P95 { get; init; }
    public required decimal P99 { get; init; }
    public required decimal Max { get; init; }
    /// <summary>Quantile richiesto (es. 0,99) espresso come frazione.</summary>
    public required double RequiredPercentile { get; init; }
    /// <summary>Il valore del quantile richiesto nella distribuzione nulla: la soglia da battere.</summary>
    public required decimal Threshold { get; init; }
    /// <summary>Percentile (0-100) occupato dal reale nella distribuzione nulla.</summary>
    public required double PercentileOfReal { get; init; }
    /// <summary>Quota di gemelli con Sharpe ≥ reale (p-value empirico a una coda).</summary>
    public required double EmpiricalPValue { get; init; }
    public required bool Passed { get; init; }
}

/// <summary>
/// [A1 roadmap integrazione] Il giudice del gemello nullo, UNIFICATO: unico punto in cui si decide
/// quanti gemelli servono e quale quantile va battuto. Nato dal bug del 2026-07-25: i tool CLI
/// usavano 15 gemelli e la soglia al 95° — con quindici campioni il "95° percentile" coincide quasi
/// col massimo osservato, quindi la soglia era essa stessa rumore, e un falso positivo (SEI/USDT)
/// l'ha superata. Con ~15.000 combinazioni provate, un test al 95% lascia passare il 5% del rumore
/// per costruzione: serve il 99° su una distribuzione stimata come si deve (200 gemelli).
/// I default di questa classe SONO la policy; chi giudica con parametri più deboli deve motivarlo
/// nel punto di chiamata.
/// </summary>
public interface INullTwinJudge
{
    /// <summary>
    /// Giudica <paramref name="realSharpe"/> contro la distribuzione nulla costruita generando
    /// <paramref name="twins"/> gemelli di <paramref name="realCandles"/> e ribattezzandoli con lo
    /// STESSO motore e la STESSA configurazione (costi inclusi) del backtest reale.
    /// Null se i gemelli validi sono meno della metà dei richiesti: un giudice che non può
    /// giudicare non deve né bocciare né promuovere.
    /// </summary>
    Task<NullTwinVerdict?> JudgeAsync(
        BacktestConfiguration config,
        IReadOnlyList<OhlcvData> realCandles,
        decimal realSharpe,
        int twins = NullTwinJudge.DefaultTwins,
        double requiredPercentile = NullTwinJudge.DefaultRequiredPercentile,
        int seedBase = 2000,
        double meanBlockLength = 24,
        CancellationToken ct = default);
}

/// <inheritdoc cref="INullTwinJudge"/>
public sealed class NullTwinJudge(IBacktestEngine engine) : INullTwinJudge
{
    public const int DefaultTwins = 200;
    public const double DefaultRequiredPercentile = 0.99;

    public async Task<NullTwinVerdict?> JudgeAsync(
        BacktestConfiguration config,
        IReadOnlyList<OhlcvData> realCandles,
        decimal realSharpe,
        int twins = DefaultTwins,
        double requiredPercentile = DefaultRequiredPercentile,
        int seedBase = 2000,
        double meanBlockLength = 24,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(realCandles);
        if (twins < 1) throw new ArgumentOutOfRangeException(nameof(twins));

        var ppy = Statistics.PeriodsPerYear(config.Timeframe);
        var nulls = new List<decimal>(twins);
        for (var t = 0; t < twins; t++)
        {
            ct.ThrowIfCancellationRequested();
            var twin = NullTwinGenerator.Generate(realCandles, seed: seedBase + t, meanBlockLength);
            try
            {
                var result = await engine.RunBacktestAsync(config, twin, ct);
                nulls.Add(Statistics.SharpeRatio(result.EquityCurve, ppy));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Un gemello può produrre una serie su cui la strategia non genera trade o il
                // motore fallisce: si esclude dalla distribuzione, e conta il totale dei validi.
            }
        }

        return Evaluate(realSharpe, nulls, requiredPercentile, minValidTwins: twins / 2);
    }

    /// <summary>
    /// Il cuore condiviso del verdetto (statico e senza motore: i tool che generano i gemelli in
    /// proprio — es. il pairs, che rende nulle le DUE gambe — devono comunque giudicare QUI).
    /// Ordina la distribuzione, ne estrae i quantili con la convenzione ceil(q·n)−1 e confronta.
    /// Null se i validi sono meno di <paramref name="minValidTwins"/>.
    /// </summary>
    public static NullTwinVerdict? Evaluate(
        decimal realSharpe,
        List<decimal> nullSharpes,
        double requiredPercentile = DefaultRequiredPercentile,
        int minValidTwins = DefaultTwins / 2)
    {
        ArgumentNullException.ThrowIfNull(nullSharpes);
        if (requiredPercentile is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(requiredPercentile));
        if (nullSharpes.Count < Math.Max(1, minValidTwins)) return null;

        nullSharpes.Sort();
        var n = nullSharpes.Count;
        decimal Quantile(double q) => nullSharpes[(int)Math.Min(n - 1, Math.Max(0, Math.Ceiling(q * n) - 1))];

        var threshold = Quantile(requiredPercentile);
        var beaten = nullSharpes.Count(s => s < realSharpe);
        return new NullTwinVerdict
        {
            ValidTwins = n,
            RealSharpe = realSharpe,
            Median = Quantile(0.50),
            P90 = Quantile(0.90),
            P95 = Quantile(0.95),
            P99 = Quantile(0.99),
            Max = nullSharpes[n - 1],
            RequiredPercentile = requiredPercentile,
            Threshold = threshold,
            PercentileOfReal = 100.0 * beaten / n,
            EmpiricalPValue = (double)(n - beaten) / n,
            Passed = realSharpe > threshold,
        };
    }
}
