namespace ProcioneMGR.Services.Regime;

/// <summary>Esito di un fit del jump model: centroidi, percorso di stato e diagnostica.</summary>
/// <param name="Centroids">k centroidi nello spazio (standardizzato) delle feature.</param>
/// <param name="States">Stato assegnato a ogni riga di input (percorso OFFLINE, usa tutta la serie).</param>
/// <param name="Objective">Somma delle distanze quadrate + λ · numero di salti: la quantità minimizzata.</param>
/// <param name="Iterations">Iterazioni di discesa a coordinate eseguite (nel restart vincente).</param>
/// <param name="Converged">true se il percorso di stato ha smesso di cambiare prima del tetto di iterazioni.</param>
public sealed record JumpModelFit(
    double[][] Centroids,
    int[] States,
    double Objective,
    int Iterations,
    bool Converged);

/// <summary>
/// [C1 roadmap integrazione] <b>Statistical jump model</b> (Nystrup–Kolm–Lindström): clustering
/// K-means con una penalità fissa λ per ogni SALTO di stato fra osservazioni consecutive, stimando
/// cluster e persistenza CONGIUNTAMENTE:
///
///   min_{μ, s}  Σ_t ||x_t − μ_{s_t}||²  +  λ · Σ_t 1[s_t ≠ s_{t−1}]
///
/// È il candidato che sostituisce l'idea "HMM sopra i cluster K-means", bocciata con misura
/// (gate R4, 2026-07-25): nessuna decodifica a valle rende persistenti regimi i cui centroidi
/// oscillano per costruzione — qui la persistenza entra NELLA stima dei centroidi, non dopo.
/// λ = 0 degenera esattamente in K-means; λ → ∞ degenera in un solo stato: la manopola va tarata
/// e il suo effetto misurato (fase <c>jumpmodel</c> di PlatformExpand), mai assunta.
///
/// Implementazione: discesa a coordinate — dato il percorso, i centroidi sono le medie di stato;
/// dati i centroidi, il percorso ottimo è programmazione dinamica O(T·k) (la transizione è a costo
/// uniforme λ, quindi il minimo su i≠j si calcola una volta per t). Deterministico a parità di
/// seed; restarts multipli, vince l'objective più basso.
///
/// NON è cablato nel <see cref="RegimeDetector"/>: per contratto della roadmap C1 sostituisce
/// l'esistente solo se supera il gate (persistenza mediana ≥ ~3 settimane e stati sensati),
/// misurato sui nostri dati — questo file è il modello, non la decisione.
/// </summary>
public static class JumpModel
{
    /// <summary>
    /// Standardizza per colonna (z-score) sulla matrice data. Restituisce medie e deviazioni per
    /// applicare la STESSA trasformazione a dati successivi (mai ristimare sull'out-of-sample).
    /// Colonne a varianza nulla restano a zero (std=1 convenzionale).
    /// </summary>
    public static (double[][] Z, double[] Means, double[] Stds) Standardize(IReadOnlyList<double[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new ArgumentException("Matrice vuota.", nameof(rows));
        var d = rows[0].Length;
        var means = new double[d];
        var stds = new double[d];
        foreach (var r in rows)
            for (var j = 0; j < d; j++) means[j] += r[j];
        for (var j = 0; j < d; j++) means[j] /= rows.Count;
        foreach (var r in rows)
            for (var j = 0; j < d; j++) { var e = r[j] - means[j]; stds[j] += e * e; }
        for (var j = 0; j < d; j++) stds[j] = stds[j] > 0 ? Math.Sqrt(stds[j] / rows.Count) : 1.0;

        var z = new double[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
        {
            z[i] = new double[d];
            for (var j = 0; j < d; j++) z[i][j] = (rows[i][j] - means[j]) / stds[j];
        }
        return (z, means, stds);
    }

    /// <summary>Applica una standardizzazione già stimata (per l'out-of-sample).</summary>
    public static double[][] ApplyStandardization(IReadOnlyList<double[]> rows, double[] means, double[] stds)
        => [.. rows.Select(r => r.Select((v, j) => (v - means[j]) / stds[j]).ToArray())];

    /// <summary>
    /// Fit con restarts: seeding k-means++ (deterministico dal seed), poi discesa a coordinate
    /// finché il percorso smette di cambiare. Con λ=0 è un K-means (di Lloyd, sulla catena).
    /// </summary>
    public static JumpModelFit Fit(double[][] x, int k, double lambda, int seed = 1, int restarts = 5, int maxIterations = 60)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length < k) throw new ArgumentException($"Servono almeno {k} osservazioni.", nameof(x));
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));
        if (lambda < 0) throw new ArgumentOutOfRangeException(nameof(lambda));

        JumpModelFit? best = null;
        for (var r = 0; r < Math.Max(1, restarts); r++)
        {
            var fit = FitOnce(x, k, lambda, seed + 1000 * r, maxIterations);
            if (best is null || fit.Objective < best.Objective) best = fit;
        }
        return best!;
    }

    private static JumpModelFit FitOnce(double[][] x, int k, double lambda, int seed, int maxIterations)
    {
        var rng = new Random(seed);
        var centroids = SeedPlusPlus(x, k, rng);

        int[] states = [];
        var converged = false;
        var iter = 0;
        for (; iter < maxIterations; iter++)
        {
            var newStates = DecodeOffline(x, centroids, lambda);
            if (states.Length == newStates.Length && states.AsSpan().SequenceEqual(newStates)) { converged = true; break; }
            states = newStates;
            UpdateCentroids(x, states, centroids, rng);
        }

        return new JumpModelFit(centroids, states, Objective(x, centroids, states, lambda), iter, converged);
    }

    /// <summary>
    /// Percorso di stato ottimo dati i centroidi (programmazione dinamica, transizione uniforme λ).
    /// Guarda TUTTA la serie: va usato per il fit e le analisi, mai per una decisione live
    /// (per quella c'è <see cref="DecodeCausal"/>).
    /// </summary>
    public static int[] DecodeOffline(double[][] x, double[][] centroids, double lambda)
    {
        var t = x.Length;
        var k = centroids.Length;
        var cost = new double[k];
        var prev = new double[k];
        var back = new int[t][];

        for (var j = 0; j < k; j++) prev[j] = SquaredDistance(x[0], centroids[j]);

        for (var i = 1; i < t; i++)
        {
            back[i] = new int[k];
            // Con costo di salto uniforme, il predecessore ottimo è o "resto dove sono" o il
            // minimo globale + λ: basta il minimo (e il suo argomento) di prev, niente k².
            var minPrev = double.MaxValue;
            var argMin = 0;
            for (var j = 0; j < k; j++) if (prev[j] < minPrev) { minPrev = prev[j]; argMin = j; }

            for (var j = 0; j < k; j++)
            {
                var stay = prev[j];
                var jump = minPrev + lambda;
                if (stay <= jump) { cost[j] = stay + SquaredDistance(x[i], centroids[j]); back[i][j] = j; }
                else { cost[j] = jump + SquaredDistance(x[i], centroids[j]); back[i][j] = argMin; }
            }
            (prev, cost) = (cost, prev);
        }

        var states = new int[t];
        var bestEnd = 0;
        for (var j = 1; j < k; j++) if (prev[j] < prev[bestEnd]) bestEnd = j;
        states[t - 1] = bestEnd;
        for (var i = t - 1; i > 0; i--) states[i - 1] = back[i][states[i]];
        return states;
    }

    /// <summary>
    /// Decodifica CAUSALE (filtro, per il live): la STESSA ricorsione in avanti del DP offline,
    /// ma a ogni barra lo stato riportato è l'argmin del costo accumulato FINO A LÌ — solo passato,
    /// mai un'occhiata avanti. Non è l'isteresi greedy "cambia se l'altro centroide batte λ su una
    /// barra": quella confronta λ con UNA osservazione e con λ sensati non cambia mai stato
    /// (misurato: 33% di accordo con l'offline, cioè uno stato solo). Qui l'evidenza contraria si
    /// ACCUMULA barra dopo barra finché ripaga il costo del salto — la semantica di λ del modello,
    /// con il ritardo ai bordi dei segmenti come unico prezzo, che è il prezzo giusto del non
    /// guardare avanti. <paramref name="initialState"/> ≥ 0 àncora la partenza (es. l'ultimo stato
    /// del train quando si continua out-of-sample).
    /// </summary>
    public static int[] DecodeCausal(double[][] x, double[][] centroids, double lambda, int initialState = -1)
    {
        var t = x.Length;
        var k = centroids.Length;
        var states = new int[t];
        var prev = new double[k];
        var cost = new double[k];

        for (var j = 0; j < k; j++)
        {
            prev[j] = SquaredDistance(x[0], centroids[j]);
            if (initialState >= 0 && j != initialState) prev[j] += lambda;
        }
        states[0] = ArgMin(prev);

        for (var i = 1; i < t; i++)
        {
            var minPrev = prev[ArgMin(prev)];
            for (var j = 0; j < k; j++)
            {
                cost[j] = Math.Min(prev[j], minPrev + lambda) + SquaredDistance(x[i], centroids[j]);
            }
            states[i] = ArgMin(cost);
            // Normalizzazione: i costi accumulati crescono senza limite, le DIFFERENZE (le uniche
            // che decidono argmin e salti) no — si sottrae il minimo per non degradare in double.
            var floor = cost[states[i]];
            for (var j = 0; j < k; j++) prev[j] = cost[j] - floor;
        }
        return states;
    }

    private static int ArgMin(double[] values)
    {
        var best = 0;
        for (var j = 1; j < values.Length; j++) if (values[j] < values[best]) best = j;
        return best;
    }

    /// <summary>Durate (in barre) dei tratti consecutivi nello stesso stato.</summary>
    public static List<int> RunLengths(IReadOnlyList<int> states)
    {
        var runs = new List<int>();
        if (states.Count == 0) return runs;
        var run = 1;
        for (var i = 1; i < states.Count; i++)
        {
            if (states[i] == states[i - 1]) { run++; continue; }
            runs.Add(run);
            run = 1;
        }
        runs.Add(run);
        return runs;
    }

    private static double Objective(double[][] x, double[][] centroids, int[] states, double lambda)
    {
        var obj = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            obj += SquaredDistance(x[i], centroids[states[i]]);
            if (i > 0 && states[i] != states[i - 1]) obj += lambda;
        }
        return obj;
    }

    private static void UpdateCentroids(double[][] x, int[] states, double[][] centroids, Random rng)
    {
        var k = centroids.Length;
        var d = x[0].Length;
        var counts = new int[k];
        var sums = new double[k][];
        for (var j = 0; j < k; j++) sums[j] = new double[d];
        for (var i = 0; i < x.Length; i++)
        {
            counts[states[i]]++;
            var row = x[i];
            var sum = sums[states[i]];
            for (var c = 0; c < d; c++) sum[c] += row[c];
        }
        for (var j = 0; j < k; j++)
        {
            if (counts[j] == 0)
            {
                // Stato svuotato: si risemina su un punto a caso — lasciarlo dov'era congelerebbe
                // un centroide morto che il DP non sceglierà mai più.
                centroids[j] = [.. x[rng.Next(x.Length)]];
                continue;
            }
            for (var c = 0; c < d; c++) centroids[j][c] = sums[j][c] / counts[j];
        }
    }

    /// <summary>Seeding k-means++ deterministico dal rng: primo a caso, i successivi ∝ D².</summary>
    private static double[][] SeedPlusPlus(double[][] x, int k, Random rng)
    {
        var centroids = new double[k][];
        centroids[0] = [.. x[rng.Next(x.Length)]];
        var d2 = new double[x.Length];
        for (var j = 1; j < k; j++)
        {
            var total = 0.0;
            for (var i = 0; i < x.Length; i++)
            {
                var min = double.MaxValue;
                for (var c = 0; c < j; c++) min = Math.Min(min, SquaredDistance(x[i], centroids[c]));
                d2[i] = min;
                total += min;
            }
            if (total <= 0) { centroids[j] = [.. x[rng.Next(x.Length)]]; continue; }
            var target = rng.NextDouble() * total;
            var acc = 0.0;
            var pick = x.Length - 1;
            for (var i = 0; i < x.Length; i++) { acc += d2[i]; if (acc >= target) { pick = i; break; } }
            centroids[j] = [.. x[pick]];
        }
        return centroids;
    }

    private static double SquaredDistance(double[] a, double[] b)
    {
        var s = 0.0;
        for (var i = 0; i < a.Length; i++) { var e = a[i] - b[i]; s += e * e; }
        return s;
    }
}
