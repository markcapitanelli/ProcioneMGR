using System.Net;
using System.Net.Http;
using Anthropic.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del chokepoint delle chiamate Claude: classificazione degli errori del SDK (il billing è
/// un 400 riconoscibile solo dal testo), circuit breaker con notifiche one-shot, half-open probe
/// col cooldown, ripristino automatico. Tutto in-memory, nessuna chiamata reale e nessun DB.
/// </summary>
public sealed class LlmCallGuardTests
{
    private sealed class ScriptedLlm : ILlmClient
    {
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;
        public string Model => "test-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => throw new NotSupportedException("Il guard riceve la chiamata come lambda: il client non viene usato qui.");
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

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static (LlmCallGuard Guard, ScriptedLlm Llm, RecordingNotifier Notifier, FakeTimeProvider Time) Build(LlmOptions? options = null)
    {
        var llm = new ScriptedLlm();
        var notifier = new RecordingNotifier();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-19T12:00:00Z"));
        var guard = new LlmCallGuard(llm, (options ?? new LlmOptions()).AsMonitor(),
            NullLogger<LlmCallGuard>.Instance, metrics: null, notifier: notifier, timeProvider: time);
        return (guard, llm, notifier, time);
    }

    private static AnthropicBadRequestException BillingException() =>
        new(new HttpRequestException("Status Code: BadRequest"))
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""",
        };

    private static readonly Func<CancellationToken, Task<string>> BillingFailure = _ => throw BillingException();

    // --- Classificazione ---

    [Fact]
    public void Classify_BillingBadRequest_IsRetryableWithCause()
    {
        var (retryable, cause) = LlmCallGuard.Classify(BillingException());
        Assert.True(retryable);
        Assert.Equal("credito API", cause);
    }

    [Fact]
    public void Classify_PlainBadRequest_IsPermanent()
    {
        var ex = new AnthropicBadRequestException(new HttpRequestException("Status Code: BadRequest"))
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "max_tokens must be positive",
        };
        var (retryable, cause) = LlmCallGuard.Classify(ex);
        Assert.False(retryable);
        Assert.Equal("richiesta non valida", cause);
    }

    [Fact]
    public void Classify_TableOfTypedExceptions()
    {
        var inner = new HttpRequestException("boom");
        Assert.Equal((true, "rate-limit"), LlmCallGuard.Classify(
            new AnthropicRateLimitException(inner) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" }));
        Assert.Equal((true, "credenziali"), LlmCallGuard.Classify(
            new AnthropicUnauthorizedException(inner) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "" }));
        Assert.Equal((true, "credenziali"), LlmCallGuard.Classify(
            new AnthropicForbiddenException(inner) { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "" }));
        Assert.Equal((true, "server"), LlmCallGuard.Classify(
            new Anthropic5xxException(inner) { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "" }));
        Assert.Equal((true, "rete"), LlmCallGuard.Classify(new AnthropicIOException("rete giù", inner)));
        Assert.Equal((true, "rete"), LlmCallGuard.Classify(new HttpRequestException("timeout tcp")));
        Assert.Equal((false, "richiesta non valida"), LlmCallGuard.Classify(
            new AnthropicNotFoundException(inner) { StatusCode = HttpStatusCode.NotFound, ResponseBody = "" }));
        Assert.Equal((false, "inatteso"), LlmCallGuard.Classify(new InvalidOperationException("refusal")));
    }

    // --- Breaker ---

    [Fact]
    public async Task Breaker_OpensAfterThreshold_WithSingleWarning_ThenSkips()
    {
        var (guard, _, notifier, _) = Build(new LlmOptions { BreakerFailureThreshold = 3 });
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            var r = await guard.ExecuteAsync("advisory", ct => { calls++; return BillingFailure(ct); });
            Assert.Equal(LlmCallOutcome.FailedRetryable, r.Outcome);
            Assert.Equal("credito API", r.Cause);
        }

        Assert.True(guard.GetStatus().BreakerOpen);
        Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Warning, notifier.Sent[0].Severity);
        Assert.Contains("sospesa", notifier.Sent[0].Title, StringComparison.OrdinalIgnoreCase);

        // A breaker aperto: nessuna chiamata, esito skipped, nessuna nuova notifica.
        var skipped = await guard.ExecuteAsync("advisory", ct => { calls++; return BillingFailure(ct); });
        Assert.Equal(LlmCallOutcome.SkippedBreakerOpen, skipped.Outcome);
        Assert.Equal(3, calls);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Breaker_HalfOpenProbe_AfterCooldown_FailureStaysOpenSilently()
    {
        var (guard, _, notifier, time) = Build(new LlmOptions { BreakerFailureThreshold = 1, BreakerCooldownMinutes = 30 });
        var calls = 0;
        Func<CancellationToken, Task<string>> failing = ct => { calls++; return BillingFailure(ct); };

        await guard.ExecuteAsync("advisory", failing);
        Assert.True(guard.GetStatus().BreakerOpen);
        Assert.Single(notifier.Sent);

        // Entro il cooldown: skip senza chiamate.
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(LlmCallOutcome.SkippedBreakerOpen, (await guard.ExecuteAsync("advisory", failing)).Outcome);
        Assert.Equal(1, calls);

        // Cooldown scaduto: UN probe passa, fallisce, il breaker resta aperto in silenzio.
        time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(LlmCallOutcome.FailedRetryable, (await guard.ExecuteAsync("advisory", failing)).Outcome);
        Assert.Equal(2, calls);
        Assert.True(guard.GetStatus().BreakerOpen);
        Assert.Single(notifier.Sent); // nessuna seconda Warning

        // Subito dopo il probe fallito: di nuovo skip fino al prossimo cooldown.
        Assert.Equal(LlmCallOutcome.SkippedBreakerOpen, (await guard.ExecuteAsync("advisory", failing)).Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Breaker_ForceProbe_BypassesCooldown_AndSuccessRecoversWithSingleInfo()
    {
        var (guard, _, notifier, _) = Build(new LlmOptions { BreakerFailureThreshold = 1, BreakerCooldownMinutes = 60 });
        await guard.ExecuteAsync("advisory", BillingFailure);
        Assert.True(guard.GetStatus().BreakerOpen);

        // forceProbe ignora il cooldown ("Riprova adesso"); il successo chiude e notifica UNA Info.
        var ok = await guard.ExecuteAsync("advisory", _ => Task.FromResult("{}"), forceProbe: true);
        Assert.Equal(LlmCallOutcome.Ok, ok.Outcome);
        Assert.Equal("{}", ok.Text);

        var status = guard.GetStatus();
        Assert.False(status.BreakerOpen);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessUtc);

        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal(NotificationSeverity.Info, notifier.Sent[1].Severity);
        Assert.Contains("ripristinata", notifier.Sent[1].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PermanentErrors_DoNotTripBreaker()
    {
        var (guard, _, notifier, _) = Build(new LlmOptions { BreakerFailureThreshold = 2 });
        for (var i = 0; i < 4; i++)
        {
            var r = await guard.ExecuteAsync("advisory", _ => throw new InvalidOperationException("refusal"));
            Assert.Equal(LlmCallOutcome.FailedPermanent, r.Outcome);
        }
        Assert.False(guard.GetStatus().BreakerOpen);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task PermanentErrorDuringProbe_ClosesBreaker_ApiIsReachableAgain()
    {
        var (guard, _, notifier, _) = Build(new LlmOptions { BreakerFailureThreshold = 1 });
        await guard.ExecuteAsync("advisory", BillingFailure);
        Assert.True(guard.GetStatus().BreakerOpen);

        // L'API risponde nel merito (anche se con errore permanente) ⇒ la condizione transitoria è rientrata.
        var r = await guard.ExecuteAsync("advisory", _ => throw new InvalidOperationException("refusal"), forceProbe: true);
        Assert.Equal(LlmCallOutcome.FailedPermanent, r.Outcome);
        Assert.False(guard.GetStatus().BreakerOpen);
        Assert.Contains(notifier.Sent, n => n.Severity == NotificationSeverity.Info);
    }

    // --- Skip e cancellazioni ---

    [Fact]
    public async Task NotConfigured_SkipsWithoutCalling_AndWithoutMovingBreaker()
    {
        var (guard, llm, notifier, _) = Build();
        llm.Configured = false;
        var calls = 0;

        var r = await guard.ExecuteAsync("advisory", _ => { calls++; return Task.FromResult("x"); });

        Assert.Equal(LlmCallOutcome.SkippedNotConfigured, r.Outcome);
        Assert.Equal(0, calls);
        Assert.False(guard.GetStatus().BreakerOpen);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task InternalTimeout_IsRetryableWithTimeoutCause()
    {
        var (guard, _, _, _) = Build();
        var r = await guard.ExecuteAsync("advisory",
            async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return "mai"; },
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(LlmCallOutcome.FailedRetryable, r.Outcome);
        Assert.Equal("timeout", r.Cause);
    }

    [Fact]
    public async Task ExternalCancellation_Rethrows_WithoutCountingAsFailure()
    {
        var (guard, _, notifier, _) = Build(new LlmOptions { BreakerFailureThreshold = 1 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.ExecuteAsync("advisory",
            async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return "mai"; },
            ct: cts.Token));

        var status = guard.GetStatus();
        Assert.False(status.BreakerOpen);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Empty(notifier.Sent);
    }
}
