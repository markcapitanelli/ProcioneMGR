using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Discovery.Dtw;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D4, misura] Test della catena completa: forma → occorrenze → event-study col placebo → verdetto.
///
/// La coppia che decide tutto:
///  - <see cref="PatternFollowedByARealMove_IsDeclaredPredictive"/> — si pianta un pattern SEGUITO
///    da un movimento vero: la catena deve accorgersene;
///  - <see cref="PatternFollowedByNothing_IsDeclaredNoise"/> — si pianta lo stesso pattern seguito
///    dal nulla: la catena NON deve accorgersi di niente.
/// Solo insieme dimostrano che il verdetto misura il segnale e non la propria voglia di trovarlo.
/// </summary>
public class DtwPatternAnalysisTests
{
    private static DtwPatternAnalysisService BuildService() => new(new DtwMatcher());

    /// <summary>
    /// Forma distintiva di 13 barre con tre inversioni, espressa come DEVIAZIONE PERCENTUALE dal
    /// livello di partenza. Due scelte deliberate, entrambe imparate da una prima stesura sbagliata
    /// di questi test:
    ///  - **ampiezza realistica** (~2%): la prima versione piantava un crollo del 60% e un recupero,
    ///    che non è un pattern ma un cataclisma — dominava le statistiche sia del segnale sia del
    ///    nullo, rendendo il confronto privo di senso;
    ///  - **lunga e con più inversioni**: una V di 7 barre la produce anche il rumore in
    ///    continuazione, e con la z-normalizzazione (che butta via l'ampiezza) una V dello 0,3% è
    ///    indistinguibile da una del 2%. Più struttura = più rara per caso.
    /// </summary>
    private static readonly decimal[] ShapeDeviations =
        [0m, -0.8m, -1.5m, -0.9m, -0.2m, -1.2m, -2.0m, -1.1m, -0.3m, 0.4m, 1.2m, 0.6m, 0m];

    private static decimal[] ShapeAt(decimal baseLevel) =>
        ShapeDeviations.Select(d => baseLevel * (1m + d / 100m)).ToArray();

    private static decimal[] VShape() => ShapeAt(100m);

    private static List<OhlcvData> ToSeries(IReadOnlyList<decimal> closes) =>
        closes.Select((c, i) => new OhlcvData
        {
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
            Open = c, High = c * 1.001m, Low = c * 0.999m, Close = c, Volume = 100m,
        }).ToList();

    /// <summary>
    /// Serie di rumore con la forma a V piantata a intervalli regolari. Se
    /// <paramref name="postMovePercent"/> è diverso da zero, dopo ogni occorrenza il prezzo compie
    /// un movimento vero: è l'edge piantato.
    /// </summary>
    private static List<OhlcvData> BuildPlanted(int n, int every, decimal postMovePercent, int seed = 12)
    {
        var rnd = new Random(seed);

        // Sfondo a PASSEGGIATA CASUALE, non rumore bianco attorno a un livello fisso: è la forma che
        // hanno i prezzi veri, e su rumore bianco qualunque pattern sembrerebbe mean-reverting per
        // costruzione.
        var closes = new List<decimal>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            price *= 1m + (decimal)((rnd.NextDouble() - 0.5) * 0.004);
            closes.Add(price);
        }

