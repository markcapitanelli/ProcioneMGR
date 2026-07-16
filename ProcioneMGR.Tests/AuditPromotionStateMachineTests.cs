using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 3 — la state machine della promozione automatica, attaccata dal lato AVVERSARIALE:
/// (a) fuzz di 20.000 combinazioni metriche×opzioni×modalità su <see cref="PromotionEvaluator.Decide"/>
/// per dimostrare che NESSUN input produce mai un suggerimento Live o un'azione su una corsia Live;
/// (b) il <see cref="PromotionWorker"/> di fronte a decisioni avvelenate (ShouldPromote con
/// SuggestedMode=Live, come farebbe un evaluator buggato o una config corrotta) NON deve mai
/// chiamare il promoter; (c) il tick sopravvive al fallimento di una corsia e processa le altre.
/// </summary>
public class AuditPromotionStateMachineTests
{
    // --- Fuzz deterministico: nessun input porta a Live ----------------------------------------

    [Fact]
    public void Decide_Fuzz20k_NeverSuggestsLive_NeverActsOnLiveLanes()
    {
        var rnd = new Random(20260716); // seed fisso: il fuzz è riproducibile
        var modes = new[] { TradingMode.Paper, TradingMode.Testnet, TradingMode.Live };

        for (var i = 0; i < 20_000; i++)
        {
            var metrics = new LaneMetrics
            {
                RealizedSharpe = (decimal)(rnd.NextDouble() * 210 - 10),   // da -10 a +200: anche assurdi
                RealizedProfitFactor = (decimal)(rnd.NextDouble() * 50),
                MaxDrawdown = (decimal)(rnd.NextDouble() * 120 - 10),      // anche negativi/oltre 100
                TradeCount = rnd.Next(0, 100_000),
                WinRate = (decimal)(rnd.NextDouble() * 1.5 - 0.2),         // anche fuori [0,1]
                ObservationPeriod = TimeSpan.FromDays(rnd.NextDouble() * 1000),
            };
            var opt = new PromotionEvaluatorOptions
            {
                MinSharpeRealized = (decimal)(rnd.NextDouble() * 3 - 1),
                MinTradeCount = rnd.Next(0, 100),
                MaxDrawdownPercent = (decimal)(rnd.NextDouble() * 50),
                MinObservationWeeks = rnd.Next(0, 10),
                MinWinRate = (decimal)rnd.NextDouble(),
                AutoPromoteToTestnet = rnd.Next(2) == 0,
                AutoDemoteToPaper = rnd.Next(2) == 0,
                HardMaxDrawdownPercent = (decimal)(rnd.NextDouble() * 60),
                DemoteSharpeThreshold = (decimal)(rnd.NextDouble() * 2 - 0.5),
                DemoteMinWeeks = rnd.Next(0, 8),
            };
            var mode = modes[rnd.Next(modes.Length)];

            var d = PromotionEvaluator.Decide(metrics, mode, isRunning: rnd.Next(2) == 0, opt);

            // Invariante 1: un'azione automatica non punta MAI a Live.
            if (d.ShouldPromote || d.ShouldDemote)
            {
                Assert.NotEqual(TradingMode.Live, d.SuggestedMode);
            }
            // Invariante 2: Live compare come SuggestedMode SOLO come "nessun cambio" di una
            // corsia già Live, mai come transizione.
            if (d.SuggestedMode == TradingMode.Live)
            {
                Assert.Equal(TradingMode.Live, mode);
                Assert.False(d.ShouldPromote);
                Assert.False(d.ShouldDemote);
            }
            // Invariante 3: le corsie Live non vengono mai toccate.
            if (mode == TradingMode.Live)
            {
                Assert.False(d.ShouldPromote || d.ShouldDemote,
                    $"iterazione {i}: azione automatica su corsia Live (promote={d.ShouldPromote}, demote={d.ShouldDemote})");
            }
        }
    }

    // --- PromotionWorker: il filtro d'azione regge anche con decisioni avvelenate ---------------

    private sealed class FakeEvaluator(IReadOnlyList<PromotionDecision> decisions) : IPromotionEvaluator
    {
        public Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default)
            => Task.FromResult(decisions[laneId]);

