using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF0] La flotta a 8 con cinque corsie DORMIENTI (registrate, mai avviate) attraversa la
/// valutazione di promozione senza eccezioni e senza azioni: una corsia mai partita non ha metriche,
/// e "nessuna metrica" deve restare "nessuna decisione", non un crash né — peggio — una promozione
/// costruita su zeri.
/// </summary>
[Collection("TradingLanes")]
public sealed class DormantFleetPromotionTests : IDisposable
{
    public DormantFleetPromotionTests() => TradingLanes.ResetForTests();

    public void Dispose() => TradingLanes.ResetForTests();

    private sealed class ScriptedEngine(int laneId, bool running, TradingMode mode) : ITradingEngine
    {
        public int LaneId => laneId;
        public bool IsRunning => running;

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus
            {
                Mode = mode,
                IsRunning = running,
                Symbol = running ? "BTC/USDT" : string.Empty,
                StartedAtUtc = running ? DateTime.UtcNow.AddDays(-30) : null,
            });

        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
            => Task.FromResult(new TradingPerformance()); // corsia dormiente/giovane: tutto a zero

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EightLaneFleet_FiveDormant_NoExceptions_NoActionsOnDormantLanes()
    {
        TradingLanes.Configure(8);

        var services = new ServiceCollection();
        for (var lane = 0; lane < 8; lane++)
        {
            var laneId = lane;
            services.AddKeyedSingleton<ITradingEngine>(laneId,
                (_, _) => new ScriptedEngine(laneId, running: laneId < 3, mode: TradingMode.Paper));
        }

        await using var provider = services.BuildServiceProvider();
        var evaluator = new PromotionEvaluator(provider,
            new StaticOptionsMonitor<PromotionEvaluatorOptions>(new PromotionEvaluatorOptions()));

        var decisions = await evaluator.EvaluateAllLanesAsync();

        Assert.Equal(8, decisions.Count);
        Assert.All(decisions, d =>
        {
            // Con performance a zero NESSUNA corsia — attiva o dormiente — merita un'azione, e la
            // modalità suggerita resta quella corrente (mai un cambio costruito su metriche vuote).
            Assert.False(d.ShouldPromote);
            Assert.False(d.ShouldDemote);
            Assert.Equal(TradingMode.Paper, d.SuggestedMode);
        });
        Assert.All(decisions.Skip(3), d => Assert.False(d.IsRunning));
    }
}
