using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione dell'incidente del 2026-08-09 (pod procionemgr-trading), stavolta con la catena
/// VERA: due corsie persistite a DB sullo stesso simbolo facevano fallire il refresh delle
/// sottoscrizioni di <see cref="RealtimePriceWorker"/> con «ArgumentException: An item with the
/// same key has already been added. Key: DOTUSDT» — il log prometteva «ritento» ma il set non
/// convergeva mai, e nessuna delle due corsie riceveva i tick.
///
/// Qui si monta il worker reale su un Postgres reale (Testcontainers) con due
/// <see cref="TradingEngineState"/> in esecuzione sulla stessa coppia, e si verifica la proprietà
/// che l'incidente aveva negato: il refresh converge senza errori ed ENTRAMBE le corsie ricevono
/// lo stesso tick.
/// </summary>
[Collection("Postgres")]
public sealed class RealtimeSharedSymbolLanesTests(PostgresFixture pg) : IAsyncDisposable
{
    private ServiceProvider? _provider;

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    /// <summary>Motore che registra i tick ricevuti: è l'osservabile con cui si prova il routing.</summary>
    private sealed class TickRecordingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public ConcurrentBag<decimal> Ticks { get; } = [];

        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default)
        {
            Ticks.Add(price);
            return Task.CompletedTask;
        }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Transport a copione: consegna i frame previsti e poi pende fino alla cancellazione.</summary>
    private sealed class ScriptedTransportFactory(params Queue<string?>[] scripts) : IWebSocketTransportFactory
    {
        private int _created;

        public ConcurrentBag<Uri> Connections { get; } = [];

        public IWebSocketTransport Create()
        {
            var index = Interlocked.Increment(ref _created) - 1;
            var script = index < scripts.Length ? scripts[index] : new Queue<string?>();
            return new Scripted(script, this);
        }

        private sealed class Scripted(Queue<string?> script, ScriptedTransportFactory owner) : IWebSocketTransport
        {
            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                owner.Connections.Add(uri);
                return Task.CompletedTask;
            }

            public Task SendAsync(string message, CancellationToken ct) => Task.CompletedTask;

            public async Task<string?> ReceiveAsync(CancellationToken ct)
            {
                if (script.Count == 0)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return script.Dequeue();
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Logger che conserva i messaggi di errore: l'incidente si manifestava ESATTAMENTE come un
    /// LogError ripetuto («aggiornamento delle sottoscrizioni fallito; ritento»), quindi la sua
    /// assenza è parte della proprietà da fissare — non basta che i tick arrivino.
    /// </summary>
    private sealed class ErrorRecordingLogger : ILogger<RealtimePriceWorker>
    {
        public ConcurrentBag<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timeout in attesa di: {what}");
    }

    // --- Il test -----------------------------------------------------------------------------

    [Fact]
    public async Task TwoRunningLanesOnTheSameSymbol_BothReceiveTheTick_AndTheRefreshConverges()
    {
        var connString = pg.CreateDatabase();

        var engine1 = new TickRecordingEngine(1);
        var engine2 = new TickRecordingEngine(2);

        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(connString));
        services.AddKeyedSingleton<ITradingEngine>(1, engine1);
        services.AddKeyedSingleton<ITradingEngine>(2, engine2);
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();

            // Le due corsie dell'incidente: stessa coppia, timeframe diversi, entrambe in
            // esecuzione. È il caso che il vecchio ToDictionary non sapeva rappresentare.
            db.TradingEngineStates.AddRange(
                new TradingEngineState
                {
                    LaneId = 1, Mode = TradingMode.Paper, MarketType = MarketType.Spot,
                    IsRunning = true, ExchangeName = "Binance", Symbol = "DOT/USDT", Timeframe = "15m",
                    UpdatedAtUtc = DateTime.UtcNow,
                },
                new TradingEngineState
                {
                    LaneId = 2, Mode = TradingMode.Paper, MarketType = MarketType.Spot,
                    IsRunning = true, ExchangeName = "Binance", Symbol = "DOT/USDT", Timeframe = "1h",
                    UpdatedAtUtc = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        const string dotTicker = """
            {"stream":"dotusdt@bookTicker","data":{"s":"DOTUSDT","b":"4.00","B":"1","a":"4.02","A":"1"}}
            """;
        var transport = new ScriptedTransportFactory(new Queue<string?>([dotTicker]));
        var logger = new ErrorRecordingLogger();

        var worker = new RealtimePriceWorker(
            _provider,
            dbFactory,
            [new BinanceStreamMapper()],
            transport,
            new RealtimeFeedOptions { Enabled = true, SubscriptionRefreshSeconds = 1 }.AsMonitor(),
            logger,
            switchPollInterval: TimeSpan.FromMilliseconds(20));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // ENTRAMBE le corsie devono ricevere lo stesso tick: prima del fix nessuna delle due
            // lo riceveva, perché il refresh moriva sull'ArgumentException e l'indice del parsing
            // restava vuoto.
            await WaitForAsync(() => !engine1.Ticks.IsEmpty && !engine2.Ticks.IsEmpty,
                "il tick DOT/USDT consegnato a tutte e due le corsie");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(4.01m, Assert.Single(engine1.Ticks)); // mid di 4.00/4.02
        Assert.Equal(4.01m, Assert.Single(engine2.Ticks));

        // Il set è convergito in UNA connessione che porta il bookTicker e TUTTI i timeframe.
        var uri = Assert.Single(transport.Connections).AbsoluteUri;
        Assert.Contains("dotusdt@bookTicker", uri, StringComparison.Ordinal);
        Assert.Contains("dotusdt@kline_15m", uri, StringComparison.Ordinal);
        Assert.Contains("dotusdt@kline_1h", uri, StringComparison.Ordinal);

        // E il sintomo dell'incidente — l'errore di refresh ripetuto — non deve esistere più.
        Assert.DoesNotContain(logger.Errors, e => e.Contains("sottoscrizioni fallito", StringComparison.Ordinal));
    }
}
