namespace ProcioneMGR.Services.Alpha;

/// <summary>
/// Utilità numeriche condivise dai fattori. Tutto in <see cref="decimal"/> per coerenza con il
/// resto della piattaforma (prezzi esatti). Le finestre rolling calcolano il valore alla
/// candela i usando SOLO gli indici ≤ i (anti-look-ahead per costruzione).
/// </summary>
internal static class FactorMath
{
    /// <summary>Radice quadrata in decimal (Newton-Raphson), coerente con <c>Statistics.Sqrt</c>.</summary>
    public static decimal Sqrt(decimal value)
    {
        if (value <= 0m) return 0m;
        var guess = (decimal)Math.Sqrt((double)value);
        for (var i = 0; i < 12; i++)
        {
            if (guess == 0m) break;
            var next = (guess + value / guess) / 2m;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }

    /// <summary>Media semplice su una finestra <c>[start..end]</c> inclusi.</summary>
    public static decimal Mean(IReadOnlyList<decimal> values, int start, int end)
    {
        decimal sum = 0m;
        var n = 0;
        for (var i = start; i <= end; i++) { sum += values[i]; n++; }
        return n > 0 ? sum / n : 0m;
    }

    /// <summary>Deviazione standard di popolazione su <c>[start..end]</c> inclusi.</summary>
    public static decimal StdDev(IReadOnlyList<decimal> values, int start, int end)
    {
        var mean = Mean(values, start, end);
        decimal sumSq = 0m;
        var n = 0;
        for (var i = start; i <= end; i++) { var d = values[i] - mean; sumSq += d * d; n++; }
        if (n < 2) return 0m;
        return Sqrt(sumSq / n);
    }

    /// <summary>
    /// EMA seeded con SMA dei primi <paramref name="period"/> valori (stessa convenzione del
    /// <c>TechnicalIndicatorsService</c>). Restituisce una lista allineata: null in warm-up.
    /// </summary>
    public static List<decimal?> Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new List<decimal?>(new decimal?[values.Count]);
        if (period < 1 || values.Count < period) return result;

        var k = 2m / (period + 1m);
        decimal seed = 0m;
        for (var i = 0; i < period; i++) seed += values[i];
        seed /= period;

        decimal ema = seed;
        result[period - 1] = ema;
        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * k + ema;
            result[i] = ema;
        }
        return result;
    }

    /// <summary>
    /// RSI di Wilder. Primo valore calcolabile all'indice <paramref name="period"/>.
    /// Restituisce lista allineata (null in warm-up), valori in [0, 100].
    /// </summary>
    public static List<decimal?> WilderRsi(IReadOnlyList<decimal> closes, int period)
    {
        var result = new List<decimal?>(new decimal?[closes.Count]);
        if (period < 1 || closes.Count <= period) return result;

        decimal gainSum = 0m, lossSum = 0m;
        for (var i = 1; i <= period; i++)
        {
            var ch = closes[i] - closes[i - 1];
            if (ch >= 0m) gainSum += ch; else lossSum -= ch;
        }
        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        result[period] = Rsi(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            var ch = closes[i] - closes[i - 1];
            var gain = ch > 0m ? ch : 0m;
            var loss = ch < 0m ? -ch : 0m;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            result[i] = Rsi(avgGain, avgLoss);
        }
        return result;
    }

    private static decimal Rsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }
}
