using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

// =============================================================================================
//  Libreria di fattori alpha (rif. Appendice cap. 24 di Jansen), reimplementati in C#.
//  Ogni fattore rispetta il contratto anti-look-ahead di IAlphaFactor: il valore a i usa
//  solo candles[0..i]. Serie allineata: null in warm-up o dove non calcolabile.
// =============================================================================================

/// <summary>
/// MOMENTUM con "skip": rendimento su una finestra <c>Lookback</c> che termina <c>Skip</c>
/// candele fa. Lo skip (tipico: saltare le 1-2 barre più recenti) attenua la mean-reversion
/// di brevissimo periodo, isolando il momentum "pulito".
///   value[i] = (c[i-skip] - c[i-skip-lookback]) / c[i-skip-lookback]
/// </summary>
public sealed class MomentumFactor : IAlphaFactor
{
    public string Name => "Momentum";
    public string DisplayName => "Momentum (skip)";
    public FactorCategory Category => FactorCategory.Momentum;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 2m, 1000m),
        new("Skip", "Skip recenti", 0m, 0m, 50m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(2, p.GetIntOrDefault("Lookback", 20));
        var skip = Math.Max(0, p.GetIntOrDefault("Skip", 0));
        var n = candles.Count;
        var r = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            var recent = i - skip;
            var past = recent - lookback;
            if (past < 0) continue;
            var basePrice = candles[past].Close;
            if (basePrice <= 0m) continue;
            r[i] = (candles[recent].Close - basePrice) / basePrice;
        }
        return r;
    }
}

/// <summary>
/// MEAN REVERSION: z-score NEGATIVO del prezzo rispetto alla sua media rolling. Valore alto
/// quando il prezzo è molto SOTTO la media (attesa di rimbalzo verso l'alto).
///   z = (c[i] - mean) / std   (su finestra [i-lookback+1 .. i])   ->   value = -z
/// </summary>
public sealed class MeanReversionFactor : IAlphaFactor
{
    public string Name => "MeanReversion";
    public string DisplayName => "Mean Reversion (z-score)";
    public FactorCategory Category => FactorCategory.MeanReversion;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 3m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(3, p.GetIntOrDefault("Lookback", 20));
        var n = candles.Count;
        var closes = new decimal[n];
        for (var i = 0; i < n; i++) closes[i] = candles[i].Close;

        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            var start = i - lookback + 1;
            var mean = FactorMath.Mean(closes, start, i);
            var std = FactorMath.StdDev(closes, start, i);
            if (std <= 0m) continue;
            r[i] = -((closes[i] - mean) / std);
        }
        return r;
    }
}

/// <summary>
/// VOLATILITÀ REALIZZATA: deviazione standard dei rendimenti logaritmici sugli ultimi
/// <c>Lookback</c> periodi. Fattore di rischio/regime (non direzionale).
/// </summary>
public sealed class RealizedVolatilityFactor : IAlphaFactor
{
    public string Name => "RealizedVol";
    public string DisplayName => "Volatilità realizzata";
    public FactorCategory Category => FactorCategory.Volatility;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 3m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(3, p.GetIntOrDefault("Lookback", 20));
        var n = candles.Count;
        // Rendimenti log: ret[i] definito per i>=1.
        var ret = new decimal[n];
        for (var i = 1; i < n; i++)
        {
            var prev = candles[i - 1].Close;
            var cur = candles[i].Close;
            ret[i] = prev > 0m && cur > 0m ? (decimal)Math.Log((double)(cur / prev)) : 0m;
        }
        var r = new decimal?[n];
        // Servono `lookback` rendimenti: finestra [i-lookback+1 .. i], tutti con i>=1.
        for (var i = lookback; i < n; i++)
        {
            r[i] = FactorMath.StdDev(ret, i - lookback + 1, i);
        }
        return r;
    }
}

/// <summary>
/// VOLATILITÀ DI PARKINSON: stima basata sul range High-Low, più efficiente della sola close.
///   value[i] = sqrt( (1/(4 ln2)) * media( ln(High/Low)^2 ) su [i-lookback+1 .. i] )
/// </summary>
public sealed class ParkinsonVolatilityFactor : IAlphaFactor
{
    public string Name => "ParkinsonVol";
    public string DisplayName => "Volatilità di Parkinson (H-L)";
    public FactorCategory Category => FactorCategory.Volatility;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 2m, 500m),
    ];

    private static readonly decimal InvFourLn2 = 1m / (4m * (decimal)Math.Log(2.0));

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(2, p.GetIntOrDefault("Lookback", 20));
        var n = candles.Count;
        var hl2 = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            var high = candles[i].High;
            var low = candles[i].Low;
            if (high > 0m && low > 0m && high >= low)
            {
                var lr = Math.Log((double)(high / low));
                hl2[i] = (decimal)(lr * lr);
            }
        }
        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            var mean = FactorMath.Mean(hl2, i - lookback + 1, i);
            r[i] = FactorMath.Sqrt(InvFourLn2 * mean);
        }
        return r;
    }
}

