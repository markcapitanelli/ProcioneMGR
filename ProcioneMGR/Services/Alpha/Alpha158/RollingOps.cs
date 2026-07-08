using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha.Alpha158;

/// <summary>
/// Serie di prezzo/volume estratte una sola volta da uno storico di candele, in
/// <see cref="decimal"/> (coerente con il resto della piattaforma), pronte per gli operatori
/// rolling di <see cref="RollingOps"/>. Tutte le colonne hanno la stessa lunghezza dell'input.
/// </summary>
public readonly struct Bars
{
    public decimal[] Open { get; }
    public decimal[] High { get; }
    public decimal[] Low { get; }
    public decimal[] Close { get; }
    public decimal[] Volume { get; }
    public int Count => Close.Length;

    public Bars(IReadOnlyList<OhlcvData> candles)
    {
        var n = candles.Count;
        Open = new decimal[n];
        High = new decimal[n];
        Low = new decimal[n];
        Close = new decimal[n];
        Volume = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            Open[i] = c.Open;
            High[i] = c.High;
            Low[i] = c.Low;
            Close[i] = c.Close;
            Volume[i] = c.Volume;
        }
    }
}

/// <summary>
/// Operatori rolling <b>causali</b> in stile Alpha158 di Qlib, reimplementati in C#/decimal.
///
/// INVARIANTE ANTI-LOOK-AHEAD (identico a <see cref="IAlphaFactor"/>): il valore all'indice
/// <c>i</c> dipende ESCLUSIVAMENTE dalla finestra che termina a <c>i</c> (indici ≤ i). Ogni
/// metodo restituisce una serie allineata all'input (stessa lunghezza), con <c>null</c> nel
/// warm-up o dove il valore non è calcolabile (divisione per zero, dati insufficienti).
///
/// Molti operatori sono normalizzati sul prezzo/volume corrente per renderli comparabili fra
/// simboli e regimi (es. <c>Ma = SMA(close,d)/close</c>), esattamente come in Alpha158.
/// </summary>
public static class RollingOps
{
    private const decimal Eps = 0.000000000001m; // 1e-12: stabilizzatore dei denominatori (come Qlib)

    // === Operatori sul prezzo ================================================================

