using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF1] Consumo e budget del layer AI. Le proprietà difese: (1) col tracking SPENTO il
/// comportamento è bit-identico a prima — zero conteggi, zero tetti, zero notifiche; (2) il budget
/// esaurito salta la chiamata SENZA muovere il breaker e avvisa una volta per transizione; (3) il
/// consumo è attribuito al provider che ha SERVITO, col path del guard che fluisce per contesto.
/// </summary>
public sealed class LlmUsageTrackerTests
{
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Questo test non deve toccare il DB.");
    }

    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-08-02T12:00:00Z");

    private static (LlmUsageTracker Tracker, MutableTime Time) Build(LlmBudgetOptions options)
    {
        var time = new MutableTime(Noon);
        var tracker = new LlmUsageTracker(new ThrowingDbFactory(), options.AsMonitor(),
            NullLogger<LlmUsageTracker>.Instance, time);
        return (tracker, time);
    }

    private static LlmUsageEvent Event(int prompt = 100, int completion = 50, string provider = "nvidia", string path = "advisory")
        => new(provider, "test-model", path, prompt, completion, Noon.UtcDateTime);

    [Fact]
    public void TrackingOff_CountsNothing_EnforcesNothing()
    {
        // Bit-identico a prima della fase: anche coi tetti impostati, senza TrackingEnabled non
        // esiste né conteggio né tetto (non si applica ciò che non si misura).
        var (tracker, _) = Build(new LlmBudgetOptions { TrackingEnabled = false, DailyCallLimit = 1 });

        tracker.Record(Event());
        tracker.Record(Event());

        Assert.False(tracker.CheckBudget().Exhausted);
        Assert.Empty(tracker.GetSnapshot().Today);
        Assert.Equal(0, tracker.GetSnapshot().TodayCalls);
    }

    [Fact]
    public void DailyCallLimit_ExhaustsAtTheThreshold()
    {
        var (tracker, _) = Build(new LlmBudgetOptions { TrackingEnabled = true, DailyCallLimit = 2 });

        tracker.Record(Event());
        Assert.False(tracker.CheckBudget().Exhausted);

        tracker.Record(Event());
        var verdict = tracker.CheckBudget();
        Assert.True(verdict.Exhausted);
        Assert.Contains("2/2 chiamate", verdict.Reason);
    }

    [Fact]
    public void DailyTokenLimit_SumsPromptAndCompletion()
    {
        var (tracker, _) = Build(new LlmBudgetOptions { TrackingEnabled = true, DailyTokenLimit = 200 });

        tracker.Record(Event(prompt: 100, completion: 50));
        Assert.False(tracker.CheckBudget().Exhausted);

        tracker.Record(Event(prompt: 40, completion: 10));
        Assert.True(tracker.CheckBudget().Exhausted); // 200 >= 200: il tetto è compreso
    }

    [Fact]
    public void ZeroLimits_MeanNoCeiling()
    {
        var (tracker, _) = Build(new LlmBudgetOptions { TrackingEnabled = true });
        for (var i = 0; i < 1000; i++) tracker.Record(Event());
        Assert.False(tracker.CheckBudget().Exhausted);
        Assert.Equal(1000, tracker.GetSnapshot().TodayCalls);
    }

    [Fact]
    public void MidnightRollover_ResetsTheDailyBudget_AndRearmsTheNotification()
    {
        var (tracker, time) = Build(new LlmBudgetOptions { TrackingEnabled = true, DailyCallLimit = 1 });

        tracker.Record(Event());
        Assert.True(tracker.CheckBudget().Exhausted);
        Assert.True(tracker.TryMarkExhaustionNotified());
        Assert.False(tracker.TryMarkExhaustionNotified()); // una notifica per transizione

        time.Now = Noon.AddDays(1); // mezzanotte passata
        Assert.False(tracker.CheckBudget().Exhausted);

        // Nuovo esaurimento nel giorno nuovo = nuova notifica.
        tracker.Record(new LlmUsageEvent("nvidia", "test-model", "advisory", 10, 5, time.Now.UtcDateTime));
        Assert.True(tracker.CheckBudget().Exhausted);
        Assert.True(tracker.TryMarkExhaustionNotified());
    }

    [Fact]
    public void MonthlyLimit_SurvivesTheDailyRollover()
    {
        var (tracker, time) = Build(new LlmBudgetOptions { TrackingEnabled = true, MonthlyTokenLimit = 200 });

        tracker.Record(Event(prompt: 100, completion: 50));
        time.Now = Noon.AddDays(1); // giorno nuovo, stesso mese

        tracker.Record(new LlmUsageEvent("nvidia", "test-model", "advisory", 40, 10, time.Now.UtcDateTime));
        Assert.True(tracker.CheckBudget().Exhausted); // 150 + 50 = 200 nel mese
    }

    [Fact]
    public void Snapshot_GroupsByProviderModelPath()
    {
        var (tracker, _) = Build(new LlmBudgetOptions { TrackingEnabled = true });

        tracker.Record(Event(provider: "nvidia", path: "advisory"));
        tracker.Record(Event(provider: "nvidia", path: "advisory"));
        tracker.Record(Event(provider: "groq", path: "sentiment"));

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(2, snapshot.Today.Count);
        var nvidia = snapshot.Today.Single(r => r.Provider == "nvidia");
        Assert.Equal(2, nvidia.Calls);
        Assert.Equal(200, nvidia.PromptTokens);
    }
}

