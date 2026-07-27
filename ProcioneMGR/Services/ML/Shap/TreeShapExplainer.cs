namespace ProcioneMGR.Services.ML.Shap;

// =============================================================================================
//  [D1 roadmap scoperta-pattern] TreeSHAP — attribuzione ESATTA dei valori di Shapley su un
//  ensemble di alberi, in tempo polinomiale (Lundberg, Erion & Lee 2019, algoritmo 2).
//
//  Perché non basta la permutation importance che già abbiamo: quella dice quanto CONTA una
//  feature in media su tutto il dataset, e nient'altro. SHAP dice quanto quella feature ha
//  spinto in su o in giù UNA specifica predizione, con segno, e le attribuzioni sommano
//  esattamente alla predizione (proprietà di efficienza). Serve per rispondere a "perché il
//  modello ha detto compra QUI", non solo a "quali fattori sono importanti in generale".
//
//  AVVERTENZA che accompagna ogni uso di questi numeri, in UI e nei report: SHAP misura la
//  CORRELAZIONE che il modello ha sfruttato, non la causalità. Un fattore può risultare
//  dominante perché è il proxy di qualcos'altro che non abbiamo misurato.
// =============================================================================================

/// <summary>Contributo di una singola feature a una singola predizione.</summary>
public sealed record ShapContribution(string FeatureName, double Value, float FeatureValue);

/// <summary>
/// Spiegazione locale di una predizione: <c>Baseline + Σ Contributi == Prediction</c>
/// (efficienza, verificata dai test).
/// </summary>
public sealed record ShapExplanation(
    double Baseline,
    double Prediction,
    IReadOnlyList<ShapContribution> Contributions);

/// <summary>Riga della sintesi globale: importanza media e direzione prevalente di una feature.</summary>
public sealed record ShapSummaryRow(
    string FeatureName,
    double MeanAbsShap,
    double MeanShap)
{
    /// <summary>
    /// Quanto il contributo è direzionalmente coerente: |media| / media|·|. Vicino a 1 = la feature
    /// spinge quasi sempre dalla stessa parte; vicino a 0 = spinge in su e in giù a seconda del
    /// contesto (interazione o non-monotonia), che è un'informazione diversa e utile.
    /// </summary>
    public double DirectionalConsistency => MeanAbsShap > 1e-12 ? Math.Abs(MeanShap) / MeanAbsShap : 0d;
}

/// <summary>
/// TreeSHAP path-dependent su un <see cref="ShapTreeEnsemble"/>. Deterministico e senza stato:
/// stessa istanza riutilizzabile su più righe.
/// </summary>
public sealed class TreeShapExplainer(ShapTreeEnsemble ensemble)
{
    private readonly ShapTreeEnsemble _ensemble = ensemble ?? throw new ArgumentNullException(nameof(ensemble));

    /// <summary>Valore atteso sul background: il punto di partenza di ogni spiegazione.</summary>
    public double Baseline { get; } = ensemble.ExpectedValue();

    /// <summary>
    /// Valori SHAP grezzi per una riga, indicizzati per posizione di feature. La somma più
    /// <see cref="Baseline"/> ricostruisce la predizione dell'ensemble.
    /// </summary>
    public double[] Explain(ReadOnlySpan<float> features)
    {
        var phi = new double[_ensemble.FeatureCount];
        for (var t = 0; t < _ensemble.Trees.Count; t++)
        {
            var tree = _ensemble.Trees[t];
            var weight = _ensemble.Weights[t];
            if (weight == 0) continue;

            // Buffer dei cammini: alla profondità d servono d+1 elementi, e ogni livello della
            // ricorsione ne ricopia una fetta — da cui la dimensione triangolare.
            var depth = tree.MaxDepth + 2;
            var path = new PathElement[depth * (depth + 1) / 2];
            var contribution = new double[_ensemble.FeatureCount];
            Recurse(tree, features, contribution, node: 0, path, offset: 0, uniqueDepth: 0,
                fractionZero: 1, fractionOne: 1, featureIndex: -1);

            for (var f = 0; f < phi.Length; f++) phi[f] += weight * contribution[f];
        }
        return phi;
    }

