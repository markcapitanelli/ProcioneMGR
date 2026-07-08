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

    /// <summary>Meta-learner ridge su predizioni OUT-OF-FOLD (purged CV): lo stacking "corretto".</summary>
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

        // Ridge (normali equazioni con L2 sui pesi, intercetta non regolarizzata) su righe coperte.
        var used = Enumerable.Range(0, n).Where(i => covered[i]).ToList();
        if (used.Count <= k)
        {
            for (var i = 0; i < _weights.Length; i++) _weights[i] = 1d / _weights.Length;
            return;
        }
        SolveRidge(oof, rows, used, k);
    }

    /// <summary>Risolve (XᵀX + λR)β = Xᵀy con X = [1 | predizioni base], R azzera la riga dell'intercetta.</summary>
    private void SolveRidge(double[,] oof, List<FeatureRow> rows, List<int> used, int k)
    {
        var m = k + 1; // +1 per l'intercetta (colonna di 1)
        var ata = new double[m, m];
        var aty = new double[m];

        foreach (var idx in used)
        {
            var x = new double[m];
            x[0] = 1d;
            for (var b = 0; b < k; b++) x[b + 1] = oof[idx, b];
            var y = rows[idx].Label;

            for (var r = 0; r < m; r++)
            {
                aty[r] += x[r] * y;
                for (var c = 0; c < m; c++) ata[r, c] += x[r] * x[c];
            }
        }
        for (var d = 1; d < m; d++) ata[d, d] += _ridgeLambda; // regolarizza solo i pesi, non l'intercetta

        var beta = GaussSolve(ata, aty);
        _intercept = beta[0];
        for (var b = 0; b < k; b++) _weights[b] = beta[b + 1];
    }

    /// <summary>Eliminazione di Gauss con pivot parziale su un sistema piccolo ((K+1)×(K+1)).</summary>
    private static double[] GaussSolve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-15) continue; // colonna degenere: lascia 0
            if (pivot != col) for (var j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

            var d = m[col, col];
            for (var j = col; j <= n; j++) m[col, j] /= d;
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col];
                for (var j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = m[i, n];
        return x;
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
