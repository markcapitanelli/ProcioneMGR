using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// L'anti-spam n.6 del <see cref="SafetyChecker"/> (<c>MinOrderIntervalSeconds</c>) deve frenare gli
/// INGRESSI ravvicinati e nient'altro. <c>PositionCloser</c> segnava <c>LastOrderUtc</c> anche in
/// chiusura, e siccome un'inversione di segnale chiude e riapre sullo STESSO timestamp di candela
/// (vedi <c>TradingEngine</c>, casi <c>Signal.Long</c>/<c>Signal.Short</c>), l'apertura opposta
/// arrivava al controllo con <c>elapsed = 0</c> e veniva rifiutata. Riguardava tutte e 12 le
/// strategie del catalogo, ognuna delle quali può emettere un segnale opposto a quello in corso
/// (osservato dal vivo: 430 ordini rifiutati su 500 — docs/REPORT-RICERCA-2026-07.md).
///
/// I due test sono una coppia e vanno letti insieme: il primo verifica che l'inversione passi, il
/// secondo che il freno sugli ingressi ravvicinati sia ancora lì. Da soli sarebbero entrambi
/// soddisfatti da una regressione (rispettivamente: togliere l'anti-spam, o rimetterlo in chiusura).
/// </summary>
[Collection("Postgres")]
public class SignalReversalThrottleTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SignalReversalThrottleTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
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

    /// <summary>In Paper il motore non tocca l'exchange: se lo facesse, questo esplode invece di mentire.</summary>
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
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(
        Func<int, Signal> script, params EnsembleStrategy[] strategies)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "1h",
            TotalCapital = 10_000m,
            Strategies = [.. strategies],
        };

        // SafetyConfiguration di DEFAULT, di proposito: MinOrderIntervalSeconds vale 10s e le candele
        // qui distano un'ora, quindi l'unico modo di finire sotto la soglia è che due ordini
        // condividano il timestamp della stessa candela — esattamente il caso in esame.
        var engine = new TradingEngine(
            0,
            dbFactory,
            new ScriptedStrategyFactory(script),
            new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(),
            new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory(),
            NullLogger<TradingEngine>.Instance);

        return (engine, dbFactory);
    }

    private static EnsembleStrategy Strategy(string id) => new()
    {
        StrategyId = id,
        StrategyName = "Scripted",
        DisplayName = "Scripted",
        IsActive = true,
    };

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 100m,
    };

    private static async Task<List<Order>> RejectedOrdersAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Rejected).ToListAsync();
    }

    [Fact]
    public async Task SignalReversal_OpensOppositePosition_OnTheSameCandle()
    {
        // Long alla candela 4, Short alla 5: la 5 chiude il long e deve aprire lo short subito,
        // senza pagare il throttle per una chiusura avvenuta nello stesso istante.
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : i >= 5 ? Signal.Short : Signal.Hold,
            Strategy("s1"));
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        await engine.ProcessCandleAsync(Candle(4, 100m));   // apertura Long

        var opened = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(OrderSide.Buy, opened.Side);

        await engine.ProcessCandleAsync(Candle(5, 100m));   // inversione: chiude Long, apre Short

        var reversed = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(OrderSide.Sell, reversed.Side);
        Assert.Empty(await RejectedOrdersAsync(dbFactory));

        await using var db = await dbFactory.CreateDbContextAsync();
        var closed = Assert.Single(await db.TradeRecords.AsNoTracking().ToListAsync());
        Assert.Equal("Signal", closed.ExitReason);
    }

    [Fact]
    public async Task TwoEntriesOnTheSameCandle_SecondIsStillThrottled()
    {
        // Il gemello del test sopra: due strategie che aprono entrambe sulla candela 4 producono due
        // INGRESSI a distanza zero, e il secondo deve continuare a essere rifiutato. Se un giorno
        // questo test diventa verde-per-caso perché l'anti-spam è sparito, il primo test da solo non
        // se ne accorgerebbe.
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : Signal.Hold,
            Strategy("s1"), Strategy("s2"));
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        await engine.ProcessCandleAsync(Candle(4, 100m));

        Assert.Single(await engine.GetOpenPositionsAsync());
        var rejected = Assert.Single(await RejectedOrdersAsync(dbFactory));
        Assert.Contains("troppo ravvicinati", rejected.ErrorMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
