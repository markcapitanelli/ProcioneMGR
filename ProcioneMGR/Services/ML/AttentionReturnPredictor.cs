using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Predittore di rendimento basato su <b>self-attention</b>, in C# puro SENZA TorchSharp (bivio §1.4
/// risolto verso "attention a mano", coerente col precedente <see cref="MlpReturnPredictor"/>).
/// L'input è una finestra di T timestep × F fattori (vedi <see cref="ISequencePredictor"/> e
/// <see cref="SequenceWindowing"/>). Architettura minimale ma reale:
///   X[T,F] → (standardizzazione) → embed lineare E=X·Wᵢₙ+bᵢₙ [T,D] → + positional encoding
///   → self-attention a 1 testa con residuo (Q,K,V,O) → mean-pool sui timestep → testa FFN (tanh)
///   → scalare (rendimento forward atteso).
///
/// Addestramento con backpropagation MANUALE (softmax/matmul/residuo) e mini-batch SGD con weight
/// decay e gradient clipping; deterministico a parità di seed (init pesi + shuffling). Persistenza
/// JSON (pesi + normalizzazione + config), come l'MLP — nessun ITransformer ML.NET.
/// </summary>
public sealed class AttentionReturnPredictor : IReturnPredictor, ISequencePredictor
{
    public string Name => "Attention";
    public bool IsFitted { get; private set; }

    public int WindowLength => _t;
    public int FeaturesPerStep => _f;

    // Iperparametri
    private int _t;                 // timestep (finestra)
    private int _f;                 // fattori per timestep
    private int _d;                 // dimensione di embedding (dal costruttore o dal blob al Load)
    private int _hff;               // unità nascoste della testa FFN (idem)
    private readonly int _epochs;
    private readonly double _lr;
    private readonly int _seed;
    private const double WeightDecay = 1e-4;
    private const double GradClip = 5.0;
    private const int BatchSize = 32;

    // Parametri appresi
    private double[,] _win = new double[0, 0];   // [F,D]
    private double[] _bin = [];                   // [D]
    private double[,] _wq = new double[0, 0], _wk = new double[0, 0], _wv = new double[0, 0], _wo = new double[0, 0]; // [D,D]
    private double[,] _w1 = new double[0, 0];    // [D,Hff]
    private double[] _b1 = [];                    // [Hff]
    private double[] _w2 = [];                    // [Hff]
    private double _b2;
    private double[,] _pe = new double[0, 0];    // [T,D] positional encoding (fisso)
    private double[] _mean = [], _std = [];      // [F] standardizzazione

    public AttentionReturnPredictor(int windowLength = 8, int embedDim = 16, int hiddenUnits = 16, int epochs = 150, double learningRate = 0.01, int seed = 42)
    {
        _t = Math.Max(2, windowLength);
        _d = Math.Max(2, embedDim);
        _hff = Math.Max(2, hiddenUnits);
        _epochs = Math.Max(1, epochs);
        _lr = learningRate;
        _seed = seed;
    }

    // --- Addestramento -----------------------------------------------------------------------

    public void Fit(MLContext mlContext, IDataView trainingData)
    {
        ArgumentNullException.ThrowIfNull(trainingData);
        var featureCount = ((VectorDataViewType)trainingData.Schema["Features"].Type).Size;
        if (featureCount % _t != 0)
            throw new InvalidOperationException($"La finestra ({_t}) non divide il numero di feature ({featureCount}). Usa SequenceWindowing con lo stesso windowLength.");
        _f = featureCount / _t;

        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(trainingData, reuseRowObject: false).ToList();
        var samples = rows.Select(r => Reshape(r.Features)).ToList(); // [T,F] non standardizzato
        var labels = rows.Select(r => (double)r.Label).ToArray();
        var n = samples.Count;

        ComputeStandardization(samples);
        var std = samples.Select(Standardize).ToList();

        var rng = new Random(_seed);
        InitParameters(rng);
        _pe = PositionalEncoding(_t, _d);

        var indices = Enumerable.Range(0, n).ToArray();
        for (var epoch = 0; epoch < _epochs; epoch++)
        {
            Shuffle(indices, rng);
            for (var start = 0; start < n; start += BatchSize)
            {
                var end = Math.Min(start + BatchSize, n);
                var grads = new Grads(_f, _d, _hff);
                for (var s = start; s < end; s++)
                {
                    var i = indices[s];
                    var cache = Forward(std[i]);
                    Backward(std[i], cache, labels[i], grads);
                }
                ApplyGradients(grads, end - start);
            }
        }

        IsFitted = true;
    }

