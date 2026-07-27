using ProcioneMGR.Data;

namespace ProcioneMGR.Services.ML.Shap;

/// <summary>Una cella della matrice contesto × fattore: importanza media del fattore in quel contesto.</summary>
public sealed record ShapContextCell(string Context, string FeatureName, double MeanAbsShap, int Rows);

/// <summary>
/// La lente con cui le righe vengono raggruppate nella matrice: le etichette per timestamp, l'ordine
/// stabile delle colonne e un nome leggibile da mostrare in UI.
/// </summary>
public sealed record ShapContextLens(
    IReadOnlyDictionary<DateTime, string> LabelByTimestamp,
    IReadOnlyList<string> OrderedLabels,
    string Name);

/// <summary>
/// Esito completo dell'analisi SHAP di un modello: sintesi globale e rottura per contesto.
/// <see cref="LensName"/> dice CON QUALE lente le colonne sono state costruite — senza, la matrice
/// mostrerebbe numeri senza dire come sono raggruppati.
/// </summary>
public sealed record ShapAnalysisResult(
    double Baseline,
    int RowsAnalyzed,
    int TreeCount,
    IReadOnlyList<ShapSummaryRow> Global,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<ShapContextCell> ByContext,
    string LensName);

/// <summary>
/// Orchestrazione dell'analisi SHAP sopra <see cref="TreeShapExplainer"/>: campionamento delle
/// righe, sintesi globale e rottura per contesto.
///
/// <b>Quale lente.</b> La lente preferita sono i <b>regimi K-means</b> della pagina <c>/regimes</c>
/// (PRD §5a): raggruppare i contributi per stato di mercato riconosciuto dalla piattaforma è più
/// informativo di una terzina calcolata al volo. Ma quel modello dev'essere ATTIVO e della STESSA
/// serie del modello ML, e spesso non lo è — per questo la lente arriva dall'esterno
/// (<see cref="ShapContextLens"/>) e, quando manca, si ripiega sui terzili di volatilità realizzata,
/// sempre calcolabili dalle candele che il modello ha già. Il pannello non resta mai vuoto, e
/// <see cref="ShapAnalysisResult.LensName"/> dichiara sempre quale delle due si sta guardando.
///
/// <b>Qui il regime NON decide nulla.</b> È un asse di raggruppamento descrittivo, non un criterio
/// operativo: non deve superare alcun gate di discriminazione. È la differenza con
/// <c>LaneRegimeRouter</c>, che invece consulta il modello attivo per filtrare quali strategie
/// possono operare e resta giustamente in osservazione dopo l'esito del gate C1 (i regimi durano ma
/// non discriminano la performance). Confondere i due usi sarebbe l'errore da evitare.
/// </summary>
public static class ShapAnalyzer
{
    /// <summary>Etichette della lente di ripiego, dalla più calma alla più mossa.</summary>
    public static readonly IReadOnlyList<string> VolatilityLabels = ["Calmo", "Normale", "Turbolento"];

    /// <summary>Nome leggibile della lente di ripiego.</summary>
    public const string VolatilityLensName = "Volatilità realizzata (terzili)";

