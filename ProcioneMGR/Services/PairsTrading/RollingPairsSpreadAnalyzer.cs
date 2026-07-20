using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Services.PairsTrading;

// NOTA: a differenza di PairsSpreadAnalyzer (Fase C, screening full-sample), qui serve solo la
// regressione OLS per l'hedge ratio ad ogni ricalibrazione, non l'intero test ADF di
// cointegrazione (costerebbe una regressione ausiliaria ad ogni finestra, inutile per il segnale
// di trading) — nessuna dipendenza da ICointegrationTest.

/// <summary>Esito dell'analisi rolling dello spread: hedge ratio, spread e z-score, allineati per indice, con null durante il warm-up.</summary>
public sealed class RollingPairsAnalysis
{
    public required IReadOnlyList<double?> HedgeRatio { get; init; }
    public required IReadOnlyList<double?> Spread { get; init; }
    public required IReadOnlyList<double?> ZScore { get; init; }
}

/// <summary>
/// A differenza di <see cref="PairsSpreadAnalyzer"/> (hedge ratio stimato una volta sull'intero
/// campione, adatto solo allo SCREENING di quali coppie sono cointegrate), questa versione
/// ristima l'hedge ratio periodicamente in modo <b>rolling/walk-forward</b>: ogni
/// <paramref name="recalibrationInterval"/> barre, la regressione Y~X viene rifatta usando SOLO
/// le <paramref name="lookbackWindow"/> osservazioni PASSATE (mai quelle future) — il risultato è
/// anti-look-ahead corretto ed è quello che rende <see cref="IPairsBacktestEngine"/> un backtest
/// vero, non solo uno screening statistico.
///
/// Come <see cref="EngleGrangerCointegrationTest"/>, la regressione gira sui LOG dei prezzi: le due
/// DEVONO usare la stessa specificazione, altrimenti lo screening dichiara cointegrata una
/// combinazione e il backtest ne negozia un'altra. Di conseguenza lo spread qui è un
/// <b>log-spread</b> — adimensionale, e confrontabile fra coppie con prezzi di scala diversa, cosa
/// che lo spread in unità di prezzo non era (il suo z-score dipendeva dal livello del prezzo di X).
/// </summary>
public sealed class RollingPairsSpreadAnalyzer
{
    public RollingPairsAnalysis Analyze(
        IReadOnlyList<decimal> seriesY,
        IReadOnlyList<decimal> seriesX,
        int lookbackWindow,
        int recalibrationInterval,
        int zScoreLookback)
    {
        ArgumentNullException.ThrowIfNull(seriesY);
        ArgumentNullException.ThrowIfNull(seriesX);
        if (seriesY.Count != seriesX.Count)
        {
            throw new ArgumentException("Le due serie devono avere la stessa lunghezza (allineate per timestamp).", nameof(seriesX));
        }
        if (lookbackWindow < 10) throw new ArgumentOutOfRangeException(nameof(lookbackWindow), "Servono almeno 10 osservazioni per stimare un hedge ratio.");
        if (recalibrationInterval < 1) throw new ArgumentOutOfRangeException(nameof(recalibrationInterval));
        if (zScoreLookback < 3) throw new ArgumentOutOfRangeException(nameof(zScoreLookback));

        var n = seriesY.Count;
        var hedgeRatio = new double?[n];
        var spread = new double?[n];

        double? beta = null;
        double? alpha = null;

        for (var i = 0; i < n; i++)
        {
            var isRecalibrationPoint = i >= lookbackWindow && (i - lookbackWindow) % recalibrationInterval == 0;
            if (beta is null || isRecalibrationPoint)
            {
                if (i >= lookbackWindow)
                {
                    // Finestra [i-lookbackWindow, i-1]: SOLO dati passati, mai il presente/futuro.
                    var (a, b) = FitHedgeRatio(seriesY, seriesX, i - lookbackWindow, i - 1);
                    alpha = a;
                    beta = b;
                }
            }

            if (beta is not null)
            {
                hedgeRatio[i] = beta;
                spread[i] = Log(seriesY[i]) - (alpha!.Value + beta.Value * Log(seriesX[i]));
            }
        }

        var zScore = ComputeCausalZScore(spread, zScoreLookback);
        return new RollingPairsAnalysis { HedgeRatio = hedgeRatio, Spread = spread, ZScore = zScore };
    }

    private static (double Alpha, double Beta) FitHedgeRatio(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX, int start, int endInclusive)
    {
        var count = endInclusive - start + 1;
        var y = Vector<double>.Build.Dense(count, k => Log(seriesY[start + k]));
        var design = Matrix<double>.Build.Dense(count, 2, (row, col) => col == 0 ? 1.0 : Log(seriesX[start + row]));
        var ols = OlsRegression.Fit(design, y);
        return (ols.Coefficients[0], ols.Coefficients[1]);
    }

    /// <summary>Log del prezzo. Un prezzo non positivo non esiste in un OHLCV sano: meglio fermarsi che propagare -Infinity.</summary>
    private static double Log(decimal price)
        => price > 0m
            ? Math.Log((double)price)
            : throw new ArgumentException($"Prezzo non positivo ({price}): il log-spread richiede prezzi strettamente positivi.");

    /// <summary>Z-score rolling causale su uno spread con warm-up (null iniziale): riusa la stessa finestra di <see cref="PairsSpreadAnalyzer.RollingZScore"/> sulla parte densa.</summary>
    private static IReadOnlyList<double?> ComputeCausalZScore(double?[] spread, int lookback)
    {
        var n = spread.Length;
        var firstValid = Array.FindIndex(spread, v => v.HasValue);
        if (firstValid < 0) return new double?[n];

        var dense = spread.Skip(firstValid).Select(v => v!.Value).ToList();
        var denseZ = PairsSpreadAnalyzer.RollingZScore(dense, lookback);

        var result = new double?[n];
        for (var k = 0; k < denseZ.Count; k++)
        {
            result[firstValid + k] = denseZ[k];
        }
        return result;
    }
}
