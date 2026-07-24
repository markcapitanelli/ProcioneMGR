namespace ProcioneMGR.Services.Indicators;

/// <summary>
/// Implementazione stateless degli indicatori tecnici. Tutti gli algoritmi sono O(n)
/// (formula ricorsiva per le EMA, sliding window per SMA/deviazione standard).
/// Il calcolo e' sincrono ma esposto come Task per uniformita' API; la cancellazione
/// e' cooperativa (controllata periodicamente nei loop).
/// </summary>
public sealed class TechnicalIndicatorsService : ITechnicalIndicatorsService
{
    public Task<List<decimal?>> CalculateEmaAsync(List<decimal> values, int period, CancellationToken ct = default)
        => Task.FromResult(Ema(values, period, ct));

    public Task<List<decimal?>> CalculateRsiAsync(List<decimal> closes, int period = 14, CancellationToken ct = default)
        => Task.FromResult(Rsi(closes, period, ct));

    public List<decimal?> CalculateRsi(List<decimal> closes, int period = 14, CancellationToken ct = default)
        => Rsi(closes, period, ct);

    public Task<(List<decimal?> Macd, List<decimal?> Signal, List<decimal?> Histogram)> CalculateMacdAsync(
        List<decimal> closes, int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9, CancellationToken ct = default)
        => Task.FromResult(Macd(closes, fastPeriod, slowPeriod, signalPeriod, ct));

    public Task<(List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower)> CalculateBollingerAsync(
        List<decimal> closes, int period = 20, decimal stdDevMultiplier = 2.0m, CancellationToken ct = default)
        => Task.FromResult(Bollinger(closes, period, stdDevMultiplier, ct));

    public Task<List<decimal?>> CalculateSmaAsync(List<decimal> values, int period, CancellationToken ct = default)
        => Task.FromResult(Sma(values, period, ct));

    public Task<(List<decimal?> Upper, List<decimal?> Lower)> CalculateDonchianAsync(
        List<decimal> highs, List<decimal> lows, int period = 20, CancellationToken ct = default)
        => Task.FromResult(Donchian(highs, lows, period, ct));

    public Task<List<decimal?>> CalculateAtrAsync(
        List<decimal> highs, List<decimal> lows, List<decimal> closes, int period = 14, CancellationToken ct = default)
        => Task.FromResult(Atr(highs, lows, closes, period, ct));

    public Task<List<decimal?>> CalculateObvAsync(List<decimal> closes, List<decimal> volumes, CancellationToken ct = default)
        => Task.FromResult(Obv(closes, volumes, ct));

    public Task<List<decimal?>> CalculateMfiAsync(
        List<decimal> highs, List<decimal> lows, List<decimal> closes, List<decimal> volumes, int period = 14, CancellationToken ct = default)
        => Task.FromResult(Mfi(highs, lows, closes, volumes, period, ct));

    public Task<List<decimal?>> CalculateRollingVwapAsync(
        List<decimal> highs, List<decimal> lows, List<decimal> closes, List<decimal> volumes, int period = 20, CancellationToken ct = default)
        => Task.FromResult(RollingVwap(highs, lows, closes, volumes, period, ct));

    // ----------------------------------------------------------------- EMA

    internal static List<decimal?> Ema(IReadOnlyList<decimal> values, int period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        var result = NullList(values.Count);
        if (values.Count < period)
        {
            return result; // niente e' calcolabile
        }

        var k = 2m / (period + 1);

        // Seed: SMA dei primi `period` valori, posizionato all'indice period-1.
        decimal sum = 0m;
        for (var i = 0; i < period; i++)
        {
            sum += values[i];
        }
        var ema = sum / period;
        result[period - 1] = ema;

        // Formula ricorsiva: EMA_i = (price_i - EMA_{i-1}) * k + EMA_{i-1}
        for (var i = period; i < values.Count; i++)
        {
            ThrottleCancel(i, ct);
            ema = (values[i] - ema) * k + ema;
            result[i] = ema;
        }

        return result;
    }

    // ----------------------------------------------------------------- RSI (Wilder)

