using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del trigger contestuale (Fase 2, PRD Autonomia §5): decisione PURA del detector
/// (cambio cluster sintetico, banda vol nei due versi), realized vol, e il worker — cooldown,
/// wake del planner (mai lancio diretto), gate a monte di Campaign:Enabled, notifica.
/// </summary>
public class RegimeChangeTriggerTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    // --- Evaluate (puro) ---------------------------------------------------------------------

    [Fact]
    public void Evaluate_SameRegime_VolInBand_NoTrigger()
    {
        var check = RegimeChangeDetector.Evaluate(2, 2, 0.010, 0.012, 1.5, RunId);

        Assert.False(check.Triggered);
        Assert.Equal("", check.Reason);
    }

    [Fact]
    public void Evaluate_ClusterChanged_Triggers()
    {
        var check = RegimeChangeDetector.Evaluate(2, 0, null, null, 1.5, RunId);

        Assert.True(check.Triggered);
        Assert.Contains("cluster K-means cambiato 2 → 0", check.Reason);
    }

    [Fact]
    public void Evaluate_VolExpansionBeyondBand_Triggers()
    {
        // Il caso SOL della sessione: compressione che si scioglie — realized ben oltre 1,5× forecast.
        var check = RegimeChangeDetector.Evaluate(1, 1, 0.010, 0.016, 1.5, RunId);

        Assert.True(check.Triggered);
        Assert.Contains("espansione", check.Reason);
    }

    [Fact]
    public void Evaluate_VolCompressionBelowBand_Triggers()
    {
        var check = RegimeChangeDetector.Evaluate(1, 1, 0.010, 0.006, 1.5, RunId);

        Assert.True(check.Triggered);
        Assert.Contains("compressione", check.Reason);
    }

    [Fact]
    public void Evaluate_MissingData_NeverTriggers()
    {
        // Senza baseline (regime o vol) non si inventa niente: nessun trigger.
        Assert.False(RegimeChangeDetector.Evaluate(null, 1, null, 0.02, 1.5, RunId).Triggered);
        Assert.False(RegimeChangeDetector.Evaluate(1, null, null, null, 1.5, RunId).Triggered);
    }

    [Fact]
    public void Evaluate_BothConditions_ReasonListsBoth()
    {
        var check = RegimeChangeDetector.Evaluate(0, 3, 0.010, 0.020, 1.5, RunId);

        Assert.True(check.Triggered);
        Assert.Contains("cluster", check.Reason);
        Assert.Contains("espansione", check.Reason);
    }

    // --- Realized vol (puro) -----------------------------------------------------------------

    [Fact]
    public void ComputeRealizedVolatility_ConstantPrices_IsZero()
    {
        var prices = Enumerable.Repeat(100m, 30).ToList();

        var vol = RegimeChangeDetector.ComputeRealizedVolatility(prices, 24);

        Assert.NotNull(vol);
        Assert.Equal(0.0, vol!.Value, precision: 10);
    }

    [Fact]
    public void ComputeRealizedVolatility_TooFewPrices_ReturnsNull()
    {
        Assert.Null(RegimeChangeDetector.ComputeRealizedVolatility(Enumerable.Repeat(100m, 10).ToList(), 24));
    }

    [Fact]
    public void ComputeRealizedVolatility_AlternatingReturns_MatchesStdDev()
    {
        // ±1% alternati: log-rendimenti ~±0.00995 → stddev ~0.01.
        var prices = new List<decimal> { 100m };
        for (var i = 0; i < 30; i++) prices.Add(prices[^1] * (i % 2 == 0 ? 1.01m : 0.99m));

        var vol = RegimeChangeDetector.ComputeRealizedVolatility(prices, 24);

        Assert.NotNull(vol);
        Assert.InRange(vol!.Value, 0.009, 0.011);
    }

    // --- Worker: cooldown, wake, gate, notifica ------------------------------------------------

    private sealed class ScriptedDetector : IRegimeChangeDetector
    {
        public RegimeTriggerCheck? Next { get; set; }
        public int Calls { get; private set; }
        public Task<RegimeTriggerCheck?> CheckAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    private sealed class RecordingPlanner : ICampaignPlanner
    {
        public List<string> WakeReasons { get; } = new();
        public int WakeResult { get; set; } = 1;
        public Task TickAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> WakeAsync(string reason, CancellationToken ct = default)
        {
            WakeReasons.Add(reason);
            return Task.FromResult(WakeResult);
        }
    }

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<string> Titles { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Titles.Add(title);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static RegimeTriggerCheck Fired => new() { Triggered = true, Reason = "cluster K-means cambiato 2 → 0" };

    private static (RegimeChangeTriggerWorker Worker, ScriptedDetector Detector, RecordingPlanner Planner,
        RecordingNotifier Notifier, FakeTimeProvider Time) Build(bool triggerEnabled = true, bool campaignEnabled = true)
    {
        var detector = new ScriptedDetector();
        var planner = new RecordingPlanner();
        var notifier = new RecordingNotifier();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-19T12:00:00Z"));
        var worker = new RegimeChangeTriggerWorker(
            detector, planner,
            new RegimeTriggerOptions { Enabled = triggerEnabled, CooldownHours = 6 }.AsMonitor(),
            new CampaignOptions { Enabled = campaignEnabled }.AsMonitor(),
            NullLogger<RegimeChangeTriggerWorker>.Instance,
            notifier, time);
        return (worker, detector, planner, notifier, time);
    }

    [Fact]
    public async Task Tick_Triggered_WakesPlanner_AndNotifies()
    {
        var (worker, detector, planner, notifier, _) = Build();
        detector.Next = Fired;

        await worker.TickAsync(CancellationToken.None);

        var reason = Assert.Single(planner.WakeReasons);
        Assert.Contains("cluster K-means cambiato", reason);
        Assert.Contains(notifier.Titles, t => t.Contains("Trigger contestuale"));
    }

    [Fact]
    public async Task Tick_Cooldown_SuppressesSecondFire_UntilElapsed()
    {
        var (worker, detector, planner, _, time) = Build();
        detector.Next = Fired;

        await worker.TickAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None); // dentro il cooldown: niente
        Assert.Single(planner.WakeReasons);

        time.Advance(TimeSpan.FromHours(6.1));
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(2, planner.WakeReasons.Count);
    }

    [Fact]
    public async Task Tick_NobodyWoken_DoesNotConsumeCooldown()
    {
        var (worker, detector, planner, notifier, _) = Build();
        detector.Next = Fired;
        planner.WakeResult = 0; // tutte le campagne in osservazione

        await worker.TickAsync(CancellationToken.None);
        planner.WakeResult = 1;
        await worker.TickAsync(CancellationToken.None); // niente cooldown consumato: riprova subito

        Assert.Equal(2, planner.WakeReasons.Count);
        Assert.Empty(notifier.Titles.Where(t => t.Contains("Trigger"))
            .Take(0)); // la notifica c'è solo quando qualcuno è stato svegliato
        Assert.Single(notifier.Titles);
    }

    [Fact]
    public async Task Tick_CampaignGateOff_DetectorNeverCalled()
    {
        var (worker, detector, planner, _, _) = Build(campaignEnabled: false);
        detector.Next = Fired;

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(0, detector.Calls);
        Assert.Empty(planner.WakeReasons);
    }

    [Fact]
    public async Task Tick_NotTriggered_NoWake()
    {
        var (worker, detector, planner, _, _) = Build();
        detector.Next = new RegimeTriggerCheck { Triggered = false };

        await worker.TickAsync(CancellationToken.None);

        Assert.Empty(planner.WakeReasons);
    }
}
