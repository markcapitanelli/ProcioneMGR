namespace ProcioneMGR.Services.ML.Shap;

// =============================================================================================
//  [D1 roadmap scoperta-pattern] Rappresentazione NEUTRA di un ensemble di alberi, estratta da
//  un modello ML.NET (FastForest / LightGBM) e resa indipendente da ML.NET perché l'algoritmo
//  TreeSHAP possa lavorarci sopra senza conoscerne l'origine.
//
//  Due scelte da capire prima di leggere il codice:
//
//  1. INDICIZZAZIONE UNIFICATA. ML.NET tiene nodi interni e foglie in due spazi separati
//     (LeftChild[i] >= 0 = nodo interno, < 0 = foglia ~valore). TreeSHAP è molto più leggibile
//     su un albero dove foglie e nodi vivono nello stesso array, quindi qui le foglie diventano
//     nodi con indice InternalCount + k e figli -1.
//
//  2. COPERTURA (cover) CALCOLATA, NON LETTA. TreeSHAP path-dependent ha bisogno di sapere
//     quanti campioni attraversano ogni nodo, per pesare l'attesa quando una feature è "assente".
//     ML.NET NON espone questo dato. Lo ricaviamo passando un dataset di background attraverso
//     gli alberi e contando: è la definizione stessa di cover, quindi il risultato è esatto, non
//     un'approssimazione.
// =============================================================================================

/// <summary>
/// Un albero di regressione in forma neutra, con indicizzazione unificata nodi+foglie e la
/// copertura per nodo calcolata da un dataset di background.
/// </summary>
public sealed class ShapTree
{
    /// <summary>Figlio sinistro per nodo (−1 sulle foglie). Si scende a sinistra quando <c>x[feature] &lt;= soglia</c>.</summary>
    public required int[] Left { get; init; }

    /// <summary>Figlio destro per nodo (−1 sulle foglie).</summary>
    public required int[] Right { get; init; }

    /// <summary>Indice della feature su cui splitta il nodo (irrilevante sulle foglie).</summary>
    public required int[] SplitFeature { get; init; }

    /// <summary>Soglia dello split (irrilevante sulle foglie).</summary>
    public required double[] Threshold { get; init; }

    /// <summary>Valore predetto dal nodo se è foglia (0 sui nodi interni).</summary>
    public required double[] Value { get; init; }

    /// <summary>Numero di campioni di background che attraversano il nodo.</summary>
    public required double[] Cover { get; init; }

    /// <summary>Profondità massima dell'albero — dimensiona i buffer di TreeSHAP.</summary>
    public required int MaxDepth { get; init; }

    public int NodeCount => Left.Length;

    public bool IsLeaf(int node) => Left[node] < 0;

    /// <summary>
    /// Frazione della copertura del nodo che finisce nel figlio indicato. Se nessun campione di
    /// background raggiunge il nodo la copertura è zero e il rapporto sarebbe 0/0: in quel caso si
    /// ripiega su 1/2 e 1/2. Non è una toppa arbitraria — è l'unica scelta che mantiene coerenti
    /// TreeSHAP e il valore atteso (che usa le STESSE frazioni, vedi <see cref="ExpectedValue"/>),
    /// e quindi preserva la proprietà di efficienza su cui si regge tutta l'interpretazione.
    /// </summary>
    public double ChildFraction(int node, int child)
    {
        var total = Cover[node];
        if (total <= 0) return 0.5;
        return Cover[child] / total;
    }

    /// <summary>Predizione dell'albero per un vettore di feature (traversata secca).</summary>
    public double Predict(ReadOnlySpan<float> features)
    {
        var node = 0;
        while (!IsLeaf(node))
        {
            var f = SplitFeature[node];
            var v = f >= 0 && f < features.Length ? features[f] : 0f;
            node = v <= Threshold[node] ? Left[node] : Right[node];
        }
        return Value[node];
    }

    /// <summary>
    /// Valore atteso dell'albero sulla distribuzione di background, calcolato propagando le
    /// stesse frazioni di copertura usate da TreeSHAP. È il "punto zero" da cui partono i
    /// contributi SHAP: <c>somma(shap) + atteso == predizione</c>.
    /// </summary>
    public double ExpectedValue()
    {
        return Walk(0, 1.0);

        double Walk(int node, double weight)
        {
            if (IsLeaf(node)) return weight * Value[node];
            var l = Left[node];
            var r = Right[node];
            return Walk(l, weight * ChildFraction(node, l)) + Walk(r, weight * ChildFraction(node, r));
        }
    }
}

/// <summary>
/// Un ensemble di alberi in forma neutra: <c>predizione = Bias + Σ peso_i · albero_i(x)</c>.
/// </summary>
public sealed class ShapTreeEnsemble
{
    public required IReadOnlyList<ShapTree> Trees { get; init; }

    /// <summary>Peso di ciascun albero nella somma (LightGBM: 1; FastForest: 1/numeroAlberi).</summary>
    public required IReadOnlyList<double> Weights { get; init; }

    public required double Bias { get; init; }

    /// <summary>Numero di feature del vettore di input (serve a dimensionare i vettori SHAP).</summary>
    public required int FeatureCount { get; init; }

    /// <summary>
    /// Predizione ricostruita dalla struttura estratta. Esiste per essere CONFRONTATA con la
    /// predizione del modello ML.NET vero: se le due divergono, l'estrazione ha frainteso la
    /// convenzione degli alberi e ogni valore SHAP a valle sarebbe spazzatura ben formattata.
    /// Il test di regressione che le confronta è la spia di quel guasto.
    /// </summary>
    public double Predict(ReadOnlySpan<float> features)
    {
        var sum = Bias;
        for (var i = 0; i < Trees.Count; i++)
        {
            sum += Weights[i] * Trees[i].Predict(features);
        }
        return sum;
    }

    /// <summary>Valore atteso dell'ensemble sul background: la baseline dei contributi SHAP.</summary>
    public double ExpectedValue()
    {
        var sum = Bias;
        for (var i = 0; i < Trees.Count; i++)
        {
            sum += Weights[i] * Trees[i].ExpectedValue();
        }
        return sum;
    }
}
