using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il token del bot Telegram sta nel PATH dell'URL (vincolo dell'API Telegram) e il logging di
/// default di HttpClientFactory scrive l'URI completo a Information: questi test verificano che
/// il client nominato "telegram-notifier" registrato da AddProcioneNotifications sia SENZA
/// logger HTTP (RemoveAllLoggers), con un client di controllo che dimostra che la cattura
/// funziona (il test non deve essere tautologico).
/// </summary>
public sealed class NotificationHttpLoggingTests
{
    private const string FakeToken = "999999:FAKE-TOKEN-CHE-NON-DEVE-FINIRE-NEI-LOG";

    private sealed record LogEntry(string Category, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(category, formatter(state, exception)));
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static async Task<ConcurrentQueue<LogEntry>> SendThroughFactoryAsync(string clientName)
    {
        var provider = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        services.AddProcioneNotifications(new ConfigurationBuilder().Build());
        // "control" nasce qui; per "telegram-notifier" AddHttpClient ritorna il builder del client
        // già registrato, quindi si aggiunge solo l'handler scriptato (nessuna chiamata reale).
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());

        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{FakeToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = "1" }));
        return provider.Entries;
    }

    [Fact]
    public async Task TelegramNotifierClient_HasNoHttpLoggers_SoTheTokenNeverReachesTheLogs()
    {
        var entries = await SendThroughFactoryAsync(TelegramNotifier.HttpClientName);

        Assert.DoesNotContain(entries, e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.Contains(FakeToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlClient_DoesLogTheFullUrl_ProvingTheCaptureWorks()
    {
        var entries = await SendThroughFactoryAsync("control-client");

        Assert.Contains(entries, e =>
            e.Category.StartsWith("System.Net.Http.HttpClient.control-client", StringComparison.Ordinal)
            && e.Message.Contains(FakeToken, StringComparison.Ordinal));
    }
}
