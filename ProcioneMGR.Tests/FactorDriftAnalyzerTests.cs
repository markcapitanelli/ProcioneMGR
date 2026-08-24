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
    /// <param name="breakFraction">
    /// [2026-08-24] DOVE cade la rottura, come frazione della serie. Prima era fissa a metà, e con
    /// il verdetto basato su una soglia assoluta non faceva differenza. Ora sì, ed è un fatto sul
    /// mondo, non sul codice: con 8 finestre e <c>RecentWindows = 2</c>, una rottura a metà cade
    /// <b>dentro il periodo di riferimento</b> — quattro finestre con segnale e due senza. Il
    /// riferimento diventa esso stesso incoerente, e chiedere allo strumento di gridare lì
    /// significherebbe chiedergli di ignorare la propria dispersione. Il decadimento che questo
    /// monitor dichiara di trovare è quello che cade sul CONFINE riferimento/recente: 0,75 con
    /// otto finestre.
    /// </param>
    private static (List<OhlcvData> Candles, List<decimal?> Values) BuildSeries(
        int n, int firstHalfSign, int secondHalfSign, int seed = 3, double breakFraction = 0.5)
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
            var sign = i < (int)(n * breakFraction) ? firstHalfSign : secondHalfSign;
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

    /// <summary>
    /// La rottura cade di default sul CONFINE riferimento/recente (0,75 con otto finestre e due
    /// recenti): è la posizione che il monitor dichiara di sorvegliare. Vedi <c>breakFraction</c>.
    /// </summary>
    private static FactorDriftReport Run(
        int firstHalfSign, int secondHalfSign, int n = 2400, double breakFraction = 0.75)
    {
        var (candles, values) = BuildSeries(n, firstHalfSign, secondHalfSign, breakFraction: breakFraction);
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
        // IL CONTROLLO A EDGE PIANTATO, ed è la condizione che rende leggibile il silenzio della
        // scheda: se dopo la correzione statistica del 2026-08-24 il pannello si svuota, deve
        // essere perché non c'è niente da dire — non perché lo strumento ha smesso di vedere.
        // Qui l'edge c'è, è enorme e cade dove il monitor dichiara di guardare.
        var report = Run(firstHalfSign: 1, secondHalfSign: 0);

        Assert.Equal(FactorDriftStatus.Weakening, report.Status);
        Assert.True(report.IsAlert);
        Assert.Contains("indebolito", report.StatusMessage);
        // E lo trova con margine: |IC| da ~1,0 a ~0,0 vale decine di errori standard.
        Assert.True(report.TStatistic > 5d, $"il calo piantato deve superare largamente il rumore, t = {report.TStatistic:F1}");
        Assert.True(report.PValue < 0.001d, $"p atteso vicino a zero, ottenuto {report.PValue:F4}");
    }

    /// <summary>
    /// Il complemento onesto del test qui sopra, e riguarda il MONDO, non il codice: con otto
    /// finestre e due «recenti», un decadimento a metà serie cade <b>dentro il periodo di
    /// riferimento</b>. Il riferimento diventa allora incoerente con sé stesso — quattro finestre
    /// informative e due no — e chiedere allo strumento di gridare lì significherebbe chiedergli di
    /// ignorare la propria dispersione, che è il difetto corretto il 2026-08-24.
    ///
    /// <para>Non è un limite da nascondere: è la ragione per cui questo monitor va guardato spesso,
    /// e non è un sostituto del pannello di /feature-selection, che mostra la serie intera.</para>
    /// </summary>
    [Fact]
    public void UnDecadimentoVECCHIO_NonScattaPiu_ELoStrumentoLoAmmette()
    {
        var report = Run(firstHalfSign: 1, secondHalfSign: 0, breakFraction: 0.5);

        Assert.False(report.IsAlert);

        // E lo strumento non tace per caso: il riferimento non supera il proprio cancello perché
        // metà delle sue finestre informano e metà no, quindi la sua dispersione INTERNA è enorme.
        // Detto in italiano: «questo fattore è troppo incoerente nel periodo di riferimento perché
        // quel periodo faccia da metro». È una risposta migliore di un allarme, e soprattutto è la
        // risposta a una domanda che il monitor può davvero porre.
        Assert.True(Math.Abs(report.ReferenceIc) < report.ReferenceGate,
            $"|IC| di riferimento {Math.Abs(report.ReferenceIc):F3} contro cancello {report.ReferenceGate:F3}");
        Assert.Contains("Non informava già", report.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Il fatto resta visibile dove va guardato: la serie di finestre porta il crollo per intero.
        var prime = report.Series.Take(4).Average(p => Math.Abs(p.InformationCoefficient));
        var ultime = report.Series.TakeLast(4).Average(p => Math.Abs(p.InformationCoefficient));
        Assert.True(prime > ultime * 3, "il crollo c'è ed è nella serie: è il confronto medio-contro-medio che non lo può vedere");
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
    }

    /// <summary>
    /// IL CONTROLLO SUL RUMORE, misurato come TASSO e non su un seme solo — livello 2 dello
    /// standard di verifica.
    ///
    /// <para>È il gemello del controllo a edge piantato, e insieme dicono la cosa che serve: lo
    /// strumento vede i cali veri (test sopra) e tace sul rumore (questo). Senza il secondo, una
    /// regola che grida sempre passerebbe il primo a pieni voti — ed è esattamente quello che
    /// faceva la regola precedente, dove un nullo che conservava il meccanismo di selezione
    /// produceva PIÙ allarmi del reale (85 su 165 contro 39 su 131).</para>
    ///
    /// <para>Si ammette qualche falso: la soglia è α = 0,05 per costruzione, e su 40 semi
    /// l'attesa è 2. Zero allarmi ammessi sarebbe una pretesa sbagliata — vorrebbe dire una
    /// soglia molto più severa di quella dichiarata.</para>
    /// </summary>
    [Fact]
    public void SoloRUMORE_IlTassoDiFalsiAllarmiRestaSottoIlDichiarato()
    {
        var allarmi = 0;
        const int semi = 40;
        for (var seed = 1; seed <= semi; seed++)
        {
            var (candles, values) = BuildSeries(2400, 0, 0, seed: seed);
            var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());
            var report = new FactorDriftAnalyzer().Analyze(spec, candles,
                new FactorDriftConfig { ForwardHorizon = 1, WindowSize = 300, RecentWindows = 2 });
            if (report.IsAlert) allarmi++;
        }

        // 4 su 40 = 10%: il doppio del nominale, che su 40 prove è ancora dentro la variabilità
        // campionaria. Sopra questo, la regola starebbe fabbricando allarmi dal nulla.
        Assert.True(allarmi <= 4, $"{allarmi} falsi allarmi su {semi} semi di puro rumore: troppi per una soglia dichiarata al 5%.");
    }

    /// <summary>
    /// IL NULLO CHE CONSERVA IL MECCANISMO DI SELEZIONE — la lezione di D4, applicata qui.
    ///
    /// <para>È il test che ha smascherato la regola precedente: applicando lo stesso giudizio alle
    /// due finestre PIÙ VECCHIE invece che alle due più recenti, il nullo produceva più allarmi del
    /// reale. Se la posizione «fine serie» non porta informazione, lo strumento sta misurando la
    /// regressione verso la media e non la deriva.</para>
    ///
    /// <para>Qui, su un decadimento piantato al confine, il verso giusto deve gridare e il verso
    /// rovesciato deve tacere: è la prova che il verdetto dipende da DOVE cade la rottura, cioè da
    /// un fatto, e non dalla forma del test.</para>
    /// </summary>
    [Fact]
    public void IlNulloARitroso_TACE_DoveIlVersoGiustoGrida()
    {
        var config = new FactorDriftConfig { ForwardHorizon = 1, WindowSize = 300, RecentWindows = 2 };
        var (candles, values) = BuildSeries(2400, 1, 0, seed: 3, breakFraction: 0.75);
        var spec = new FactorSpec("scripted", new ScriptedFactor(values), new Dictionary<string, decimal>());
        var vero = new FactorDriftAnalyzer().Analyze(spec, candles, config);

        Assert.True(vero.IsAlert, "il verso giusto deve trovare il decadimento piantato");

        // Il nullo: le stesse finestre, ma «recenti» sono le due più VECCHIE. Sotto quella lettura
        // il fattore non si è indebolito — si è rafforzato — quindi non ci deve essere allarme.
        var rovesciate = vero.Series.Reverse().ToList();
        var nullo = FactorDriftAnalyzer.JudgeSeries("scripted", "rovesciato", rovesciate, config);

        Assert.False(nullo.IsAlert,
            "il nullo che conserva il meccanismo di selezione deve TACERE: se grida, lo strumento misura la posizione, non la deriva");
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
        // [2026-08-24] La rottura al CONFINE riferimento/recente: è la posizione che il monitor
        // dichiara di sorvegliare (vedi breakFraction).
        var (candles, decayed) = BuildSeries(2400, 1, 0, seed: 3, breakFraction: 0.75);

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
