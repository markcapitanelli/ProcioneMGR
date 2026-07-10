using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace ProcioneMGR.Services.ML;

/// <summary>Come combinare le predizioni dei modelli base in una singola predizione.</summary>
public enum StackingMode
{
    /// <summary>Media semplice (pesi uguali). Robusto, nessun rischio di overfit del meta-livello.</summary>
    Average,

    /// <summary>Pesi ∝ 1/RMSE su un holdout temporale (i modelli migliori pesano di più).</summary>
    InverseRmse,

    /// <summary>Meta-learner ridge NON-NEGATIVO (λ scelto per CV) su predizioni OUT-OF-FOLD (purged CV): lo stacking "corretto".</summary>
    StackedRidge,
}

/// <summary>
/// <b>Ensemble di modelli a livello di PREDIZIONE</b> (stacking), non di strategia (rif.
/// <c>docs/ROADMAP-QLIB.md §1.8</c>). Implementa <see cref="IReturnPredictor"/>, quindi si inserisce
/// senza modifiche in tutto ciò che consuma quell'interfaccia (<c>MlStrategy</c>, <c>SavedMlModel</c>
/// con <c>ModelType="Stacked"</c>, /ml, /optimization, /ensemble) — stesso pattern con cui
/// <c>MlpReturnPredictor</c> si è inserito.
///
/// La predizione finale è una combinazione lineare delle predizioni dei modelli base:
/// <c>ŷ = intercetta + Σ wᵢ·baseᵢ(x)</c>. I pesi si stimano secondo <see cref="StackingMode"/>. Per
/// <see cref="StackingMode.StackedRidge"/> si usano predizioni OUT-OF-FOLD ottenute con
/// <see cref="IPurgedTimeSeriesCv"/>: nessun modello base vede le proprie predizioni di training nel
/// meta-training (niente leakage).
/// </summary>
public sealed class StackedReturnPredictor : IReturnPredictor
{
    public string Name => "Stacked";
    public bool IsFitted { get; private set; }

    private List<string> _baseTypes;
    private StackingMode _mode;
    private readonly double _ridgeLambda;
    private readonly int _cvFolds;
    private readonly IPurgedTimeSeriesCv _cv;

    private IReturnPredictor[] _bases = [];
    private double[] _weights = [];
    private double _intercept;
    private int _featureCount;
    private MLContext? _mlContext;

    /// <summary>Costruttore per l'ADDESTRAMENTO: base scelti dall'utente + modalità.</summary>
    public StackedReturnPredictor(
        IEnumerable<string> baseTypes,
        StackingMode mode = StackingMode.StackedRidge,
        IPurgedTimeSeriesCv? cv = null,
        double ridgeLambda = 1.0,
        int cvFolds = 5)
    {
        _baseTypes = baseTypes?.ToList() ?? [];
        if (_baseTypes.Count == 0) throw new ArgumentException("Serve almeno un modello base.", nameof(baseTypes));
        _mode = mode;
        _cv = cv ?? new PurgedTimeSeriesCv();
        _ridgeLambda = ridgeLambda;
        _cvFolds = Math.Max(2, cvFolds);
    }

    /// <summary>Costruttore per il CARICAMENTO: lo stato reale arriva da <see cref="Load"/>.</summary>
    public StackedReturnPredictor(IPurgedTimeSeriesCv? cv = null)
    {
        _baseTypes = [];
        _mode = StackingMode.Average;
        _cv = cv ?? new PurgedTimeSeriesCv();
        _ridgeLambda = 1.0;
        _cvFolds = 5;
    }

    public void Fit(MLContext mlContext, IDataView trainingData)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(trainingData);
        _mlContext = mlContext;

        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(trainingData, reuseRowObject: false).ToList();
        _featureCount = ((VectorDataViewType)trainingData.Schema["Features"].Type).Size;
        var n = rows.Count;
        var k = _baseTypes.Count;

        // Pesi del meta-livello secondo la modalità (fallback ad Average se i dati sono pochi).
        _weights = new double[k];
        _intercept = 0d;
        if (_mode == StackingMode.Average || n < _cvFolds * 4)
        {
            for (var i = 0; i < k; i++) _weights[i] = 1d / k;
        }
        else if (_mode == StackingMode.InverseRmse)
        {
            ComputeInverseRmseWeights(mlContext, rows);
        }
        else // StackedRidge
        {
            ComputeStackedRidgeWeights(mlContext, rows);
        }

