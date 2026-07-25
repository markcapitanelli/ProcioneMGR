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

    /// <summary>
    /// [3.8a/4.9] Breadth interna: frazione (0..1) dei simboli /USDT tracciati con chiusura sopra
    /// la PROPRIA SMA50 a questa barra. Popolata solo quando la feature è richiesta
    /// (<see cref="TrainingConfiguration.IncludeBreadthFeature"/>); 0,5 = neutro/non calcolata.
    /// </summary>
    public decimal MarketBreadth { get; set; } = 0.5m;

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
    /// ridondanti/rumorose (ATR≈Volatility, RSI, HighLowRange) che abbassano la qualità
    /// del clustering. Le altre restano disponibili per i profili.
    ///
    /// [3.8a] Il VOLUME (e la breadth interna) entrano SOLO opt-in: cambiano le etichette dei
    /// regimi di tutti i modelli riaddestrati, quindi la scelta è esplicita in
    /// <see cref="TrainingConfiguration"/> e viaggia col modello via <see cref="FeatureScaling.Names"/>.
    /// </summary>
    public double[] ToClusteringVector(bool includeVolume = false, bool includeBreadth = false)
    {
        var vec = new List<double>(6)
        {
            (double)Volatility,
            (double)TrendStrength,
            (double)TrendDirection,
            (double)DistanceFromMa,
        };
        if (includeVolume) vec.Add((double)VolumeRatio);
        if (includeBreadth) vec.Add((double)MarketBreadth);
        return [.. vec];
    }
}

/// <summary>Parametri di standardizzazione (mean/std per feature) per inference futura.</summary>
public class FeatureScaling
{
    public double[] Means { get; set; } = [];
    public double[] Stds { get; set; } = [];

    /// <summary>
    /// [3.8a] Nomi delle feature di QUESTO modello, nell'ordine del vettore. Persistiti nel
    /// FeatureScalingJson: l'inference ricostruisce il vettore giusto dal modello stesso.
    /// Default = le 4 storiche, così i modelli salvati PRIMA del campo restano leggibili
    /// (il deserializzatore non trova "Names" e lascia il default).
    /// </summary>
    public string[] Names { get; set; } = ["Volatility", "TrendStrength", "TrendDirection", "DistanceFromMa"];

    /// <summary>Nomi delle 4 feature storiche (i modelli pre-3.8a hanno solo queste).</summary>
    public static readonly string[] FeatureNames =
    [
        "Volatility", "TrendStrength", "TrendDirection", "DistanceFromMa",
    ];

    /// <summary>Dimensione del vettore storico (baseline; i modelli 3.8a possono averne 5 o 6).</summary>
    public const int FeatureCount = 4;

    /// <summary>Nomi per la combinazione di flag richiesta (stesso ordine di <see cref="MarketFeatures.ToClusteringVector"/>).</summary>
    public static string[] NamesFor(bool includeVolume, bool includeBreadth)
    {
        var names = new List<string>(FeatureNames);
        if (includeVolume) names.Add("VolumeRatio");
        if (includeBreadth) names.Add("MarketBreadth");
        return [.. names];
    }

    /// <summary>True se il modello include la feature (per ricostruire i flag all'inference).</summary>
    public bool Uses(string featureName) => Names.Contains(featureName);

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
    public static (float[][] Matrix, FeatureScaling Scaling) NormalizeFeatures(
        IReadOnlyList<MarketFeatures> features, bool includeVolume = false, bool includeBreadth = false)
    {
        var n = features.Count;
        var names = FeatureScaling.NamesFor(includeVolume, includeBreadth);
        var k = names.Length;
        var means = new double[k];
        var stds = new double[k];

        var vectors = features.Select(f => f.ToClusteringVector(includeVolume, includeBreadth)).ToArray();

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

        var scaling = new FeatureScaling { Means = means, Stds = stds, Names = names };
        var matrix = new float[n][];
        for (var i = 0; i < n; i++)
        {
            matrix[i] = scaling.Transform(vectors[i]);
        }
        return (matrix, scaling);
    }
}