    internal static List<decimal?> Rsi(IReadOnlyList<decimal> closes, int period = 14, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        var n = closes.Count;
        var result = NullList(n);
        if (n <= period)
        {
            return result; // servono almeno `period` variazioni
        }

        // Media iniziale di gain/loss sulle prime `period` variazioni (indici 1..period).
        decimal gainSum = 0m, lossSum = 0m;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change >= 0) gainSum += change; else lossSum -= change;
        }
        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        result[period] = ToRsi(avgGain, avgLoss);

        // Smoothing di Wilder per i valori successivi.
        for (var i = period + 1; i < n; i++)
        {
            ThrottleCancel(i, ct);
            var change = closes[i] - closes[i - 1];
            var gain = change >= 0 ? change : 0m;
            var loss = change < 0 ? -change : 0m;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            result[i] = ToRsi(avgGain, avgLoss);
        }

        return result;
    }

    private static decimal ToRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m) return 100m;   // nessuna perdita -> forza massima
        if (avgGain == 0m) return 0m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }

    // ----------------------------------------------------------------- MACD

    internal static (List<decimal?> Macd, List<decimal?> Signal, List<decimal?> Histogram) Macd(
        IReadOnlyList<decimal> closes, int fastPeriod, int slowPeriod, int signalPeriod, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(fastPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slowPeriod, fastPeriod + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(signalPeriod, 1);

        var n = closes.Count;
        var fast = Ema(closes, fastPeriod, ct);
        var slow = Ema(closes, slowPeriod, ct);

        var macd = NullList(n);
        for (var i = 0; i < n; i++)
        {
            if (fast[i].HasValue && slow[i].HasValue)
            {
                macd[i] = fast[i]!.Value - slow[i]!.Value;
            }
        }

        // Signal = EMA della linea MACD, calcolata sulla sotto-serie non-null e ri-mappata.
        var signal = NullList(n);
        var firstIdx = macd.FindIndex(v => v.HasValue);
        if (firstIdx >= 0)
        {
            var dense = new List<decimal>(n - firstIdx);
            for (var i = firstIdx; i < n; i++)
            {
                dense.Add(macd[i]!.Value);
            }
            var denseSignal = Ema(dense, signalPeriod, ct);
            for (var j = 0; j < denseSignal.Count; j++)
            {
                signal[firstIdx + j] = denseSignal[j];
            }
        }

        var histogram = NullList(n);
        for (var i = 0; i < n; i++)
        {
            if (macd[i].HasValue && signal[i].HasValue)
            {
                histogram[i] = macd[i]!.Value - signal[i]!.Value;
            }
        }

        return (macd, signal, histogram);
    }

    // ----------------------------------------------------------------- Bollinger

    internal static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) Bollinger(
        IReadOnlyList<decimal> closes, int period, decimal stdDevMultiplier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        var n = closes.Count;
        var upper = NullList(n);
        var middle = NullList(n);
        var lower = NullList(n);
        if (n < period)
        {
            return (upper, middle, lower);
        }

        // Sliding window: somma e somma dei quadrati mantenute in O(1) per passo.
        decimal sum = 0m, sumSq = 0m;
        for (var i = 0; i < period; i++)
        {
            sum += closes[i];
            sumSq += closes[i] * closes[i];
        }

        for (var i = period - 1; i < n; i++)
        {
            ThrottleCancel(i, ct);
            if (i >= period)
            {
                var dropped = closes[i - period];
                var added = closes[i];
                sum += added - dropped;
                sumSq += added * added - dropped * dropped;
            }

            var mean = sum / period;
            var variance = sumSq / period - mean * mean;
            if (variance < 0m) variance = 0m; // protezione da arrotondamenti
            var std = Sqrt(variance);

            middle[i] = mean;
            upper[i] = mean + stdDevMultiplier * std;
            lower[i] = mean - stdDevMultiplier * std;
        }

        return (upper, middle, lower);
    }

    // ----------------------------------------------------------------- SMA

    internal static List<decimal?> Sma(IReadOnlyList<decimal> values, int period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        var n = values.Count;
        var result = NullList(n);
        if (n < period)
        {
            return result;
        }

        decimal sum = 0m;
        for (var i = 0; i < period; i++)
        {
            sum += values[i];
        }
        result[period - 1] = sum / period;

        for (var i = period; i < n; i++)
        {
            ThrottleCancel(i, ct);
            sum += values[i] - values[i - period];
            result[i] = sum / period;
        }

        return result;
    }

    // ----------------------------------------------------------------- Donchian (HHV/LLV)

    internal static (List<decimal?> Upper, List<decimal?> Lower) Donchian(
        IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, int period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        if (highs.Count != lows.Count)
        {
            throw new ArgumentException("Le serie highs e lows devono avere la stessa lunghezza.");
        }

        var n = highs.Count;
        var upper = NullList(n);
        var lower = NullList(n);

        // Rolling max/min in O(n) con deque monotone (indici).
        var maxDeque = new LinkedList<int>();
        var minDeque = new LinkedList<int>();

        for (var i = 0; i < n; i++)
        {
            ThrottleCancel(i, ct);

            while (maxDeque.Count > 0 && highs[maxDeque.Last!.Value] <= highs[i]) maxDeque.RemoveLast();
            maxDeque.AddLast(i);
            while (minDeque.Count > 0 && lows[minDeque.Last!.Value] >= lows[i]) minDeque.RemoveLast();
            minDeque.AddLast(i);

            var windowStart = i - period + 1;
            if (maxDeque.First!.Value < windowStart) maxDeque.RemoveFirst();
            if (minDeque.First!.Value < windowStart) minDeque.RemoveFirst();

            if (i >= period - 1)
            {
                upper[i] = highs[maxDeque.First!.Value];
                lower[i] = lows[minDeque.First!.Value];
            }
        }

        return (upper, lower);
    }

    // ----------------------------------------------------------------- ATR (Wilder)

    internal static List<decimal?> Atr(
        IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        if (highs.Count != lows.Count || highs.Count != closes.Count)
        {
            throw new ArgumentException("Le serie highs/lows/closes devono avere la stessa lunghezza.");
        }

        var n = highs.Count;
        var result = NullList(n);
        if (n <= period)
        {
            return result; // servono almeno `period` True Range (da indice 1)
        }

        // True Range da indice 1 (serve la close precedente).
        static decimal TrueRange(decimal high, decimal low, decimal prevClose)
        {
            var a = high - low;
            var b = Math.Abs(high - prevClose);
            var c = Math.Abs(low - prevClose);
            return Math.Max(a, Math.Max(b, c));
        }

        // Seed: media dei primi `period` TR (indici 1..period), posizionato all'indice period.
        decimal trSum = 0m;
        for (var i = 1; i <= period; i++)
        {
            trSum += TrueRange(highs[i], lows[i], closes[i - 1]);
        }
        var atr = trSum / period;
        result[period] = atr;

        // Smoothing di Wilder: ATR_i = (ATR_{i-1}*(period-1) + TR_i) / period
        for (var i = period + 1; i < n; i++)
        {
            ThrottleCancel(i, ct);
            var tr = TrueRange(highs[i], lows[i], closes[i - 1]);
            atr = (atr * (period - 1) + tr) / period;
            result[i] = atr;
        }

        return result;
    }

    // ----------------------------------------------------------------- OBV / MFI / VWAP (3.8a)

    /// <summary>
    /// On-Balance Volume: somma cumulata del volume col segno della variazione di prezzo.
    /// Non-null da indice 0 (OBV[0]=0). Scala arbitraria: chi lo consuma guardi la VARIAZIONE.
    /// </summary>
    internal static List<decimal?> Obv(IReadOnlyList<decimal> closes, IReadOnlyList<decimal> volumes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(volumes);
        if (closes.Count != volumes.Count)
        {
            throw new ArgumentException("Le serie closes/volumes devono avere la stessa lunghezza.");
        }

        var n = closes.Count;
        var result = NullList(n);
        if (n == 0) return result;

        decimal obv = 0m;
        result[0] = 0m;
        for (var i = 1; i < n; i++)
        {
            ThrottleCancel(i, ct);
            if (closes[i] > closes[i - 1]) obv += volumes[i];
            else if (closes[i] < closes[i - 1]) obv -= volumes[i];
            result[i] = obv;
        }
        return result;
    }

    /// <summary>
    /// Money Flow Index: RSI pesato per volume sul typical price (H+L+C)/3, nativo 0-100.
    /// Primo valore all'indice <paramref name="period"/> (servono `period` variazioni di typical price).
    /// </summary>
    internal static List<decimal?> Mfi(
        IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> volumes, int period = 14, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        if (highs.Count != lows.Count || highs.Count != closes.Count || highs.Count != volumes.Count)
        {
            throw new ArgumentException("Le serie highs/lows/closes/volumes devono avere la stessa lunghezza.");
        }

        var n = closes.Count;
        var result = NullList(n);
        if (n <= period) return result;

        // Flussi firmati per barra (da indice 1: serve il typical price precedente).
        var flow = new decimal[n]; // >0 = flusso positivo, <0 = negativo, 0 = typical invariato
        var prevTp = (highs[0] + lows[0] + closes[0]) / 3m;
        for (var i = 1; i < n; i++)
        {
            var tp = (highs[i] + lows[i] + closes[i]) / 3m;
            var raw = tp * volumes[i];
            flow[i] = tp > prevTp ? raw : tp < prevTp ? -raw : 0m;
            prevTp = tp;
        }

        // Finestra scorrevole di somme positive/negative su `period` barre.
        decimal pos = 0m, neg = 0m;
        for (var i = 1; i <= period; i++)
        {
            if (flow[i] > 0m) pos += flow[i]; else neg -= flow[i];
        }
        result[period] = ToMfi(pos, neg);
        for (var i = period + 1; i < n; i++)
        {
            ThrottleCancel(i, ct);
            var leaving = flow[i - period];
            if (leaving > 0m) pos -= leaving; else neg += leaving;
            if (flow[i] > 0m) pos += flow[i]; else neg -= flow[i];
            result[i] = ToMfi(pos, neg);
        }
        return result;
    }

    private static decimal ToMfi(decimal positiveFlow, decimal negativeFlow)
    {
        // Forma 100·pos/(pos+neg), matematicamente identica a 100 − 100/(1 + pos/neg) ma senza il
        // rapporto pos/neg: con neg microscopico (BNB 1h, barra da volume quasi nullo) quel
        // rapporto supera il range di Decimal e VarDecDiv lancia OverflowException — trovato dal
        // vivo, non dai test. Qui il quoziente è ≤ 1 per costruzione: l'overflow è impossibile.
        var total = positiveFlow + negativeFlow;
        if (total <= 0m) return 50m; // nessun flusso nella finestra (prezzi piatti): neutro dichiarato
        return 100m * positiveFlow / total;
    }

    /// <summary>
    /// VWAP ROLLING sulle ultime <paramref name="period"/> barre: Σ(typical·vol)/Σ(vol) a finestra
    /// scorrevole. Complementare al VWAP ancorato alla sessione UTC che vive nel SignalCatalog (id 5):
    /// questo è il riusabile senza ancora. Null nel warm-up o quando il volume della finestra è 0.
    /// </summary>
    internal static List<decimal?> RollingVwap(
        IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> volumes, int period = 20, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        if (highs.Count != lows.Count || highs.Count != closes.Count || highs.Count != volumes.Count)
        {
            throw new ArgumentException("Le serie highs/lows/closes/volumes devono avere la stessa lunghezza.");
        }

        var n = closes.Count;
        var result = NullList(n);
        decimal pvSum = 0m, vSum = 0m;
        for (var i = 0; i < n; i++)
        {
            ThrottleCancel(i, ct);
            var tp = (highs[i] + lows[i] + closes[i]) / 3m;
            pvSum += tp * volumes[i];
            vSum += volumes[i];
            if (i >= period)
            {
                var tpOut = (highs[i - period] + lows[i - period] + closes[i - period]) / 3m;
                pvSum -= tpOut * volumes[i - period];
                vSum -= volumes[i - period];
            }
            if (i >= period - 1 && vSum > 0m)
            {
                result[i] = pvSum / vSum;
            }
        }
        return result;
    }

    // ----------------------------------------------------------------- helpers

    private static List<decimal?> NullList(int count)
    {
        var list = new List<decimal?>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(null);
        }
        return list;
    }

    private static void ThrottleCancel(int i, CancellationToken ct)
    {
        if ((i & 1023) == 0)
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Radice quadrata in <c>decimal</c> (Newton-Raphson) per non perdere precisione.</summary>
    internal static decimal Sqrt(decimal value)
    {
        if (value < 0m) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0m) return 0m;

        var guess = (decimal)Math.Sqrt((double)value); // stima iniziale
        for (var i = 0; i < 12; i++)
        {
            if (guess == 0m) break;
            var next = (guess + value / guess) / 2m;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }
}