        public Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default)
            => Task.FromResult(decisions);
    }

    private sealed class RecordingPromoter : ILanePromoter
    {
        public List<(int LaneId, TradingMode Mode)> Calls { get; } = [];
        public HashSet<int> FailingLanes { get; } = [];

        public Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
        {
            Calls.Add((laneId, newMode));
            if (FailingLanes.Contains(laneId))
            {
                throw new InvalidOperationException("Credenziali Testnet mancanti (simulate).");
            }
            return Task.CompletedTask;
        }
    }

    private static PromotionWorker Worker(IPromotionEvaluator evaluator, ILanePromoter promoter, ILogger<PromotionWorker>? logger = null) =>
        new(evaluator, promoter, new PromotionEvaluatorOptions().AsMonitor(), logger ?? NullLogger<PromotionWorker>.Instance);

    /// <summary>Logger che cattura i messaggi per livello — abbastanza per asserire che un errore sia stato emesso.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private static PromotionDecision Decision(int lane, TradingMode current, TradingMode suggested,
        bool promote = false, bool demote = false, bool running = true) => new()
    {
        LaneId = lane,
        Symbol = $"S{lane}/USDT",
        CurrentMode = current,
        SuggestedMode = suggested,
        ShouldPromote = promote,
        ShouldDemote = demote,
        IsRunning = running,
        Reason = "test",
    };

    [Fact]
    public async Task Tick_PromotesRunningPaperLane_AndDemotesFadedTestnetLane()
    {
        var promoter = new RecordingPromoter();
        var worker = Worker(new FakeEvaluator([
            Decision(0, TradingMode.Paper, TradingMode.Testnet, promote: true),
            Decision(1, TradingMode.Testnet, TradingMode.Paper, demote: true),
            Decision(2, TradingMode.Paper, TradingMode.Paper), // nessun cambio
        ]), promoter);

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal([(0, TradingMode.Testnet), (1, TradingMode.Paper)], promoter.Calls);
    }

    [Fact]
    public async Task Tick_PoisonedDecision_SuggestingLive_IsNeverActedUpon_AndIsLoggedAsError()
    {
        // Simula un evaluator buggato/config corrotta che spinge verso Live CON il flag di
        // promozione alzato: il worker deve rifiutarsi di agire (difesa in profondità n.2;
        // la n.3 è il throw del LanePromoter, già coperto altrove) E rendere il caso visibile
        // in log come errore, invece di scartarlo in silenzio.
        var promoter = new RecordingPromoter();
        var logger = new CapturingLogger<PromotionWorker>();
        var worker = Worker(new FakeEvaluator([
            Decision(0, TradingMode.Paper, TradingMode.Live, promote: true),
            Decision(1, TradingMode.Testnet, TradingMode.Live, promote: true, demote: true),
        ]), promoter, logger);

        await worker.TickAsync(CancellationToken.None);

        Assert.Empty(promoter.Calls);
        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count); // una per corsia avvelenata
        Assert.All(errors, e => Assert.Contains("INCOERENTE", e.Message));
    }

    [Fact]
    public async Task Tick_StoppedLanes_AreNeverTouched()
    {
        var promoter = new RecordingPromoter();
        var worker = Worker(new FakeEvaluator([
            Decision(0, TradingMode.Paper, TradingMode.Testnet, promote: true, running: false),
        ]), promoter);

        await worker.TickAsync(CancellationToken.None);

        Assert.Empty(promoter.Calls);
    }

    [Fact]
    public async Task Tick_OneLaneFails_OthersAreStillProcessed()
    {
        var promoter = new RecordingPromoter();
        promoter.FailingLanes.Add(0); // es. credenziali Testnet mancanti
        var worker = Worker(new FakeEvaluator([
            Decision(0, TradingMode.Paper, TradingMode.Testnet, promote: true),
            Decision(1, TradingMode.Paper, TradingMode.Testnet, promote: true),
        ]), promoter);

        // Il fallimento della corsia 0 è loggato e NON deve abortire il tick.
        await worker.TickAsync(CancellationToken.None);

        Assert.Equal([(0, TradingMode.Testnet), (1, TradingMode.Testnet)], promoter.Calls);
    }
}