        // Modelli base FINALI: addestrati su tutto il train (usati poi in Predict).
        DisposeBases();
        _bases = new IReturnPredictor[k];
        for (var i = 0; i < k; i++)
        {
            _bases[i] = ReturnPredictorCatalog.CreateBase(_baseTypes[i]);
            _bases[i].Fit(mlContext, MlDatasetView.Create(mlContext, rows, _featureCount));
        }

        IsFitted = true;
    }

    public float Predict(float[] features)
    {
        if (!IsFitted) throw new InvalidOperationException("Lo stacking non è stato addestrato (Fit) né caricato (Load).");
        var sum = _intercept;
        for (var i = 0; i < _bases.Length; i++) sum += _weights[i] * _bases[i].Predict(features);
        return (float)sum;
    }

    // --- Stima dei pesi ----------------------------------------------------------------------

    private void ComputeInverseRmseWeights(MLContext mlContext, List<FeatureRow> rows)
    {
        // Holdout temporale semplice: primi 80% train, ultimi 20% validation.
        var split = (int)(rows.Count * 0.8);
        var train = rows.Take(split).ToList();
        var valid = rows.Skip(split).ToList();

        var invRmse = new double[_baseTypes.Count];
        double total = 0d;
        for (var i = 0; i < _baseTypes.Count; i++)
        {
            using var model = ReturnPredictorCatalog.CreateBase(_baseTypes[i]);
            model.Fit(mlContext, MlDatasetView.Create(mlContext, train, _featureCount));
            double se = 0d;
            foreach (var r in valid) { var e = model.Predict(r.Features) - r.Label; se += e * e; }
            var rmse = Math.Sqrt(se / Math.Max(1, valid.Count));
            invRmse[i] = 1d / Math.Max(rmse, 1e-9);
            total += invRmse[i];
        }
        for (var i = 0; i < _weights.Length; i++) _weights[i] = total > 0d ? invRmse[i] / total : 1d / _weights.Length;
    }

    private void ComputeStackedRidgeWeights(MLContext mlContext, List<FeatureRow> rows)
    {
        var n = rows.Count;
        var k = _baseTypes.Count;
        var purge = Math.Max(1, n / (_cvFolds * 10));
        var splits = _cv.Split(n, _cvFolds, purge, embargoPeriods: purge);

        // Matrice OUT-OF-FOLD: oof[riga, modello] = predizione del modello (mai addestrato su quella riga).
        var oof = new double[n, k];
        var covered = new bool[n];
        foreach (var split in splits)
        {
            var trainRows = split.TrainIndices.Select(i => rows[i]).ToList();
            if (trainRows.Count < 4) continue;
            var trainView = MlDatasetView.Create(mlContext, trainRows, _featureCount);

            for (var b = 0; b < k; b++)
            {
                using var model = ReturnPredictorCatalog.CreateBase(_baseTypes[b]);
                model.Fit(mlContext, trainView);
                foreach (var idx in split.TestIndices)
                {
                    oof[idx, b] = model.Predict(rows[idx].Features);
                    covered[idx] = true;
                }
            }
        }

        // Meta-learner su righe coperte: pesi NON-NEGATIVI + λ scelto per cross-validation.
        var used = Enumerable.Range(0, n).Where(i => covered[i]).ToList();
        if (used.Count <= k)
        {
            for (var i = 0; i < _weights.Length; i++) _weights[i] = 1d / _weights.Length;
            return;
        }

        // Matrice delle predizioni base OOF + target sulle righe coperte.
        var basePreds = new double[used.Count][];
        var targets = new double[used.Count];
        for (var p = 0; p < used.Count; p++)
        {
            var idx = used[p];
            basePreds[p] = new double[k];
            for (var b = 0; b < k; b++) basePreds[p][b] = oof[idx, b];
            targets[p] = rows[idx].Label;
        }

        // λ per CV interna sul livello meta (le OOF sono già out-of-fold rispetto ai modelli base):
        // evita di fissare arbitrariamente la regolarizzazione (con λ fisso ≈ peso poco robusto).
        var lambda = SelectLambdaByCv(basePreds, targets, k, _ridgeLambda);
        (_weights, _intercept) = FitNonNegativeRidge(basePreds, targets, lambda);
    }

    /// <summary>
    /// Ridge NON-NEGATIVO (pesi dei base ≥ 0, intercetta libera) via coordinate descent sulle normali
    /// equazioni: minimizza ||y − Xβ||² + λ·Σ_{j≥1} β_j² con X = [1 | predizioni base]. I pesi negativi
    /// nello stacking estrapolano male fuori campione; vincolarli a ≥0 (combinazione conica dei base) è
    /// la scelta robusta standard. Puro e deterministico (esposto per test).
    /// </summary>
    /// <param name="basePredictions">Per ogni riga, il vettore delle k predizioni dei modelli base.</param>
    /// <param name="targets">Target allineati per indice.</param>
    public static (double[] Weights, double Intercept) FitNonNegativeRidge(double[][] basePredictions, double[] targets, double lambda)
    {
        ArgumentNullException.ThrowIfNull(basePredictions);
        ArgumentNullException.ThrowIfNull(targets);
        var k = basePredictions.Length > 0 ? basePredictions[0].Length : 0;
        var m = k + 1;

        var ata = new double[m, m];
        var aty = new double[m];
        for (var i = 0; i < targets.Length; i++)
        {
            var x = new double[m];
            x[0] = 1d;
            for (var b = 0; b < k; b++) x[b + 1] = basePredictions[i][b];
            var y = targets[i];
            for (var r = 0; r < m; r++)
            {
                aty[r] += x[r] * y;
                for (var c = 0; c < m; c++) ata[r, c] += x[r] * x[c];
            }
        }

        var beta = new double[m];
        for (var iter = 0; iter < 500; iter++)
        {
            var maxDelta = 0d;
            for (var j = 0; j < m; j++)
            {
                var num = aty[j];
                for (var c = 0; c < m; c++) if (c != j) num -= ata[j, c] * beta[c];
                var denom = ata[j, j] + (j >= 1 ? lambda : 0d); // λ solo sui pesi, non sull'intercetta
                if (denom < 1e-15) continue;
                var bj = num / denom;
                if (j >= 1 && bj < 0d) bj = 0d; // proiezione di non-negatività
                maxDelta = Math.Max(maxDelta, Math.Abs(bj - beta[j]));
                beta[j] = bj;
            }
            if (maxDelta < 1e-10) break;
        }

        var w = new double[k];
        for (var b = 0; b < k; b++) w[b] = beta[b + 1];
        return (w, beta[0]);
    }

    /// <summary>K-fold sul livello meta per scegliere λ dalla griglia che minimizza l'MSE di validazione.</summary>
    internal static double SelectLambdaByCv(double[][] basePredictions, double[] targets, int k, double fallbackLambda)
    {
        double[] lambdas = [0.01, 0.03, 0.1, 0.3, 1.0, 3.0, 10.0, 30.0, 100.0];
        const int folds = 5;

        var bestLambda = fallbackLambda;
        var bestErr = double.PositiveInfinity;
        foreach (var lam in lambdas)
        {
            double err = 0d;
            var count = 0;
            for (var f = 0; f < folds; f++)
            {
                var trainX = new List<double[]>();
                var trainY = new List<double>();
                var valIdx = new List<int>();
                for (var p = 0; p < targets.Length; p++)
                {
                    if (p % folds == f) valIdx.Add(p);
                    else { trainX.Add(basePredictions[p]); trainY.Add(targets[p]); }
                }
                if (trainY.Count <= k + 1 || valIdx.Count == 0) continue;

                var (w, b0) = FitNonNegativeRidge([.. trainX], [.. trainY], lam);
                foreach (var p in valIdx)
                {
                    var pred = b0;
                    for (var b = 0; b < k; b++) pred += w[b] * basePredictions[p][b];
                    var e = pred - targets[p];
                    err += e * e;
                    count++;
                }
            }
            if (count == 0) continue;
            var mse = err / count;
            if (mse < bestErr) { bestErr = mse; bestLambda = lam; }
        }
        return bestLambda;
    }

    // --- Persistenza (container ZIP: meta + un blob per modello base) -------------------------

    private sealed record StackMeta(List<string> BaseTypes, string Mode, double[] Weights, double Intercept, int FeatureCount);

    public void Save(MLContext mlContext, string path)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        if (!IsFitted) throw new InvalidOperationException("Nessuno stacking addestrato da salvare.");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var meta = new StackMeta(_baseTypes, _mode.ToString(), _weights, _intercept, _featureCount);
        WriteEntry(archive, "meta.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta)));

        for (var i = 0; i < _bases.Length; i++)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"stack_base_{Guid.NewGuid():N}.bin");
            try
            {
                _bases[i].Save(mlContext, tmp);
                WriteEntry(archive, $"base_{i}.bin", File.ReadAllBytes(tmp));
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    public void Load(MLContext mlContext, string path)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        _mlContext = mlContext;

        using var archive = ZipFile.OpenRead(path);
        var meta = JsonSerializer.Deserialize<StackMeta>(ReadEntry(archive, "meta.json"))
                   ?? throw new InvalidOperationException("meta.json mancante nel modello Stacked.");

        _baseTypes = meta.BaseTypes;
        _mode = Enum.TryParse<StackingMode>(meta.Mode, out var mode) ? mode : StackingMode.Average;
        _weights = meta.Weights;
        _intercept = meta.Intercept;
        _featureCount = meta.FeatureCount;

        DisposeBases();
        _bases = new IReturnPredictor[_baseTypes.Count];
        for (var i = 0; i < _baseTypes.Count; i++)
        {
            _bases[i] = ReturnPredictorCatalog.CreateBase(_baseTypes[i]);
            var tmp = Path.Combine(Path.GetTempPath(), $"stack_base_load_{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(tmp, ReadEntry(archive, $"base_{i}.bin"));
                _bases[i].Load(mlContext, tmp);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        IsFitted = true;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Voce '{name}' mancante nel container Stacked.");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // --- Feature importance (permutazione sull'ensemble via Predict) --------------------------

    public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(evaluationData);
        ArgumentNullException.ThrowIfNull(featureNames);
        if (!IsFitted) throw new InvalidOperationException("Lo stacking non è stato addestrato né caricato.");

        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(evaluationData, reuseRowObject: false).ToList();
        var labels = rows.Select(r => (double)r.Label).ToArray();
        var basePreds = rows.Select(r => (double)Predict(r.Features)).ToArray();
        var baselineR2 = RSquared(basePreds, labels);

        var rnd = new Random(42);
        const int permutations = 5;
        var results = new List<FeatureImportance>(featureNames.Count);
        for (var f = 0; f < featureNames.Count; f++)
        {
            var drops = new double[permutations];
            for (var p = 0; p < permutations; p++)
            {
                var shuffled = rows.Select(r => r.Features[f]).ToArray();
                for (var i = shuffled.Length - 1; i > 0; i--) { var j = rnd.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]); }

                var permPreds = new double[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var vec = (float[])rows[i].Features.Clone();
                    vec[f] = shuffled[i];
                    permPreds[i] = Predict(vec);
                }
                drops[p] = baselineR2 - RSquared(permPreds, labels);
            }
            var mean = drops.Average();
            var variance = drops.Length > 1 ? drops.Sum(d => (d - mean) * (d - mean)) / (drops.Length - 1) : 0d;
            results.Add(new FeatureImportance(featureNames[f], mean, Math.Sqrt(variance)));
        }
        return results.OrderByDescending(r => r.MeanDecreaseInRSquared).ToList();
    }

    private static double RSquared(double[] pred, double[] actual)
    {
        var meanY = actual.Average();
        double ssRes = 0d, ssTot = 0d;
        for (var i = 0; i < actual.Length; i++)
        {
            ssRes += (actual[i] - pred[i]) * (actual[i] - pred[i]);
            ssTot += (actual[i] - meanY) * (actual[i] - meanY);
        }
        return ssTot <= 0d ? 0d : 1d - ssRes / ssTot;
    }

    private void DisposeBases()
    {
        foreach (var b in _bases) b?.Dispose();
        _bases = [];
    }

    public void Dispose() => DisposeBases();
}