/// <summary>
/// VOLUME relativo: volume corrente rispetto alla sua media rolling, centrato su 0.
///   value[i] = volume[i] / SMA(volume, lookback) - 1
/// </summary>
public sealed class RelativeVolumeFactor : IAlphaFactor
{
    public string Name => "RelativeVolume";
    public string DisplayName => "Volume relativo";
    public FactorCategory Category => FactorCategory.Volume;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 2m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(2, p.GetIntOrDefault("Lookback", 20));
        var n = candles.Count;
        var vol = new decimal[n];
        for (var i = 0; i < n; i++) vol[i] = candles[i].Volume;

        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            var mean = FactorMath.Mean(vol, i - lookback + 1, i);
            if (mean <= 0m) continue;
            r[i] = vol[i] / mean - 1m;
        }
        return r;
    }
}

/// <summary>
/// RSI fattorizzato: RSI di Wilder centrato e scalato in [-1, +1].
///   value[i] = (RSI(period)[i] - 50) / 50
/// </summary>
public sealed class RsiFactor : IAlphaFactor
{
    public string Name => "RsiFactor";
    public string DisplayName => "RSI (fattorizzato)";
    public FactorCategory Category => FactorCategory.Technical;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Period", "Periodo", 14m, 2m, 200m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var period = Math.Max(2, p.GetIntOrDefault("Period", 14));
        var n = candles.Count;
        var closes = new decimal[n];
        for (var i = 0; i < n; i++) closes[i] = candles[i].Close;

        var rsi = FactorMath.WilderRsi(closes, period);
        var r = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (rsi[i].HasValue) r[i] = (rsi[i]!.Value - 50m) / 50m;
        }
        return r;
    }
}

/// <summary>
/// MACD fattorizzato: istogramma MACD normalizzato sul prezzo (comparabile fra simboli).
///   macd = EMA(fast) - EMA(slow); signal = EMA(macd, signalPeriod); hist = macd - signal
///   value[i] = hist[i] / close[i]
/// </summary>
public sealed class MacdFactor : IAlphaFactor
{
    public string Name => "MacdFactor";
    public string DisplayName => "MACD istogramma (norm.)";
    public FactorCategory Category => FactorCategory.Technical;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Fast", "EMA veloce", 12m, 2m, 200m),
        new("Slow", "EMA lenta", 26m, 3m, 400m),
        new("Signal", "Signal", 9m, 2m, 100m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var fast = Math.Max(2, p.GetIntOrDefault("Fast", 12));
        var slow = Math.Max(fast + 1, p.GetIntOrDefault("Slow", 26));
        var signalPeriod = Math.Max(2, p.GetIntOrDefault("Signal", 9));
        var n = candles.Count;
        var closes = new decimal[n];
        for (var i = 0; i < n; i++) closes[i] = candles[i].Close;

        var emaFast = FactorMath.Ema(closes, fast);
        var emaSlow = FactorMath.Ema(closes, slow);

        // Linea MACD dove entrambe le EMA esistono; serie densa a partire dal primo indice valido.
        var macdDense = new List<decimal>();
        var macdStart = -1;
        for (var i = 0; i < n; i++)
        {
            if (emaFast[i].HasValue && emaSlow[i].HasValue)
            {
                if (macdStart < 0) macdStart = i;
                macdDense.Add(emaFast[i]!.Value - emaSlow[i]!.Value);
            }
        }

        var r = new decimal?[n];
        if (macdStart < 0 || macdDense.Count < signalPeriod) return r;

        var signalDense = FactorMath.Ema(macdDense, signalPeriod);
        for (var k = 0; k < macdDense.Count; k++)
        {
            if (!signalDense[k].HasValue) continue;
            var i = macdStart + k;              // reindicizzazione alla serie originale
            var hist = macdDense[k] - signalDense[k]!.Value;
            if (closes[i] > 0m) r[i] = hist / closes[i];
        }
        return r;
    }
}

/// <summary>
/// DISTANZA dalla media mobile: scostamento percentuale del prezzo dalla SMA(lookback).
///   value[i] = (c[i] - SMA) / SMA
/// </summary>
public sealed class DistanceFromMaFactor : IAlphaFactor
{
    public string Name => "DistanceFromMa";
    public string DisplayName => "Distanza dalla MA";
    public FactorCategory Category => FactorCategory.Technical;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 50m, 2m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(2, p.GetIntOrDefault("Lookback", 50));
        var n = candles.Count;
        var closes = new decimal[n];
        for (var i = 0; i < n; i++) closes[i] = candles[i].Close;

        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            var sma = FactorMath.Mean(closes, i - lookback + 1, i);
            if (sma <= 0m) continue;
            r[i] = (closes[i] - sma) / sma;
        }
        return r;
    }
}
