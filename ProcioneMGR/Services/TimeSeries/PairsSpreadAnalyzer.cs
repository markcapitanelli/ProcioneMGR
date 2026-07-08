namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Esito dell'analisi dello spread di una coppia: cointegrazione, spread e z-score rolling.</summary>
public sealed class PairsSpreadAnalysis
{
    public required double HedgeRatio { get; init; }
    public required double Intercept { get; init; }
    public required double AdfStatistic { get; init; }
    public required bool IsCointegrated { get; init; }

    /// <summary>Spread Y-(α+β·X) per ogni osservazione (stesso hedge ratio per tutto il campione).</summary>
    public required IReadOnlyList<double> Spread { get; init; }

    /// <summary>Z-score rolling dello spread: null durante il warm-up della finestra. Segnale di trading candidato.</summary>
    public required IReadOnlyList<double?> ZScore { get; init; }
}

/// <summary>
/// Combina <see cref="ICointegrationTest"/> con uno z-score rolling dello spread (cap. 9): la
/// base statistica per il pairs trading. z alto -> spread anomalo in eccesso (Y "caro" rispetto
/// a X) -> short dello spread (Short Y / Long X in proporzione all'hedge ratio); z basso ->
/// simmetrico.
///
/// DEVIAZIONE FLAGGATA / semplificazione dichiarata: l'hedge ratio è stimato UNA VOLTA
/// sull'intero campione (Engle-Granger classico, adatto allo SCREENING di quali coppie sono
/// cointegrate). Lo z-score rolling è causale rispetto allo SPREAD (usa solo valori passati
/// della finestra), ma non rispetto all'hedge ratio stesso, che "vede" l'intero campione — non
/// è quindi ancora un segnale pronto per un backtest anti-look-ahead rigoroso. Un vero
/// <c>PairsTradingStrategy : IStrategy</c> backtestabile richiederebbe: (1) ristima rolling/
/// walk-forward dell'hedge ratio, (2) un motore di backtest esteso al multi-simbolo (oggi
/// single-symbol) — entrambi rimandati come passo architetturale a sé, non implementato qui.
/// </summary>
public sealed class PairsSpreadAnalyzer(ICointegrationTest cointegrationTest)
{
    public PairsSpreadAnalysis Analyze(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX, int zScoreLookback)
    {
        if (zScoreLookback < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(zScoreLookback), "La finestra dello z-score deve essere >= 3.");
        }

        var cointegration = cointegrationTest.Test(seriesY, seriesX);
        var spread = cointegration.Spread;
        var zScore = RollingZScore(spread, zScoreLookback);

        return new PairsSpreadAnalysis
        {
            HedgeRatio = cointegration.HedgeRatio,
            Intercept = cointegration.Intercept,
            AdfStatistic = cointegration.AdfStatistic,
            IsCointegrated = cointegration.IsCointegrated,
            Spread = spread,
            ZScore = zScore,
        };
    }

    /// <summary>Z-score causale: z[i] usa solo spread[i-lookback+1 .. i]. Null durante il warm-up.</summary>
    public static IReadOnlyList<double?> RollingZScore(IReadOnlyList<double> spread, int lookback)
    {
        var n = spread.Count;
        var result = new double?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            var start = i - lookback + 1;
            var mean = 0.0;
            for (var k = start; k <= i; k++) mean += spread[k];
            mean /= lookback;

            var variance = 0.0;
            for (var k = start; k <= i; k++)
            {
                var d = spread[k] - mean;
                variance += d * d;
            }
            variance /= lookback;
            var std = Math.Sqrt(variance);

            result[i] = std > 1e-12 ? (spread[i] - mean) / std : 0.0;
        }
        return result;
    }
}
