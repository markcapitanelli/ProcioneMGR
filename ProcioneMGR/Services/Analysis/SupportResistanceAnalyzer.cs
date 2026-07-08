using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Supporti, resistenze, trend a massimi/minimi e ritracciamenti percentuali
/// (McAllen, cap. 7-8 e 15).
///
/// Metodo: si individuano i punti di swing (pivot: massimo/minimo locale su una finestra
/// simmetrica di K barre), si raggruppano i pivot vicini in LIVELLI di prezzo (piu' tocchi =
/// livello piu' significativo, come da libro), si classifica il trend dalla sequenza degli
/// swing (higher highs + higher lows = uptrend) e si misura quanto il prezzo abbia
/// ritracciato l'ultimo swing (33% sano, 50% tipico, oltre il 66% = probabile inversione).
/// </summary>
public sealed class SupportResistanceAnalyzer
{
    /// <summary>Semilarghezza della finestra pivot: high[i] deve essere il massimo di [i-K, i+K].</summary>
    public int PivotWindow { get; init; } = 3;

    /// <summary>Tolleranza % per raggruppare pivot vicini nello stesso livello.</summary>
    public decimal LevelTolerancePercent { get; init; } = 1m;

    /// <summary>Finestra per la media del volume nella conferma dei breakout.</summary>
    public int VolumeWindow { get; init; } = 20;

    /// <summary>Fattore sopra la media del volume perche' un breakout sia "confermato" (cap. 15).</summary>
    public decimal BreakoutVolumeFactor { get; init; } = 1.5m;

    public SupportResistanceReport Analyze(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var pivots = FindPivots(candles);
        var levels = BuildLevels(pivots, candles.Count);
        var breakouts = FindBreakouts(candles, levels);
        var trend = ClassifyTrend(pivots);
        var retracement = ComputeRetracement(candles, pivots, trend);

        var lastClose = candles.Count > 0 ? candles[^1].Close : 0m;
        return new SupportResistanceReport
        {
            Pivots = pivots,
            Levels = levels,
            Breakouts = breakouts,
            Trend = trend,
            Retracement = retracement,
            NearestSupport = levels.Where(l => l.Price < lastClose).OrderByDescending(l => l.Price).FirstOrDefault(),
            NearestResistance = levels.Where(l => l.Price > lastClose).OrderBy(l => l.Price).FirstOrDefault(),
        };
    }

    /// <summary>
    /// Pivot: estremo locale su una finestra simmetrica di <see cref="PivotWindow"/> barre.
    /// In caso di pareggio sull'estremo vince la barra piu' a sinistra (regola standard:
    /// confronto stretto verso sinistra, non stretto verso destra) — evita che due barre
    /// adiacenti con lo stesso massimo si annullino a vicenda.
    /// </summary>
    public IReadOnlyList<SwingPoint> FindPivots(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var k = PivotWindow;
        var pivots = new List<SwingPoint>();

        for (var i = k; i < candles.Count - k; i++)
        {
            var isHigh = true;
            var isLow = true;
            for (var j = i - k; j <= i + k && (isHigh || isLow); j++)
            {
                if (j == i) continue;
                if (j < i)
                {
                    if (candles[j].High >= candles[i].High) isHigh = false;
                    if (candles[j].Low <= candles[i].Low) isLow = false;
                }
                else
                {
                    if (candles[j].High > candles[i].High) isHigh = false;
                    if (candles[j].Low < candles[i].Low) isLow = false;
                }
            }
            if (isHigh)
            {
                pivots.Add(new SwingPoint(i, candles[i].TimestampUtc, candles[i].High, IsHigh: true));
            }
            if (isLow)
            {
                pivots.Add(new SwingPoint(i, candles[i].TimestampUtc, candles[i].Low, IsHigh: false));
            }
        }
        return pivots;
    }

