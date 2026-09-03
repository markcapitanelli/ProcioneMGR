using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Revisione 2026-09-03/04] <b>Anche il ritiro scrive l'intento PRIMA di fermare.</b>
///
/// <para>K51 lo aveva fatto solo per le assegnazioni: nel ritiro lo stop precedeva il journal, e un
/// INSERT fallito lasciava la corsia ferma senza riga — i «quattro arresti su quattro senza riga»
/// del 2026-08-31. Fail-closed come l'assegnazione: se l'intento non si scrive, non si ferma.</para>
/// </summary>
[Collection("Postgres")]
public class RitiroConIntentoTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RitiroConIntentoTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedReader : IFleetStateReader
    {
        public Task<FleetState> ReadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Un motore che sa solo dire il proprio stato e contare gli stop.</summary>
    private sealed class MotoreFinto(int laneId, TradingMode mode, bool running) : ITradingEngine
    {
        public int LaneId => laneId;
        public int StopCalls { get; private set; }
        public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { Mode = mode, IsRunning = running, Symbol = "DOGE/USDT", Timeframe = "15m" });

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotImplementedException();
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
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private async Task<(FleetOrchestratorWorker Worker, IDbContextFactory<ApplicationDbContext> Db, MotoreFinto Motore)> BuildAsync(
        TradingMode mode = TradingMode.Paper, bool running = true, string? connString = null)
    {
        var motore = new MotoreFinto(4, mode, running);
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(connString ?? _connString));
        services.AddKeyedSingleton<ITradingEngine>(4, motore);
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (connString is null)
        {
            await using var c = await db.CreateDbContextAsync();
            await c.Database.EnsureCreatedAsync();
        }

        var worker = new FleetOrchestratorWorker(
            new UnusedReader(), db,
            new FleetOptions { TickMinutes = 15 }.AsMonitor(),
            new CarryOptions().AsMonitor(),
            provider, NullLogger<FleetOrchestratorWorker>.Instance);
        return (worker, db, motore);
    }

    [Fact]
    public async Task UNritiroESEGUITO_lasciaUNAriga_apertaCOMEintentoEchiusaAPPLICATA()
    {
        var (worker, db, motore) = await BuildAsync();

        await worker.RitiraPerTestAsync(new StopAndFreeLane(4, "forward test perdente"));

        Assert.Equal(1, motore.StopCalls);
        await using var check = await db.CreateDbContextAsync();
        var riga = Assert.Single(await check.OrchestratorDecisions.AsNoTracking().ToListAsync());
        Assert.Equal("Retire", riga.Kind);
        Assert.Equal(4, riga.LaneId);
        Assert.Equal(DecisionOutcome.Applied, riga.Outcome);
        Assert.True(riga.Applied);
        Assert.Null(riga.Error);
    }

    /// <summary>La corsia che non è più Paper non si tocca, e la riga lo dice come RIFIUTO, non come guasto.</summary>
    [Fact]
    public async Task UNAcorsiaNONpaper_nonSiFERMA_eLaRIGAeRIFIUTATA()
    {
        var (worker, db, motore) = await BuildAsync(mode: TradingMode.Testnet);

        await worker.RitiraPerTestAsync(new StopAndFreeLane(4, "forward test perdente"));

        Assert.Equal(0, motore.StopCalls);
        await using var check = await db.CreateDbContextAsync();
        var riga = Assert.Single(await check.OrchestratorDecisions.AsNoTracking().ToListAsync());
        Assert.Equal(DecisionOutcome.Refused, riga.Outcome);
        Assert.False(riga.Applied);
        Assert.Contains("non Paper", riga.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Fail-closed.</b> Se l'intento non si scrive (database irraggiungibile), la corsia NON si
    /// ferma: fermarla senza riga è esattamente il caso del 2026-08-31.
    /// </summary>
    [Fact]
    public async Task SEilJOURNALnonSCRIVE_laCORSIAnonSiFERMA()
    {
        var (worker, _, motore) = await BuildAsync(
            connString: "Host=127.0.0.1;Port=1;Database=mai-aperto;Username=x;Password=y;Timeout=1");

        await worker.RitiraPerTestAsync(new StopAndFreeLane(4, "forward test perdente"));

        Assert.Equal(0, motore.StopCalls);
    }
}