    public float Predict(float[] features)
    {
        if (!IsFitted) throw new InvalidOperationException("Il modello attention non è stato addestrato (Fit) né caricato (Load).");
        if (features.Length != _t * _f) throw new ArgumentException($"Attesa una finestra di {_t}×{_f}={_t * _f} valori, ricevuti {features.Length}.");
        var x = Standardize(Reshape(features));
        return (float)Forward(x).Yhat;
    }

    // --- Forward / Backward ------------------------------------------------------------------

    private sealed class Cache
    {
        public required double[,] X;      // [T,F] standardizzato
        public required double[,] E;      // [T,D] embedding + PE
        public required double[,] Q, K, V, A, C, O, Z;
        public required double[] Zpool;   // [D]
        public required double[] H;       // [Hff]
        public double Yhat;
    }

    private Cache Forward(double[,] x)
    {
        var e = AddInPlace(AddBias(MatMul(x, _win), _bin), _pe); // E = X·Win + bin + PE  [T,D]
        var q = MatMul(e, _wq);
        var k = MatMul(e, _wk);
        var v = MatMul(e, _wv);
        var scale = 1.0 / Math.Sqrt(_d);
        var s = ScaleInPlace(MatMul(q, Transpose(k)), scale);    // [T,T]
        var a = SoftmaxRows(s);
        var c = MatMul(a, v);                                    // [T,D]
        var o = MatMul(c, _wo);                                  // [T,D]
        var z = Add(e, o);                                       // residuo
        // Readout sull'ULTIMO timestep ("ora"): via attention ha già raccolto tutta la storia della
        // finestra, mantenendo la selettività posizionale che una media perderebbe.
        var zpool = Row(z, _t - 1);                              // [D]
        var h = new double[_hff];
        for (var j = 0; j < _hff; j++)
        {
            double sum = _b1[j];
            for (var d = 0; d < _d; d++) sum += zpool[d] * _w1[d, j];
            h[j] = Math.Tanh(sum);
        }
        double yhat = _b2;
        for (var j = 0; j < _hff; j++) yhat += h[j] * _w2[j];

        return new Cache { X = x, E = e, Q = q, K = k, V = v, A = a, C = c, O = o, Z = z, Zpool = zpool, H = h, Yhat = yhat };
    }

    private void Backward(double[,] x, Cache cache, double y, Grads g)
    {
        var dyhat = cache.Yhat - y; // dL/dyhat con L = 0.5(yhat−y)²

        // Testa FFN
        var dh = new double[_hff];
        for (var j = 0; j < _hff; j++)
        {
            g.W2[j] += cache.H[j] * dyhat;
            dh[j] = _w2[j] * dyhat;
        }
        g.B2 += dyhat;

        var dzpool = new double[_d];
        for (var j = 0; j < _hff; j++)
        {
            var dpre = dh[j] * (1.0 - cache.H[j] * cache.H[j]); // tanh'
            g.B1[j] += dpre;
            for (var d = 0; d < _d; d++)
            {
                g.W1[d, j] += cache.Zpool[d] * dpre;
                dzpool[d] += _w1[d, j] * dpre;
            }
        }

        // Readout sull'ultimo timestep: solo Z[T-1] riceve il gradiente del pool.
        var dZ = new double[_t, _d];
        for (var d = 0; d < _d; d++) dZ[_t - 1, d] = dzpool[d];

        // Residuo Z = E + O
        var dE = (double[,])dZ.Clone();
        var dO = dZ;

        // O = C·Wo
        var dC = MatMul(dO, Transpose(_wo));
        AccumulateOuter(g.Wo, Transpose(cache.C), dO); // dWo = Cᵀ·dO

        // C = A·V
        var dA = MatMul(dC, Transpose(cache.V));
        var dV = MatMul(Transpose(cache.A), dC);

        // A = softmax(S) per riga
        var dS = new double[_t, _t];
        for (var t = 0; t < _t; t++)
        {
            double dot = 0.0;
            for (var u = 0; u < _t; u++) dot += cache.A[t, u] * dA[t, u];
            for (var u = 0; u < _t; u++) dS[t, u] = cache.A[t, u] * (dA[t, u] - dot);
        }

        // S = (Q·Kᵀ)/√D
        var scale = 1.0 / Math.Sqrt(_d);
        var dQ = ScaleInPlace(MatMul(dS, cache.K), scale);
        var dK = ScaleInPlace(MatMul(Transpose(dS), cache.Q), scale);

        // Q,K,V = E·Wq,E·Wk,E·Wv  → gradi pesi + accumulo su dE
        AccumulateOuter(g.Wq, Transpose(cache.E), dQ);
        AccumulateOuter(g.Wk, Transpose(cache.E), dK);
        AccumulateOuter(g.Wv, Transpose(cache.E), dV);
        AddMatMulInto(dE, dQ, Transpose(_wq));
        AddMatMulInto(dE, dK, Transpose(_wk));
        AddMatMulInto(dE, dV, Transpose(_wv));

        // E = X·Win + bin (+ PE)
        AccumulateOuter(g.Win, Transpose(x), dE);
        for (var t = 0; t < _t; t++)
            for (var d = 0; d < _d; d++) g.Bin[d] += dE[t, d];
    }

