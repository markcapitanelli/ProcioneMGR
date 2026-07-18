namespace ProcioneMGR.Services.TimeSeries;

/// <summary>
/// Z-score rolling causale di uno spread (cap. 9): la base statistica del pairs trading. z alto ->
/// spread anomalo in eccesso (Y "caro" rispetto a X) -> short dello spread; z basso -> simmetrico.
/// Il calcolo usa solo valori passati della finestra (causale, anti-look-ahead).
///
/// Unico consumatore: <see cref="ProcioneMGR.Services.PairsTrading.RollingPairsSpreadAnalyzer"/>,
/// che ristima l'hedge ratio in walk-forward e riusa questa finestra sulla parte densa dello spread.
/// Lo screening full-sample (hedge ratio stimato una volta sull'intero campione, con test di
/// cointegrazione) vive invece direttamente in <see cref="ICointegrationTest"/>: non serve più un
/// wrapper istanza dedicato (era codice morto, mai risolto da DI).
/// </summary>
public static class PairsSpreadAnalyzer
{
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
