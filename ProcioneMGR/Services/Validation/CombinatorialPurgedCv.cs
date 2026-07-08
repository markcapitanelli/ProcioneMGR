namespace ProcioneMGR.Services.Validation;

/// <summary>
/// Uno split combinatorio: un sottoinsieme di gruppi usati come test, il resto come train, con le
/// bande di purge/embargo già rimosse dal train. Analogo combinatorio di <c>ML.CvSplit</c>, con in
/// più l'indice dei gruppi di test scelti (per tracciare i "percorsi" backtestabili).
/// </summary>
public sealed record CpcvSplit(
    int Combination,
    IReadOnlyList<int> TestGroups,
    IReadOnlyList<int> TrainIndices,
    IReadOnlyList<int> TestIndices);

/// <summary>
/// <b>Combinatorial Purged Cross-Validation</b> (López de Prado, "Advances in Financial ML", cap. 12):
/// invece di un solo blocco di test contiguo per fold (come <c>PurgedTimeSeriesCv</c>), si scelgono
/// TUTTE le combinazioni di <c>testGroups</c> gruppi su <c>groups</c> totali → C(groups, testGroups)
/// split, ciascuno con purge/embargo attorno a OGNI gruppo di test. Genera molti più percorsi
/// out-of-sample dallo stesso storico, riducendo la varianza della stima e alimentando il calcolo
/// del PBO. Deterministico (combinazioni in ordine lessicografico), stateless → registrabile Singleton.
/// </summary>
public interface ICombinatorialPurgedCv
{
    /// <summary>
    /// Divide <paramref name="sampleCount"/> campioni ordinati temporalmente in
    /// <paramref name="groups"/> gruppi contigui (l'ultimo assorbe il resto) e produce
    /// C(<paramref name="groups"/>, <paramref name="testGroups"/>) split. Per ogni split il train
    /// esclude i gruppi di test e le bande di <paramref name="purgeWindow"/> prima ed
    /// <paramref name="embargoPeriods"/> dopo ciascun gruppo di test.
    /// </summary>
    IReadOnlyList<CpcvSplit> Split(int sampleCount, int groups, int testGroups, int purgeWindow, int embargoPeriods);
}

/// <inheritdoc cref="ICombinatorialPurgedCv"/>
public sealed class CombinatorialPurgedCv : ICombinatorialPurgedCv
{
    public IReadOnlyList<CpcvSplit> Split(int sampleCount, int groups, int testGroups, int purgeWindow, int embargoPeriods)
    {
        if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (groups < 2) throw new ArgumentOutOfRangeException(nameof(groups));
        if (testGroups < 1 || testGroups >= groups)
            throw new ArgumentOutOfRangeException(nameof(testGroups), "testGroups deve essere in [1, groups-1].");
        if (purgeWindow < 0) throw new ArgumentOutOfRangeException(nameof(purgeWindow));
        if (embargoPeriods < 0) throw new ArgumentOutOfRangeException(nameof(embargoPeriods));

        var groupSize = sampleCount / groups;
        if (groupSize < 1) throw new ArgumentException("Troppi gruppi per il numero di campioni disponibili.", nameof(groups));

        // Confini [start, end) di ogni gruppo; l'ultimo assorbe il resto della divisione intera.
        var bounds = new (int Start, int End)[groups];
        for (var g = 0; g < groups; g++)
        {
            var start = g * groupSize;
            var end = g == groups - 1 ? sampleCount : start + groupSize;
            bounds[g] = (start, end);
        }

        var result = new List<CpcvSplit>();
        var combo = 0;
        foreach (var chosen in Combinations(groups, testGroups))
        {
            var testGroupsList = chosen.ToArray();

            var testIndices = new List<int>();
            // Banda esclusa dal train = unione, su tutti i gruppi di test, di
            // [start − purge, end + embargo). Uso un set per gestire gruppi adiacenti sovrapposti.
            var excluded = new HashSet<int>();
            foreach (var g in testGroupsList)
            {
                var (start, end) = bounds[g];
                for (var i = start; i < end; i++) testIndices.Add(i);

                var from = Math.Max(0, start - purgeWindow);
                var to = Math.Min(sampleCount, end + embargoPeriods);
                for (var i = from; i < to; i++) excluded.Add(i);
            }
            testIndices.Sort();

            var trainIndices = new List<int>(sampleCount - excluded.Count);
            for (var i = 0; i < sampleCount; i++)
                if (!excluded.Contains(i)) trainIndices.Add(i);

            result.Add(new CpcvSplit(combo, testGroupsList, trainIndices, testIndices));
            combo++;
        }
        return result;
    }

    /// <summary>Combinazioni di <paramref name="k"/> indici su <paramref name="n"/>, ordine lessicografico.</summary>
    public static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = new int[k];
        for (var i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return (int[])idx.Clone();

            // Avanza l'ultimo indice che può crescere (algoritmo standard delle combinazioni).
            var pos = k - 1;
            while (pos >= 0 && idx[pos] == n - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (var i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
