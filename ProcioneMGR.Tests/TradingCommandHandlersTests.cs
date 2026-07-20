using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Commands;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dei 7 comandi (Fase 1, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.6 passo 3): ciascuno deve
/// risolvere il motore della corsia GIUSTA (LaneId sulla richiesta) e passargli gli argomenti
/// esatti, senza alterarli. `CloseAllPositionsCommand` non esiste: il suo unico chiamante reale è
/// `LanePromoter`, esplicitamente escluso da Mediator (PRD §4.2) — nessun caller Blazor da
/// migrare, quindi nessun comando da creare (sarebbe superficie morta).
/// </summary>
public class TradingCommandHandlersTests
{
    private sealed class RecordingTradingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public TradingMode? StartedWith { get; private set; }
        public bool StopCalled { get; private set; }
        public string? LastEmergencyReason { get; private set; }
        public string? LastClosedPositionId { get; private set; }
        public (string PositionId, decimal? Sl, decimal? Tp, decimal? Tsl)? LastSlTp { get; private set; }
        public (string OrderId, string? UserId)? LastConfirmed { get; private set; }
        public (string OrderId, string? UserId)? LastRejected { get; private set; }

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) { StartedWith = mode; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { StopCalled = true; return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) { LastEmergencyReason = reason; return Task.CompletedTask; }
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) { LastClosedPositionId = positionId; return Task.CompletedTask; }
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
        {
            LastSlTp = (positionId, stopLoss, takeProfit, trailingStopPercent);
            return Task.CompletedTask;
        }
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) { LastConfirmed = (orderId, userId); return Task.CompletedTask; }
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) { LastRejected = (orderId, userId); return Task.CompletedTask; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static IServiceProvider BuildProvider(out RecordingTradingEngine lane0, out RecordingTradingEngine lane1)
    {
        lane0 = new RecordingTradingEngine(0);
        lane1 = new RecordingTradingEngine(1);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<ITradingEngine>(0, lane0);
        services.AddKeyedSingleton<ITradingEngine>(1, lane1);
        services.AddMediator();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartLaneCommand_ResolvesRequestedLane_AndPassesMode()
    {
        var provider = BuildProvider(out var lane0, out var lane1);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new StartLaneCommand(1, TradingMode.Testnet));

        Assert.Null(lane0.StartedWith);
        Assert.Equal(TradingMode.Testnet, lane1.StartedWith);
    }

    [Fact]
    public async Task StopLaneCommand_ResolvesRequestedLane()
    {
        var provider = BuildProvider(out var lane0, out var lane1);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new StopLaneCommand(1));

        Assert.False(lane0.StopCalled);
        Assert.True(lane1.StopCalled);
    }

    [Fact]
    public async Task EmergencyStopCommand_PassesReasonThrough()
    {
        var provider = BuildProvider(out var lane0, out _);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new EmergencyStopCommand(0, "test reason"));

        Assert.Equal("test reason", lane0.LastEmergencyReason);
    }

    [Fact]
    public async Task ClosePositionCommand_PassesPositionIdThrough()
    {
        var provider = BuildProvider(out var lane0, out _);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ClosePositionCommand(0, "p1"));

        Assert.Equal("p1", lane0.LastClosedPositionId);
    }

    [Fact]
    public async Task SetStopLossTakeProfitCommand_PassesAllValuesThrough()
    {
        var provider = BuildProvider(out var lane0, out _);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new SetStopLossTakeProfitCommand(0, "p1", 100m, 200m, 5m));

        Assert.Equal(("p1", 100m, 200m, 5m), lane0.LastSlTp);
    }

    [Fact]
    public async Task ConfirmOrderCommand_PassesOrderIdAndUserIdThrough()
    {
        var provider = BuildProvider(out var lane0, out _);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new ConfirmOrderCommand(0, "o1", "user-42"));

        Assert.Equal(("o1", (string?)"user-42"), lane0.LastConfirmed);
    }

    [Fact]
    public async Task RejectOrderCommand_PassesOrderIdAndUserIdThrough()
    {
        var provider = BuildProvider(out var lane0, out _);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new RejectOrderCommand(0, "o1", "user-42"));

        Assert.Equal(("o1", (string?)"user-42"), lane0.LastRejected);
    }
}
