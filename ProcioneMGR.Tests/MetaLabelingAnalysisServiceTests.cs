using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML.Labeling;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C4, consumo] Test di INTEGRAZIONE della catena completa: strategia reale → segnali barra per
/// barra → etichette triple-barrier → meta-modello out-of-fold → verdetto. È il livello che
/// mancava: le classi erano coperte una per una, ma nessun test verificava che messe insieme
/// facessero qualcosa di sensato — ed è esattamente ciò che la pagina Backtest invoca.
///
/// I componenti sono REALI (nessun mock): `EmaCross` dal catalogo vero, i fattori alpha veri, il
/// servizio indicatori vero. L'unica cosa sintetica sono le candele, perché serve conoscere la
/// risposta giusta.
/// </summary>
public class MetaLabelingAnalysisServiceTests
{
    private static MetaLabelingAnalysisService BuildService() => new(
        new TripleBarrierLabeler(),
        new MetaLabeler(),
        new MetaModelTrainer(),
        new AlphaFactorFactory(),
        new TechnicalIndicatorsService());

    /// <summary>Strategia di prova che emette Long a cadenza fissa: segnali abbondanti e prevedibili.</summary>
    private sealed class PeriodicLong(int every) : IStrategy
    {
        public string Name => "PeriodicLong";
        public string DisplayName => "Long periodico (test)";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } = [];

        public Task InitializeAsync(
            IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators,
            CancellationToken ct) => Task.CompletedTask;

        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
            => index % every == 0 ? Signal.Long : Signal.Hold;
    }

    /// <summary>Serie con dinamica realistica: senza movimento le barriere non verrebbero mai toccate.</summary>
    private static List<OhlcvData> BuildSeries(int n, int seed = 4)
    {
        var rnd = new Random(seed);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < n; i++)
        {
            var drift = (decimal)((rnd.NextDouble() - 0.5) * 0.03);
            var next = Math.Max(1m, price * (1m + drift));
            var high = Math.Max(price, next) * (1m + (decimal)(rnd.NextDouble() * 0.006));
            var low = Math.Min(price, next) * (1m - (decimal)(rnd.NextDouble() * 0.006));
            candles.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = start.AddHours(i),
                Open = price, High = high, Low = low, Close = next,
                Volume = 1000m + (decimal)(rnd.NextDouble() * 500),
            });
            price = next;
        }
        return candles;
    }

    // --- Il giro completo -------------------------------------------------------------------------

    [Fact]
    public async Task FullChain_ProducesACoherentReportOnRealComponents()
    {
        var candles = BuildSeries(3000);

        var analysis = await BuildService().RunAsync(
            new PeriodicLong(every: 3), new Dictionary<string, decimal>(), candles,
            verticalBarrierBars: 10, threshold: 0.5);

        Assert.True(analysis.IsUsable, analysis.Verdict);
        Assert.True(analysis.PrimarySignalCount > 500, $"segnali primari: {analysis.PrimarySignalCount}");
        Assert.True(analysis.SamplesScored > 100, $"campioni valutati out-of-fold: {analysis.SamplesScored}");
        Assert.Equal(5, analysis.FeatureCount);           // i cinque fattori alpha di default
        Assert.Equal(10, analysis.VerticalBarrierBars);

        // Le barriere devono venire dai dati, non da costanti.
        Assert.True(analysis.ProfitTakePercent > 0m);
        Assert.True(analysis.StopLossPercent > 0m);

        // Coerenza interna del report: il filtro e' un sottoinsieme del primario.
        var r = analysis.Report;
        Assert.True(r.FilteredCount <= r.PrimaryCount);
        Assert.True(r.FilteredWins <= r.PrimaryWins);
        Assert.InRange(r.SurvivalRate, 0.0, 1.0);
        Assert.False(string.IsNullOrWhiteSpace(analysis.Verdict));
    }

    [Fact]
    public async Task OnASeriesWithoutEdge_TheVerdictIsNotAnImprovement()
    {
        // Serie casuale + segnali a cadenza fissa: non c'e' nulla da imparare. Se il verdetto qui
        // dicesse "migliora", la catena starebbe fabbricando un edge dal rumore.
        var candles = BuildSeries(3000, seed: 77);

        var analysis = await BuildService().RunAsync(
            new PeriodicLong(every: 3), new Dictionary<string, decimal>(), candles,
            verticalBarrierBars: 10, threshold: 0.5);

        Assert.True(analysis.IsUsable, analysis.Verdict);
        Assert.False(analysis.Report.IsImprovement,
            $"verdetto inatteso su serie senza edge: {analysis.Verdict}");
    }

    // --- I percorsi di fallimento, che la UI mostra all'operatore ----------------------------------

    [Fact]
    public async Task TooFewCandles_AreRefusedWithAReadableReason()
    {
        var analysis = await BuildService().RunAsync(
            new PeriodicLong(every: 3), new Dictionary<string, decimal>(), BuildSeries(40),
            verticalBarrierBars: 10, threshold: 0.5);

        Assert.False(analysis.IsUsable);
        Assert.Contains("candele", analysis.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStrategyThatBarelyTrades_IsRefusedInsteadOfMeasuredOnNothing()
    {
        // Un segnale ogni 500 barre: troppo pochi perche' un meta-modello impari qualcosa.
        var analysis = await BuildService().RunAsync(
            new PeriodicLong(every: 500), new Dictionary<string, decimal>(), BuildSeries(3000),
            verticalBarrierBars: 10, threshold: 0.5);

        Assert.False(analysis.IsUsable);
        Assert.Contains("ingressi", analysis.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptySeries_DoesNotThrow()
    {
        var analysis = await BuildService().RunAsync(
            new PeriodicLong(every: 3), new Dictionary<string, decimal>(), [],
            verticalBarrierBars: 10, threshold: 0.5);

        Assert.False(analysis.IsUsable);
    }

    // --- Una strategia del catalogo VERO ----------------------------------------------------------

    [Fact]
    public async Task WorksWithARealCatalogStrategy()
    {
        // EmaCross e' una strategia reale della piattaforma: verifica che l'estrazione dei segnali
        // funzioni anche con chi usa davvero InitializeAsync e gli indicatori.
        var factory = new StrategyFactory();
        var strategy = factory.Create("EmaCross");
        var parameters = strategy.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);

        var analysis = await BuildService().RunAsync(
            strategy, parameters, BuildSeries(4000), verticalBarrierBars: 10, threshold: 0.5);

        // EmaCross emette pochi ingressi: l'esito atteso e' il RIFIUTO motivato, non un crash —
        // ed e' proprio il caso che l'operatore incontra piu' spesso.
        Assert.False(string.IsNullOrWhiteSpace(analysis.Verdict));
        Assert.Equal("EMA Cross", analysis.StrategyName);
    }

    [Fact]
    public async Task AnalysisIsDeterministic()
    {
        var candles = BuildSeries(3000);
        var service = BuildService();

        var a = await service.RunAsync(new PeriodicLong(3), new Dictionary<string, decimal>(), candles, 10, 0.5);
        var b = await service.RunAsync(new PeriodicLong(3), new Dictionary<string, decimal>(), candles, 10, 0.5);

        Assert.Equal(a.Report.FilteredCount, b.Report.FilteredCount);
        Assert.Equal(a.Report.FilteredWins, b.Report.FilteredWins);
        Assert.Equal(a.Report.SelectionZScore, b.Report.SelectionZScore, 10);
    }
}
