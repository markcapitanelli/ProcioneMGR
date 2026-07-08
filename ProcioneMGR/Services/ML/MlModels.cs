using Microsoft.ML;
using Microsoft.ML.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Un fattore alpha con i suoi parametri, associato a un nome di feature stabile (usato nelle
/// colonne del dataset e nella feature-importance dei modelli). Più parametrizzazioni dello
/// stesso fattore (es. Momentum a lookback diversi) sono feature distinte.
/// </summary>
public sealed record FactorSpec(string FeatureName, IAlphaFactor Factor, IReadOnlyDictionary<string, decimal> Parameters);

/// <summary>Riga del dataset supervisionato: vettore di feature (fattori) + target (rendimento forward).</summary>
public sealed class FeatureRow
{
    public float[] Features { get; set; } = Array.Empty<float>();
    public float Label { get; set; }
}

/// <summary>
/// Dataset supervisionato pronto per l'addestramento: righe allineate temporalmente (necessario
/// per <see cref="IPurgedTimeSeriesCv"/>, che opera per indice di riga). La conversione a
/// <see cref="IDataView"/> di ML.NET è on-demand tramite <see cref="ToDataView(MLContext)"/>,
/// così il chiamante controlla quale <see cref="MLContext"/> usare (stesso context per
/// training/predict = determinismo).
/// </summary>
public sealed class MlDataset
{
    public required IReadOnlyList<FeatureRow> Rows { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }
    public required IReadOnlyList<DateTime> Timestamps { get; init; }

    public int RowCount => Rows.Count;
    public int FeatureCount => FeatureNames.Count;

    /// <summary>Vista ML.NET dell'intero dataset.</summary>
    public IDataView ToDataView(MLContext mlContext) => MlDatasetView.Create(mlContext, Rows, FeatureCount);

    /// <summary>Vista ML.NET di un sottoinsieme di righe (per i fold della cross-validation).</summary>
    public IDataView ToDataView(MLContext mlContext, IReadOnlyList<int> indices)
        => MlDatasetView.Create(mlContext, indices.Select(i => Rows[i]), FeatureCount);
}

/// <summary>Costruzione dell'<see cref="IDataView"/> ML.NET da righe con vettore feature a dimensione dinamica.</summary>
public static class MlDatasetView
{
    public static IDataView Create(MLContext mlContext, IEnumerable<FeatureRow> rows, int featureCount)
    {
        var schemaDefinition = SchemaDefinition.Create(typeof(FeatureRow));
        schemaDefinition["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, featureCount);
        return mlContext.Data.LoadFromEnumerable(rows, schemaDefinition);
    }
}

/// <summary>Uno split train/test prodotto dalla cross-validation temporale (indici di riga in <see cref="MlDataset"/>).</summary>
public sealed record CvSplit(int Fold, IReadOnlyList<int> TrainIndices, IReadOnlyList<int> TestIndices);

/// <summary>
/// Importanza di una feature per un modello addestrato, da permutation importance: quanto
/// peggiora la qualità delle predizioni (calo di R²) quando quella feature viene mescolata
/// casualmente, a parità delle altre. Più alta -> la feature conta di più per il modello.
/// </summary>
public sealed record FeatureImportance(string FeatureName, double MeanDecreaseInRSquared, double StdDevDecreaseInRSquared);

/// <summary>
/// DTO serializzabile di un <see cref="FactorSpec"/> (l'interfaccia <c>IAlphaFactor</c> non lo
/// è). Usato per persistere/ricostruire i fattori di un <c>SavedMlModel</c>: il nome del
/// fattore si ricrea via <c>IAlphaFactorFactory.Create</c>, i parametri sono già serializzabili.
/// </summary>
public sealed record SavedFactorSpecDto(string FeatureName, string FactorName, Dictionary<string, decimal> Parameters);
