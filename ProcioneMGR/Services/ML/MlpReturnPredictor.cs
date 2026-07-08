using System.Text.Json;
using Microsoft.ML;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Rete neurale feed-forward (MLP) per la previsione dei rendimenti — l'essenza del cap. 17
/// di ML4T (Jansen) in C# puro, SENZA TorchSharp: un solo strato nascosto con attivazione
/// tanh, uscita lineare, addestramento con mini-batch gradient descent e L2 (weight decay).
///
/// Scelte di progetto:
///  - implementa <see cref="IReturnPredictor"/> direttamente (non <c>RegressionPredictorBase</c>,
///    che incapsula un ITransformer ML.NET): Fit legge le righe dall'IDataView, il modello vive
///    in array C# e la persistenza e' JSON (pesi + normalizzazione + config);
///  - feature standardizzate su media/deviazione del TRAIN (come SDCA: le reti sono sensibili
///    alla scala; parametri salvati per l'inferenza);
///  - deterministico a parita' di seed (inizializzazione pesi e shuffling dei batch);
///  - early stop implicito via numero fisso di epoche + weight decay (nessun validation split
///    interno: la valutazione onesta e' gia' fuori, in PurgedTimeSeriesCv / split temporale).
/// </summary>
public sealed class MlpReturnPredictor(int hiddenUnits = 16, int epochs = 200, double learningRate = 0.01, int seed = 42)
    : IReturnPredictor
{
    public string Name => "Mlp";
    public bool IsFitted { get; private set; }

    private int _featureCount;
    private double[] _featureMeans = [];
    private double[] _featureStds = [];

    // Pesi: hidden = tanh(W1 x + b1); output = W2 hidden + b2.
    private double[,] _w1 = new double[0, 0];
    private double[] _b1 = [];
    private double[] _w2 = [];
    private double _b2;

    private const double WeightDecay = 1e-4;
    private const int BatchSize = 32;

    public void Fit(MLContext mlContext, IDataView trainingData)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(trainingData);

        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(trainingData, reuseRowObject: false).ToList();
        if (rows.Count < 10)
        {
            throw new InvalidOperationException($"Dataset troppo piccolo per un MLP: {rows.Count} righe (minimo 10).");
        }
        _featureCount = rows[0].Features.Length;

        // Standardizzazione sulle statistiche del train.
        _featureMeans = new double[_featureCount];
        _featureStds = new double[_featureCount];
        for (var f = 0; f < _featureCount; f++)
        {
            double sum = 0;
            foreach (var r in rows) sum += r.Features[f];
            var mean = sum / rows.Count;
            double sumSq = 0;
            foreach (var r in rows)
            {
                var d = r.Features[f] - mean;
                sumSq += d * d;
            }
            _featureMeans[f] = mean;
            _featureStds[f] = Math.Max(Math.Sqrt(sumSq / rows.Count), 1e-8);
        }

        var inputs = rows.Select(r => Standardize(r.Features)).ToArray();
        var targets = rows.Select(r => (double)r.Label).ToArray();

        // Inizializzazione Xavier deterministica.
        var rnd = new Random(seed);
        _w1 = new double[hiddenUnits, _featureCount];
        _b1 = new double[hiddenUnits];
        _w2 = new double[hiddenUnits];
        _b2 = 0;
        var scale1 = Math.Sqrt(1.0 / _featureCount);
        var scale2 = Math.Sqrt(1.0 / hiddenUnits);
        for (var h = 0; h < hiddenUnits; h++)
        {
            for (var f = 0; f < _featureCount; f++) _w1[h, f] = (rnd.NextDouble() * 2 - 1) * scale1;
            _w2[h] = (rnd.NextDouble() * 2 - 1) * scale2;
        }

        // Mini-batch gradient descent su MSE + L2.
        var indices = Enumerable.Range(0, inputs.Length).ToArray();
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            ShuffleInPlace(indices, rnd);
            for (var start = 0; start < indices.Length; start += BatchSize)
            {
                var count = Math.Min(BatchSize, indices.Length - start);

                var gradW1 = new double[hiddenUnits, _featureCount];
                var gradB1 = new double[hiddenUnits];
                var gradW2 = new double[hiddenUnits];
                double gradB2 = 0;

                for (var k = 0; k < count; k++)
                {
                    var i = indices[start + k];
                    var x = inputs[i];

                    // Forward.
                    var hidden = new double[hiddenUnits];
                    for (var h = 0; h < hiddenUnits; h++)
                    {
                        var z = _b1[h];
                        for (var f = 0; f < _featureCount; f++) z += _w1[h, f] * x[f];
                        hidden[h] = Math.Tanh(z);
                    }
                    var output = _b2;
                    for (var h = 0; h < hiddenUnits; h++) output += _w2[h] * hidden[h];

                    // Backward (dMSE/dout = 2*(out-y), il fattore 2 e' assorbito nel learning rate).
                    var deltaOut = output - targets[i];
                    gradB2 += deltaOut;
                    for (var h = 0; h < hiddenUnits; h++)
                    {
                        gradW2[h] += deltaOut * hidden[h];
                        var deltaHidden = deltaOut * _w2[h] * (1 - hidden[h] * hidden[h]);
                        gradB1[h] += deltaHidden;
                        for (var f = 0; f < _featureCount; f++)
                        {
                            gradW1[h, f] += deltaHidden * x[f];
                        }
                    }
                }

                var lr = learningRate / count;
                for (var h = 0; h < hiddenUnits; h++)
                {
                    _b1[h] -= lr * gradB1[h];
                    _w2[h] -= lr * (gradW2[h] + WeightDecay * _w2[h]);
                    for (var f = 0; f < _featureCount; f++)
                    {
                        _w1[h, f] -= lr * (gradW1[h, f] + WeightDecay * _w1[h, f]);
                    }
                }
                _b2 -= lr * gradB2;
            }
        }

        IsFitted = true;
    }

    public float Predict(float[] features)
    {
        if (!IsFitted)
        {
            throw new InvalidOperationException("Il modello non è stato addestrato (Fit) né caricato (Load).");
        }
        ArgumentNullException.ThrowIfNull(features);
        if (features.Length != _featureCount)
        {
            throw new ArgumentException($"Attese {_featureCount} feature, ricevute {features.Length}.");
        }

        var x = Standardize(features);
        var output = _b2;
        for (var h = 0; h < _b1.Length; h++)
        {
            var z = _b1[h];
            for (var f = 0; f < _featureCount; f++) z += _w1[h, f] * x[f];
            output += _w2[h] * Math.Tanh(z);
        }
        return (float)output;
    }

    // ----------------------------------------------------------------- persistenza (JSON)

    private sealed record MlpState(
        int FeatureCount, int HiddenUnits,
        double[] FeatureMeans, double[] FeatureStds,
        double[][] W1, double[] B1, double[] W2, double B2);

    public void Save(MLContext mlContext, string path)
    {
        if (!IsFitted)
        {
            throw new InvalidOperationException("Nessun modello addestrato da salvare.");
        }

        var hidden = _b1.Length;
        var w1 = new double[hidden][];
        for (var h = 0; h < hidden; h++)
        {
            w1[h] = new double[_featureCount];
            for (var f = 0; f < _featureCount; f++) w1[h][f] = _w1[h, f];
        }
        var state = new MlpState(_featureCount, hidden, _featureMeans, _featureStds, w1, _b1, _w2, _b2);
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    public void Load(MLContext mlContext, string path)
    {
        var state = JsonSerializer.Deserialize<MlpState>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException($"File modello MLP non valido: {path}");

        _featureCount = state.FeatureCount;
        _featureMeans = state.FeatureMeans;
        _featureStds = state.FeatureStds;
        _b1 = state.B1;
        _w2 = state.W2;
        _b2 = state.B2;
        _w1 = new double[state.HiddenUnits, state.FeatureCount];
        for (var h = 0; h < state.HiddenUnits; h++)
        {
            for (var f = 0; f < state.FeatureCount; f++) _w1[h, f] = state.W1[h][f];
        }
        IsFitted = true;
    }

    // ----------------------------------------------------------------- feature importance

    public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(
        MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(mlContext);
        ArgumentNullException.ThrowIfNull(evaluationData);
        ArgumentNullException.ThrowIfNull(featureNames);
        if (!IsFitted)
        {
            throw new InvalidOperationException("Il modello non è stato addestrato (Fit) né caricato (Load).");
        }

        // Stessa permutation importance manuale di RegressionPredictorBase, con R² calcolato
        // sulle predizioni dirette del MLP (niente pipeline ML.NET da trasformare).
        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(evaluationData, reuseRowObject: false).ToList();
        var baseline = RSquared(rows.Select(r => (double)Predict(r.Features)).ToArray(),
                                rows.Select(r => (double)r.Label).ToArray());
        var rnd = new Random(42);
        const int permutations = 5;

        var results = new List<FeatureImportance>(featureNames.Count);
        for (var f = 0; f < featureNames.Count; f++)
        {
            var drops = new double[permutations];
            for (var p = 0; p < permutations; p++)
            {
                var shuffled = rows.Select(r => r.Features[f]).ToArray();
                ShuffleInPlace(shuffled, rnd);

                var predictions = new double[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var vec = (float[])rows[i].Features.Clone();
                    vec[f] = shuffled[i];
                    predictions[i] = Predict(vec);
                }
                drops[p] = baseline - RSquared(predictions, rows.Select(r => (double)r.Label).ToArray());
            }

            var mean = drops.Average();
            var variance = drops.Length > 1 ? drops.Sum(d => (d - mean) * (d - mean)) / (drops.Length - 1) : 0.0;
            results.Add(new FeatureImportance(featureNames[f], mean, Math.Sqrt(variance)));
        }
        return results.OrderByDescending(r => r.MeanDecreaseInRSquared).ToList();
    }

    private static double RSquared(double[] predictions, double[] labels)
    {
        var meanLabel = labels.Average();
        double ssRes = 0, ssTot = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            ssRes += (labels[i] - predictions[i]) * (labels[i] - predictions[i]);
            ssTot += (labels[i] - meanLabel) * (labels[i] - meanLabel);
        }
        return ssTot == 0 ? 0 : 1 - ssRes / ssTot;
    }

    private double[] Standardize(float[] features)
    {
        var x = new double[_featureCount];
        for (var f = 0; f < _featureCount; f++)
        {
            x[f] = (features[f] - _featureMeans[f]) / _featureStds[f];
        }
        return x;
    }

    private static void ShuffleInPlace<T>(T[] values, Random rnd)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>Nessuna risorsa nativa: il modello vive in array gestiti.</summary>
    public void Dispose()
    {
    }
}
