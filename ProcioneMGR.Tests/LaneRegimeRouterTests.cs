using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 4 — docs/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Il router di regime: classifica il regime
/// corrente col <see cref="IRegimeDetector"/> K-means vero e lascia operare solo le strategie che
/// vi hanno senso. Fino a qui il routing per regime esisteva soltanto dentro il backtest, e per di
/// più con un surrogato (pendenza di una SMA), mentre il motore live il regime non lo consultava
/// affatto.
///
/// Ciò che questi test difendono non è la classificazione — quella è del detector, già testato —
/// ma le tre proprietà che rendono il filtro sicuro da accendere su una corsia vera:
/// <b>fallisce verso il permesso</b>, <b>distingue "non so" da "qui non si opera"</b>, e
/// <b>non tocca le posizioni già aperte</b>.
/// </summary>
public sealed class LaneRegimeRouterTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<OhlcvData> Candles(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = Start.AddHours(i),
            Open = 100m + i, High = 101m + i, Low = 99m + i, Close = 100m + i, Volume = 10m,
        })];

    /// <summary>Detector scriptato: modello dichiarato e regime assegnato a piacere del test.</summary>
    private sealed class FakeRegimeDetector(RegimeModel? model, int? regimeId) : IRegimeDetector
    {
        public int LabelCalls { get; private set; }

        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ActivateModelAsync(RegimeModel m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => Task.FromResult(model);

        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default)
        {
            LabelCalls++;
            foreach (var f in features) f.RegimeId = regimeId;
            return Task.FromResult(features);
        }
    }

    /// <summary>Estrattore che restituisce una feature per candela (il contenuto non conta qui).</summary>
    private sealed class FakeFeatureExtractor(bool produceFeatures = true) : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe,
            DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();

        public List<MarketFeatures> ComputeFeatures(IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
            => produceFeatures
                ? [.. candles.Select(c => new MarketFeatures { Timestamp = c.TimestampUtc, Price = c.Close })]
                : [];
    }

    /// <summary>Estrattore che esplode: simula un guasto del percorso di classificazione.</summary>
    private sealed class ThrowingFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe,
            DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();

        public List<MarketFeatures> ComputeFeatures(IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
            => throw new InvalidOperationException("guasto simulato nel calcolo delle feature");
    }

    private static RegimeModel Model(string symbol = "BTC/USDT", string timeframe = "1h") =>
        new() { Symbol = symbol, Timeframe = timeframe, ExchangeName = "Binance", NumberOfRegimes = 3 };

    private static LaneRegimeRouter Router(
        RegimeRoutingOptions options, IRegimeDetector detector, IMarketFeatureExtractor? extractor = null) =>
        new(extractor ?? new FakeFeatureExtractor(), detector, options.AsMonitor(), NullLogger<LaneRegimeRouter>.Instance);

    private static RegimeRoutingOptions Enabled(params RegimeRoutingRule[] rules) => new()
    {
        Enabled = true, MinCandles = 10, Rules = [.. rules],
    };

    // --- Il comportamento centrale ------------------------------------------------------------

    [Fact]
    public async Task AllowsOnlyTheStrategiesMappedToTheCurrentRegime()
    {
        var options = Enabled(
            new RegimeRoutingRule { RegimeId = 0, Strategies = ["Supertrend", "EmaCross"] },
            new RegimeRoutingRule { RegimeId = 1, Strategies = ["BollingerMeanReversion"] });

        var decision = await Router(options, new FakeRegimeDetector(Model(), regimeId: 1))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.True(decision.IsKnown);
        Assert.Equal(1, decision.RegimeId);
        Assert.True(decision.Allows("BollingerMeanReversion"));
        Assert.False(decision.Allows("Supertrend"));
        Assert.False(decision.Allows("EmaCross"));
    }

    [Fact]
    public async Task EmptyRuleMeansStandAside_NotEveryoneAllowed()
    {
        // La distinzione che conta: una regola con lista VUOTA è una decisione ("in questo regime
        // la corsia sta ferma"), non un'assenza di configurazione. Saper riconoscere il regime in
        // cui non si ha edge è metà del valore dell'idea del PDF.
        var options = Enabled(new RegimeRoutingRule { RegimeId = 2, Strategies = [] });

        var decision = await Router(options, new FakeRegimeDetector(Model(), regimeId: 2))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.True(decision.IsKnown);
        Assert.True(decision.HasRule);
        Assert.False(decision.Allows("Supertrend"));
        Assert.False(decision.Allows("QualsiasiCosa"));
    }

    [Fact]
    public async Task UnmappedRegime_IsPermissiveByDefault()
    {
        // Un regime senza regola — o un modello riaddestrato con più cluster di quanti ne conosca
        // la configurazione — non deve zittire la corsia di soppiatto.
        var options = Enabled(new RegimeRoutingRule { RegimeId = 0, Strategies = ["Supertrend"] });

        var decision = await Router(options, new FakeRegimeDetector(Model(), regimeId: 7))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.True(decision.IsKnown);
        Assert.False(decision.HasRule);
        Assert.True(decision.Allows("QualsiasiStrategia"));
    }

    [Fact]
    public async Task UnmappedRegime_CanBeMadeRestrictiveExplicitly()
    {
        var options = Enabled(new RegimeRoutingRule { RegimeId = 0, Strategies = ["Supertrend"] });
        options.AllowUnmappedRegimes = false;

        var decision = await Router(options, new FakeRegimeDetector(Model(), regimeId: 7))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.True(decision.IsKnown);
        Assert.False(decision.Allows("QualsiasiStrategia"));
    }

    // --- Fail-safe: ogni assenza di informazione lascia passare --------------------------------

    [Fact]
    public async Task Disabled_IsInert()
    {
        var detector = new FakeRegimeDetector(Model(), regimeId: 1);

        var decision = await Router(new RegimeRoutingOptions(), detector).DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("QualsiasiStrategia"));
        Assert.Equal(0, detector.LabelCalls);   // spento = nessun lavoro speso
    }

    [Fact]
    public async Task NoActiveModel_AllowsEverything()
    {
        var decision = await Router(Enabled(), new FakeRegimeDetector(model: null, regimeId: null))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
    }

    [Fact]
    public async Task ModelOfAnotherSeries_IsRefused_NotUsedAnyway()
    {
        // Etichettare BTC 1h col modello di ETH 4h darebbe un numero perfettamente formato e
        // completamente privo di senso: il router deve accorgersene PRIMA di spendere il calcolo.
        var detector = new FakeRegimeDetector(Model("ETH/USDT", "4h"), regimeId: 0);
        var options = Enabled(new RegimeRoutingRule { RegimeId = 0, Strategies = [] });

        var decision = await Router(options, detector).DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
        Assert.Equal(0, detector.LabelCalls);
        Assert.Contains("ETH/USDT", decision.Reason);
    }

    [Fact]
    public async Task NotEnoughCandles_AllowsEverything()
    {
        var decision = await Router(Enabled(), new FakeRegimeDetector(Model(), regimeId: 0))
            .DecideAsync("BTC/USDT", "1h", Candles(5));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
    }

    [Fact]
    public async Task NoFeatures_AllowsEverything()
    {
        var decision = await Router(Enabled(), new FakeRegimeDetector(Model(), regimeId: 0), new FakeFeatureExtractor(produceFeatures: false))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
    }

    [Fact]
    public async Task UnlabelledBars_AllowEverything()
    {
        var decision = await Router(Enabled(), new FakeRegimeDetector(Model(), regimeId: null))
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
    }

    [Fact]
    public async Task AFailureInTheRouter_NeverBecomesAFailureOfTheLane()
    {
        // Il caso peggiore: il percorso di classificazione esplode. La corsia deve proseguire senza
        // filtro, non fermarsi. Un filtro di rischio che fallisse verso il blocco trasformerebbe
        // un'assenza di informazione in una decisione di trading.
        var decision = await Router(Enabled(), new FakeRegimeDetector(Model(), regimeId: 0), new ThrowingFeatureExtractor())
            .DecideAsync("BTC/USDT", "1h", Candles(60));

        Assert.False(decision.IsKnown);
        Assert.True(decision.Allows("Supertrend"));
    }

    [Fact]
    public async Task Cancellation_IsPropagated_NotSwallowedAsAFailure()
    {
        // La cancellazione è un ordine, non un guasto: inghiottirla farebbe sembrare "regime non
        // noto" uno spegnimento in corso.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var router = Router(Enabled(), new FakeRegimeDetector(Model(), regimeId: 0), new CancellingFeatureExtractor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => router.DecideAsync("BTC/USDT", "1h", Candles(60), cts.Token));
    }

    private sealed class CancellingFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe,
            DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();

        public List<MarketFeatures> ComputeFeatures(IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return [];
        }
    }
}
