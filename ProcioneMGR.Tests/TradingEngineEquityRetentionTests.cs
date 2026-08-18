using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [M1] Curva equity in-memory bounded (<see cref="TradingEngine.TrimEquity"/>) e max-drawdown di
/// sessione PERSISTITO: prima il MaxDrawdown viveva solo nella curva in-memory, quindi un riavvio
/// lo azzerava — e il gate assoluto HardMaxDrawdownPercent del PromotionEvaluator poteva
/// promuovere una corsia che aveva già bucato il limite prima del riavvio.
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineEquityRetentionTests : IAsyncDisposable
{
    private readonly string _connString;
    private readonly List<ServiceProvider> _providers = new();

    public TradingEngineEquityRetentionTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- TrimEquity puro -----------------------------------------------------------------------

    private static List<EquityPoint> Curve(int n)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n).Select(i => new EquityPoint { Timestamp = t0.AddMinutes(i), Capital = 100m + i }).ToList();
    }

    [Fact]
    public void TrimEquity_UnderLimit_Untouched()
    {
        var curve = Curve(10_000);
        TradingEngine.TrimEquity(curve);
        Assert.Equal(10_000, curve.Count);
        Assert.Equal(100m, curve[0].Capital);   // nessun punto rimosso
    }

    [Fact]
    public void TrimEquity_OverLimit_DropsOldestBlock()
    {
        var curve = Curve(10_001);
        TradingEngine.TrimEquity(curve);
        Assert.Equal(8_001, curve.Count);            // 10'001 − blocco 2'000
        Assert.Equal(100m + 2_000, curve[0].Capital); // rimossi i PIÙ VECCHI
        Assert.Equal(100m + 10_000, curve[^1].Capital);
    }

    [Fact]
    public void TrimEquity_RepeatedGrowth_StaysBounded()
    {
        var curve = Curve(9_999);
        var t0 = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5_000; i++)
        {
            curve.Add(new EquityPoint { Timestamp = t0.AddMinutes(i), Capital = 50m });
            TradingEngine.TrimEquity(curve);   // come nel loop candele: un check per Add
        }
        Assert.InRange(curve.Count, 8_001, 10_000);   // mai oltre il tetto, mai svuotata
    }

    // --- MaxDrawdown persistente ----------------------------------------------------------------

    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct) => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => script(index);
    }

    private sealed class ScriptedStrategyFactory(Func<int, Signal> script) : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new ScriptedStrategy(script);
    }

    private sealed class FakeEnsembleManager(EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingExchangeClientFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Ogni chiamata crea un'ISTANZA NUOVA di engine sullo stesso DB (simula il riavvio del processo).</summary>
    private async Task<TradingEngine> NewEngineInstanceAsync(Func<int, Signal> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };
        // Size 80% (spot) per rendere il drawdown sensibile al prezzo; limiti alzati di conseguenza,
        // MaxDrawdownPercent 20 default: il dip da 12% NON deve fermare la corsia.
        var safety = new SafetyConfiguration
        {
            MinOrderIntervalSeconds = 0,
            PositionSizePercent = 80m,
            MaxPositionSizePercent = 90m,
            MaxTotalExposurePercent = 100m,
        };

        return new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(safety),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    [Fact]
    public async Task MaxDrawdown_SurvivesRestart_EvenAfterFullRecovery()
    {
        // Sessione 1: apertura long 80% @100, dip a 85 (equity −12%), pieno recupero a 100.
        var engine = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));   // apre @100
        await engine.ProcessCandleAsync(Candle(5, 85m));    // dip: equity ≈ 19'920 + 800×85 = 87'920
        await engine.ProcessCandleAsync(Candle(6, 100m));   // recupero completo

        var status1 = await engine.GetStatusAsync();
        Assert.False(status1.IsEmergencyStopped);           // 12% < MaxDrawdownPercent (20%)

        // "Riavvio": nuova istanza sullo stesso DB, curva equity in-memory VUOTA.
        var restarted = await NewEngineInstanceAsync(_ => Signal.Hold);
        await restarted.GetStatusAsync();                   // forza EnsureLoaded (stato dal DB)
        var perf = await restarted.GetPerformanceAsync();

        // Prima del fix: MaxDrawdown ricalcolato dalla curva vuota = 0 → il gate assoluto
        // HardMaxDrawdownPercent del PromotionEvaluator non vedeva più il dip da 12%.
        Assert.True(perf.MaxDrawdown >= 12m,
            $"il MaxDrawdown di sessione ({perf.MaxDrawdown:F2}%) deve sopravvivere al riavvio (atteso ≥ 12%)");
    }

    [Fact]
    public async Task MaxDrawdown_ResetOnNewSession()
    {
        var engine = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        await engine.ProcessCandleAsync(Candle(5, 85m));    // dip 12%

        await engine.StartAsync(TradingMode.Paper);         // NUOVA sessione: azzera il tracker
        var perf = await engine.GetPerformanceAsync();

        Assert.Equal(0m, perf.MaxDrawdown);
    }

    // --- Segnalibro di sessione: niente replay dopo un riavvio ----------------------------------

    /// <summary>
    /// [2026-08-17] La guardia anti-replay confrontava la candela SOLO col buffer in memoria, che
    /// dopo un riavvio del processo è vuoto — mentre lo stato (capitale, PnL, posizioni aperte) è
    /// stato restaurato da <c>EnsureLoadedAsync</c>. Il feed, che a sua volta perdeva il cursore,
    /// ripartiva da trenta giorni prima e le candele vecchie passavano tutte: stop valutati su
    /// prezzi di settimane fa contro posizioni vive, TradeRecord con durata NEGATIVA, e decine di
    /// operazioni duplicate proprio nelle metriche su cui si decide la promozione a Testnet.
    /// </summary>
    [Fact]
    public async Task AfterRestart_OldCandlesAreRejected_NoReplayOnRestoredState()
    {
        var engine = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));   // apre @100
        await engine.ProcessCandleAsync(Candle(5, 100m));

        var prima = await engine.GetStatusAsync();
        Assert.Equal(Candle(5, 100m).TimestampUtc, prima.LastCandleUtc);   // il segnalibro è persistito

        // "Riavvio": istanza nuova sullo stesso DB. Buffer vuoto, ma lo stato è stato restaurato.
        var restarted = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        var dopoRiavvio = await restarted.GetStatusAsync();
        Assert.Equal(prima.LastCandleUtc, dopoRiavvio.LastCandleUtc);
        Assert.Null(dopoRiavvio.LastProcessedCandleUtc);   // il BATTITO invece riparte da zero: è la verità

        // Si rigioca una candela GIÀ VALUTATA, con un minimo che avrebbe bucato qualunque stop.
        await restarted.ProcessCandleAsync(new OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = Candle(2, 100m).TimestampUtc,
            Open = 100m, High = 100m, Low = 1m, Close = 1m, Volume = 100m,
        });

        var dopo = await restarted.GetStatusAsync();
        Assert.Equal(prima.LastCandleUtc, dopo.LastCandleUtc);        // il segnalibro non torna indietro
        Assert.Single(await restarted.GetOpenPositionsAsync());        // la posizione NON è stata chiusa
        Assert.Equal(prima.AvailableCapital, dopo.AvailableCapital);   // e nessun trade fantasma
    }

    /// <summary>Una sessione DAVVERO nuova azzera il segnalibro: il replay osservabile resta intatto.</summary>
    [Fact]
    public async Task NewSession_ResetsTheBookmark()
    {
        var engine = await NewEngineInstanceAsync(_ => Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 2; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.NotNull((await engine.GetStatusAsync()).LastCandleUtc);

        await engine.StartAsync(TradingMode.Paper);

        Assert.Null((await engine.GetStatusAsync()).LastCandleUtc);
    }

    /// <summary>
    /// [2026-08-17] La migrazione: una corsia che girava già PRIMA che il segnalibro esistesse ha
    /// <c>LastCandleUtc</c> nullo pur non avendo nulla da rigiocare. Senza questa regola il primo
    /// avvio del motore corretto avrebbe fatto esattamente il danno che la correzione previene —
    /// trenta giorni di candele rigiocate su sette corsie vive. Decide l'ETÀ della sessione.
    /// </summary>
    [Theory]
    [InlineData(0, true)]      // sessione appena avviata: il replay osservabile ci sta
    [InlineData(60, false)]    // sessione di un'ora fa senza segnalibro: è una RIPRESA
    [InlineData(43_200, false)]
    public void CursorOrigin_WithoutBookmark_DependsOnSessionAge(int minutiFa, bool attesoReplay)
    {
        var status = new TradingEngineStatus
        {
            Mode = TradingMode.Paper,
            IsRunning = true,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-minutiFa),
            LastCandleUtc = null,
        };

        var replay = TradingWorker.WouldReplayHistory(status, DateTime.UtcNow);

        Assert.Equal(attesoReplay, replay);
    }

    [Fact]
    public void CursorOrigin_WithBookmark_NeverReplays()
    {
        var status = new TradingEngineStatus
        {
            Mode = TradingMode.Paper,
            IsRunning = true,
            StartedAtUtc = DateTime.UtcNow,               // appena avviata...
            LastCandleUtc = DateTime.UtcNow.AddHours(-4), // ...ma con un segnalibro: riprende da lì
        };

        Assert.False(TradingWorker.WouldReplayHistory(status, DateTime.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers) await p.DisposeAsync();
    }
}