    private void ApplyGradients(Grads g, int batch)
    {
        var scale = 1.0 / Math.Max(1, batch);

        // Norma globale per il clipping.
        double sq = 0.0;
        sq += SumSq(g.Win) + SumSq(g.Bin) + SumSq(g.Wq) + SumSq(g.Wk) + SumSq(g.Wv) + SumSq(g.Wo)
            + SumSq(g.W1) + SumSq(g.B1) + SumSq(g.W2) + g.B2 * g.B2;
        var norm = Math.Sqrt(sq) * scale;
        var clip = norm > GradClip ? GradClip / norm : 1.0;
        var step = _lr * scale * clip;

        UpdateMat(_win, g.Win, step);
        UpdateVec(_bin, g.Bin, step, decay: false);
        UpdateMat(_wq, g.Wq, step); UpdateMat(_wk, g.Wk, step); UpdateMat(_wv, g.Wv, step); UpdateMat(_wo, g.Wo, step);
        UpdateMat(_w1, g.W1, step);
        UpdateVec(_b1, g.B1, step, decay: false);
        UpdateVec(_w2, g.W2, step, decay: true);
        _b2 -= step * g.B2;
    }

    private void UpdateMat(double[,] w, double[,] grad, double step)
    {
        for (var i = 0; i < w.GetLength(0); i++)
            for (var j = 0; j < w.GetLength(1); j++)
                w[i, j] -= step * (grad[i, j] + WeightDecay * w[i, j]);
    }

    private static void UpdateVec(double[] w, double[] grad, double step, bool decay)
    {
        for (var i = 0; i < w.Length; i++)
            w[i] -= step * (grad[i] + (decay ? WeightDecay * w[i] : 0.0));
    }

    // --- Inizializzazione / normalizzazione --------------------------------------------------

    private void InitParameters(Random rng)
    {
        _win = Glorot(_f, _d, rng);
        _bin = new double[_d];
        _wq = Glorot(_d, _d, rng); _wk = Glorot(_d, _d, rng); _wv = Glorot(_d, _d, rng); _wo = Glorot(_d, _d, rng);
        _w1 = Glorot(_d, _hff, rng);
        _b1 = new double[_hff];
        _w2 = new double[_hff];
        var a = Math.Sqrt(6.0 / (_hff + 1));
        for (var j = 0; j < _hff; j++) _w2[j] = (rng.NextDouble() * 2 - 1) * a;
        _b2 = 0.0;
    }

