namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Funzioni pure per assegnare le candele ai regimi: nearest-centroid, smoothing a
/// conferma di 3 candele (anti flip-flop), e Silhouette Score (qualità del clustering).
/// </summary>
public static class RegimeAssignment
{
    /// <summary>Indice del centroide euclideo più vicino al vettore normalizzato.</summary>
    public static int NearestCentroid(float[] normalized, float[][] centroids)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var c = 0; c < centroids.Length; c++)
        {
            double dist = 0;
            var cen = centroids[c];
            for (var j = 0; j < normalized.Length && j < cen.Length; j++)
            {
                var d = normalized[j] - cen[j];
                dist += d * d;
            }
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        return best;
    }

    public static int[] AssignRaw(float[][] normalizedRows, float[][] centroids)
    {
        var labels = new int[normalizedRows.Length];
        for (var i = 0; i < normalizedRows.Length; i++)
        {
            labels[i] = NearestCentroid(normalizedRows[i], centroids);
        }
        return labels;
    }

    /// <summary>
    /// Smoothing: il regime cambia SOLO se un nuovo regime è confermato per
    /// <paramref name="confirmFrames"/> candele consecutive. Riduce drasticamente i flip-flop.
    /// </summary>
    public static int[] Smooth(int[] raw, int confirmFrames = 3)
    {
        var n = raw.Length;
        var smoothed = new int[n];
        if (n == 0) return smoothed;

        var current = raw[0];
        smoothed[0] = current;
        var pendingRegime = current;
        var pendingCount = 0;

        for (var i = 1; i < n; i++)
        {
            if (raw[i] == current)
            {
                pendingCount = 0;
                pendingRegime = current;
            }
            else
            {
                if (raw[i] == pendingRegime)
                {
                    pendingCount++;
                }
                else
                {
                    pendingRegime = raw[i];
                    pendingCount = 1;
                }

                if (pendingCount >= confirmFrames)
                {
                    current = pendingRegime;
                    pendingCount = 0;
                }
            }
            smoothed[i] = current;
        }
        return smoothed;
    }

    /// <summary>
    /// Voto di maggioranza causale su una finestra mobile: smoothed[i] = regime più frequente
    /// in raw[i-window+1 .. i]. Riduce i flip senza collassare la struttura e usa SOLO dati
    /// passati (no look-ahead). Seguito da conferma a <paramref name="confirmFrames"/> candele.
    /// </summary>
    public static int[] SmoothRolling(int[] raw, int window, int confirmFrames, int k)
    {
        var n = raw.Length;
        if (n == 0) return raw;
        if (window < 1) window = 1;

        var mode = new int[n];
        var counts = new int[k];
        for (var i = 0; i < n; i++)
        {
            // Aggiorna finestra [i-window+1 .. i].
            if (raw[i] >= 0 && raw[i] < k) counts[raw[i]]++;
            var drop = i - window;
            if (drop >= 0 && raw[drop] >= 0 && raw[drop] < k) counts[raw[drop]]--;

            // Regime di maggioranza nella finestra.
            var best = 0; var bestCount = -1;
            for (var c = 0; c < k; c++)
            {
                if (counts[c] > bestCount) { bestCount = counts[c]; best = c; }
            }
            mode[i] = best;
        }

        return Smooth(mode, confirmFrames);
    }

    /// <summary>Numero di transizioni di regime in una sequenza (per validare lo smoothing).</summary>
    public static int CountTransitions(int[] labels)
    {
        var t = 0;
        for (var i = 1; i < labels.Length; i++)
        {
            if (labels[i] != labels[i - 1]) t++;
        }
        return t;
    }

    /// <summary>
    /// Silhouette Score medio, stimato su un campione casuale (per restare O(sample²)).
    /// Per ogni punto: a = distanza media intra-cluster, b = minima distanza media verso un
    /// altro cluster; s = (b-a)/max(a,b). Range [-1, 1].
    /// </summary>
    public static double Silhouette(float[][] points, int[] labels, int k, int sampleSize = 2000, int seed = 0)
    {
        var n = points.Length;
        if (n < 3 || k < 2) return 0d;

        var rnd = new Random(seed);
        var idx = Enumerable.Range(0, n).ToArray();
        // Fisher-Yates parziale per campionare.
        var take = Math.Min(sampleSize, n);
        for (var i = 0; i < take; i++)
        {
            var j = rnd.Next(i, n);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        var sample = idx.Take(take).ToArray();

        double total = 0;
        var counted = 0;
        foreach (var pi in sample)
        {
            var own = labels[pi];
            var sumByCluster = new double[k];
            var cntByCluster = new int[k];
            foreach (var pj in sample)
            {
                if (pj == pi) continue;
                var dist = EuclDist(points[pi], points[pj]);
                var lj = labels[pj];
                sumByCluster[lj] += dist;
                cntByCluster[lj]++;
            }

            if (cntByCluster[own] == 0) continue; // cluster con un solo campione: salta
            var a = sumByCluster[own] / cntByCluster[own];

            var b = double.MaxValue;
            for (var c = 0; c < k; c++)
            {
                if (c == own || cntByCluster[c] == 0) continue;
                var mean = sumByCluster[c] / cntByCluster[c];
                if (mean < b) b = mean;
            }
            if (b == double.MaxValue) continue;

            var denom = Math.Max(a, b);
            if (denom > 0) { total += (b - a) / denom; counted++; }
        }

        return counted == 0 ? 0d : total / counted;
    }

    private static double EuclDist(float[] a, float[] b)
    {
        double sum = 0;
        for (var j = 0; j < a.Length && j < b.Length; j++)
        {
            var d = a[j] - b[j];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }
}