    /// <summary>ROC: rapporto prezzo di d periodi fa / prezzo corrente (Qlib: Ref($close,d)/$close).</summary>
    public static decimal?[] Roc(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d; i < n; i++)
        {
            if (close[i] > 0m) r[i] = close[i - d] / close[i];
        }
        return r;
    }

    /// <summary>MA: media mobile del prezzo, normalizzata sul prezzo corrente.</summary>
    public static decimal?[] Ma(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            r[i] = FactorMath.Mean(close, i - d + 1, i) / close[i];
        }
        return r;
    }

    /// <summary>STD: deviazione standard del prezzo, normalizzata sul prezzo corrente.</summary>
    public static decimal?[] Std(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            r[i] = FactorMath.StdDev(close, i - d + 1, i) / close[i];
        }
        return r;
    }

    /// <summary>BETA: pendenza (slope) della regressione lineare del prezzo su d barre, /prezzo.</summary>
    public static decimal?[] Beta(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 2) return r;
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            var (slope, _, _) = Ols(close, i - d + 1, d);
            r[i] = slope / close[i];
        }
        return r;
    }

    /// <summary>RSQR: R² della regressione lineare del prezzo su d barre (bontà del trend, 0..1).</summary>
    public static decimal?[] Rsqr(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 2) return r;
        for (var i = d - 1; i < n; i++)
        {
            r[i] = OlsRSquared(close, i - d + 1, d);
        }
        return r;
    }

    /// <summary>RESI: residuo della regressione lineare all'ultimo punto, normalizzato sul prezzo.</summary>
    public static decimal?[] Resi(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 2) return r;
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            var (slope, intercept, _) = Ols(close, i - d + 1, d);
            var fitted = intercept + slope * (d - 1); // x dell'ultimo punto della finestra
            r[i] = (close[i] - fitted) / close[i];
        }
        return r;
    }

    /// <summary>MAX: massimo dei massimi su d barre, normalizzato sul prezzo corrente.</summary>
    public static decimal?[] Max(decimal[] high, decimal[] close, int d)
    {
        var n = high.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            r[i] = WindowMax(high, i - d + 1, i) / close[i];
        }
        return r;
    }

    /// <summary>MIN: minimo dei minimi su d barre, normalizzato sul prezzo corrente.</summary>
    public static decimal?[] Min(decimal[] low, decimal[] close, int d)
    {
        var n = low.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            r[i] = WindowMin(low, i - d + 1, i) / close[i];
        }
        return r;
    }

    /// <summary>QTLU: quantile alto (0.8) del prezzo su d barre, normalizzato sul prezzo corrente.</summary>
    public static decimal?[] Qtlu(decimal[] close, int d) => Quantile(close, d, 0.8m);

    /// <summary>QTLD: quantile basso (0.2) del prezzo su d barre, normalizzato sul prezzo corrente.</summary>
    public static decimal?[] Qtld(decimal[] close, int d) => Quantile(close, d, 0.2m);

    private static decimal?[] Quantile(decimal[] close, int d, decimal q)
    {
        var n = close.Length;
        var r = new decimal?[n];
        var buffer = new decimal[d];
        for (var i = d - 1; i < n; i++)
        {
            if (close[i] <= 0m) continue;
            Array.Copy(close, i - d + 1, buffer, 0, d);
            Array.Sort(buffer);
            r[i] = QuantileSorted(buffer, q) / close[i];
        }
        return r;
    }

    /// <summary>RANK: rango percentile causale del prezzo corrente nella finestra di d barre (0..1).</summary>
    public static decimal?[] Rank(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            var cur = close[i];
            var below = 0;
            for (var j = i - d + 1; j <= i; j++) if (close[j] <= cur) below++;
            r[i] = (decimal)below / d;
        }
        return r;
    }

    /// <summary>RSV: posizione stocastica del prezzo nel range [min-low, max-high] su d barre (0..1).</summary>
    public static decimal?[] Rsv(decimal[] high, decimal[] low, decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            var hi = WindowMax(high, i - d + 1, i);
            var lo = WindowMin(low, i - d + 1, i);
            var range = hi - lo;
            if (range <= 0m) continue;
            r[i] = (close[i] - lo) / range;
        }
        return r;
    }

    /// <summary>IMAX: recenza del massimo (posizione 0=più vecchia .. d-1=più recente) /d.</summary>
    public static decimal?[] Imax(decimal[] high, int d) => IdxExtreme(high, d, max: true);

    /// <summary>IMIN: recenza del minimo (posizione 0=più vecchia .. d-1=più recente) /d.</summary>
    public static decimal?[] Imin(decimal[] low, int d) => IdxExtreme(low, d, max: false);

    /// <summary>IMXD: differenza fra recenza del massimo e del minimo (-1..1).</summary>
    public static decimal?[] Imxd(decimal[] high, decimal[] low, int d)
    {
        var n = high.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            r[i] = (decimal)(LocalArg(high, i - d + 1, i, max: true) - LocalArg(low, i - d + 1, i, max: false)) / d;
        }
        return r;
    }

    private static decimal?[] IdxExtreme(decimal[] src, int d, bool max)
    {
        var n = src.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            r[i] = (decimal)LocalArg(src, i - d + 1, i, max) / d;
        }
        return r;
    }

    // === Correlazioni prezzo-volume ==========================================================

    /// <summary>CORR: correlazione (Pearson) fra prezzo e log(volume) su d barre.</summary>
    public static decimal?[] Corr(decimal[] close, decimal[] volume, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 3) return r;
        var xs = new double[d];
        var ys = new double[d];
        for (var i = d - 1; i < n; i++)
        {
            for (var k = 0; k < d; k++)
            {
                var j = i - d + 1 + k;
                xs[k] = (double)close[j];
                ys[k] = Math.Log(1.0 + (double)Math.Max(0m, volume[j]));
            }
            r[i] = (decimal)Correlation.Pearson(xs, ys);
        }
        return r;
    }

    /// <summary>CORD: correlazione fra variazione di prezzo e variazione di log-volume su d barre.</summary>
    public static decimal?[] Cord(decimal[] close, decimal[] volume, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 3) return r;
        var xs = new double[d];
        var ys = new double[d];
        for (var i = d; i < n; i++)
        {
            for (var k = 0; k < d; k++)
            {
                var j = i - d + 1 + k;
                var pc = close[j - 1] > 0m ? (double)(close[j] / close[j - 1]) : 1.0;
                var pv = volume[j - 1] > 0m ? (double)(volume[j] / volume[j - 1]) : 1.0;
                xs[k] = pc;
                ys[k] = Math.Log(1.0 + Math.Max(0.0, pv));
            }
            r[i] = (decimal)Correlation.Pearson(xs, ys);
        }
        return r;
    }

    // === Conteggi e somme direzionali del prezzo =============================================

    /// <summary>CNTP: frazione di barre in salita su d variazioni (0..1).</summary>
    public static decimal?[] Cntp(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d; i < n; i++)
        {
            var up = 0;
            for (var j = i - d + 1; j <= i; j++) if (close[j] > close[j - 1]) up++;
            r[i] = (decimal)up / d;
        }
        return r;
    }

    /// <summary>CNTN: frazione di barre in discesa su d variazioni (0..1).</summary>
    public static decimal?[] Cntn(decimal[] close, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        for (var i = d; i < n; i++)
        {
            var dn = 0;
            for (var j = i - d + 1; j <= i; j++) if (close[j] < close[j - 1]) dn++;
            r[i] = (decimal)dn / d;
        }
        return r;
    }

    /// <summary>CNTD: differenza fra frazione in salita e in discesa (-1..1).</summary>
    public static decimal?[] Cntd(decimal[] close, int d) => Combine(Cntp(close, d), Cntn(close, d), (a, b) => a - b);

    /// <summary>SUMP: quota di guadagno sul movimento assorbito totale (RSI-like, 0..1).</summary>
    public static decimal?[] Sump(decimal[] close, int d) => DirectionalSum(close, d, positive: true);

    /// <summary>SUMN: quota di perdita sul movimento assoluto totale (0..1).</summary>
    public static decimal?[] Sumn(decimal[] close, int d) => DirectionalSum(close, d, positive: false);

    /// <summary>SUMD: differenza fra quota di guadagno e di perdita (-1..1).</summary>
    public static decimal?[] Sumd(decimal[] close, int d) => Combine(Sump(close, d), Sumn(close, d), (a, b) => a - b);

    private static decimal?[] DirectionalSum(decimal[] src, int d, bool positive)
    {
        var n = src.Length;
        var r = new decimal?[n];
        for (var i = d; i < n; i++)
        {
            decimal dir = 0m, abs = 0m;
            for (var j = i - d + 1; j <= i; j++)
            {
                var delta = src[j] - src[j - 1];
                abs += Math.Abs(delta);
                if (positive) { if (delta > 0m) dir += delta; }
                else { if (delta < 0m) dir += -delta; }
            }
            r[i] = dir / (abs + Eps);
        }
        return r;
    }

    // === Operatori sul volume ================================================================

    /// <summary>VMA: media mobile del volume, normalizzata sul volume corrente.</summary>
    public static decimal?[] Vma(decimal[] volume, int d)
    {
        var n = volume.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            r[i] = FactorMath.Mean(volume, i - d + 1, i) / (volume[i] + Eps);
        }
        return r;
    }

    /// <summary>VSTD: deviazione standard del volume, normalizzata sul volume corrente.</summary>
    public static decimal?[] Vstd(decimal[] volume, int d)
    {
        var n = volume.Length;
        var r = new decimal?[n];
        for (var i = d - 1; i < n; i++)
        {
            r[i] = FactorMath.StdDev(volume, i - d + 1, i) / (volume[i] + Eps);
        }
        return r;
    }

    /// <summary>WVMA: volatilità del flusso |rendimento|·volume (dispersione/attività, ≥0).</summary>
    public static decimal?[] Wvma(decimal[] close, decimal[] volume, int d)
    {
        var n = close.Length;
        var r = new decimal?[n];
        if (d < 2) return r;
        var flow = new decimal[d];
        for (var i = d; i < n; i++)
        {
            for (var k = 0; k < d; k++)
            {
                var j = i - d + 1 + k;
                var ret = close[j - 1] > 0m ? Math.Abs(close[j] / close[j - 1] - 1m) : 0m;
                flow[k] = ret * volume[j];
            }
            var mean = FactorMath.Mean(flow, 0, d - 1);
            r[i] = FactorMath.StdDev(flow, 0, d - 1) / (mean + Eps);
        }
        return r;
    }

    /// <summary>VSUMP: quota di aumento del volume sul movimento assoluto totale (0..1).</summary>
    public static decimal?[] Vsump(decimal[] volume, int d) => DirectionalSum(volume, d, positive: true);

    /// <summary>VSUMN: quota di calo del volume sul movimento assoluto totale (0..1).</summary>
    public static decimal?[] Vsumn(decimal[] volume, int d) => DirectionalSum(volume, d, positive: false);

    /// <summary>VSUMD: differenza fra quota di aumento e di calo del volume (-1..1).</summary>
    public static decimal?[] Vsumd(decimal[] volume, int d) => Combine(Vsump(volume, d), Vsumn(volume, d), (a, b) => a - b);

    // === KBAR: forma della candela (orizzonte-indipendenti) ==================================

    public static decimal?[] Kmid(Bars b) => PerCandle(b, (o, h, l, c) => o > 0m ? (c - o) / o : null);
    public static decimal?[] Klen(Bars b) => PerCandle(b, (o, h, l, c) => o > 0m ? (h - l) / o : null);
    public static decimal?[] Kmid2(Bars b) => PerCandle(b, (o, h, l, c) => h - l > 0m ? (c - o) / (h - l) : null);
    public static decimal?[] Kup(Bars b) => PerCandle(b, (o, h, l, c) => o > 0m ? (h - Math.Max(o, c)) / o : null);
    public static decimal?[] Kup2(Bars b) => PerCandle(b, (o, h, l, c) => h - l > 0m ? (h - Math.Max(o, c)) / (h - l) : null);
    public static decimal?[] Klow(Bars b) => PerCandle(b, (o, h, l, c) => o > 0m ? (Math.Min(o, c) - l) / o : null);
    public static decimal?[] Klow2(Bars b) => PerCandle(b, (o, h, l, c) => h - l > 0m ? (Math.Min(o, c) - l) / (h - l) : null);
    public static decimal?[] Ksft(Bars b) => PerCandle(b, (o, h, l, c) => o > 0m ? (2m * c - h - l) / o : null);
    public static decimal?[] Ksft2(Bars b) => PerCandle(b, (o, h, l, c) => h - l > 0m ? (2m * c - h - l) / (h - l) : null);

    private static decimal?[] PerCandle(Bars b, Func<decimal, decimal, decimal, decimal, decimal?> f)
    {
        var n = b.Count;
        var r = new decimal?[n];
        for (var i = 0; i < n; i++) r[i] = f(b.Open[i], b.High[i], b.Low[i], b.Close[i]);
        return r;
    }

    // === Helper numerici =====================================================================

    /// <summary>Regressione OLS di y[start..start+len-1] su x=0..len-1. Ritorna (slope, intercept, meanY).</summary>
    private static (decimal Slope, decimal Intercept, decimal MeanY) Ols(decimal[] y, int start, int len)
    {
        // x = 0..len-1: somme in forma chiusa (nessun accumulo di errore su x).
        decimal sumX = (decimal)len * (len - 1) / 2m;
        decimal meanX = sumX / len;
        decimal sxx = 0m, sxy = 0m, sumY = 0m;
        for (var k = 0; k < len; k++) sumY += y[start + k];
        decimal meanY = sumY / len;
        for (var k = 0; k < len; k++)
        {
            var dx = k - meanX;
            var dy = y[start + k] - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
        }
        var slope = sxx > 0m ? sxy / sxx : 0m;
        var intercept = meanY - slope * meanX;
        return (slope, intercept, meanY);
    }

    private static decimal OlsRSquared(decimal[] y, int start, int len)
    {
        var (slope, intercept, meanY) = Ols(y, start, len);
        decimal ssTot = 0m, ssRes = 0m;
        for (var k = 0; k < len; k++)
        {
            var actual = y[start + k];
            var fitted = intercept + slope * k;
            var dt = actual - meanY;
            var dr = actual - fitted;
            ssTot += dt * dt;
            ssRes += dr * dr;
        }
        if (ssTot <= 0m) return 0m;
        var r2 = 1m - ssRes / ssTot;
        return r2 < 0m ? 0m : r2;
    }

    private static decimal QuantileSorted(decimal[] sortedAsc, decimal q)
    {
        var m = sortedAsc.Length;
        if (m == 1) return sortedAsc[0];
        // Interpolazione lineare fra i due ranghi adiacenti (convenzione "linear" di NumPy/pandas).
        var pos = q * (m - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAsc[lo];
        var frac = pos - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * frac;
    }

    private static decimal WindowMax(decimal[] src, int start, int end)
    {
        var m = src[start];
        for (var i = start + 1; i <= end; i++) if (src[i] > m) m = src[i];
        return m;
    }

    private static decimal WindowMin(decimal[] src, int start, int end)
    {
        var m = src[start];
        for (var i = start + 1; i <= end; i++) if (src[i] < m) m = src[i];
        return m;
    }

    /// <summary>Posizione locale (0=inizio finestra .. len-1=fine) dell'estremo, l'ultima in caso di pari merito.</summary>
    private static int LocalArg(decimal[] src, int start, int end, bool max)
    {
        var bestIdx = start;
        var best = src[start];
        for (var i = start + 1; i <= end; i++)
        {
            if ((max && src[i] >= best) || (!max && src[i] <= best)) { best = src[i]; bestIdx = i; }
        }
        return bestIdx - start;
    }

    private static decimal?[] Combine(decimal?[] a, decimal?[] b, Func<decimal, decimal, decimal> op)
    {
        var n = a.Length;
        var r = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (a[i].HasValue && b[i].HasValue) r[i] = op(a[i]!.Value, b[i]!.Value);
        }
        return r;
    }
}
