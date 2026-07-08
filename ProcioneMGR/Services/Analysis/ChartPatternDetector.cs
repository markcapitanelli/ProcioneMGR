using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>Tipi di pattern grafici di inversione (McAllen, cap. 9-10).</summary>
public enum ChartPatternType
{
    DoubleTop,
    DoubleBottom,
    HeadAndShoulders,
    InverseHeadAndShoulders,
}

/// <summary>
/// Pattern grafico individuato dai pivot. <c>Confirmed</c> = il prezzo ha completato il
/// pattern chiudendo oltre la neckline (senza conferma e' solo un'ipotesi, come da libro).
/// </summary>
public sealed record ChartPatternMatch(
    ChartPatternType Type,
    int StartIndex,
    int EndIndex,
    /// <summary>Neckline / livello di conferma: chiusura oltre questo prezzo completa il pattern.</summary>
    decimal Neckline,
    bool Confirmed,
    int? ConfirmationIndex,
    /// <summary>true = implicazione rialzista (double bottom, H&S inverso).</summary>
    bool IsBullish);

/// <summary>
/// Riconoscimento dei pattern grafici di inversione dai punti di swing (McAllen cap. 9-10):
/// Double Top/Bottom (due picchi/valli allo stesso livello, conferma sotto/sopra il trough
/// centrale) e Head &amp; Shoulders dritto/inverso (tre picchi con il centrale piu' estremo,
/// conferma alla violazione della neckline). La conferma volumetrica va verificata a parte
/// con <see cref="SupportResistanceAnalyzer"/> (breakout a basso volume = sospetto).
/// </summary>
public sealed class ChartPatternDetector
{
    /// <summary>Tolleranza % tra i due picchi (o le due spalle) perche' siano "allo stesso livello".</summary>
    public decimal PeakTolerancePercent { get; init; } = 3m;

    /// <summary>Profondita' minima del trough centrale in % (evita pattern piatti insignificanti).</summary>
    public decimal MinDepthPercent { get; init; } = 2m;

    private readonly SupportResistanceAnalyzer _pivotFinder;

    public ChartPatternDetector(int pivotWindow = 3)
    {
        _pivotFinder = new SupportResistanceAnalyzer { PivotWindow = pivotWindow };
    }

    public IReadOnlyList<ChartPatternMatch> Detect(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var pivots = _pivotFinder.FindPivots(candles);
        var result = new List<ChartPatternMatch>();
        result.AddRange(DetectDouble(candles, pivots, isTop: true));
        result.AddRange(DetectDouble(candles, pivots, isTop: false));
        result.AddRange(DetectHeadAndShoulders(candles, pivots, inverse: false));
        result.AddRange(DetectHeadAndShoulders(candles, pivots, inverse: true));
        return result.OrderBy(p => p.EndIndex).ToList();
    }

    /// <summary>Double Top: pivot high - pivot low - pivot high con i due massimi entro tolleranza.</summary>
    private List<ChartPatternMatch> DetectDouble(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<SwingPoint> pivots, bool isTop)
    {
        var result = new List<ChartPatternMatch>();
        var ordered = pivots.OrderBy(p => p.Index).ToList();

        for (var i = 0; i + 2 < ordered.Count; i++)
        {
            var a = ordered[i];
            var mid = ordered[i + 1];
            var b = ordered[i + 2];

            // Sequenza alternata: estremo - contro-estremo - estremo.
            if (isTop && (!a.IsHigh || mid.IsHigh || !b.IsHigh)) continue;
            if (!isTop && (a.IsHigh || !mid.IsHigh || b.IsHigh)) continue;
            if (a.Price <= 0m) continue;

            var peakDiff = Math.Abs(b.Price - a.Price) / a.Price * 100m;
            if (peakDiff > PeakTolerancePercent) continue;

            var reference = isTop ? Math.Max(a.Price, b.Price) : Math.Min(a.Price, b.Price);
            var depth = Math.Abs(reference - mid.Price) / reference * 100m;
            if (depth < MinDepthPercent) continue;

            var neckline = mid.Price;
            var (confirmed, confirmIndex) = FindConfirmation(candles, b.Index + 1, neckline, breakDown: isTop);

            result.Add(new ChartPatternMatch(
                isTop ? ChartPatternType.DoubleTop : ChartPatternType.DoubleBottom,
                StartIndex: a.Index,
                EndIndex: b.Index,
                Neckline: neckline,
                Confirmed: confirmed,
                ConfirmationIndex: confirmIndex,
                IsBullish: !isTop));
        }
        return result;
    }

    /// <summary>
    /// Head &amp; Shoulders: cinque swing alternati high-low-high-low-high con la testa piu' alta
    /// delle spalle e spalle entro tolleranza. Neckline = il piu' basso dei due trough (versione
    /// conservativa). Conferma = chiusura sotto la neckline dopo la spalla destra.
    /// </summary>
    private List<ChartPatternMatch> DetectHeadAndShoulders(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<SwingPoint> pivots, bool inverse)
    {
        var result = new List<ChartPatternMatch>();
        var ordered = pivots.OrderBy(p => p.Index).ToList();

        for (var i = 0; i + 4 < ordered.Count; i++)
        {
            var s1 = ordered[i];     // spalla sinistra
            var t1 = ordered[i + 1]; // primo trough
            var head = ordered[i + 2];
            var t2 = ordered[i + 3]; // secondo trough
            var s2 = ordered[i + 4]; // spalla destra

            var wantHigh = !inverse;
            if (s1.IsHigh != wantHigh || head.IsHigh != wantHigh || s2.IsHigh != wantHigh) continue;
            if (t1.IsHigh == wantHigh || t2.IsHigh == wantHigh) continue;
            if (s1.Price <= 0m) continue;

            // La testa deve superare entrambe le spalle; spalle entro tolleranza tra loro.
            var headBeyond = inverse
                ? head.Price < s1.Price && head.Price < s2.Price
                : head.Price > s1.Price && head.Price > s2.Price;
            if (!headBeyond) continue;
            if (Math.Abs(s2.Price - s1.Price) / s1.Price * 100m > PeakTolerancePercent * 2m) continue;

            var headDepth = Math.Abs(head.Price - (inverse ? Math.Min(t1.Price, t2.Price) : Math.Max(t1.Price, t2.Price)))
                            / head.Price * 100m;
            if (headDepth < MinDepthPercent) continue;

            var neckline = inverse ? Math.Max(t1.Price, t2.Price) : Math.Min(t1.Price, t2.Price);
            var (confirmed, confirmIndex) = FindConfirmation(candles, s2.Index + 1, neckline, breakDown: !inverse);

            result.Add(new ChartPatternMatch(
                inverse ? ChartPatternType.InverseHeadAndShoulders : ChartPatternType.HeadAndShoulders,
                StartIndex: s1.Index,
                EndIndex: s2.Index,
                Neckline: neckline,
                Confirmed: confirmed,
                ConfirmationIndex: confirmIndex,
                IsBullish: inverse));
        }
        return result;
    }

    private static (bool Confirmed, int? Index) FindConfirmation(
        IReadOnlyList<OhlcvData> candles, int fromIndex, decimal neckline, bool breakDown)
    {
        for (var i = Math.Max(fromIndex, 0); i < candles.Count; i++)
        {
            if (breakDown ? candles[i].Close < neckline : candles[i].Close > neckline)
            {
                return (true, i);
            }
        }
        return (false, null);
    }
}