/// <summary>Il guard davanti a un budget esaurito: salta senza chiamare, senza muovere il breaker.</summary>
public sealed class LlmBudgetGuardTests
{
    private sealed class ScriptedLlm : ILlmClient
    {
        public bool IsConfigured => true;
        public string Model => "test-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedSink : ILlmUsageSink
    {
        public bool Exhausted { get; set; }
        public int NotifiedCount { get; private set; }
        public void Record(LlmUsageEvent e) { }
        public LlmBudgetVerdict CheckBudget() => Exhausted ? new(true, "budget: test") : LlmBudgetVerdict.Allowed;
        public bool TryMarkExhaustionNotified() => ++NotifiedCount == 1;
        public LlmUsageSnapshot GetSnapshot() => new(DateTime.UtcNow.Date, [], 0, 0, 0, true);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    private static (LlmCallGuard Guard, ScriptedSink Sink, RecordingNotifier Notifier) Build(bool exhausted)
    {
        var sink = new ScriptedSink { Exhausted = exhausted };
        var notifier = new RecordingNotifier();
        var guard = new LlmCallGuard(new ScriptedLlm(), new LlmOptions().AsMonitor(),
            NullLogger<LlmCallGuard>.Instance, metrics: null, notifier: notifier, timeProvider: null, usageSink: sink);
        return (guard, sink, notifier);
    }

    [Fact]
    public async Task ExhaustedBudget_SkipsWithoutCalling_AndWithoutMovingTheBreaker()
    {
        var (guard, _, _) = Build(exhausted: true);
        var called = false;

        var result = await guard.ExecuteAsync("advisory", _ => { called = true; return Task.FromResult("x"); });

        Assert.Equal(LlmCallOutcome.SkippedBudgetExhausted, result.Outcome);
        Assert.False(called);
        Assert.False(guard.GetStatus().BreakerOpen);
        Assert.Equal(0, guard.GetStatus().ConsecutiveFailures);
    }

    [Fact]
    public async Task ExhaustedBudget_NotifiesOncePerTransition()
    {
        var (guard, _, notifier) = Build(exhausted: true);

        await guard.ExecuteAsync("advisory", _ => Task.FromResult("x"));
        await guard.ExecuteAsync("veto", _ => Task.FromResult("x"));

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Warning, sent.Severity);
        Assert.Contains("Budget AI", sent.Title);
    }

    [Fact]
    public async Task ForceProbe_DoesNotBypassTheBudget()
    {
        // Il probe forza il cooldown di un GUASTO, non un tetto di spesa.
        var (guard, _, _) = Build(exhausted: true);

        var result = await guard.ExecuteAsync("advisory", _ => Task.FromResult("x"), forceProbe: true);

        Assert.Equal(LlmCallOutcome.SkippedBudgetExhausted, result.Outcome);
    }

