using System.Net;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del provider Telegram (Fase 4): payload corretto (chat_id + testo con icona di gravità),
/// token dall'env con fallback sul file della plancia (mai in config — la risoluzione è
/// <see cref="TelegramNotifier.ResolveToken"/>, pura), errori HTTP che diventano eccezioni (che il
/// dispatcher contiene). Handler HTTP scriptato e lettore-file iniettato: nessuna chiamata reale e
/// nessuna lettura del file VERO, che sulla macchina del proprietario esiste col token dentro.
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

    private TelegramNotifier Build(string chatId = "12345", string? tokenFile = null)
        => new(new SingleClientFactory(_handler),
            new NotificationOptions { Enabled = true, Provider = "Telegram", ChatId = chatId }.AsMonitor(),
            tokenFileReader: () => tokenFile);

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
    public async Task MissingToken_EnvEFile_Throws_NominandoEntrambeLeFonti()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, null);
        var notifier = Build(tokenFile: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None));
        // Chi legge l'errore deve sapere DOVE mettere il token: entrambe le fonti, per nome.
        Assert.Contains(TelegramNotifier.TokenEnvVar, ex.Message);
        Assert.Contains("telegram.token", ex.Message);
    }

    [Fact]
    public async Task EnvAssente_IlFileDellaPlanciaBasta()
    {
        // [2026-08-28] Il canale non deve dipendere dalla catena che AVVIA il guscio: un processo
        // partito senza la variabile resta muto per giorni con la piattaforma sana. Il file
        // ~/.procione/telegram.token è la stessa rete di sicurezza che la plancia ha da agosto.
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, null);
        var notifier = Build(tokenFile: "tok-dal-file\n");

        await notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None);

        Assert.Equal("https://api.telegram.org/bottok-dal-file/sendMessage",
            _handler.LastRequest!.RequestUri!.ToString()); // e il newline del file NON finisce nell'URL
    }

    [Fact]
    public async Task EnvPresente_VinceSulFile()
    {
        Environment.SetEnvironmentVariable(TelegramNotifier.TokenEnvVar, "tok-env");
        var notifier = Build(tokenFile: "tok-dal-file");

        await notifier.SendAsync(NotificationSeverity.Info, "T", "B", CancellationToken.None);

        Assert.Equal("https://api.telegram.org/bottok-env/sendMessage",
            _handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  ", "\t", null)]                  // il bianco è assenza, da entrambe le fonti
    [InlineData("tok-env", "tok-file", "tok-env")]  // l'ambiente è il canale dichiarato: vince
    [InlineData(null, " tok-file ", "tok-file")]    // il file si usa rifilato
    public void ResolveToken_PuroEDeterministico(string? env, string? file, string? atteso)
        => Assert.Equal(atteso, TelegramNotifier.ResolveToken(env, file));

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
