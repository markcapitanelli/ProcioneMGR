namespace ProcioneMGR.Services.ML;

/// <summary>Implementazione di <see cref="IHierarchicalClustering"/>. Pura/stateless -> registrabile Singleton.</summary>
public sealed class HierarchicalClustering : IHierarchicalClustering
{
    public ClusterNode BuildDendrogram(double[,] distanceMatrix, IReadOnlyList<string> labels, LinkageMethod method = LinkageMethod.Average)
    {
        ArgumentNullException.ThrowIfNull(distanceMatrix);
        ArgumentNullException.ThrowIfNull(labels);
        var n = labels.Count;
        if (n < 2)
        {
            throw new ArgumentException("Servono almeno 2 elementi da raggruppare.", nameof(labels));
        }
        if (distanceMatrix.GetLength(0) != n || distanceMatrix.GetLength(1) != n)
        {
            throw new ArgumentException("La matrice delle distanze deve essere n x n quanto i label.", nameof(distanceMatrix));
        }

        // Cluster attivi: id 0..n-1 = foglie originali; ogni fusione crea un nuovo id >= n.
        var nodeById = new Dictionary<int, ClusterNode>(n);
        var sizeById = new Dictionary<int, int>(n);
        for (var i = 0; i < n; i++)
        {
            nodeById[i] = new ClusterNode { Label = labels[i], Distance = 0, LeafIndices = [i] };
            sizeById[i] = 1;
        }

        // Distanze fra cluster attivi, chiave normalizzata (min,max) per evitare duplicati.
        var dist = new Dictionary<(int, int), double>();
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                dist[(i, j)] = distanceMatrix[i, j];
            }
        }

        var active = Enumerable.Range(0, n).ToList();
        var nextId = n;

        while (active.Count > 1)
        {
            var bestA = -1;
            var bestB = -1;
            var bestDist = double.MaxValue;
            for (var ai = 0; ai < active.Count; ai++)
            {
                for (var bi = ai + 1; bi < active.Count; bi++)
                {
                    var a = active[ai];
                    var b = active[bi];
                    var d = GetDist(dist, a, b);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            var merged = new ClusterNode
            {
                Left = nodeById[bestA],
                Right = nodeById[bestB],
                Distance = bestDist,
                LeafIndices = [.. nodeById[bestA].LeafIndices, .. nodeById[bestB].LeafIndices],
            };

            var newId = nextId++;
            nodeById[newId] = merged;
            sizeById[newId] = sizeById[bestA] + sizeById[bestB];

            // Formula di Lance-Williams: aggiorna la distanza dal nuovo cluster verso ogni altro
            // cluster attivo, a partire dalle distanze già note verso i due cluster fusi.
            foreach (var other in active)
            {
                if (other == bestA || other == bestB) continue;
                var dA = GetDist(dist, bestA, other);
                var dB = GetDist(dist, bestB, other);
                var newDist = method switch
                {
                    LinkageMethod.Single => Math.Min(dA, dB),
                    LinkageMethod.Complete => Math.Max(dA, dB),
                    LinkageMethod.Average => (sizeById[bestA] * dA + sizeById[bestB] * dB) / (double)(sizeById[bestA] + sizeById[bestB]),
                    _ => throw new NotSupportedException($"Linkage non supportato: {method}"),
                };
                SetDist(dist, newId, other, newDist);
            }

            active.Remove(bestA);
            active.Remove(bestB);
            active.Add(newId);
        }

        return nodeById[active[0]];
    }

    private static double GetDist(Dictionary<(int, int), double> dist, int a, int b) =>
        dist[a < b ? (a, b) : (b, a)];

    private static void SetDist(Dictionary<(int, int), double> dist, int a, int b, double value) =>
        dist[a < b ? (a, b) : (b, a)] = value;
}