    /// <summary>
    /// Raggruppa i pivot in livelli: due pivot appartengono allo stesso livello se distano meno
    /// di <see cref="LevelTolerancePercent"/>. Il prezzo del livello e' la media dei tocchi.
    /// "Piu' volte un livello e' stato testato, piu' e' significativo" (McAllen cap. 8).
    /// </summary>
    private List<PriceLevel> BuildLevels(IReadOnlyList<SwingPoint> pivots, int totalBars)
    {
        var levels = new List<(decimal Sum, int Count, int LastIndex, int HighTouches)>();
        foreach (var p in pivots.OrderBy(p => p.Price))
        {
            var placed = false;
            for (var l = 0; l < levels.Count; l++)
            {
                var avg = levels[l].Sum / levels[l].Count;
                if (avg > 0m && Math.Abs(p.Price - avg) / avg * 100m <= LevelTolerancePercent)
                {
                    levels[l] = (levels[l].Sum + p.Price, levels[l].Count + 1,
                        Math.Max(levels[l].LastIndex, p.Index), levels[l].HighTouches + (p.IsHigh ? 1 : 0));
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                levels.Add((p.Price, 1, p.Index, p.IsHigh ? 1 : 0));
            }
        }

        return levels
            .Select(l => new PriceLevel(
                Price: l.Sum / l.Count,
                Touches: l.Count,
                LastTouchIndex: l.LastIndex,
                BarsSinceLastTouch: totalBars - 1 - l.LastIndex))
            .OrderByDescending(l => l.Touches)
            .ToList();
    }

    /// <summary>
    /// Breakout: chiusura che attraversa un livello (dal basso = rottura di resistenza, dall'alto
    /// = violazione di supporto). Confermato solo se il volume supera la media recente — un
    /// breakout a basso volume e' quasi sempre falso (McAllen cap. 10 e 15).
    /// </summary>
    private List<BreakoutEvent> FindBreakouts(IReadOnlyList<OhlcvData> candles, IReadOnlyList<PriceLevel> levels)
    {
        var events = new List<BreakoutEvent>();
        if (candles.Count < 2 || levels.Count == 0) return events;

        // Media mobile del volume.
        var volAvg = new decimal[candles.Count];
        decimal volSum = 0m;
        for (var i = 0; i < candles.Count; i++)
        {
            volSum += candles[i].Volume;
            if (i >= VolumeWindow) volSum -= candles[i - VolumeWindow].Volume;
            volAvg[i] = volSum / Math.Min(i + 1, VolumeWindow);
        }

        foreach (var level in levels)
        {
            for (var i = 1; i < candles.Count; i++)
            {
                var prev = candles[i - 1].Close;
                var cur = candles[i].Close;
                var crossedUp = prev <= level.Price && cur > level.Price;
                var crossedDown = prev >= level.Price && cur < level.Price;
                if (!crossedUp && !crossedDown) continue;

                // Solo attraversamenti di livelli gia' COMPLETAMENTE formati: un pivot e'
                // identificabile solo PivotWindow barre dopo il suo indice (servono K barre
                // future per qualificarlo) — segnalare un breakout prima sarebbe look-ahead.
                if (i <= level.LastTouchIndex + PivotWindow) continue;

                var volumeRatio = volAvg[i] > 0m ? candles[i].Volume / volAvg[i] : 0m;
                events.Add(new BreakoutEvent(
                    Index: i,
                    Timestamp: candles[i].TimestampUtc,
                    LevelPrice: level.Price,
                    IsUpside: crossedUp,
                    VolumeRatio: volumeRatio,
                    VolumeConfirmed: volumeRatio >= BreakoutVolumeFactor));
            }
        }
        return events.OrderBy(e => e.Index).ToList();
    }

    /// <summary>
    /// Trend dalla sequenza degli swing (Dow/McAllen cap. 1): higher highs + higher lows =
    /// uptrend; lower highs + lower lows = downtrend; altrimenti laterale/indeterminato.
    /// </summary>
    public static SwingTrend ClassifyTrend(IReadOnlyList<SwingPoint> pivots)
    {
        var highs = pivots.Where(p => p.IsHigh).OrderBy(p => p.Index).ToList();
        var lows = pivots.Where(p => !p.IsHigh).OrderBy(p => p.Index).ToList();
        if (highs.Count < 2 || lows.Count < 2) return SwingTrend.Undefined;

        var higherHighs = highs[^1].Price > highs[^2].Price;
        var higherLows = lows[^1].Price > lows[^2].Price;
        var lowerHighs = highs[^1].Price < highs[^2].Price;
        var lowerLows = lows[^1].Price < lows[^2].Price;

        if (higherHighs && higherLows) return SwingTrend.Uptrend;
        if (lowerHighs && lowerLows) return SwingTrend.Downtrend;
        return SwingTrend.Sideways;
    }

    /// <summary>
    /// Ritracciamento dell'ultimo swing (cap. 15): in uptrend misura quanto il prezzo attuale
    /// ha ritracciato dell'ultima gamba minimo->massimo (e viceversa in downtrend), con i
    /// livelli di riferimento 33/50/66%. Oltre il 66% il libro considera probabile l'inversione.
    /// </summary>
    private static RetracementInfo? ComputeRetracement(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<SwingPoint> pivots, SwingTrend trend)
    {
        if (candles.Count == 0) return null;
        var lastClose = candles[^1].Close;

        SwingPoint? from = null, to = null;
        if (trend is SwingTrend.Uptrend or SwingTrend.Sideways or SwingTrend.Undefined)
        {
            // Ultima gamba rialzista: ultimo pivot low seguito da un pivot high.
            var lastHigh = pivots.Where(p => p.IsHigh).OrderBy(p => p.Index).LastOrDefault();
            if (lastHigh is not null)
            {
                var priorLow = pivots.Where(p => !p.IsHigh && p.Index < lastHigh.Index)
                    .OrderBy(p => p.Index).LastOrDefault();
                if (priorLow is not null)
                {
                    from = priorLow;
                    to = lastHigh;
                }
            }
        }
        if (trend == SwingTrend.Downtrend)
        {
            // Ultima gamba ribassista: ultimo pivot high seguito da un pivot low.
            var lastLow = pivots.Where(p => !p.IsHigh).OrderBy(p => p.Index).LastOrDefault();
            if (lastLow is not null)
            {
                var priorHigh = pivots.Where(p => p.IsHigh && p.Index < lastLow.Index)
                    .OrderBy(p => p.Index).LastOrDefault();
                if (priorHigh is not null)
                {
                    from = priorHigh;
                    to = lastLow;
                }
            }
        }
        if (from is null || to is null) return null;

        var swing = to.Price - from.Price;
        if (swing == 0m) return null;

        // Frazione dello swing gia' "restituita" dal prezzo attuale rispetto all'estremo.
        var retraced = (to.Price - lastClose) / swing * 100m;

        return new RetracementInfo(
            SwingFrom: from.Price,
            SwingTo: to.Price,
            CurrentRetracementPercent: retraced,
            Level33: to.Price - swing * 0.33m,
            Level50: to.Price - swing * 0.50m,
            Level66: to.Price - swing * 0.66m,
            IsReversalWarning: retraced > 66m);
    }
}

/// <summary>Punto di swing (pivot) sui massimi o sui minimi.</summary>
public sealed record SwingPoint(int Index, DateTime Timestamp, decimal Price, bool IsHigh);

/// <summary>Livello di supporto/resistenza aggregato dai pivot. Piu' tocchi = piu' significativo.</summary>
public sealed record PriceLevel(decimal Price, int Touches, int LastTouchIndex, int BarsSinceLastTouch);

/// <summary>Attraversamento di un livello da parte della chiusura, con conferma volumetrica.</summary>
public sealed record BreakoutEvent(
    int Index, DateTime Timestamp, decimal LevelPrice, bool IsUpside, decimal VolumeRatio, bool VolumeConfirmed);

/// <summary>Trend classificato dagli swing.</summary>
public enum SwingTrend
{
    Undefined,
    Uptrend,
    Downtrend,
    Sideways,
}

/// <summary>Ritracciamento dell'ultimo swing con i livelli 33/50/66 (McAllen cap. 15).</summary>
public sealed record RetracementInfo(
    decimal SwingFrom,
    decimal SwingTo,
    decimal CurrentRetracementPercent,
    decimal Level33,
    decimal Level50,
    decimal Level66,
    bool IsReversalWarning);

/// <summary>Report complessivo di supporti/resistenze, trend e ritracciamento.</summary>
public sealed record SupportResistanceReport
{
    public IReadOnlyList<SwingPoint> Pivots { get; init; } = [];
    public IReadOnlyList<PriceLevel> Levels { get; init; } = [];
    public IReadOnlyList<BreakoutEvent> Breakouts { get; init; } = [];
    public SwingTrend Trend { get; init; }
    public RetracementInfo? Retracement { get; init; }
    public PriceLevel? NearestSupport { get; init; }
    public PriceLevel? NearestResistance { get; init; }
}
