using ProcioneMGR.Data;

namespace ProcioneMGR.Services.ML.Shap;

/// <summary>Una cella della matrice contesto × fattore: importanza media del fattore in quel contesto.</summary>
public sealed record ShapContextCell(string Context, string FeatureName, double MeanAbsShap, int Rows);

/// <summary>
/// Esito completo dell'analisi SHAP di un modello: sintesi globale e rottura per contesto di
/// volatilità.
/// </summary>
public sealed record ShapAnalysisResult(
    double Baseline,
    int RowsAnalyzed,
    int TreeCount,
    IReadOnlyList<ShapSummaryRow> Global,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<ShapContextCell> ByContext);

/// <summary>
/// Orchestrazione dell'analisi SHAP sopra <see cref="TreeShapExplainer"/>: campionamento delle
/// righe, sintesi globale e rottura per <b>contesto di volatilità</b>.
///
/// Sul contesto, una precisazione di vocabolario che conta: il PRD parlava di rottura "per
/// regime", intendendo i cluster K-means della pagina <c>/regimes</c>. Qui si usa invece una
/// terzina di volatilità realizzata calcolata dalle candele stesse. Due motivi, entrambi
/// pratici: (a) il modello K-means dev'essere ATTIVO e della stessa serie del modello ML, cosa
/// che nella maggior parte dei casi non è vera, e il pannello risulterebbe vuoto quasi sempre;
/// (b) i regimi K-means della piattaforma durano 2,2 giorni mediani e il gate C1 ha misurato che
/// non discriminano la performance — appoggiarci sopra una lente descrittiva darebbe
/// un'impressione di significato che la misura non sostiene. La terzina di volatilità è sempre
/// disponibile, si calcola dai dati che il modello ha già, e risponde alla stessa domanda utile:
/// il modello cambia idea quando il mercato si agita?
/// </summary>
public static class ShapAnalyzer
{
    /// <summary>Etichette dei tre contesti, dal più calmo al più mosso.</summary>
    public static readonly IReadOnlyList<string> ContextLabels = ["Calmo", "Normale", "Turbolento"];

    /// <summary>
    /// Esegue l'analisi su un campione di righe. <paramref name="maxRows"/> limita il costo: SHAP
    /// è esatto ma non gratuito (alberi × profondità² per riga), e una sintesi su qualche centinaio
    /// di righe è già stabile.
    /// </summary>
    public static ShapAnalysisResult Analyze(
        ShapTreeEnsemble ensemble,
        IReadOnlyList<float[]> rows,
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<string> featureNames,
        IReadOnlyList<OhlcvData> candles,
        int maxRows = 400)
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

        var contextByTimestamp = BuildVolatilityContext(candles);
        var accum = new Dictionary<(string Context, int Feature), double>();
        var countByContext = new Dictionary<string, int>();

        foreach (var i in sampled)
        {
            var context = i < timestamps.Count && contextByTimestamp.TryGetValue(timestamps[i], out var c)
                ? c
                : null;
            if (context is null) continue; // riga in warm-up della volatilità: fuori dalla matrice, non inventata

            countByContext[context] = countByContext.GetValueOrDefault(context) + 1;
            var phi = explainer.Explain(rows[i]);
            for (var f = 0; f < phi.Length; f++)
            {
                accum[(context, f)] = accum.GetValueOrDefault((context, f)) + Math.Abs(phi[f]);
            }
        }

        var cells = new List<ShapContextCell>();
        foreach (var context in ContextLabels)
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
            ContextLabels.Where(c => countByContext.ContainsKey(c)).ToList(),
            cells);
    }

    /// <summary>
    /// Etichetta ogni candela con la terzina di volatilità realizzata a cui appartiene. Le soglie
    /// sono i terzili della distribuzione dell'intero periodo analizzato: una definizione relativa,
    /// non una soglia assoluta che non avrebbe senso confrontabile fra serie diverse.
    /// Le prime <c>lookback</c> candele restano senza etichetta (warm-up), e le righe corrispondenti
    /// escono dalla matrice invece di finire in un contesto inventato.
    /// </summary>
    private static Dictionary<DateTime, string> BuildVolatilityContext(IReadOnlyList<OhlcvData> candles, int lookback = 20)
    {
        var result = new Dictionary<DateTime, string>();
        if (candles is null || candles.Count <= lookback) return result;

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
        if (known.Count < 3) return result;
        var lower = known[known.Count / 3];
        var upper = known[known.Count * 2 / 3];

        for (var i = 0; i < candles.Count; i++)
        {
            if (vol[i] is not { } v) continue;
            result[candles[i].TimestampUtc] = v <= lower ? "Calmo" : v <= upper ? "Normale" : "Turbolento";
        }
        return result;
    }
}
