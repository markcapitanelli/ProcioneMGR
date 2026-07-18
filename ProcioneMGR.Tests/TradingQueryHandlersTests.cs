using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Queries;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dei 5 handler pilota (Fase 1, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.6 "query pilota"):
/// ciascuno deve risolvere il motore della corsia GIUSTA (LaneId sulla richiesta, non
/// un'istanza fissa) e restituirne il risultato inalterato. Due corsie keyed registrate con
/// valori diversi provano che il routing avviene per dato (§4.2), non per una singola istanza
/// implicita — <see cref="TradingPageServiceTests"/> usa sempre la sola corsia 0 e non
/// eserciterebbe questo aspetto.
/// </summary>
public class TradingQueryHandlersTests
{
    private sealed class StubTradingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public TradingEngineStatus StatusToReturn { get; set; } = new();
        public List<OpenPosition> PositionsToReturn { get; set; } = [];
        public List<Order> OrdersToReturn { get; set; } = [];
        public List<Order> PendingToReturn { get; set; } = [];
        public TradingPerformance PerformanceToReturn { get; set; } = new();
        public DateTime? LastFromRequested { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(StatusToReturn);
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(PositionsToReturn);
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)
        {
            LastFromRequested = from;
            return Task.FromResult(OrdersToReturn);
        }
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(PendingToReturn);
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
        {
            LastFromRequested = from;
            return Task.FromResult(PerformanceToReturn);
        }

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static IServiceProvider BuildProvider(out StubTradingEngine lane0, out StubTradingEngine lane1)
    {
        lane0 = new StubTradingEngine(0);
        lane1 = new StubTradingEngine(1);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<ITradingEngine>(0, lane0);
        services.AddKeyedSingleton<ITradingEngine>(1, lane1);
        services.AddMediator();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetLaneStatusQuery_ResolvesRequestedLane_NotAlwaysLaneZero()
    {
        var provider = BuildProvider(out var lane0, out var lane1);
        lane0.StatusToReturn = new TradingEngineStatus { Mode = TradingMode.Paper };
        lane1.StatusToReturn = new TradingEngineStatus { Mode = TradingMode.Testnet };
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetLaneStatusQuery(1));

        Assert.Equal(TradingMode.Testnet, result.Mode);
    }

    [Fact]
    public async Task GetOpenPositionsQuery_ReturnsResolvedEnginesPositions()
    {
        var provider = BuildProvider(out var lane0, out _);
        lane0.PositionsToReturn = [new OpenPosition { PositionId = "p1" }];
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetOpenPositionsQuery(0));

        Assert.Single(result);
        Assert.Equal("p1", result[0].PositionId);
    }

    [Fact]
    public async Task GetPerformanceQuery_PassesFromThroughToEngine()
    {
        var provider = BuildProvider(out var lane0, out _);
        var from = DateTime.UtcNow.AddDays(-90);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new GetPerformanceQuery(0, from));

        Assert.Equal(from, lane0.LastFromRequested);
    }

    [Fact]
    public async Task GetOrderHistoryQuery_ReturnsResolvedEnginesOrders()
    {
        var provider = BuildProvider(out var lane0, out _);
        lane0.OrdersToReturn = [new Order { OrderId = "o1" }];
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetOrderHistoryQuery(0));

        Assert.Single(result);
        Assert.Equal("o1", result[0].OrderId);
    }

    [Fact]
    public async Task GetPendingOrdersQuery_ReturnsResolvedEnginesPendingOrders()
    {
        var provider = BuildProvider(out var lane0, out _);
        lane0.PendingToReturn = [new Order { OrderId = "p-pending" }];
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetPendingOrdersQuery(0));

        Assert.Single(result);
        Assert.Equal("p-pending", result[0].OrderId);
    }
}
