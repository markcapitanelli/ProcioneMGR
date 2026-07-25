using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>Tipo di evento di mercato rilevato dai soli prezzi/volumi (nessuna fonte esterna).</summary>
public enum MarketEventKind
{
    /// <summary>Rendimento di barra sotto −k·σ della propria storia recente.</summary>
    Crash,

    /// <summary>Rendimento di barra sopra +k·σ (il simmetrico del crash: anche gli squeeze sono episodi).</summary>
    Surge,

    /// <summary>La vol realizzata breve supera di un multiplo quella lunga (regime shift improvviso).</summary>
    VolSpike,

    /// <summary>Volume oltre un multiplo della mediana rolling (partecipazione anomala).</summary>
    VolumeBlowout,
}

/// <summary>Un evento: quando, di che tipo, e quanto estremo (in unità della sua soglia).</summary>
public sealed record MarketEvent(DateTime TimestampUtc, MarketEventKind Kind, double Magnitude);

/// <summary>Soglie del rilevatore. I default sono deliberatamente conservativi: pochi eventi veri.</summary>
public sealed class MarketEventDetectorConfig
{
    /// <summary>Finestra della σ rolling dei rendimenti per Crash/Surge.</summary>
    public int VolWindow { get; set; } = 50;

    /// <summary>Soglia in σ per Crash (sotto −k) e Surge (sopra +k).</summary>
    public double ReturnSigma { get; set; } = 4.0;

    public int VolSpikeShortWindow { get; set; } = 10;
    public int VolSpikeLongWindow { get; set; } = 100;

    /// <summary>VolSpike quando σ_breve / σ_lunga supera questo rapporto.</summary>
    public double VolSpikeRatio { get; set; } = 2.5;

    public int VolumeWindow { get; set; } = 50;

    /// <summary>VolumeBlowout quando volume &gt; multiplo × mediana rolling.</summary>
    public double VolumeMultiple { get; set; } = 5.0;

    /// <summary>Barre minime fra due eventi dello STESSO tipo: un cluster è UN episodio, non dieci.</summary>
    public int CooldownBars { get; set; } = 20;
}

/// <summary>
/// [T2.7 roadmap macchina-ricerca] Rileva "eventi di mercato" dai prezzi stessi — crash, squeeze,
/// spike di volatilità, blowout di volume — su tutta la profondità OHLCV (sei anni), dove l'alt-data
/// ne copre venti giorni. CAUSALE per costruzione: la decisione alla barra i usa solo statistiche
/// delle barre PRECEDENTI (la barra giudicata non contribuisce mai alla propria soglia). Gli eventi
/// alimentano l'<see cref="EventStudy"/> e, in prospettiva, filtri di strategia — che passano dal
/// gate standard come ogni altra ipotesi.
/// </summary>
public static class MarketEventDetector
{
    public static List<MarketEvent> Detect(IReadOnlyList<OhlcvData> candles, MarketEventDetectorConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var cfg = config ?? new MarketEventDetectorConfig();
        var n = candles.Count;
        var events = new List<MarketEvent>();
        if (n < 3) return events;

        // Rendimenti per barra (r[0] non esiste).
        var returns = new double?[n];
        for (var i = 1; i < n; i++)
        {
            var prev = candles[i - 1].Close;
            if (prev > 0m) returns[i] = (double)(candles[i].Close / prev - 1m);
        }

        var lastByKind = new Dictionary<MarketEventKind, int>();

        bool CooledDown(MarketEventKind kind, int i) =>
            !lastByKind.TryGetValue(kind, out var last) || i - last >= cfg.CooldownBars;

        void Emit(MarketEventKind kind, int i, double magnitude)
        {
            events.Add(new MarketEvent(candles[i].TimestampUtc, kind, magnitude));
            lastByKind[kind] = i;
        }

        for (var i = 1; i < n; i++)
        {
            // --- Crash / Surge: r_i contro la σ dei rendimenti in [i-VolWindow, i) ---------------
            if (returns[i] is { } r && i > cfg.VolWindow)
            {
                var sigma = StdOfReturns(returns, i - cfg.VolWindow, i);
                if (sigma > 1e-12)
                {
                    var z = r / sigma;
                    if (z <= -cfg.ReturnSigma && CooledDown(MarketEventKind.Crash, i))
                        Emit(MarketEventKind.Crash, i, Math.Abs(z) / cfg.ReturnSigma);
                    else if (z >= cfg.ReturnSigma && CooledDown(MarketEventKind.Surge, i))
                        Emit(MarketEventKind.Surge, i, z / cfg.ReturnSigma);
                }
            }

            // --- VolSpike: σ breve fino a i incluso vs σ lunga fino a i-shortWindow --------------
            // (le due finestre non si sovrappongono: la lunga è la "normalità" PRIMA dello spike).
            if (i >= cfg.VolSpikeShortWindow + cfg.VolSpikeLongWindow)
            {
                var shortSigma = StdOfReturns(returns, i - cfg.VolSpikeShortWindow + 1, i + 1);
                var longSigma = StdOfReturns(returns, i - cfg.VolSpikeShortWindow - cfg.VolSpikeLongWindow + 1, i - cfg.VolSpikeShortWindow + 1);
                if (longSigma > 1e-12)
                {
                    var ratio = shortSigma / longSigma;
                    if (ratio >= cfg.VolSpikeRatio && CooledDown(MarketEventKind.VolSpike, i))
                        Emit(MarketEventKind.VolSpike, i, ratio / cfg.VolSpikeRatio);
                }
            }

            // --- VolumeBlowout: volume_i vs mediana di [i-VolumeWindow, i) -----------------------
            if (i > cfg.VolumeWindow)
            {
                var median = MedianVolume(candles, i - cfg.VolumeWindow, i);
                if (median > 0d)
                {
                    var mult = (double)candles[i].Volume / median;
                    if (mult >= cfg.VolumeMultiple && CooledDown(MarketEventKind.VolumeBlowout, i))
                        Emit(MarketEventKind.VolumeBlowout, i, mult / cfg.VolumeMultiple);
                }
            }
        }

        return events;
    }

    /// <summary>Deviazione standard campionaria dei rendimenti non-null in [from, to).</summary>
    private static double StdOfReturns(double?[] returns, int from, int to)
    {
        var vals = new List<double>(to - from);
        for (var j = Math.Max(1, from); j < to; j++)
        {
            if (returns[j] is { } v) vals.Add(v);
        }
        if (vals.Count < 3) return 0d;
        var mean = vals.Average();
        return Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / (vals.Count - 1));
    }

    private static double MedianVolume(IReadOnlyList<OhlcvData> candles, int from, int to)
    {
        var vals = new List<double>(to - from);
        for (var j = Math.Max(0, from); j < to; j++) vals.Add((double)candles[j].Volume);
        if (vals.Count == 0) return 0d;
        vals.Sort();
        var mid = vals.Count / 2;
        return vals.Count % 2 == 1 ? vals[mid] : (vals[mid - 1] + vals[mid]) / 2d;
    }
}
