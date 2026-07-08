using Microsoft.ML;
using Microsoft.ML.Data;

namespace ProcioneMGR.Services.ML;

/// <summary>Colonna di output dei trainer di regressione ML.NET.</summary>
internal sealed class PredictedReturn
{
    public float Score { get; set; }
}

/// <summary>
/// Infrastruttura comune a tutti i predittori di rendimento basati su un singolo
/// <c>ITransformer</c> di regressione ML.NET con colonne Features/Label: gestione schema
/// (vettore a dimensione dinamica), prediction engine, persistenza, permutation feature
/// importance. Le sottoclassi implementano solo <see cref="BuildPipeline"/> — la scelta del
/// trainer (SDCA, FastForest, LightGBM, ...) è l'unica cosa che le distingue.
/// </summary>
public abstract class RegressionPredictorBase : IReturnPredictor
{
    public abstract string Name { get; }
    public bool IsFitted { get; private set; }

    private ITransformer? _model;
    private PredictionEngine<FeatureRow, PredictedReturn>? _engine;
    private DataViewSchema? _inputSchema;
    private int _featureCount;

    /// <summary>Costruisce la pipeline di addestramento (eventuale pre-processing + trainer).</summary>
    protected abstract IEstimator<ITransformer> BuildPipeline(MLContext mlContext);

    public void Fit(MLContext mlContext, IDataView trainingData)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(trainingData);

        _featureCount = ((VectorDataViewType)trainingData.Schema["Features"].Type).Size;
        _inputSchema = trainingData.Schema;

        var pipeline = BuildPipeline(mlContext);
        _model = pipeline.Fit(trainingData);
        CreateEngine(mlContext);
        IsFitted = true;
    }

    public float Predict(float[] features)
    {
        if (!IsFitted || _engine is null)
        {
            throw new InvalidOperationException("Il modello non è stato addestrato (Fit) né caricato (Load).");
        }
        return _engine.Predict(new FeatureRow { Features = features }).Score;
    }

    public void Save(MLContext mlContext, string path)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        if (_model is null || _inputSchema is null)
        {
            throw new InvalidOperationException("Nessun modello addestrato da salvare.");
        }
        using var stream = File.Create(path);
        mlContext.Model.Save(_model, _inputSchema, stream);
    }

    public void Load(MLContext mlContext, string path)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        using var stream = File.OpenRead(path);
        _model = mlContext.Model.Load(stream, out var loadedSchema);
        _inputSchema = loadedSchema;
        _featureCount = ((VectorDataViewType)loadedSchema["Features"].Type).Size;
        CreateEngine(mlContext);
        IsFitted = true;
    }

    public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(evaluationData);
        ArgumentNullException.ThrowIfNull(featureNames);
        if (!IsFitted || _model is null)
        {
            throw new InvalidOperationException("Il modello non è stato addestrato (Fit) né caricato (Load).");
        }

        // Permutation importance implementata manualmente (non l'overload ML.NET PFI, che per un
        // ITransformer generico tratta l'intera colonna Features come UN solo "feature" invece
        // che per singolo slot del vettore — inutile qui, dove serve l'importanza per fattore).
        // Per ogni feature: mescola quello slot nel dataset di valutazione, rimisura R², ripeti.
        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(evaluationData, reuseRowObject: false).ToList();
        var baselineR2 = EvaluateRSquared(mlContext, evaluationData);
        var rnd = new Random(42);
        const int permutations = 5;

        var results = new List<FeatureImportance>(featureNames.Count);
        for (var f = 0; f < featureNames.Count; f++)
        {
            var drops = new double[permutations];
            for (var p = 0; p < permutations; p++)
            {
                var shuffled = rows.Select(r => r.Features[f]).ToArray();
                Shuffle(shuffled, rnd);

                var permutedRows = rows.Select((r, idx) =>
                {
                    var vec = (float[])r.Features.Clone();
                    vec[f] = shuffled[idx];
                    return new FeatureRow { Features = vec, Label = r.Label };
                });

                var permutedView = MlDatasetView.Create(mlContext, permutedRows, featureNames.Count);
                drops[p] = baselineR2 - EvaluateRSquared(mlContext, permutedView);
            }

            var mean = drops.Average();
            var variance = drops.Length > 1 ? drops.Sum(d => (d - mean) * (d - mean)) / (drops.Length - 1) : 0.0;
            results.Add(new FeatureImportance(featureNames[f], mean, Math.Sqrt(variance)));
        }
        return results.OrderByDescending(r => r.MeanDecreaseInRSquared).ToList();
    }

    private double EvaluateRSquared(MLContext mlContext, IDataView data)
    {
        var predictions = _model!.Transform(data);
        return mlContext.Regression.Evaluate(predictions, labelColumnName: "Label", scoreColumnName: "Score").RSquared;
    }

    private static void Shuffle(float[] values, Random rnd)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private void CreateEngine(MLContext mlContext)
    {
        _engine?.Dispose(); // Fit/Load richiamati una seconda volta sulla stessa istanza non devono perdere il PredictionEngine precedente.
        var schemaDefinition = SchemaDefinition.Create(typeof(FeatureRow));
        schemaDefinition["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, _featureCount);
        _engine = mlContext.Model.CreatePredictionEngine<FeatureRow, PredictedReturn>(_model!, inputSchemaDefinition: schemaDefinition);
    }

    /// <summary>
    /// Il <see cref="PredictionEngine{TSrc,TDst}"/> incapsula risorse native di ML.NET: senza
    /// Dispose, ogni predittore creato (es. uno per combo/finestra in uno sweep di Optimization,
    /// vedi <c>BacktestEngine.LoadMlStrategyAsync</c>) le perde silenziosamente.
    /// </summary>
    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