    /// <summary>
    /// Esegue l'analisi su un campione di righe. <paramref name="maxRows"/> limita il costo: SHAP
    /// è esatto ma non gratuito (alberi × profondità² per riga), e una sintesi su qualche centinaio
    /// di righe è già stabile. <paramref name="lens"/> a null ⇒ terzili di volatilità.
    /// </summary>
    public static ShapAnalysisResult Analyze(
        ShapTreeEnsemble ensemble,
        IReadOnlyList<float[]> rows,
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<string> featureNames,
        IReadOnlyList<OhlcvData> candles,
        int maxRows = 400,
        ShapContextLens? lens = null)
    {
        ArgumentNullException.ThrowIfNull(ensemble);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(featureNames);

        var explainer = new TreeShapExplainer(ensemble);

        // Campionamento UNIFORME nel tempo (passo costante), non i primi N: prendere solo la testa
        // del periodo darebbe una sintesi del passato remoto spacciata per sintesi del modello.
        var step = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)Math.Max(1, maxRows)));
        var sampled = new List<int>();
        for (var i = 0; i < rows.Count; i += step) sampled.Add(i);

        var sampledRows = sampled.Select(i => rows[i]).ToList();
        var global = explainer.Summarize(sampledRows, featureNames);

        var effectiveLens = lens ?? BuildVolatilityLens(candles);
        var accum = new Dictionary<(string Context, int Feature), double>();
        var countByContext = new Dictionary<string, int>();

        foreach (var i in sampled)
        {
            var context = i < timestamps.Count && effectiveLens.LabelByTimestamp.TryGetValue(timestamps[i], out var c)
                ? c
                : null;
            // Riga senza etichetta (warm-up della volatilità, o barra che il modello di regime non
            // copre): esce dalla matrice invece di finire in una colonna inventata.
            if (context is null) continue;

            countByContext[context] = countByContext.GetValueOrDefault(context) + 1;
            var phi = explainer.Explain(rows[i]);
            for (var f = 0; f < phi.Length; f++)
            {
                accum[(context, f)] = accum.GetValueOrDefault((context, f)) + Math.Abs(phi[f]);
            }
        }

        // L'ordine delle colonne viene dalla lente, non dall'ordine di comparsa: così la matrice non
        // cambia disposizione fra due esecuzioni sugli stessi dati.
        var cells = new List<ShapContextCell>();
        foreach (var context in effectiveLens.OrderedLabels)
        {
            var n = countByContext.GetValueOrDefault(context);
            if (n == 0) continue;
            for (var f = 0; f < ensemble.FeatureCount; f++)
            {
                var name = f < featureNames.Count ? featureNames[f] : $"feature[{f}]";
                cells.Add(new ShapContextCell(context, name, accum.GetValueOrDefault((context, f)) / n, n));
            }
        }

        return new ShapAnalysisResult(
            explainer.Baseline,
            sampled.Count,
            ensemble.Trees.Count,
            global,
            effectiveLens.OrderedLabels.Where(c => countByContext.ContainsKey(c)).ToList(),
            cells,
            effectiveLens.Name);
    }

    /// <summary>
    /// Lente di RIPIEGO: etichetta ogni candela con la terzina di volatilità realizzata a cui
    /// appartiene. Le soglie sono i terzili della distribuzione dell'intero periodo analizzato —
    /// una definizione relativa, non una soglia assoluta che non avrebbe senso confrontabile fra
    /// serie diverse. Le prime <c>lookback</c> candele restano senza etichetta (warm-up).
    /// </summary>
    public static ShapContextLens BuildVolatilityLens(IReadOnlyList<OhlcvData> candles, int lookback = 20)
    {
        var result = new Dictionary<DateTime, string>();
        if (candles is null || candles.Count <= lookback)
        {
            return new ShapContextLens(result, VolatilityLabels, VolatilityLensName);
        }

        var vol = new double?[candles.Count];
        for (var i = lookback; i < candles.Count; i++)
        {
            double sum = 0;
            var n = 0;
            for (var k = i - lookback + 1; k <= i; k++)
            {
                var prev = candles[k - 1].Close;
                if (prev <= 0m) continue;
                var r = (double)((candles[k].Close - prev) / prev);
                sum += r * r;
                n++;
            }
            if (n > 0) vol[i] = Math.Sqrt(sum / n);
        }

        var known = vol.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (known.Count < 3)
        {
            return new ShapContextLens(result, VolatilityLabels, VolatilityLensName);
        }
        var lower = known[known.Count / 3];
        var upper = known[known.Count * 2 / 3];

        for (var i = 0; i < candles.Count; i++)
        {
            if (vol[i] is not { } v) continue;
            result[candles[i].TimestampUtc] = v <= lower ? "Calmo" : v <= upper ? "Normale" : "Turbolento";
        }
        return new ShapContextLens(result, VolatilityLabels, VolatilityLensName);
    }
}
