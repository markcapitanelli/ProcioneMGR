namespace ProcioneMGR.Services.Validation;

/// <summary>Esito del calcolo del PBO: la probabilità e le diagnostiche per pannello/UI.</summary>
public sealed record PboResult(
    double ProbabilityOfBacktestOverfitting,
    int Combinations,
    int Strategies,
    IReadOnlyList<double> Logits)
{
    /// <summary>Comodo: PBO in percentuale.</summary>
    public double PboPercent => ProbabilityOfBacktestOverfitting * 100.0;
}

/// <summary>
/// <b>Probability of Backtest Overfitting</b> (Bailey, Borwein, López de Prado, Zhu, 2015) via
/// <b>Combinatorially Symmetric Cross-Validation (CSCV)</b>. Data una matrice di rendimenti
/// periodici (una serie per ogni strategia/combinazione candidata, tutte sullo stesso asse
/// temporale), stima la probabilità che la strategia scelta come migliore IN-SAMPLE risulti
/// <i>sotto la mediana</i> OUT-OF-SAMPLE — cioè che la selezione sia guidata dall'overfitting.
///
/// Interpretazione: PBO ≈ 0.5 su un pannello di pure strategie-rumore (nessun edge, la scelta è
/// casuale); PBO basso quando esiste un edge reale e persistente. Complementare al Deflated Sharpe:
/// il DSR giudica il singolo migliore, il PBO giudica il <i>processo di selezione</i> nel suo insieme.
/// Puro e deterministico.
/// </summary>
public static class BacktestOverfitting
{
    /// <summary>
    /// Calcola il PBO via CSCV. <paramref name="perStrategyReturns"/>: una serie di rendimenti per
    /// strategia (stessa lunghezza temporale; se differiscono si usa la lunghezza minima comune).
    /// <paramref name="partitions"/> S (pari, ≥ 4): l'asse temporale è diviso in S blocchi e per ogni
    /// combinazione di S/2 blocchi a train (resto a test) si confronta il migliore IS con il suo rango OOS.
    /// </summary>
    public static PboResult ProbabilityOfOverfitting(IReadOnlyList<IReadOnlyList<double>> perStrategyReturns, int partitions = 10)
    {
        ArgumentNullException.ThrowIfNull(perStrategyReturns);
        var n = perStrategyReturns.Count;
        if (n < 2) throw new ArgumentException("Servono almeno 2 strategie candidate.", nameof(perStrategyReturns));
        if (partitions < 4 || partitions % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(partitions), "partitions deve essere pari e ≥ 4.");

        var t = int.MaxValue;
        foreach (var s in perStrategyReturns) t = Math.Min(t, s?.Count ?? 0);
        if (t < partitions) throw new ArgumentException("Serie troppo corta per il numero di partizioni richiesto.", nameof(partitions));

        // Confini [start, end) delle S partizioni temporali contigue (l'ultima assorbe il resto).
        var part = partitions;
        var block = t / part;
        var bounds = new (int Start, int End)[part];
        for (var p = 0; p < part; p++)
            bounds[p] = (p * block, p == part - 1 ? t : (p + 1) * block);

        var logits = new List<double>();
        var half = part / 2;
        foreach (var trainParts in CombinatorialPurgedCv.Combinations(part, half))
        {
            var isTrain = new bool[part];
            foreach (var p in trainParts) isTrain[p] = true;

            // Sharpe per-periodo IS e OOS per ciascuna strategia sulla concatenazione dei blocchi.
            var isSharpe = new double[n];
            var oosSharpe = new double[n];
            for (var s = 0; s < n; s++)
            {
                var series = perStrategyReturns[s];
                isSharpe[s] = PartitionedSharpe(series, bounds, isTrain, wantTrain: true);
                oosSharpe[s] = PartitionedSharpe(series, bounds, isTrain, wantTrain: false);
            }

            // Migliore in-sample.
            var best = 0;
            for (var s = 1; s < n; s++) if (isSharpe[s] > isSharpe[best]) best = s;

            // Rango OOS del migliore IS: r in [1, n] (n = migliore anche OOS).
            var rank = 1;
            for (var s = 0; s < n; s++)
                if (s != best && oosSharpe[s] <= oosSharpe[best]) rank++;

            // Rango relativo ω ∈ (0,1) e logit λ. λ ≤ 0 ⇔ il migliore IS è sotto la mediana OOS.
            var omega = rank / (double)(n + 1);
            var lambda = Math.Log(omega / (1.0 - omega));
            logits.Add(lambda);
        }

        var overfit = 0;
        foreach (var l in logits) if (l <= 0.0) overfit++;
        var pbo = logits.Count == 0 ? double.NaN : overfit / (double)logits.Count;
        return new PboResult(pbo, logits.Count, n, logits);
    }

    /// <summary>Sharpe per-periodo sulla concatenazione dei blocchi train (o test) di una strategia.</summary>
    private static double PartitionedSharpe(IReadOnlyList<double> series, (int Start, int End)[] bounds, bool[] isTrain, bool wantTrain)
    {
        var slice = new List<double>();
        for (var p = 0; p < bounds.Length; p++)
        {
            if (isTrain[p] != wantTrain) continue;
            var (start, end) = bounds[p];
            for (var i = start; i < end; i++) slice.Add(series[i]);
        }
        return ReturnMoments.PerPeriodSharpe(slice);
    }
}
