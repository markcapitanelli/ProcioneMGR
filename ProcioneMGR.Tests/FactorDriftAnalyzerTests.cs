using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D2] Verifica del monitor di deriva dei fattori. Il metodo è lo stesso usato altrove nella
/// piattaforma: si costruiscono serie in cui la risposta giusta è NOTA per costruzione (un
/// fattore che informa e poi smette, uno che si capovolge, uno che non ha mai informato) e si
/// controlla che il verdetto la trovi — invece di verificare che il codice "giri".
/// </summary>
public class FactorDriftAnalyzerTests
{
    /// <summary>
    /// Fattore di prova che restituisce una serie decisa dal test: permette di piantare
    /// esattamente la relazione fattore→rendimento che si vuole misurare.
    /// </summary>
    private sealed class ScriptedFactor(IReadOnlyList<decimal?> values) : IAlphaFactor
    {
        public string Name => "Scripted";
        public string DisplayName => "Fattore pilotato";
        public FactorCategory Category => FactorCategory.Momentum;
        public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } = [];
        public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p) => values;
    }

    /// <summary>
    /// Costruisce candele e valori di fattore tali che, nella prima metà, il fattore predice il
    /// rendimento successivo con correlazione <paramref name="firstHalfSign"/>, e nella seconda
    /// con <paramref name="secondHalfSign"/> (0 = nessuna relazione, solo rumore).
    /// </summary>
    private static (List<OhlcvData> Candles, List<decimal?> Values) BuildSeries(
        int n, int firstHalfSign, int secondHalfSign, int seed = 3)
    {
        var rnd = new Random(seed);
        var candles = new List<OhlcvData>(n);
        var values = new List<decimal?>(n);

        var price = 100m;
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Il valore del fattore alla barra i decide il rendimento fra i e i+1: la relazione è
        // quindi CAUSALE per costruzione, e l'IC deve ritrovarla col segno giusto.
        var signal = new double[n];
        for (var i = 0; i < n; i++) signal[i] = rnd.NextDouble() * 2 - 1;

        for (var i = 0; i < n; i++)
        {
            var sign = i < n / 2 ? firstHalfSign : secondHalfSign;
            // Rendimento della barra i: guidato dal segnale della barra PRECEDENTE.
            var driver = i > 0 ? signal[i - 1] : 0;
            var noise = (rnd.NextDouble() * 2 - 1) * 0.002;
            var ret = sign * driver * 0.02 + noise;
            price *= (decimal)(1 + ret);

            candles.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = start.AddHours(i),
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = 1000m,
            });
            values.Add((decimal)signal[i]);
        }
        return (candles, values);
    }

    private static FactorDriftReport Run(int firstHalfSign, int secondHalfSign, int n = 2400)
    {
        var (candles, values) = BuildSeries(n, firstHalfSign, secondHalfSign);
        var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());
        var analyzer = new FactorDriftAnalyzer();
        return analyzer.Analyze(spec, candles, new FactorDriftConfig
        {
            ForwardHorizon = 1,
            WindowSize = 300,
            RecentWindows = 2,
        });
    }

    // --- Verdetto sulla storia REGISTRATA (D2, persistenza) --------------------------------------
    //
    //  La Home, dopo un riavvio del guscio, giudica finestre lette da tabella invece di ricalcolarle
    //  dalle candele. Questi test dicono che le due strade danno lo STESSO verdetto: se divergessero
    //  avremmo due monitor diversi con lo stesso nome, e l'alert in Home potrebbe contraddire il
    //  pannello di /feature-selection sulla stessa serie.

    [Theory]
    [InlineData(1, 0)]   // si spegne
    [InlineData(1, -1)]  // si capovolge
    [InlineData(1, 1)]   // resta stabile
    [InlineData(0, 0)]   // non ha mai informato
    public void JudgeSeries_ReproducesTheVerdictOfAFreshAnalysis(int firstHalfSign, int secondHalfSign)
    {
        var fresh = Run(firstHalfSign, secondHalfSign);
        var config = new FactorDriftConfig { ForwardHorizon = 1, WindowSize = 300, RecentWindows = 2 };

        // Solo le finestre sopravvivono al giro in tabella: il verdetto si ricostruisce da quelle.
        var rebuilt = FactorDriftAnalyzer.JudgeSeries("scripted", "Fattore pilotato", fresh.Series, config);

        Assert.Equal(fresh.Status, rebuilt.Status);
        Assert.Equal(fresh.ReferenceIc, rebuilt.ReferenceIc, 12);
        Assert.Equal(fresh.RecentIc, rebuilt.RecentIc, 12);
        Assert.Equal(fresh.NoiseFloor, rebuilt.NoiseFloor, 12);
        Assert.Equal(fresh.StatusMessage, rebuilt.StatusMessage);
    }

    [Fact]
    public void JudgeSeries_TakesTheWindowSizeFromTheSeries_NotFromTheConfig()
    {
        // La soglia è max(minimo economico, 1,96/√n): leggere n dalla config invece che dai punti
        // registrati significherebbe giudicare finestre da 2000 osservazioni col pavimento di
        // finestre da 250 — cioè promuovere rumore a segnale, l'errore che i test del primo giro
        // avevano già trovato una volta.
        var points = Enumerable.Range(0, 10)
            .Select(i => new FactorIcPoint(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i + 1),
                0.05, Observations: 2500))
            .ToList();

        var report = FactorDriftAnalyzer.JudgeSeries("x", "x", points, new FactorDriftConfig { WindowSize = 250 });

        Assert.Equal(FactorDriftAnalyzer.NoiseFloorFor(2500), report.NoiseFloor, 12);
        Assert.True(report.NoiseFloor < FactorDriftAnalyzer.NoiseFloorFor(250));
    }

    [Fact]
    public void JudgeSeries_WithTooFewPoints_SaysInsufficientInsteadOfGuessing()
    {
        var points = new List<FactorIcPoint>
        {
            new(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 0.09, 500),
            new(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), 0.01, 500),
        };

        var report = FactorDriftAnalyzer.JudgeSeries("x", "x", points, new FactorDriftConfig());

        Assert.Equal(FactorDriftStatus.Insufficient, report.Status);
        Assert.False(report.IsAlert);
    }

    [Fact]
    public void FactorThatKeepsWorking_IsStable()
    {
        var report = Run(firstHalfSign: 1, secondHalfSign: 1);

        Assert.Equal(FactorDriftStatus.Stable, report.Status);
        Assert.True(Math.Abs(report.ReferenceIc) > 0.02, $"il riferimento dovrebbe informare: {report.ReferenceIc:F4}");
        Assert.True(Math.Abs(report.RecentIc) > 0.02, $"il recente dovrebbe informare: {report.RecentIc:F4}");
    }

    [Fact]
    public void FactorThatStopsWorking_IsFlaggedAsWeakening()
    {
        // Informa nella prima metà, poi solo rumore: è il decadimento classico.
        var report = Run(firstHalfSign: 1, secondHalfSign: 0);

        Assert.Equal(FactorDriftStatus.Weakening, report.Status);
        Assert.True(report.IsAlert);
        Assert.Contains("spento", report.StatusMessage);
    }

    [Fact]
    public void FactorThatInverts_IsFlaggedAsSignFlip()
    {
        var report = Run(firstHalfSign: 1, secondHalfSign: -1);

        Assert.Equal(FactorDriftStatus.SignFlip, report.Status);
        Assert.True(report.IsAlert);
        Assert.True(report.ReferenceIc * report.RecentIc < 0, "riferimento e recente devono avere segni opposti");
    }

    [Fact]
    public void FactorThatNeverWorked_IsNotAnAlert()
    {
        // Rumore da sempre: NON è un decadimento. Segnalarlo come allarme sarebbe rumore in un
        // pannello che deve restare leggibile.
        var report = Run(firstHalfSign: 0, secondHalfSign: 0);

        Assert.Equal(FactorDriftStatus.Stable, report.Status);
        Assert.False(report.IsAlert);
        Assert.Contains("non informava già", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooFewWindows_ReportsInsufficientInsteadOfGuessing()
    {
        var (candles, values) = BuildSeries(400, 1, 1);
        var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());

        var report = new FactorDriftAnalyzer().Analyze(spec, candles, new FactorDriftConfig { WindowSize = 300 });

        Assert.Equal(FactorDriftStatus.Insufficient, report.Status);
        Assert.Contains("insufficienti", report.StatusMessage);
    }

    [Fact]
    public void Series_UsesNonOverlappingWindowsInChronologicalOrder()
    {
        var (candles, values) = BuildSeries(2400, 1, 1);
        var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());

        var report = new FactorDriftAnalyzer().Analyze(spec, candles, new FactorDriftConfig { WindowSize = 300 });

        Assert.True(report.Series.Count >= 4);
        for (var i = 1; i < report.Series.Count; i++)
        {
            // Non sovrapposte: ogni finestra inizia dopo la fine della precedente.
            Assert.True(report.Series[i].WindowStartUtc > report.Series[i - 1].WindowEndUtc,
                $"finestra {i} inizia {report.Series[i].WindowStartUtc:o}, la precedente finisce {report.Series[i - 1].WindowEndUtc:o}");
        }
        Assert.All(report.Series, p => Assert.Equal(300, p.Observations));
    }

    [Fact]
    public void AnalyzeMany_PutsAlertsFirst()
    {
        // Attenzione alla costruzione: i valori del fattore dipendono SOLO dal seme, mentre il
        // segno cambia le candele. Due chiamate a BuildSeries con lo stesso seme darebbero quindi
        // due fattori IDENTICI — errore commesso nella prima stesura di questo test. Qui si usa
        // una sola serie di candele (segnale che si spegne a metà) e due fattori diversi su di
        // essa: quello che si spegne, e un predittore perfetto che regge fino in fondo.
        var (candles, decayed) = BuildSeries(2400, 1, 0, seed: 3);

        var perfect = new List<decimal?>(candles.Count);
        for (var i = 0; i < candles.Count; i++)
        {
            perfect.Add(i + 1 < candles.Count && candles[i].Close > 0m
                ? (candles[i + 1].Close - candles[i].Close) / candles[i].Close
                : null);
        }

        var specs = new List<FactorSpec>
        {
            new("stabile", new ScriptedFactor(perfect), new Dictionary<string, decimal>()),
            new("decaduto", new ScriptedFactor(decayed), new Dictionary<string, decimal>()),
        };

        var reports = new FactorDriftAnalyzer().AnalyzeMany(specs, candles,
            new FactorDriftConfig { WindowSize = 300, RecentWindows = 2 });

        Assert.Equal("decaduto", reports[0].FeatureName);
        Assert.True(reports[0].IsAlert);
        Assert.False(reports[1].IsAlert);
    }

    /// <summary>
    /// REGRESSIONE sull'errore che questi test hanno trovato nella prima versione dell'analizzatore:
    /// giudicare l'IC contro la soglia fissa 0,02 senza tenere conto dell'ampiezza della finestra.
    /// Su 300 osservazioni l'errore standard di una correlazione attorno a zero è ≈ 0,058: un |IC|
    /// di 0,04 è rumore, e la soglia fissa lo avrebbe promosso a segnale, fabbricando allarmi e
    /// perfino "inversioni di segno" dal caso. La soglia operativa deve stare SOPRA il rumore.
    /// </summary>
    [Theory]
    [InlineData(100, 0.196)]
    [InlineData(300, 0.113)]
    [InlineData(2500, 0.039)]
    public void NoiseFloor_ScalesWithWindowSize(int window, double expected)
    {
        var floor = FactorDriftAnalyzer.NoiseFloorFor(window);

        Assert.Equal(expected, floor, 3);
        Assert.True(floor > 0.02,
            $"con finestre da {window} barre il pavimento di rumore ({floor:F3}) deve superare la soglia economica 0,02");
    }

    [Fact]
    public void PureNoiseFactor_NeverProducesAnAlert_AcrossManySeeds()
    {
        // Il controllo che conta davvero: su tanti semi diversi di puro rumore, il monitor non deve
        // MAI gridare. Un tasso di falsi allarmi anche solo del 10% renderebbe il pannello inutile.
        var analyzer = new FactorDriftAnalyzer();
        var alerts = 0;

        for (var seed = 1; seed <= 40; seed++)
        {
            var (candles, values) = BuildSeries(2400, 0, 0, seed);
            var spec = new FactorSpec("rumore", new ScriptedFactor(values), new Dictionary<string, decimal>());
            var report = analyzer.Analyze(spec, candles, new FactorDriftConfig { WindowSize = 300, RecentWindows = 2 });
            if (report.IsAlert) alerts++;
        }

        Assert.Equal(0, alerts);
    }

    [Fact]
    public void Analysis_IsDeterministic()
    {
        var (candles, values) = BuildSeries(2400, 1, 0);
        var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());
        var analyzer = new FactorDriftAnalyzer();
        var cfg = new FactorDriftConfig { WindowSize = 300 };

        var a = analyzer.Analyze(spec, candles, cfg);
        var b = analyzer.Analyze(spec, candles, cfg);

        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.RecentIc, b.RecentIc, 12);
        Assert.Equal(a.Series.Count, b.Series.Count);
    }
}
