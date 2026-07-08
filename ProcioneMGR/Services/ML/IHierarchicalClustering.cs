namespace ProcioneMGR.Services.ML;

/// <summary>Criterio di linkage per l'agglomerative clustering (formula di Lance-Williams).</summary>
public enum LinkageMethod
{
    /// <summary>Distanza fra cluster = distanza minima fra un elemento dell'uno e uno dell'altro.</summary>
    Single,
    /// <summary>Distanza fra cluster = distanza massima fra un elemento dell'uno e uno dell'altro.</summary>
    Complete,
    /// <summary>Distanza fra cluster = media delle distanze pesata per dimensione dei cluster (UPGMA). Usata da HRP.</summary>
    Average,
}

/// <summary>Nodo di un dendrogramma: foglia (asset singolo) o fusione di due sotto-cluster.</summary>
public sealed class ClusterNode
{
    /// <summary>Nome dell'asset, valorizzato solo per le foglie (Left/Right nulli).</summary>
    public string? Label { get; init; }
    public ClusterNode? Left { get; init; }
    public ClusterNode? Right { get; init; }

    /// <summary>Distanza a cui questo cluster si è formato (0 per le foglie).</summary>
    public double Distance { get; init; }

    /// <summary>Indici originali (nell'ordine dei label passati a BuildDendrogram) contenuti in questo sotto-albero.</summary>
    public required IReadOnlyList<int> LeafIndices { get; init; }

    public bool IsLeaf => Left is null && Right is null;
}

/// <summary>
/// Clustering gerarchico agglomerativo (cap. 13) su una matrice di distanza: costruisce il
/// dendrogramma unendo via via i due cluster più vicini. Riusato da Hierarchical Risk Parity
/// (Fase C, §3.5) per la quasi-diagonalizzazione della matrice di correlazione e la bisezione
/// ricorsiva dei pesi di portafoglio — qui si costruisce solo l'albero, indipendente dall'uso
/// che se ne farà.
/// </summary>
public interface IHierarchicalClustering
{
    /// <summary>
    /// <paramref name="distanceMatrix"/> deve essere simmetrica, n x n, con diagonale nulla
    /// (distanza di un elemento da se stesso = 0). <paramref name="labels"/> assegna un nome a
    /// ciascuna riga/colonna, nello stesso ordine.
    /// </summary>
    ClusterNode BuildDendrogram(double[,] distanceMatrix, IReadOnlyList<string> labels, LinkageMethod method = LinkageMethod.Average);
}

/// <summary>Conversione di una matrice di correlazione in distanza (Mantegna), usata da PCA/HRP/clustering.</summary>
public static class CorrelationDistance
{
    /// <summary>
    /// d = sqrt(0.5 * (1 - corr)), in [0,1]: 0 quando corr=1 (identici), 1 quando corr=-1
    /// (opposti). Metrica standard in finanza per trasformare correlazioni in distanze valide
    /// (soddisfa la disuguaglianza triangolare, a differenza di 1-corr).
    /// </summary>
    public static double[,] FromCorrelationMatrix(double[,] correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        var n = correlation.GetLength(0);
        if (correlation.GetLength(1) != n)
        {
            throw new ArgumentException("La matrice di correlazione deve essere quadrata.", nameof(correlation));
        }

        var distance = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                distance[i, j] = Math.Sqrt(Math.Max(0.0, 0.5 * (1.0 - correlation[i, j])));
            }
        }
        return distance;
    }
}
