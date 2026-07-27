using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// Operazioni condivise sulle serie di spread con warm-up (null iniziali). Estratte da
/// <see cref="RollingPairsSpreadAnalyzer"/> quando è nato il secondo estimatore
/// (<see cref="KalmanPairsSpreadAnalyzer"/>, C2): i due DEVONO standardizzare lo spread nello
/// stesso identico modo, o l'A/B confronterebbe la definizione di z-score invece che l'estimatore
/// dell'hedge ratio.
/// </summary>
internal static class PairsSpreadSeries
{
    /// <summary>Z-score rolling causale su uno spread con warm-up (null iniziale): riusa la stessa finestra di <see cref="PairsSpreadAnalyzer.RollingZScore"/> sulla parte densa.</summary>
    internal static IReadOnlyList<double?> CausalZScore(double?[] spread, int lookback)
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
