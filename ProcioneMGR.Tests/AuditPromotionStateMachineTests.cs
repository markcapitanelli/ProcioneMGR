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
    //
    // [AF4a] Il fuzz ora copre ANCHE le opzioni della retrocessione di sicurezza Live→Testnet:
    // l'unica estensione mai concessa al perimetro Live, e solo nella direzione che RIDUCE il
    // rischio. Gli invarianti storici 1-2 restano identici; il 3 si sdoppia (3′ col flag OFF =
    // bit-identico a prima; 4-5 col flag ON = da Live si può solo scendere a Testnet).

    private static LaneMetrics RandomMetrics(Random rnd) => new()
    {
        RealizedSharpe = (decimal)(rnd.NextDouble() * 210 - 10),   // da -10 a +200: anche assurdi
        RealizedProfitFactor = (decimal)(rnd.NextDouble() * 50),
        MaxDrawdown = (decimal)(rnd.NextDouble() * 120 - 10),      // anche negativi/oltre 100
        TradeCount = rnd.Next(0, 100_000),
        WinRate = (decimal)(rnd.NextDouble() * 1.5 - 0.2),         // anche fuori [0,1]
        ObservationPeriod = TimeSpan.FromDays(rnd.NextDouble() * 1000),
    };

    private static PromotionEvaluatorOptions RandomOptions(Random rnd) => new()
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
        // [AF4a] Anche corrotte/negative: nessuna combinazione deve aprire strade nuove.
        AutoDemoteLiveToTestnet = rnd.Next(2) == 0,
        DemoteLiveDryRun = rnd.Next(2) == 0,
        DemoteLiveSharpeThreshold = (decimal)(rnd.NextDouble() * 4 - 2),
        DemoteLiveMaxDrawdownPercent = (decimal)(rnd.NextDouble() * 80 - 20),
        DemoteLiveMinWeeks = rnd.Next(-2, 8),
        DemoteLiveMinTrades = rnd.Next(-5, 100),
    };

    [Fact]
    public void Decide_Fuzz20k_NeverSuggestsLive_AndFromLiveOnlyTestnetIsReachable()
    {
        var rnd = new Random(20260716); // seed fisso: il fuzz è riproducibile
        var modes = new[] { TradingMode.Paper, TradingMode.Testnet, TradingMode.Live };

        for (var i = 0; i < 20_000; i++)
        {
            var metrics = RandomMetrics(rnd);
            var opt = RandomOptions(rnd);
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
            // Invariante 4 [AF4a]: qualunque azione su una corsia Live è SOLO la retrocessione a
            // Testnet — mai Paper diretto, mai una promozione, mai col dry-run acceso.
            if (mode == TradingMode.Live && (d.ShouldPromote || d.ShouldDemote))
            {
                Assert.True(opt.AutoDemoteLiveToTestnet, $"iterazione {i}: azione su Live col flag OFF");
                Assert.False(opt.DemoteLiveDryRun, $"iterazione {i}: azione su Live col dry-run acceso");
                Assert.False(d.ShouldPromote, $"iterazione {i}: promozione da Live");
                Assert.True(d.ShouldDemote);
                Assert.Equal(TradingMode.Testnet, d.SuggestedMode);
            }
            // Il dry-run è visibilità, mai azione — e non esiste fuori dal perimetro Live.
            if (d.WouldDemoteLive)
            {
                Assert.Equal(TradingMode.Live, mode);
                Assert.False(d.ShouldDemote);
            }
        }
    }

    [Fact]
    public void Decide_Fuzz20k_FlagOff_LiveLanesAreBitIdenticalToTheHistoricalBehaviour()
    {
        // Invariante 3′ [AF4a]: con AutoDemoteLiveToTestnet=false le corsie Live non vengono MAI
        // toccate, qualunque siano metriche e altre opzioni — il comportamento storico, difeso
        // come proprietà e non come ricordo.
        var rnd = new Random(20260802);

        for (var i = 0; i < 20_000; i++)
        {
            var opt = RandomOptions(rnd);
            opt.AutoDemoteLiveToTestnet = false;

            var d = PromotionEvaluator.Decide(RandomMetrics(rnd), TradingMode.Live, isRunning: rnd.Next(2) == 0, opt);

            Assert.False(d.ShouldPromote || d.ShouldDemote,
                $"iterazione {i}: azione automatica su corsia Live col flag spento");
            Assert.False(d.WouldDemoteLive, $"iterazione {i}: dry-run attivo col flag spento");
            Assert.Equal(TradingMode.Live, d.SuggestedMode);
        }
    }

    [Fact]
    public void Decide_LiveDemotion_RequiresHistory_AndFiresOnDegradation()
    {
        // Il caso nominale a mano (il fuzz dimostra i confini, non il comportamento voluto):
        // Live degradata con storia sufficiente → retrocessione a Testnet; con dry-run → solo
        // l'annuncio; con storia insufficiente → niente, qualunque sia il degrado.
        var degraded = new LaneMetrics
        {
            RealizedSharpe = -0.5m,
            MaxDrawdown = 20m,
            TradeCount = 25,
            WinRate = 0.3m,
            ObservationPeriod = TimeSpan.FromDays(14),
        };
        var opt = new PromotionEvaluatorOptions
        {
            AutoDemoteLiveToTestnet = true,
            DemoteLiveDryRun = false,
            DemoteLiveSharpeThreshold = 0.0m,
            DemoteLiveMaxDrawdownPercent = 15m,
            DemoteLiveMinWeeks = 1,
            DemoteLiveMinTrades = 10,
        };

        var d = PromotionEvaluator.Decide(degraded, TradingMode.Live, isRunning: true, opt);
        Assert.True(d.ShouldDemote);
        Assert.Equal(TradingMode.Testnet, d.SuggestedMode);

        // Dry-run: l'annuncio senza l'azione.
        opt.DemoteLiveDryRun = true;
        var dry = PromotionEvaluator.Decide(degraded, TradingMode.Live, isRunning: true, opt);
        Assert.False(dry.ShouldDemote);
        Assert.True(dry.WouldDemoteLive);
        Assert.Contains("DRY-RUN", dry.Reason);

        // Storia insufficiente: il degrado da solo non basta (5 trade in 2 giorni non sono un giudizio).
        opt.DemoteLiveDryRun = false;
        var young = PromotionEvaluator.Decide(new LaneMetrics
        {
            RealizedSharpe = -3m, MaxDrawdown = 40m, TradeCount = 5,
            ObservationPeriod = TimeSpan.FromDays(2),
        }, TradingMode.Live, isRunning: true, opt);
        Assert.False(young.ShouldDemote);
        Assert.False(young.WouldDemoteLive);

        // Live in salute: nessuna azione.
        var healthy = PromotionEvaluator.Decide(new LaneMetrics
        {
            RealizedSharpe = 1.5m, MaxDrawdown = 5m, TradeCount = 50,
            ObservationPeriod = TimeSpan.FromDays(30),
        }, TradingMode.Live, isRunning: true, opt);
        Assert.False(healthy.ShouldDemote);
        Assert.False(healthy.WouldDemoteLive);
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

    // --- [AF4a] La retrocessione di sicurezza Live→Testnet attraverso il worker ----------------

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<(ProcioneMGR.Services.Notifications.NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Tick_LiveDemotionDecision_IsActedUpon_WithAWarningNotification()
    {
        // La retrocessione da Live non è routine come Paper↔Testnet: la notifica è Warning, non
        // Info — soldi veri appena spostati fuori pericolo, l'operatore deve alzare gli occhi.
        var promoter = new RecordingPromoter();
        var notifier = new RecordingNotifier();
        var worker = new PromotionWorker(new FakeEvaluator([
            Decision(0, TradingMode.Live, TradingMode.Testnet, demote: true),
        ]), promoter, new PromotionEvaluatorOptions().AsMonitor(),
            NullLogger<PromotionWorker>.Instance, metrics: null, notifier: notifier);

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal([(0, TradingMode.Testnet)], promoter.Calls);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Warning, sent.Severity);
    }

    [Fact]
    public async Task Tick_PoisonedLiveDecisions_AreNeverActedUpon()
    {
        // Da Live NON esiste Paper diretto, né una "promozione" che parta da Live: qualunque
        // decisione del genere è un evaluator buggato e va rifiutata a voce alta (LogError),
        // esattamente come le decisioni verso Live.
        var promoter = new RecordingPromoter();
        var logger = new CapturingLogger<PromotionWorker>();
        var worker = Worker(new FakeEvaluator([
            Decision(0, TradingMode.Live, TradingMode.Paper, demote: true),     // salto di due gradini
            Decision(1, TradingMode.Live, TradingMode.Testnet, promote: true),  // "promozione" da Live
            Decision(2, TradingMode.Paper, TradingMode.Testnet, demote: true),  // demote che scala
        ]), promoter, logger);

        await worker.TickAsync(CancellationToken.None);

        Assert.Empty(promoter.Calls);
        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Error));
    }
}