    /// <summary>Spiegazione locale già confezionata per la UI (contributi ordinati per |valore|).</summary>
    public ShapExplanation ExplainRow(float[] features, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(featureNames);

        var phi = Explain(features);
        var contributions = new List<ShapContribution>(phi.Length);
        for (var f = 0; f < phi.Length; f++)
        {
            var name = f < featureNames.Count ? featureNames[f] : $"feature[{f}]";
            contributions.Add(new ShapContribution(name, phi[f], f < features.Length ? features[f] : 0f));
        }

        return new ShapExplanation(
            Baseline,
            _ensemble.Predict(features),
            contributions.OrderByDescending(c => Math.Abs(c.Value)).ToList());
    }

    /// <summary>
    /// Sintesi globale su un campione di righe: importanza media (media dei |SHAP|) e direzione
    /// media per feature. È l'equivalente SHAP del summary plot, ordinato per importanza.
    /// </summary>
    public IReadOnlyList<ShapSummaryRow> Summarize(IReadOnlyList<float[]> rows, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(featureNames);

        var n = _ensemble.FeatureCount;
        var sumAbs = new double[n];
        var sum = new double[n];

        foreach (var row in rows)
        {
            var phi = Explain(row);
            for (var f = 0; f < n; f++)
            {
                sumAbs[f] += Math.Abs(phi[f]);
                sum[f] += phi[f];
            }
        }

        var count = Math.Max(1, rows.Count);
        var result = new List<ShapSummaryRow>(n);
        for (var f = 0; f < n; f++)
        {
            var name = f < featureNames.Count ? featureNames[f] : $"feature[{f}]";
            result.Add(new ShapSummaryRow(name, sumAbs[f] / count, sum[f] / count));
        }
        return result
            .OrderByDescending(r => r.MeanAbsShap)
            .ThenBy(r => r.FeatureName, StringComparer.Ordinal)
            .ToList();
    }

    // --- Algoritmo -----------------------------------------------------------------------------
    //
    //  Il cammino (unique path) rappresenta l'insieme delle feature già incontrate scendendo
    //  l'albero, ciascuna con: la frazione di campioni che la seguirebbero "da assente"
    //  (fractionZero), se è presente nel sottoinsieme (fractionOne), e il peso combinatorio
    //  (Weight) che tiene conto di tutti gli ordini di inserimento possibili. Extend aggiunge una
    //  feature al cammino; Unwind la toglie (serve quando la stessa feature ricompare più in
    //  basso nell'albero); UnwoundSum calcola quanto peserebbe il cammino senza una feature, che
    //  è esattamente il coefficiente di Shapley di quella feature su quella foglia.

    private struct PathElement
    {
        public int FeatureIndex;
        public double FractionZero;
        public double FractionOne;
        public double Weight;
    }

    private static void Recurse(
        ShapTree tree, ReadOnlySpan<float> x, double[] phi,
        int node, PathElement[] path, int offset, int uniqueDepth,
        double fractionZero, double fractionOne, int featureIndex)
    {
        // Ogni livello lavora su una PROPRIA copia del cammino del padre: la ricorsione ne modifica
        // i pesi, e il fratello deve ripartire dal cammino intatto.
        var current = offset + uniqueDepth;
        Array.Copy(path, offset, path, current, uniqueDepth);
        ExtendPath(path, current, uniqueDepth, fractionZero, fractionOne, featureIndex);

        if (tree.IsLeaf(node))
        {
            var leafValue = tree.Value[node];
            for (var i = 1; i <= uniqueDepth; i++)
            {
                var w = UnwoundPathSum(path, current, uniqueDepth, i);
                ref var el = ref path[current + i];
                phi[el.FeatureIndex] += w * (el.FractionOne - el.FractionZero) * leafValue;
            }
            return;
        }

        var splitFeature = tree.SplitFeature[node];
        var value = splitFeature >= 0 && splitFeature < x.Length ? x[splitFeature] : 0f;
        var hot = value <= tree.Threshold[node] ? tree.Left[node] : tree.Right[node];
        var cold = hot == tree.Left[node] ? tree.Right[node] : tree.Left[node];

        var incomingZero = 1.0;
        var incomingOne = 1.0;

        // Se questa feature è già sul cammino, il suo contributo va tolto e ricalcolato più in
        // basso con la condizione più stringente: senza questo passo una feature usata due volte
        // sullo stesso ramo verrebbe contata due volte.
        var pathIndex = 1;
        for (; pathIndex <= uniqueDepth; pathIndex++)
        {
            if (path[current + pathIndex].FeatureIndex == splitFeature) break;
        }
        if (pathIndex <= uniqueDepth)
        {
            incomingZero = path[current + pathIndex].FractionZero;
            incomingOne = path[current + pathIndex].FractionOne;
            UnwindPath(path, current, uniqueDepth, pathIndex);
            uniqueDepth--;
        }

        Recurse(tree, x, phi, hot, path, current, uniqueDepth + 1,
            incomingZero * tree.ChildFraction(node, hot), incomingOne, splitFeature);
        Recurse(tree, x, phi, cold, path, current, uniqueDepth + 1,
            incomingZero * tree.ChildFraction(node, cold), 0, splitFeature);
    }

