using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del canale di notifica (Fase 4, PRD Autonomia §7): gate default OFF, selezione provider,
/// rate-limit a finestra scorrevole con coalescing (i soppressi vengono riportati, mai persi in
/// silenzio), e la garanzia più importante per i producer: il dispatcher NON propaga MAI —
/// una notifica fallita non deve far cadere un watchdog o un planner.
/// </summary>
public class NotificationDispatcherTests
{
    /// <summary>TimeProvider a orologio manuale (evita il pacchetto Microsoft.Extensions.TimeProvider.Testing per due metodi).</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RecordingProvider(string name = "Fake") : INotificationProvider
    {
        public string Name => name;
        public List<(NotificationSeverity Severity, string Title, string Body)> Sent { get; } = new();
        public Exception? ThrowOnSend { get; set; }

        public Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)
        {
            if (ThrowOnSend is not null) throw ThrowOnSend;
            Sent.Add((severity, title, body));
            return Task.CompletedTask;
        }
    }

    private static (NotificationDispatcher Dispatcher, RecordingProvider Provider, FakeTimeProvider Time) Build(
        NotificationOptions? options = null)
    {
        var provider = new RecordingProvider();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-19T10:00:00Z"));
        var dispatcher = new NotificationDispatcher(
            (options ?? new NotificationOptions { Enabled = true, Provider = "Fake" }).AsMonitor(),
            [provider],
            NullLogger<NotificationDispatcher>.Instance,
            time);
        return (dispatcher, provider, time);
    }

    [Fact]
    public async Task DisabledByDefault_NothingIsSent()
    {
        var (dispatcher, provider, _) = Build(new NotificationOptions { Provider = "Fake" }); // Enabled=false (default)

        await dispatcher.NotifyAsync(NotificationSeverity.Critical, "Titolo", "Corpo");

        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task Enabled_RoutesToSelectedProvider_CaseInsensitive()
    {
        var (dispatcher, provider, _) = Build(new NotificationOptions { Enabled = true, Provider = "fake" });

        await dispatcher.NotifyAsync(NotificationSeverity.Warning, "Corsia 2 in QUARANTENA", "dettagli");

        var sent = Assert.Single(provider.Sent);
        Assert.Equal(NotificationSeverity.Warning, sent.Severity);
        Assert.Equal("Corsia 2 in QUARANTENA", sent.Title);
    }

    [Fact]
    public async Task UnknownProvider_DoesNotThrow_AndSendsNothing()
    {
        var (dispatcher, provider, _) = Build(new NotificationOptions { Enabled = true, Provider = "Piccione" });

        await dispatcher.NotifyAsync(NotificationSeverity.Info, "T", "B");

        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task ProviderFailure_IsSwallowed_ProducerNeverFails()
    {
        var (dispatcher, provider, _) = Build();
        provider.ThrowOnSend = new HttpRequestException("telegram giù");

        // Nessuna eccezione: il producer (watchdog/planner) non deve mai cadere per una notifica.
        await dispatcher.NotifyAsync(NotificationSeverity.Critical, "T", "B");
    }

    [Fact]
    public async Task RateLimit_SuppressesExcess_AndReportsCoalescedCount()
    {
        var (dispatcher, provider, time) = Build(new NotificationOptions { Enabled = true, Provider = "Fake", MaxPerHour = 2 });

        await dispatcher.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "2", "b");
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "3", "b"); // soppressa
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "4", "b"); // soppressa
        Assert.Equal(2, provider.Sent.Count);

        // Passata un'ora la finestra si libera: il primo messaggio riporta i 2 soppressi.
        time.Advance(TimeSpan.FromMinutes(61));
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "5", "corpo");

        Assert.Equal(3, provider.Sent.Count);
        Assert.Contains("+2 notifiche soppresse", provider.Sent[^1].Body);
    }

    [Fact]
    public async Task RateLimit_WindowSlides_OldSendsExpire()
    {
        var (dispatcher, provider, time) = Build(new NotificationOptions { Enabled = true, Provider = "Fake", MaxPerHour = 1 });

        await dispatcher.NotifyAsync(NotificationSeverity.Info, "1", "b");
        time.Advance(TimeSpan.FromMinutes(30));
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "2", "b"); // soppressa (finestra piena)
        time.Advance(TimeSpan.FromMinutes(31));
        await dispatcher.NotifyAsync(NotificationSeverity.Info, "3", "b"); // la prima è uscita dalla finestra

        Assert.Equal(2, provider.Sent.Count);
        Assert.Equal("3", provider.Sent[^1].Title);
    }
}