    [Fact]
    public async Task AvailableBudget_LetsTheCallThrough_WithThePathInContext()
    {
        var (guard, _, _) = Build(exhausted: false);
        string? observedPath = null;

        var result = await guard.ExecuteAsync("sentiment", _ =>
        {
            observedPath = LlmCallContext.CurrentPath; // ciò che il client vedrà, attraverso il failover
            return Task.FromResult("ok");
        });

        Assert.Equal(LlmCallOutcome.Ok, result.Outcome);
        Assert.Equal("sentiment", observedPath);
        Assert.Null(LlmCallContext.CurrentPath); // lo scope si chiude con la chiamata
    }
}

/// <summary>Il client compat dichiara al sink il campo `usage` che prima scartava.</summary>
public sealed class CompatClientUsageTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeKeyStore : IAiKeyStore
    {
        public Task<string?> GetKeyAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>("key");
        public string? GetCachedKey(string provider) => "key";
        public AiKeySource GetCachedSource(string provider) => AiKeySource.Database;
        public Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveKeyAsync(string provider, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSink : ILlmUsageSink
    {
        public List<LlmUsageEvent> Events { get; } = new();
        public void Record(LlmUsageEvent e) => Events.Add(e);
        public LlmBudgetVerdict CheckBudget() => LlmBudgetVerdict.Allowed;
        public bool TryMarkExhaustionNotified() => false;
        public LlmUsageSnapshot GetSnapshot() => new(DateTime.UtcNow.Date, [], 0, 0, 0, true);
    }

    private const string BodyWithUsage = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":123,"completion_tokens":45,"total_tokens":168}}
        """;

    private static GroqLlmClient Groq(string body, RecordingSink sink) =>
        new(new SingleClientFactory(new ScriptedHandler(HttpStatusCode.OK, body)),
            new LlmOptions().AsMonitor(), new FakeKeyStore(), NullLogger<GroqLlmClient>.Instance, sink);

    [Fact]
    public async Task Usage_IsAttributedToTheServingProvider_WithAmbientPath()
    {
        var sink = new RecordingSink();

        using (LlmCallContext.Enter("advisory"))
        {
            await Groq(BodyWithUsage, sink).CompleteAsync("sys", "user", CancellationToken.None);
        }

        var e = Assert.Single(sink.Events);
        Assert.Equal(AiProviders.Groq, e.Provider); // chi ha SERVITO, non il provider "attivo"
        Assert.Equal("advisory", e.Path);
        Assert.Equal(123, e.PromptTokens);
        Assert.Equal(45, e.CompletionTokens);
    }

    [Fact]
    public async Task NoAmbientPath_FallsBackToDirect()
    {
        var sink = new RecordingSink();
        await Groq(BodyWithUsage, sink).CompleteAsync("sys", "user", CancellationToken.None);
        Assert.Equal("direct", Assert.Single(sink.Events).Path);
    }

    [Fact]
    public async Task MissingUsageField_DoesNotBreakTheCall()
    {
        var sink = new RecordingSink();
        var body = """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

        var text = await Groq(body, sink).CompleteAsync("sys", "user", CancellationToken.None);

        Assert.Equal("ok", text);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task ReasoningThatEatsAllTokens_StillCountsTheUsage()
    {
        // La risposta vuota è un errore per il chiamante, ma i token sono stati consumati lo
        // stesso: il conteggio avviene PRIMA del check sul contenuto.
        var sink = new RecordingSink();
        var body = """
            {"choices":[{"message":{"role":"assistant","content":""},"finish_reason":"length"}],
             "usage":{"prompt_tokens":50,"completion_tokens":4096}}
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Groq(body, sink).CompleteAsync("sys", "user", CancellationToken.None));

        var e = Assert.Single(sink.Events);
        Assert.Equal(4096, e.CompletionTokens);
    }
}

/// <summary>Il giro completo su Postgres: flush idempotente e ripresa dei totali dopo un riavvio.</summary>
[Collection("Postgres")]
public sealed class LlmUsagePersistenceTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly string _connString = pg.CreateDatabase();
    private ServiceProvider? _provider;

    private sealed class PassthroughEncryption : ProcioneMGR.Services.Security.IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static LlmUsageTracker Tracker(IDbContextFactory<ApplicationDbContext> factory, LlmBudgetOptions? options = null)
        => new(factory, (options ?? new LlmBudgetOptions { TrackingEnabled = true }).AsMonitor(),
            NullLogger<LlmUsageTracker>.Instance);

    [Fact]
    public async Task Flush_UpsertsAggregatedRows_AndIsIdempotent()
    {
        var factory = await DbAsync();
        var tracker = Tracker(factory);
        var now = DateTime.UtcNow;

        tracker.Record(new LlmUsageEvent("nvidia", "m1", "advisory", 100, 50, now));
        tracker.Record(new LlmUsageEvent("nvidia", "m1", "advisory", 10, 5, now));
        tracker.Record(new LlmUsageEvent("groq", "m2", "sentiment", 20, 10, now));

        await tracker.FlushAsync();
        await tracker.FlushAsync(); // senza delta nuovi: non deve raddoppiare nulla

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.LlmUsageRecords.AsNoTracking().OrderBy(r => r.Provider).ToListAsync();
        Assert.Equal(2, rows.Count);
        var nvidia = rows.Single(r => r.Provider == "nvidia");
        Assert.Equal(2, nvidia.Calls);
        Assert.Equal(110, nvidia.PromptTokens);
        Assert.Equal(55, nvidia.CompletionTokens);
    }

    [Fact]
    public async Task Restart_ResumesTodaysBudget_FromTheDatabase()
    {
        // Un budget giornaliero che si azzera riavviando il processo non è un budget.
        var factory = await DbAsync();
        var first = Tracker(factory, new LlmBudgetOptions { TrackingEnabled = true, DailyCallLimit = 2 });
        var now = DateTime.UtcNow;

        first.Record(new LlmUsageEvent("nvidia", "m1", "advisory", 10, 5, now));
        first.Record(new LlmUsageEvent("nvidia", "m1", "advisory", 10, 5, now));
        await first.FlushAsync();
        Assert.True(first.CheckBudget().Exhausted);

        // "Riavvio": tracker nuovo, stessa base. Prima del load il budget sottoconta (dichiarato),
        // dopo il load riprende da dov'era.
        var second = Tracker(factory, new LlmBudgetOptions { TrackingEnabled = true, DailyCallLimit = 2 });
        await second.LoadPersistedTotalsAsync();
        Assert.True(second.CheckBudget().Exhausted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