    private static void ExtendPath(
        PathElement[] path, int offset, int uniqueDepth, double fractionZero, double fractionOne, int featureIndex)
    {
        path[offset + uniqueDepth] = new PathElement
        {
            FeatureIndex = featureIndex,
            FractionZero = fractionZero,
            FractionOne = fractionOne,
            Weight = uniqueDepth == 0 ? 1.0 : 0.0,
        };

        for (var i = uniqueDepth - 1; i >= 0; i--)
        {
            path[offset + i + 1].Weight += fractionOne * path[offset + i].Weight * (i + 1) / (uniqueDepth + 1.0);
            path[offset + i].Weight = fractionZero * path[offset + i].Weight * (uniqueDepth - i) / (uniqueDepth + 1.0);
        }
    }

    private static void UnwindPath(PathElement[] path, int offset, int uniqueDepth, int pathIndex)
    {
        var fractionOne = path[offset + pathIndex].FractionOne;
        var fractionZero = path[offset + pathIndex].FractionZero;
        var nextOnePortion = path[offset + uniqueDepth].Weight;

        for (var i = uniqueDepth - 1; i >= 0; i--)
        {
            if (fractionOne != 0)
            {
                var tmp = path[offset + i].Weight;
                path[offset + i].Weight = nextOnePortion * (uniqueDepth + 1.0) / ((i + 1) * fractionOne);
                nextOnePortion = tmp - path[offset + i].Weight * fractionZero * (uniqueDepth - i) / (uniqueDepth + 1.0);
            }
            else if (fractionZero != 0)
            {
                path[offset + i].Weight = path[offset + i].Weight * (uniqueDepth + 1.0) / (fractionZero * (uniqueDepth - i));
            }
            else
            {
                path[offset + i].Weight = 0;
            }
        }

        for (var i = pathIndex; i < uniqueDepth; i++)
        {
            path[offset + i].FeatureIndex = path[offset + i + 1].FeatureIndex;
            path[offset + i].FractionZero = path[offset + i + 1].FractionZero;
            path[offset + i].FractionOne = path[offset + i + 1].FractionOne;
        }
    }

    private static double UnwoundPathSum(PathElement[] path, int offset, int uniqueDepth, int pathIndex)
    {
        var fractionOne = path[offset + pathIndex].FractionOne;
        var fractionZero = path[offset + pathIndex].FractionZero;
        var nextOnePortion = path[offset + uniqueDepth].Weight;
        var total = 0.0;

        for (var i = uniqueDepth - 1; i >= 0; i--)
        {
            if (fractionOne != 0)
            {
                var tmp = nextOnePortion * (uniqueDepth + 1.0) / ((i + 1) * fractionOne);
                total += tmp;
                nextOnePortion = path[offset + i].Weight - tmp * fractionZero * (uniqueDepth - i) / (uniqueDepth + 1.0);
            }
            else if (fractionZero != 0)
            {
                total += path[offset + i].Weight / fractionZero * (uniqueDepth + 1.0) / (uniqueDepth - i);
            }
        }
        return total;
    }
}
