using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>Tipi di pattern candlestick riconosciuti (McAllen, "Charting and Technical Analysis", cap. 4-6 e 14).</summary>
public enum CandlePatternType
{
    Doji,
    SpinningTop,
    Hammer,
    HangingMan,
    ShootingStar,
    BullishEngulfing,
    BearishEngulfing,
    BullishHarami,
    BearishHarami,
    BullishHaramiCross,
    BearishHaramiCross,
    EveningStar,
    MorningStar,
    TriStarBearish,
    TriStarBullish,
    ThreeWhiteSoldiers,
    ThreeBlackCrows,
    RisingThreeMethods,
    FallingThreeMethods,
    KeyReversalBullish,
    KeyReversalBearish,
}

/// <summary>Pattern rilevato su una barra (l'indice e' quello della barra che COMPLETA il pattern).</summary>
public sealed record CandlePattern(
    int Index,
    DateTime Timestamp,
    CandlePatternType Type,
    /// <summary>true = segnale rialzista, false = ribassista, null = neutro (es. doji isolato).</summary>
    bool? IsBullish,
    /// <summary>true = pattern di inversione, false = di continuazione.</summary>
    bool IsReversal,
    /// <summary>true se il volume della barra supera la media recente (conferma, McAllen cap. 15).</summary>
    bool VolumeConfirmed);

/// <summary>
/// Riconoscimento dei pattern candlestick (McAllen, cap. 4-6) + Key Reversal Day (cap. 14).
///
/// Principio del libro: un pattern di inversione ha valore SOLO dopo un trend
/// ("un minimo di cinque giorni di avanzata o declino") — i pattern direzionali vengono
/// quindi emessi solo se il contesto (variazione netta sulle N barre precedenti) e' coerente.
/// Doji e spinning top isolati sono emessi come neutri: sta al chiamante pesarli col contesto.
/// </summary>
public sealed class CandlestickPatternDetector
{
    /// <summary>Corpo massimo (in % del range) perche' una candela sia un doji.</summary>
    public decimal DojiBodyMaxPercent { get; init; } = 10m;

    /// <summary>Corpo massimo (in % del range) per lo spinning top.</summary>
    public decimal SpinningTopBodyMaxPercent { get; init; } = 30m;

    /// <summary>Barre di contesto per definire il trend precedente (il libro suggerisce >= 5).</summary>
    public int TrendLookback { get; init; } = 5;

    /// <summary>Variazione % minima sulle barre di lookback perche' ci sia un trend.</summary>
    public decimal TrendMinMovePercent { get; init; } = 2m;

    /// <summary>Finestra del massimo/minimo per il Key Reversal Day.</summary>
    public int KeyReversalLookback { get; init; } = 10;

    /// <summary>Fattore sopra la media del volume per considerare una barra "confermata".</summary>
    public decimal VolumeConfirmFactor { get; init; } = 1.2m;

