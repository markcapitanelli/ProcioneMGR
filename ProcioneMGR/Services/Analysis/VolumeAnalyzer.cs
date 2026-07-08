using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Interpretazione del volume come "grande confermatore" del trend (McAllen, cap. 15):
/// in un uptrend sano il volume e' piu' alto sulle barre in rialzo che su quelle in ribasso
/// (e viceversa nei downtrend). Quando i massimi vengono fatti a basso volume e i sell-off
/// ad alto volume, e' distribuzione: il trend non e' confermato e il segnale e' di allerta.
/// </summary>
public sealed class VolumeAnalyzer
{
    /// <summary>
    /// Conferma volumetrica su finestra scorrevole: per ciascuna barra (dalla finestra piena
    /// in poi) confronta il volume medio delle barre positive con quello delle negative
    /// nell'ultima finestra e lo incrocia con la direzione del prezzo nel medesimo periodo.
    /// </summary>
    public IReadOnlyList<VolumeConfirmation> ConfirmTrend(IReadOnlyList<OhlcvData> candles, int window = 20)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (window < 2) throw new ArgumentOutOfRangeException(nameof(window));

        var result = new List<VolumeConfirmation>();
        for (var i = window - 1; i < candles.Count; i++)
        {
            decimal upVolume = 0m, downVolume = 0m;
            int upBars = 0, downBars = 0;
            for (var j = i - window + 1; j <= i; j++)
            {
                var c = candles[j];
                if (c.Close > c.Open) { upVolume += c.Volume; upBars++; }
                else if (c.Close < c.Open) { downVolume += c.Volume; downBars++; }
            }

            var upAvg = upBars == 0 ? 0m : upVolume / upBars;
            var downAvg = downBars == 0 ? 0m : downVolume / downBars;

            var start = candles[i - window + 1].Close;
            var priceChange = start > 0m ? (candles[i].Close - start) / start * 100m : 0m;
            var rising = priceChange > 0m;

            // Il volume conferma se pesa dalla parte del movimento in corso.
            var confirmed = rising ? upAvg > downAvg : downAvg > upAvg;

            result.Add(new VolumeConfirmation(
                Index: i,
                Timestamp: candles[i].TimestampUtc,
                PriceChangePercent: priceChange,
                UpVolumeAvg: upAvg,
                DownVolumeAvg: downAvg,
                TrendConfirmed: confirmed,
                // Distribuzione/accumulazione: prezzo si muove ma il volume pesa dall'altra parte.
                DivergenceWarning: !confirmed && Math.Abs(priceChange) > 1m));
        }
        return result;
    }
}

/// <summary>Fotografia della conferma volumetrica su una finestra terminante alla barra Index.</summary>
public sealed record VolumeConfirmation(
    int Index,
    DateTime Timestamp,
    decimal PriceChangePercent,
    decimal UpVolumeAvg,
    decimal DownVolumeAvg,
    bool TrendConfirmed,
    bool DivergenceWarning);
