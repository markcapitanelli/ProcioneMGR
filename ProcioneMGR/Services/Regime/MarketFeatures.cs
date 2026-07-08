namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Features che caratterizzano il CONTESTO di mercato a una data candela.
/// Tutte calcolabili in tempo reale usando SOLO dati fino alla candela corrente
/// (nessun look-ahead). NON predicono il prezzo: descrivono il regime.
/// </summary>
public class MarketFeatures
{
    public DateTime Timestamp { get; set; }
    public decimal Price { get; set; }

    /// <summary>Std dev dei rendimenti ultimi 20 periodi, annualizzata.</summary>
    public decimal Volatility { get; set; }

    /// <summary>|Slope| della regressione lineare su 50 periodi, normalizzata sul prezzo medio.</summary>
    public decimal TrendStrength { get; set; }

    /// <summary>Segno della slope (+1 up, -1 down, 0 flat).</summary>
    public decimal TrendDirection { get; set; }

    /// <summary>Volume corrente / volume medio ultimi 20 periodi.</summary>
    public decimal VolumeRatio { get; set; }

    /// <summary>ATR(14) / Price.</summary>
    public decimal AtrNormalized { get; set; }

    /// <summary>RSI(14) medio ultimi 5 periodi.</summary>
    public decimal RsiLevel { get; set; }

    /// <summary>Media di (High - Low) / Close sugli ultimi 10 periodi.</summary>
    public decimal HighLowRange { get; set; }

    /// <summary>(Price - SMA50) / SMA50.</summary>
    public decimal DistanceFromMa { get; set; }

    // --- Label (solo analisi, NON usato nel clustering) ---
    public int? RegimeId { get; set; }
    public string? RegimeLabel { get; set; }

    /// <summary>Tutte le 8 feature numeriche (per analisi/profili), nell'ordine canonico.</summary>
    public double[] ToVector() =>
    [
        (double)Volatility,
        (double)TrendStrength,
        (double)TrendDirection,
        (double)VolumeRatio,
        (double)AtrNormalized,
        (double)RsiLevel,
        (double)HighLowRange,
        (double)DistanceFromMa,
    ];

    /// <summary>
    /// Sottoinsieme di feature usato per il CLUSTERING: le 4 dimensioni ortogonali che
    /// definiscono il regime (intensità + direzione del trend), evitando le feature
    /// ridondanti/rumorose (ATR≈Volatility, RSI, HighLowRange, VolumeRatio) che abbassano
    /// la qualità del clustering. Le altre restano disponibili per i profili.
    /// </summary>
    public double[] ToClusteringVector() =>
    [
        (double)Volatility,
        (double)TrendStrength,
        (double)TrendDirection,
        (double)DistanceFromMa,
    ];
}

/// <summary>Parametri di standardizzazione (mean/std per feature) per inference futura.</summary>
public class FeatureScaling
{
    public double[] Means { get; set; } = [];
    public double[] Stds { get; set; } = [];

    /// <summary>Nomi delle feature usate per il clustering (allineate a <see cref="MarketFeatures.ToClusteringVector"/>).</summary>
    public static readonly string[] FeatureNames =
    [
        "Volatility", "TrendStrength", "TrendDirection", "DistanceFromMa",
    ];

    public const int FeatureCount = 4;

    /// <summary>Standardizza un vettore feature con i parametri salvati (z = (x-mean)/std).</summary>
    public float[] Transform(double[] vector)
    {
        var z = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            var std = Stds[i] == 0d ? 1d : Stds[i];
            z[i] = (float)((vector[i] - Means[i]) / std);
        }
        return z;
    }
}

/// <summary>Standardizzazione di un insieme di features (mean=0, std=1 per colonna).</summary>
public static class FeatureNormalizer
{
    public static (float[][] Matrix, FeatureScaling Scaling) NormalizeFeatures(IReadOnlyList<MarketFeatures> features)
    {
        var n = features.Count;
        var k = FeatureScaling.FeatureCount;
        var means = new double[k];
        var stds = new double[k];

        var vectors = features.Select(f => f.ToClusteringVector()).ToArray();

        for (var j = 0; j < k; j++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++) sum += vectors[i][j];
            means[j] = n > 0 ? sum / n : 0d;

            double sumSq = 0;
            for (var i = 0; i < n; i++)
            {
                var d = vectors[i][j] - means[j];
                sumSq += d * d;
            }
            stds[j] = n > 0 ? Math.Sqrt(sumSq / n) : 1d;
        }

        var scaling = new FeatureScaling { Means = means, Stds = stds };
        var matrix = new float[n][];
        for (var i = 0; i < n; i++)
        {
            matrix[i] = scaling.Transform(vectors[i]);
        }
        return (matrix, scaling);
    }
}