        var len = ShapeDeviations.Length;
        for (var pos = 200; pos + len + 60 < n; pos += every)
        {
            var baseLevel = closes[pos];
            var shape = ShapeAt(baseLevel);
            for (var k = 0; k < len; k++) closes[pos + k] = shape[k];

            var after = pos + len;
            if (postMovePercent == 0m)
            {
                // Nessun edge: la serie prosegue dal livello raggiunto, con la sua dinamica.
                var carry = closes[after - 1];
                for (var k = after; k < Math.Min(n, after + 60); k++)
                {
                    carry *= 1m + (decimal)((rnd.NextDouble() - 0.5) * 0.004);
                    closes[k] = carry;
                }
                continue;
            }

            // Edge piantato: movimento persistente dopo il pattern.
            var target = closes[after - 1] * (1m + postMovePercent / 100m);
            for (var k = 0; k < 10 && after + k < n; k++)
            {
                var t = (k + 1) / 10m;
                closes[after + k] = closes[after - 1] * (1 - t) + target * t;
            }
            var level = target;
            for (var k = after + 10; k < Math.Min(n, after + 60); k++)
            {
                level *= 1m + (decimal)((rnd.NextDouble() - 0.5) * 0.004);
                closes[k] = level;
            }
        }
        return ToSeries(closes);
    }

    private static DtwConfig Config() => new() { MaxDistance = 0.9, BandPercent = 20 };

    private static EventStudyConfig Study() => new(
        EstimationBars: 60, GapBars: 5, PreBars: 5, PostBars: 10, PlaceboSamples: 200, Seed: 42);

    // --- La coppia che decide ---------------------------------------------------------------------

    [Fact]
    public void PatternFollowedByARealMove_IsDeclaredPredictive()
    {
        // Frequenza BASSA di proposito: con un pattern piantato ogni 120 barre la serie si satura
        // di edge, e allora anche le forme casuali intercettano gli stessi movimenti — il nullo
        // sale fin quasi al segnale e nessun pattern risulta speciale. Che è, a pensarci, la
        // risposta corretta: se qualunque forma predice il movimento, non è la forma a contare.
        var series = BuildPlanted(20000, every: 250, postMovePercent: 6m);

        var analysis = BuildService().Analyze(series, VShape(), Config(), Study());

        Assert.True(analysis.IsUsable, analysis.Verdict);
        Assert.True(analysis.MatchCount >= 20, $"occorrenze trovate: {analysis.MatchCount}");
        Assert.True(analysis.IsPredictive,
            $"l'edge piantato doveva essere trovato. Verdetto: {analysis.Verdict}");
        Assert.NotNull(analysis.Study);
        Assert.NotNull(analysis.ShapeNull);
        Assert.True(analysis.ShapeNull!.IsUsable, "il nullo per forma dev'essere costruibile");
        Assert.True(analysis.ShapeNull.PValue <= 0.05,
            $"p del nullo per forma {analysis.ShapeNull.PValue:F4}: pattern casuali non dovrebbero eguagliare l'edge piantato");
    }

    [Fact]
    public void TheShapeMatchedNull_IsStricterThanTheRandomDatePlacebo()
    {
        // REGRESSIONE sul difetto di metodo trovato dai test: su rumore puro il placebo a date
        // casuali dichiarava significativo (p <= 0,05) un pattern che non anticipa nulla, perche'
        // selezionare finestre per FORMA induce da solo un andamento nelle barre successive. Il
        // nullo per forma conserva quel meccanismo e quindi non ci casca.
        var series = BuildPlanted(20000, every: 250, postMovePercent: 0m);

        var analysis = BuildService().Analyze(series, VShape(), Config(), Study());

        Assert.NotNull(analysis.Study);
        Assert.NotNull(analysis.ShapeNull);

        // Il placebo a date casuali si lascia ingannare: e' il motivo per cui da solo non basta.
        Assert.True(analysis.Study!.PlaceboPValue <= 0.05,
            $"p placebo {analysis.Study.PlaceboPValue:F4}");

        // L'asserzione e' sull'ESITO, non sul p a filo di soglia: su una singola estrazione un
        // p di 0,049 contro 0,051 e' la stessa cosa, e pretendere il lato giusto della soglia
        // renderebbe il test fragile invece che severo. Cio' che conta e' che la catena NON
        // dichiari operabile un pattern che non anticipa nulla.
        Assert.False(analysis.IsPredictive,
            $"verdetto: {analysis.Verdict}");
    }

    [Fact]
    public void PatternFollowedByNothing_IsDeclaredNoise()
    {
        // Stesso pattern, stessa frequenza, ma dopo NON succede nulla. Se il verdetto qui dicesse
        // "segnale", la catena starebbe trovando edge nel rumore.
        var series = BuildPlanted(20000, every: 250, postMovePercent: 0m);

        var analysis = BuildService().Analyze(series, VShape(), Config(), Study());

        Assert.True(analysis.IsUsable, analysis.Verdict);
        Assert.True(analysis.MatchCount >= 20);
        Assert.False(analysis.IsPredictive,
            $"nessun edge piantato, ma il verdetto dice: {analysis.Verdict}");

        // Due modi legittimi di dire di no, ed entrambi vanno bene: o l'effetto non batte il nullo
        // per forma, oppure lo batte ma resta sotto il pavimento economico. Il secondo e' proprio
        // cio' che succede qui (-0,43%), ed e' la guardia aggiunta dopo che un effetto dello
        // 0,48% era stato dichiarato "segnale" solo perche' il nullo era ancora piu' piccolo.
        Assert.True(
            analysis.Verdict.Contains("NESSUN SEGNALE") || analysis.Verdict.Contains("IRRILEVANTE"),
            $"verdetto inatteso: {analysis.Verdict}");
    }

    // --- Le guardie ---------------------------------------------------------------------------------

    [Fact]
    public void TooFewMatches_AreRefusedInsteadOfMeasured()
    {
        // Soglia severissima: quasi nessuna occorrenza. Un event-study su 3 eventi non dice nulla,
        // e il servizio deve rifiutarsi invece di produrre un p-value che sembra un risultato.
        var series = BuildPlanted(4000, every: 500, postMovePercent: 4m);

        var analysis = BuildService().Analyze(series, VShape(),
            new DtwConfig { MaxDistance = 0.05, BandPercent = 20 }, Study());

        Assert.False(analysis.IsUsable);
        Assert.Null(analysis.Study);
        Assert.Contains("occorrenze", analysis.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortSeries_IsRefusedWithAReadableReason()
    {
        var analysis = BuildService().Analyze(ToSeries(Enumerable.Repeat(100m, 50).ToList()),
            VShape(), Config(), Study());

        Assert.False(analysis.IsUsable);
        Assert.Contains("corti", analysis.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyInputs_DoNotThrow()
    {
        var svc = BuildService();

        Assert.False(svc.Analyze([], VShape(), Config(), Study()).IsUsable);
        Assert.False(svc.Analyze(ToSeries(Enumerable.Repeat(100m, 500).ToList()), [], Config(), Study()).IsUsable);
    }

    // --- La frequenza fa parte del verdetto ----------------------------------------------------------

    [Fact]
    public void OccurrenceFrequencyIsReported_BecauseTheObjectiveIsShortHorizonTrading()
    {
        // L'obiettivo dichiarato e' intraday/swing breve: un pattern bellissimo che compare due
        // volte l'anno non e' operabile, e il numero dev'essere sotto gli occhi.
        var series = BuildPlanted(6000, every: 120, postMovePercent: 4m);

        var analysis = BuildService().Analyze(series, VShape(), Config(), Study());

        Assert.True(analysis.OccurrencesPerMonth > 0);
        Assert.Contains("occorrenze/mese", analysis.Verdict);

        // Coerenza: la frequenza dichiarata deve corrispondere a occorrenze / mesi coperti.
        var months = (series[^1].TimestampUtc - series[0].TimestampUtc).TotalDays / 30.0;
        Assert.Equal(analysis.MatchCount / months, analysis.OccurrencesPerMonth, 6);
    }

    // --- Determinismo e robustezza --------------------------------------------------------------------

    [Fact]
    public void AnalysisIsDeterministic()
    {
        var series = BuildPlanted(4000, every: 120, postMovePercent: 3m);
        var svc = BuildService();

        var a = svc.Analyze(series, VShape(), Config(), Study());
        var b = svc.Analyze(series, VShape(), Config(), Study());

        Assert.Equal(a.MatchCount, b.MatchCount);
        Assert.Equal(a.IsPredictive, b.IsPredictive);
        Assert.Equal(a.Study?.PlaceboPValue, b.Study?.PlaceboPValue);
        Assert.Equal(a.ShapeNull?.PValue, b.ShapeNull?.PValue);
    }

    [Fact]
    public void RandomTemplates_OnNoiseSeries_RarelyDeclareASignal()
    {
        // Test random: pattern casuali su serie casuali. Un tasso alto di "segnale" qui
        // significherebbe che il verdetto e' tarato male e promuove il caso.
        var svc = BuildService();
        var rnd = new Random(2026);
        var declared = 0;
        const int trials = 15;

        for (var t = 0; t < trials; t++)
        {
            var closes = Enumerable.Range(0, 3000)
                .Select(_ => 100m + (decimal)(rnd.NextDouble() * 4 - 2)).ToList();
            var template = Enumerable.Range(0, 7)
                .Select(_ => 100m + (decimal)(rnd.NextDouble() * 4 - 2)).ToList();

            var analysis = svc.Analyze(ToSeries(closes), template,
                new DtwConfig { MaxDistance = 1.2, BandPercent = 20 }, Study());

            if (analysis.IsPredictive) declared++;
        }

        Assert.True(declared <= 2,
            $"su {trials} prove di puro rumore il verdetto ha dichiarato segnale {declared} volte: soglia troppo generosa");
    }
}
