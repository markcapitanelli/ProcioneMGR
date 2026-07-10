using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Services.Validation;

/// <summary>
/// Numero EFFETTIVO di tentativi indipendenti per la correzione del test multiplo nel Deflated
/// Sharpe. <see cref="DeflatedSharpeRatio.ExpectedMaxSharpe"/> assume N tentativi INDIPENDENTI: se
/// molti candidati sono la stessa strategia provata in varianti correlate (griglia fitta di
/// parametri, simboli gemelli, walk-forward sovrapposti), contarli tutti gonfia la soglia SR* e
/// rende il gate <b>troppo severo</b> — N effettivo &lt; N nominale (López de Prado, "Effective
/// Number of Trials"). Qui si clusterizzano i tentativi per correlazione dei rendimenti, riusando
/// <see cref="IHierarchicalClustering"/> + <see cref="CorrelationDistance"/> (come HRP), e si conta
/// il numero di cluster a una soglia di correlazione: tentativi con ρ ≥ soglia collassano in un solo
/// trial effettivo. Puro e deterministico.
/// </summary>
public static class EffectiveTrials
{
    /// <summary>
    /// Conteggio effettivo dei tentativi date le serie di rendimenti periodici di OGNI tentativo
    /// (allineate per indice). <paramref name="correlationThreshold"/> ∈ [0,1]: due tentativi con
    /// correlazione ≥ soglia sono considerati lo stesso trial (default 0.5; 1 = disattivo ⇒ ritorna
    /// il conteggio nominale, salvo serie perfettamente correlate). Il risultato è sempre in [1, n].
    /// Serie troppo corte o a varianza nulla non correlano con nessuno ⇒ restano tentativi distinti
    /// (comportamento conservativo: non riducono il penale del test multiplo).
    /// </summary>
    public static int Count(
        IReadOnlyList<IReadOnlyList<double>> trialReturns,
        double correlationThreshold = 0.5,
        IHierarchicalClustering? clustering = null)
    {
        ArgumentNullException.ThrowIfNull(trialReturns);
        var n = trialReturns.Count;
        if (n < 2) return n;

        var corr = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            corr[i, i] = 1.0;
            for (var j = i + 1; j < n; j++)
            {
                var c = Pearson(trialReturns[i], trialReturns[j]);
                corr[i, j] = c;
                corr[j, i] = c;
            }
        }

        var distance = CorrelationDistance.FromCorrelationMatrix(corr);
        var labels = new string[n];
        for (var i = 0; i < n; i++) labels[i] = i.ToString();

        var tree = (clustering ?? new HierarchicalClustering())
            .BuildDendrogram(distance, labels, LinkageMethod.Average);

        // Taglia il dendrogramma all'altezza d* = √(0.5·(1−soglia)) corrispondente alla soglia di
        // correlazione (metrica di Mantegna): un cluster per ogni sotto-albero fuso a distanza ≤ d*.
        var threshold = Math.Clamp(correlationThreshold, -1.0, 1.0);
        var dStar = Math.Sqrt(Math.Max(0.0, 0.5 * (1.0 - threshold)));
        var clusters = CountClustersAtHeight(tree, dStar);
        return Math.Clamp(clusters, 1, n);
    }

    /// <summary>
    /// Numero di cluster tagliando il dendrogramma all'altezza <paramref name="height"/>: un nodo fuso
    /// a distanza ≤ height è interamente un cluster; sopra la soglia il taglio passa fra i due figli,
    /// che si contano separatamente. Valido perché l'Average linkage è monotòno (nessuna inversione).
    /// </summary>
    private static int CountClustersAtHeight(ClusterNode node, double height)
    {
        if (node.IsLeaf || node.Distance <= height) return 1;
        return CountClustersAtHeight(node.Left!, height) + CountClustersAtHeight(node.Right!, height);
    }

    /// <summary>
    /// Correlazione di Pearson sui primi <c>min(|a|,|b|)</c> elementi (allineamento per indice, come le
    /// serie di rendimenti periodici holdout che partono dallo stesso istante). 0 se meno di 2 punti
    /// sovrapposti o varianza nulla ⇒ i due tentativi risultano non correlati (cluster distinti).
    /// </summary>
    private static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < 2) return 0.0;

        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++) { meanA += a[i]; meanB += b[i]; }
        meanA /= n;
        meanB /= n;

        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }
        if (saa <= 0.0 || sbb <= 0.0) return 0.0;
        return sab / Math.Sqrt(saa * sbb);
    }
}