    public IReadOnlyList<CandlePattern> Detect(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var result = new List<CandlePattern>();
        var n = candles.Count;
        if (n == 0) return result;

        // Media mobile del volume per la conferma (stessa finestra del key reversal).
        var volAvg = new decimal[n];
        decimal volSum = 0m;
        for (var i = 0; i < n; i++)
        {
            volSum += candles[i].Volume;
            if (i >= KeyReversalLookback) volSum -= candles[i - KeyReversalLookback].Volume;
            var window = Math.Min(i + 1, KeyReversalLookback);
            volAvg[i] = volSum / window;
        }

        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            var volumeOk = volAvg[i] > 0m && c.Volume >= volAvg[i] * VolumeConfirmFactor;
            var trend = TrendBefore(candles, i);

            void Add(CandlePatternType type, bool? bullish, bool reversal = true)
                => result.Add(new CandlePattern(i, c.TimestampUtc, type, bullish, reversal, volumeOk));

            // --- Pattern a singola candela --------------------------------------------------
            if (IsDoji(c))
            {
                Add(CandlePatternType.Doji, trend switch
                {
                    > 0 => false, // doji dopo un'avanzata -> allerta ribassista
                    < 0 => true,  // doji dopo un declino -> allerta rialzista
                    _ => null,
                });
            }
            else if (IsSpinningTop(c))
            {
                Add(CandlePatternType.SpinningTop, trend switch { > 0 => false, < 0 => true, _ => null });
            }
            else if (IsHammerShape(c))
            {
                // Stessa forma, nome e segnale dipendono dal contesto (McAllen).
                if (trend < 0) Add(CandlePatternType.Hammer, true);
                else if (trend > 0) Add(CandlePatternType.HangingMan, false);
            }
            else if (IsShootingStarShape(c) && trend > 0)
            {
                Add(CandlePatternType.ShootingStar, false);
            }

            // --- Pattern a due candele ------------------------------------------------------
            if (i >= 1)
            {
                var p = candles[i - 1];
                var trendBeforePrev = TrendBefore(candles, i - 1);

                if (IsWhite(c) && Engulfs(c, p) && trendBeforePrev < 0)
                {
                    Add(CandlePatternType.BullishEngulfing, true);
                }
                else if (IsBlack(c) && Engulfs(c, p) && trendBeforePrev > 0)
                {
                    Add(CandlePatternType.BearishEngulfing, false);
                }
                else if (IsInsideBody(c, p) && Body(p) > Body(c) * 2m)
                {
                    // Harami: candela grande nella direzione del trend + piccola contenuta nel corpo.
                    if (trendBeforePrev > 0 && IsWhite(p))
                    {
                        Add(IsDoji(c) ? CandlePatternType.BearishHaramiCross : CandlePatternType.BearishHarami, false);
                    }
                    else if (trendBeforePrev < 0 && IsBlack(p))
                    {
                        Add(IsDoji(c) ? CandlePatternType.BullishHaramiCross : CandlePatternType.BullishHarami, true);
                    }
                }

                // Key Reversal Day (cap. 14): nuovo estremo di periodo ma chiusura oltre la
                // chiusura precedente, nella direzione opposta al trend.
                if (i >= KeyReversalLookback)
                {
                    var isNewHigh = true;
                    var isNewLow = true;
                    for (var j = i - KeyReversalLookback; j < i; j++)
                    {
                        if (candles[j].High >= c.High) isNewHigh = false;
                        if (candles[j].Low <= c.Low) isNewLow = false;
                        if (!isNewHigh && !isNewLow) break;
                    }
                    if (isNewHigh && c.Close < p.Close && trend > 0)
                    {
                        Add(CandlePatternType.KeyReversalBearish, false);
                    }
                    else if (isNewLow && c.Close > p.Close && trend < 0)
                    {
                        Add(CandlePatternType.KeyReversalBullish, true);
                    }
                }
            }

            // --- Pattern a tre candele ------------------------------------------------------
            if (i >= 2)
            {
                var p1 = candles[i - 1]; // stella / candela centrale
                var p2 = candles[i - 2]; // prima candela
                var trendBeforeFirst = TrendBefore(candles, i - 2);

                if (IsDoji(c) && IsDoji(p1) && IsDoji(p2))
                {
                    if (trendBeforeFirst > 0) Add(CandlePatternType.TriStarBearish, false);
                    else if (trendBeforeFirst < 0) Add(CandlePatternType.TriStarBullish, true);
                }
                else if (trendBeforeFirst > 0 && IsWhite(p2) && IsSmallBody(p1) && IsBlack(c)
                         && c.Close < (p2.Open + p2.Close) / 2m)
                {
                    // Evening Star: grande candela bianca + stella (corpo piccolo) + candela nera
                    // che chiude sotto la meta' della prima.
                    Add(CandlePatternType.EveningStar, false);
                }
                else if (trendBeforeFirst < 0 && IsBlack(p2) && IsSmallBody(p1) && IsWhite(c)
                         && c.Close > (p2.Open + p2.Close) / 2m)
                {
                    Add(CandlePatternType.MorningStar, true);
                }
                else if (IsWhite(c) && IsWhite(p1) && IsWhite(p2)
                         && c.Close > p1.Close && p1.Close > p2.Close
                         && OpensWithinBody(c, p1) && OpensWithinBody(p1, p2)
                         && !IsSmallBody(c) && !IsSmallBody(p1) && !IsSmallBody(p2))
                {
                    Add(CandlePatternType.ThreeWhiteSoldiers, true);
                }
                else if (IsBlack(c) && IsBlack(p1) && IsBlack(p2)
                         && c.Close < p1.Close && p1.Close < p2.Close
                         && OpensWithinBody(c, p1) && OpensWithinBody(p1, p2)
                         && !IsSmallBody(c) && !IsSmallBody(p1) && !IsSmallBody(p2))
                {
                    Add(CandlePatternType.ThreeBlackCrows, false);
                }
            }

            // --- Rising/Falling Three Methods (5 candele, continuazione) ---------------------
            if (i >= 4)
            {
                var first = candles[i - 4];
                var m1 = candles[i - 3];
                var m2 = candles[i - 2];
                var m3 = candles[i - 1];

                if (IsWhite(first) && !IsSmallBody(first) && IsWhite(c)
                    && IsBlack(m1) && IsBlack(m2) && IsBlack(m3)
                    && m1.Low >= first.Low && m2.Low >= first.Low && m3.Low >= first.Low
                    && c.Close > first.Close)
                {
                    Add(CandlePatternType.RisingThreeMethods, true, reversal: false);
                }
                else if (IsBlack(first) && !IsSmallBody(first) && IsBlack(c)
                         && IsWhite(m1) && IsWhite(m2) && IsWhite(m3)
                         && m1.High <= first.High && m2.High <= first.High && m3.High <= first.High
                         && c.Close < first.Close)
                {
                    Add(CandlePatternType.FallingThreeMethods, false, reversal: false);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Trend prima della barra <paramref name="index"/>: +1 avanzata, -1 declino, 0 laterale.
    /// Misurato come variazione % delle chiusure sulle <see cref="TrendLookback"/> barre precedenti.
    /// </summary>
    private int TrendBefore(IReadOnlyList<OhlcvData> candles, int index)
    {
        var end = index - 1;
        var start = end - TrendLookback;
        if (start < 0 || candles[start].Close <= 0m) return 0;

        var change = (candles[end].Close - candles[start].Close) / candles[start].Close * 100m;
        if (change >= TrendMinMovePercent) return 1;
        if (change <= -TrendMinMovePercent) return -1;
        return 0;
    }

    private static decimal Body(OhlcvData c) => Math.Abs(c.Close - c.Open);
    private static decimal Range(OhlcvData c) => c.High - c.Low;
    private static decimal UpperWick(OhlcvData c) => c.High - Math.Max(c.Open, c.Close);
    private static decimal LowerWick(OhlcvData c) => Math.Min(c.Open, c.Close) - c.Low;
    private static bool IsWhite(OhlcvData c) => c.Close > c.Open;
    private static bool IsBlack(OhlcvData c) => c.Close < c.Open;

    private bool IsDoji(OhlcvData c)
        => Range(c) > 0m && Body(c) <= Range(c) * DojiBodyMaxPercent / 100m;

    private bool IsSmallBody(OhlcvData c)
        => Range(c) == 0m || Body(c) <= Range(c) * SpinningTopBodyMaxPercent / 100m;

    private bool IsSpinningTop(OhlcvData c)
    {
        var range = Range(c);
        if (range == 0m) return false;
        var body = Body(c);
        // Corpo piccolo (ma non doji) e ombre significative su entrambi i lati.
        return body > range * DojiBodyMaxPercent / 100m
               && body <= range * SpinningTopBodyMaxPercent / 100m
               && UpperWick(c) >= body && LowerWick(c) >= body;
    }

    /// <summary>Forma a martello: ombra inferiore lunga (>= 2 corpi), ombra superiore trascurabile.</summary>
    private bool IsHammerShape(OhlcvData c)
    {
        var body = Body(c);
        var range = Range(c);
        if (range == 0m || body == 0m) return false;
        return LowerWick(c) >= body * 2m && UpperWick(c) <= body
               && body <= range * 0.4m;
    }

    /// <summary>Forma a stella cadente: ombra superiore lunga, ombra inferiore trascurabile.</summary>
    private bool IsShootingStarShape(OhlcvData c)
    {
        var body = Body(c);
        var range = Range(c);
        if (range == 0m || body == 0m) return false;
        return UpperWick(c) >= body * 2m && LowerWick(c) <= body
               && body <= range * 0.4m;
    }

    /// <summary>Il corpo di <paramref name="c"/> ingloba completamente il corpo di <paramref name="p"/>.</summary>
    private static bool Engulfs(OhlcvData c, OhlcvData p)
        => Math.Max(c.Open, c.Close) > Math.Max(p.Open, p.Close)
           && Math.Min(c.Open, c.Close) < Math.Min(p.Open, p.Close);

    /// <summary>Il corpo di <paramref name="c"/> e' interamente dentro il corpo di <paramref name="p"/>.</summary>
    private static bool IsInsideBody(OhlcvData c, OhlcvData p)
        => Math.Max(c.Open, c.Close) <= Math.Max(p.Open, p.Close)
           && Math.Min(c.Open, c.Close) >= Math.Min(p.Open, p.Close);

    private static bool OpensWithinBody(OhlcvData c, OhlcvData p)
        => c.Open >= Math.Min(p.Open, p.Close) && c.Open <= Math.Max(p.Open, p.Close);
}
