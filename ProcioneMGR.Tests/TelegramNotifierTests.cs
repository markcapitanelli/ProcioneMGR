using System.Net;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del provider Telegram (Fase 4): payload corretto (chat_id + testo con icona di gravità),
/// token SOLO dall'env (mai config), errori HTTP che diventano eccezioni (che il dispatcher
/// contiene). Handler HTTP scriptato: nessuna chiamata reale.
/// </summary>
public sealed class TelegramNotifierTests : IDisposable
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusToReturn { get; set; } = HttpStatusCode.OK;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(StatusToReturn);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly ScriptedHandler _handler = new();
    private readonly string? _savedToken = Environment.GetEnvironmentVariable(TelegramNotifier.TokenEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, _savedToken);

    private TelegramNotifier Build(string chatId = "12345")
        => new(new SingleClientFactory(_handler),
            new NotificationOptions { Enabled = true, Provider = "Telegram", ChatId = chatId }.AsMonitor());

    [Fact]
    public async Task Send_PostsToBotApi_WithChatIdAndSeverityIcon()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, "tok-test");
        var notifier = Build();

        await notifier.SendAsync(NotificationSeverity.Critical, "Corsia 2 in QUARANTENA", "dettagli", CancellationToken.None);

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("https://api.telegram.org/bottok-test/sendMessage", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("chat_id=12345", _handler.LastBody);
        Assert.Contains("QUARANTENA", Uri.UnescapeDataString(_handler.LastBody!));
        Assert.Contains("🔴", Uri.UnescapeDataString(_handler.LastBody!)); // 🔴 = Critical
    }

    [Fact]
    public async Task MissingToken_Throws_WithClearMessage()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, null);
        var notifier = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None));
        Assert.Contains(TelegramNotifier.TokenEnvVar, ex.Message);
    }

    [Fact]
    public async Task MissingChatId_Throws()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, "tok-test");
        var notifier = Build(chatId: "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None));
    }

    [Fact]
    public async Task HttpFailure_Throws_SoTheDispatcherLogsIt()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, "tok-test");
        _handler.StatusToReturn = HttpStatusCode.Forbidden;
        var notifier = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }
}