    private static double[,] Glorot(int rows, int cols, Random rng)
    {
        var a = Math.Sqrt(6.0 / (rows + cols));
        var m = new double[rows, cols];
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < cols; j++) m[i, j] = (rng.NextDouble() * 2 - 1) * a;
        return m;
    }

    private void ComputeStandardization(List<double[,]> samples)
    {
        _mean = new double[_f];
        _std = new double[_f];
        long count = 0;
        foreach (var x in samples)
            for (var t = 0; t < _t; t++) { for (var f = 0; f < _f; f++) _mean[f] += x[t, f]; count++; }
        var per = Math.Max(1, samples.Count * _t);
        for (var f = 0; f < _f; f++) _mean[f] /= per;

        foreach (var x in samples)
            for (var t = 0; t < _t; t++)
                for (var f = 0; f < _f; f++) { var d = x[t, f] - _mean[f]; _std[f] += d * d; }
        for (var f = 0; f < _f; f++)
        {
            _std[f] = Math.Sqrt(_std[f] / per);
            if (_std[f] < 1e-8) _std[f] = 1.0; // feature costante: nessuna scalatura
        }
    }

    private double[,] Standardize(double[,] x)
    {
        var r = new double[_t, _f];
        for (var t = 0; t < _t; t++)
            for (var f = 0; f < _f; f++) r[t, f] = (x[t, f] - _mean[f]) / _std[f];
        return r;
    }

    private double[,] Reshape(float[] flat)
    {
        var r = new double[_t, _f];
        for (var t = 0; t < _t; t++)
            for (var f = 0; f < _f; f++) r[t, f] = flat[t * _f + f];
        return r;
    }

    private static double[,] PositionalEncoding(int t, int d)
    {
        var pe = new double[t, d];
        for (var pos = 0; pos < t; pos++)
            for (var i = 0; i < d; i++)
            {
                var denom = Math.Pow(10000.0, 2.0 * (i / 2) / d);
                pe[pos, i] = (i % 2 == 0) ? Math.Sin(pos / denom) : Math.Cos(pos / denom);
            }
        return pe;
    }

    // --- Feature importance (permutazione via Predict) ---------------------------------------

    public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames)
    {
        if (!IsFitted) throw new InvalidOperationException("Il modello attention non è addestrato né caricato.");
        var rows = mlContext.Data.CreateEnumerable<FeatureRow>(evaluationData, reuseRowObject: false).ToList();
        var labels = rows.Select(r => (double)r.Label).ToArray();
        var basePred = rows.Select(r => (double)Predict(r.Features)).ToArray();
        var baseR2 = RSquared(basePred, labels);

        var rng = new Random(_seed);
        const int permutations = 3;
        var results = new List<FeatureImportance>(featureNames.Count);
        for (var f = 0; f < featureNames.Count; f++)
        {
            var drops = new double[permutations];
            for (var p = 0; p < permutations; p++)
            {
                var shuffled = rows.Select(r => r.Features[f]).ToArray();
                for (var i = shuffled.Length - 1; i > 0; i--) { var j = rng.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]); }
                var pred = new double[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var vec = (float[])rows[i].Features.Clone();
                    vec[f] = shuffled[i];
                    pred[i] = Predict(vec);
                }
                drops[p] = baseR2 - RSquared(pred, labels);
            }
            var mean = drops.Average();
            var variance = drops.Length > 1 ? drops.Sum(x => (x - mean) * (x - mean)) / (drops.Length - 1) : 0.0;
            results.Add(new FeatureImportance(featureNames[f], mean, Math.Sqrt(variance)));
        }
        return results.OrderByDescending(r => r.MeanDecreaseInRSquared).ToList();
    }

    private static double RSquared(double[] pred, double[] actual)
    {
        var mean = actual.Average();
        double ssRes = 0, ssTot = 0;
        for (var i = 0; i < actual.Length; i++) { ssRes += (actual[i] - pred[i]) * (actual[i] - pred[i]); ssTot += (actual[i] - mean) * (actual[i] - mean); }
        return ssTot <= 0 ? 0 : 1 - ssRes / ssTot;
    }

    // --- Persistenza (JSON) ------------------------------------------------------------------

    private sealed record State(int T, int F, int D, int Hff,
        double[] Win, double[] Bin, double[] Wq, double[] Wk, double[] Wv, double[] Wo,
        double[] W1, double[] B1, double[] W2, double B2, double[] Mean, double[] Std);

    public void Save(MLContext mlContext, string path)
    {
        if (!IsFitted) throw new InvalidOperationException("Nessun modello attention da salvare.");
        var state = new State(_t, _f, _d, _hff,
            Flatten(_win), _bin, Flatten(_wq), Flatten(_wk), Flatten(_wv), Flatten(_wo),
            Flatten(_w1), _b1, _w2, _b2, _mean, _std);
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    public void Load(MLContext mlContext, string path)
    {
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException("Modello attention illeggibile.");
        // Ricostruzione completa dal blob: T/F/D/Hff arrivano tutti dal file salvato, così un modello
        // addestrato con qualunque configurazione si ricarica su un'istanza costruita coi default.
        _t = state.T; _f = state.F; _d = state.D; _hff = state.Hff;

        _win = Unflatten(state.Win, _f, _d);
        _bin = state.Bin;
        _wq = Unflatten(state.Wq, _d, _d); _wk = Unflatten(state.Wk, _d, _d); _wv = Unflatten(state.Wv, _d, _d); _wo = Unflatten(state.Wo, _d, _d);
        _w1 = Unflatten(state.W1, _d, _hff);
        _b1 = state.B1; _w2 = state.W2; _b2 = state.B2;
        _mean = state.Mean; _std = state.Std;
        _pe = PositionalEncoding(_t, _d);
        IsFitted = true;
    }

    private static double[] Flatten(double[,] m)
    {
        var r = new double[m.Length];
        var k = 0;
        for (var i = 0; i < m.GetLength(0); i++) for (var j = 0; j < m.GetLength(1); j++) r[k++] = m[i, j];
        return r;
    }

    private static double[,] Unflatten(double[] v, int rows, int cols)
    {
        var m = new double[rows, cols];
        var k = 0;
        for (var i = 0; i < rows; i++) for (var j = 0; j < cols; j++) m[i, j] = v[k++];
        return m;
    }

    public void Dispose() { /* nessuna risorsa nativa: modello interamente in array gestiti */ }

    // --- Contenitore gradienti ---------------------------------------------------------------

    private sealed class Grads
    {
        public double[,] Win, Wq, Wk, Wv, Wo, W1;
        public double[] Bin, B1, W2;
        public double B2;

        public Grads(int f, int d, int hff)
        {
            Win = new double[f, d];
            Wq = new double[d, d]; Wk = new double[d, d]; Wv = new double[d, d]; Wo = new double[d, d];
            W1 = new double[d, hff];
            Bin = new double[d]; B1 = new double[hff]; W2 = new double[hff];
        }
    }

    // --- Helper di algebra (matrici piccole, chiarezza > performance) ------------------------

    private static double[,] MatMul(double[,] a, double[,] b)
    {
        int m = a.GetLength(0), k = a.GetLength(1), n = b.GetLength(1);
        var r = new double[m, n];
        for (var i = 0; i < m; i++)
            for (var p = 0; p < k; p++)
            {
                var aip = a[i, p];
                if (aip == 0) continue;
                for (var j = 0; j < n; j++) r[i, j] += aip * b[p, j];
            }
        return r;
    }

    private static double[,] Transpose(double[,] a)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var r = new double[n, m];
        for (var i = 0; i < m; i++) for (var j = 0; j < n; j++) r[j, i] = a[i, j];
        return r;
    }

    private static double[,] Add(double[,] a, double[,] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var r = new double[m, n];
        for (var i = 0; i < m; i++) for (var j = 0; j < n; j++) r[i, j] = a[i, j] + b[i, j];
        return r;
    }

    private static double[,] AddInPlace(double[,] a, double[,] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        for (var i = 0; i < m; i++) for (var j = 0; j < n; j++) a[i, j] += b[i, j];
        return a;
    }

    private static double[,] AddBias(double[,] a, double[] bias)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        for (var i = 0; i < m; i++) for (var j = 0; j < n; j++) a[i, j] += bias[j];
        return a;
    }

    private static double[,] ScaleInPlace(double[,] a, double s)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        for (var i = 0; i < m; i++) for (var j = 0; j < n; j++) a[i, j] *= s;
        return a;
    }

    private static double[] Row(double[,] a, int row)
    {
        var n = a.GetLength(1);
        var r = new double[n];
        for (var j = 0; j < n; j++) r[j] = a[row, j];
        return r;
    }

    private static double[,] SoftmaxRows(double[,] s)
    {
        int m = s.GetLength(0), n = s.GetLength(1);
        var r = new double[m, n];
        for (var i = 0; i < m; i++)
        {
            var max = double.NegativeInfinity;
            for (var j = 0; j < n; j++) if (s[i, j] > max) max = s[i, j];
            double sum = 0;
            for (var j = 0; j < n; j++) { r[i, j] = Math.Exp(s[i, j] - max); sum += r[i, j]; }
            for (var j = 0; j < n; j++) r[i, j] /= sum;
        }
        return r;
    }

    /// <summary>Accumula in <paramref name="target"/> il prodotto aᵀ-forma già trasposta · b (dW += A·B).</summary>
    private static void AccumulateOuter(double[,] target, double[,] a, double[,] b)
    {
        int m = a.GetLength(0), k = a.GetLength(1), n = b.GetLength(1);
        for (var i = 0; i < m; i++)
            for (var p = 0; p < k; p++)
            {
                var aip = a[i, p];
                if (aip == 0) continue;
                for (var j = 0; j < n; j++) target[i, j] += aip * b[p, j];
            }
    }

    private static void AddMatMulInto(double[,] target, double[,] a, double[,] b)
    {
        int m = a.GetLength(0), k = a.GetLength(1), n = b.GetLength(1);
        for (var i = 0; i < m; i++)
            for (var p = 0; p < k; p++)
            {
                var aip = a[i, p];
                if (aip == 0) continue;
                for (var j = 0; j < n; j++) target[i, j] += aip * b[p, j];
            }
    }

    private static double SumSq(double[,] a)
    {
        double s = 0;
        foreach (var v in a) s += v * v;
        return s;
    }

    private static double SumSq(double[] a)
    {
        double s = 0;
        foreach (var v in a) s += v * v;
        return s;
    }

    private static void Shuffle(int[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--) { var j = rng.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
    }
}
