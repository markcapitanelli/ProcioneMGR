using ProcioneMGR.Data;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.ML.Shap;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D1] Verifica della LENTE con cui la matrice SHAP raggruppa le righe. Il PRD §5a chiedeva i
/// regimi K-means; la lente è iniettabile e ripiega sui terzili di volatilità quando quel modello
/// non c'è. Qui si controlla che il raggruppamento sia corretto, che il ripiego non cambi il
/// comportamento preesistente, e che una riga senza etichetta esca dalla matrice invece di finire
/// in una colonna inventata.
/// </summary>
public class ShapContextLensTests
{
    private sealed class Row
    {
        [Microsoft.ML.Data.VectorType(3)]
        public float[] Features { get; set; } = new float[3];
        public float Label { get; set; }
    }

    private static OhlcvData Bar(int i, decimal close) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close * 1.002m, Low = close * 0.998m, Close = close, Volume = 100m,
    };

    /// <summary>Modello ad alberi vero + righe/timestamp allineati: il minimo per esercitare la matrice.</summary>
    private static (ShapTreeEnsemble Ensemble, List<float[]> Rows, List<DateTime> Timestamps,
        List<string> Names, List<OhlcvData> Candles, RegressionPredictorBase Predictor) BuildFixture(int n = 240)
    {
        var rnd = new Random(9);
        var rows = new List<float[]>(n);
        var labels = new List<float>(n);
        var candles = new List<OhlcvData>(n);
        var timestamps = new List<DateTime>(n);

        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var f = new[] { (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble() };
            rows.Add(f);
            labels.Add(f[0] * 2f + f[1]);
            // Prima metà calma, seconda metà mossa: così i terzili di volatilità hanno di che lavorare.
            var step = i < n / 2 ? 0.001 : 0.02;
            price *= 1m + (decimal)((rnd.NextDouble() - 0.5) * step);
            candles.Add(Bar(i, price));
            timestamps.Add(candles[i].TimestampUtc);
        }

        var ml = new Microsoft.ML.MLContext(seed: 1);
        var data = ml.Data.LoadFromEnumerable(rows.Select((f, i) => new Row { Features = f, Label = labels[i] }));
        var predictor = new GradientBoostingReturnPredictor();
        predictor.Fit(ml, data);
        var ensemble = predictor.TryBuildShapEnsemble(rows)!;

        return (ensemble, rows, timestamps, ["f0", "f1", "f2"], candles, predictor);
    }

    // --- La lente iniettata ---------------------------------------------------------------------

    [Fact]
    public void AnInjectedLens_GroupsRowsIntoItsOwnColumns()
    {
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        // Lente finta a due stati, alternati: ogni riga ha un'etichetta nota.
        var map = timestamps.Select((t, i) => (t, label: i % 2 == 0 ? "Toro" : "Orso"))
            .ToDictionary(x => x.t, x => x.label);
        var lens = new ShapContextLens(map, ["Toro", "Orso"], "Lente di prova");

        var result = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);

        Assert.Equal("Lente di prova", result.LensName);
        Assert.Equal(["Toro", "Orso"], result.Contexts);
        // Ogni colonna ha una cella per fattore, e i conteggi coprono tutte le righe campionate.
        Assert.Equal(2 * names.Count, result.ByContext.Count);
        Assert.Equal(result.RowsAnalyzed, result.ByContext.Where(c => c.FeatureName == "f0").Sum(c => c.Rows));
    }

    [Fact]
    public void ColumnOrderFollowsTheLens_NotTheOrderOfAppearance()
    {
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        // La prima riga e' "Zulu", ma la lente dichiara Alfa prima: deve vincere la lente, altrimenti
        // la matrice cambierebbe disposizione fra due esecuzioni sugli stessi dati.
        var map = timestamps.Select((t, i) => (t, label: i == 0 ? "Zulu" : "Alfa"))
            .ToDictionary(x => x.t, x => x.label);
        var lens = new ShapContextLens(map, ["Alfa", "Zulu"], "Ordine esplicito");

        var result = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);

        Assert.Equal(["Alfa", "Zulu"], result.Contexts);
    }

    [Fact]
    public void RowsWithoutALabel_StayOutOfTheMatrix()
    {
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        // Solo la prima metà dei timestamp è etichettata: le altre righe non devono comparire.
        var map = timestamps.Take(timestamps.Count / 2)
            .ToDictionary(t => t, _ => "Coperto");
        var lens = new ShapContextLens(map, ["Coperto"], "Copertura parziale");

        var result = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);

        Assert.Equal(["Coperto"], result.Contexts);
        var covered = result.ByContext.First(c => c.FeatureName == "f0").Rows;
        Assert.True(covered < result.RowsAnalyzed,
            $"righe nella matrice {covered}, campionate {result.RowsAnalyzed}: le non etichettate dovevano restare fuori");
        Assert.True(covered > 0);
    }

    [Fact]
    public void ALensWithASingleState_ProducesExactlyOneColumn()
    {
        // Controllo: nessuna colonna fantasma quando tutte le righe cadono nello stesso stato.
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        var map = timestamps.ToDictionary(t => t, _ => "Unico");
        var lens = new ShapContextLens(map, ["Unico", "Mai visto"], "Stato unico");

        var result = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);

        Assert.Equal(["Unico"], result.Contexts);
        Assert.Equal(names.Count, result.ByContext.Count);
        Assert.All(result.ByContext, c => Assert.Equal(result.RowsAnalyzed, c.Rows));
    }

    // --- Il ripiego -----------------------------------------------------------------------------

    [Fact]
    public void WithoutALens_TheResultIsTheVolatilityFallback()
    {
        // REGRESSIONE: senza lente il comportamento dev'essere quello di prima dell'introduzione
        // della lente iniettabile — terzili di volatilità, stesse etichette, stesso nome.
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        var result = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles);

        Assert.Equal(ShapAnalyzer.VolatilityLensName, result.LensName);
        Assert.All(result.Contexts, c => Assert.Contains(c, ShapAnalyzer.VolatilityLabels));
        Assert.NotEmpty(result.ByContext);
    }

    [Fact]
    public void PassingTheVolatilityLensExplicitly_MatchesTheImplicitFallback()
    {
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;

        var implicitResult = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles);
        var explicitResult = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles,
            lens: ShapAnalyzer.BuildVolatilityLens(candles));

        Assert.Equal(implicitResult.Contexts, explicitResult.Contexts);
        Assert.Equal(implicitResult.ByContext.Count, explicitResult.ByContext.Count);
        for (var i = 0; i < implicitResult.ByContext.Count; i++)
        {
            Assert.Equal(implicitResult.ByContext[i].MeanAbsShap, explicitResult.ByContext[i].MeanAbsShap, 12);
            Assert.Equal(implicitResult.ByContext[i].Rows, explicitResult.ByContext[i].Rows);
        }
    }

    [Fact]
    public void VolatilityLens_OnASeriesTooShort_IsEmptyInsteadOfWrong()
    {
        var candles = Enumerable.Range(0, 5).Select(i => Bar(i, 100m)).ToList();

        var lens = ShapAnalyzer.BuildVolatilityLens(candles);

        Assert.Empty(lens.LabelByTimestamp);
        Assert.Equal(ShapAnalyzer.VolatilityLensName, lens.Name);
    }

    [Fact]
    public void AnalysisWithALensIsDeterministic()
    {
        var (ensemble, rows, timestamps, names, candles, predictor) = BuildFixture();
        using var _ = predictor;
        var map = timestamps.Select((t, i) => (t, label: i % 3 == 0 ? "A" : "B")).ToDictionary(x => x.t, x => x.label);
        var lens = new ShapContextLens(map, ["A", "B"], "Deterministica");

        var a = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);
        var b = ShapAnalyzer.Analyze(ensemble, rows, timestamps, names, candles, lens: lens);

        Assert.Equal(a.Contexts, b.Contexts);
        for (var i = 0; i < a.ByContext.Count; i++)
        {
            Assert.Equal(a.ByContext[i].MeanAbsShap, b.ByContext[i].MeanAbsShap, 12);
        }
    }
}
