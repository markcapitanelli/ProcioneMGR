namespace ProcioneMGR.Services.Indicators;

public enum IndicatorSeriesType
{
    Line,
    Histogram,
}

/// <summary>Punto (tempo in secondi Unix UTC, valore) per il grafico.</summary>
public readonly record struct IndicatorPoint(long Time, double Value);

/// <summary>
/// Una serie da sovrapporre al grafico (EMA, Bollinger, RSI, MACD, equity curve...).
/// <see cref="Scale"/> = "price" sovrappone sulla scala prezzi; "osc" la mette in un
/// riquadro inferiore (per oscillatori come RSI/MACD).
/// </summary>
public sealed class IndicatorSeries
{
    public required string Title { get; init; }
    public required string Color { get; init; }
    public IndicatorSeriesType Type { get; init; } = IndicatorSeriesType.Line;
    public string Scale { get; init; } = "price";
    public required IReadOnlyList<IndicatorPoint> Points { get; init; }

    /// <summary>Costruisce una serie allineando valori (con null) ai timestamp delle candele.</summary>
    public static IndicatorSeries FromAligned(
        string title, string color, IReadOnlyList<long> timesSec, IReadOnlyList<decimal?> values,
        IndicatorSeriesType type = IndicatorSeriesType.Line, string scale = "price")
    {
        var pts = new List<IndicatorPoint>(values.Count);
        for (var i = 0; i < values.Count && i < timesSec.Count; i++)
        {
            if (values[i].HasValue)
            {
                pts.Add(new IndicatorPoint(timesSec[i], (double)values[i]!.Value));
            }
        }
        return new IndicatorSeries { Title = title, Color = color, Type = type, Scale = scale, Points = pts };
    }
}
