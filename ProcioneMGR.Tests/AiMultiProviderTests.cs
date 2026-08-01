using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Multi-provider AI 2026-08] Il mattone NVIDIA e l'instradamento per provider: client
/// OpenAI-compatible (parse, errori parlanti, chiave assente), delegante che segue le opzioni a
/// caldo, e store delle chiavi cifrato con fallback env. Il contratto che difendono: cambiare
/// provider è un dato di configurazione, mai un riavvio né una modifica ai consumatori.
/// </summary>
public class AiMultiProviderTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public string? LastAuthorization { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeKeyStore(string? nvidiaKey = "nvapi-test") : IAiKeyStore
    {
        public Task<string?> GetKeyAsync(string provider, CancellationToken ct = default) => Task.FromResult(GetCachedKey(provider));
        public string? GetCachedKey(string provider) => provider == AiProviders.Nvidia ? nvidiaKey : null;
        public AiKeySource GetCachedSource(string provider) => GetCachedKey(provider) is null ? AiKeySource.None : AiKeySource.Database;
        public Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveKeyAsync(string provider, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static NvidiaLlmClient Nvidia(ScriptedHandler handler, IAiKeyStore? keys = null, LlmOptions? options = null) =>
        new(new SingleClientFactory(handler),
            (options ?? new LlmOptions()).AsMonitor(),
            keys ?? new FakeKeyStore(),
            NullLogger<NvidiaLlmClient>.Instance);

    private const string OkBody = """
        {"choices":[{"message":{"role":"assistant","content":"OK meta/llama-3.3-70b-instruct"},"finish_reason":"stop"}]}
        """;

    // ------------------------------------------------------------------ NvidiaLlmClient

    [Fact]
    public async Task Nvidia_SendsOpenAiShape_AndParsesContent()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, OkBody);

        var text = await Nvidia(handler).CompleteAsync("sys", "user", CancellationToken.None);

        Assert.Equal("OK meta/llama-3.3-70b-instruct", text);
        Assert.Equal("Bearer nvapi-test", handler.LastAuthorization);
        Assert.EndsWith("/chat/completions", handler.LastUri!.AbsolutePath);
        Assert.Contains("\"model\":\"meta/llama-3.3-70b-instruct\"", handler.LastRequestBody);
        Assert.Contains("\"role\":\"system\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task Nvidia_HttpError_SurfacesStatusAndBody()
    {
        // Un 401/402/429 di NVIDIA spiega la causa nel JSON: l'errore deve portarla al pannello,
        // mai un "HTTP 4xx" muto — è la lezione del credito Anthropic esaurito in silenzio.
        var handler = new ScriptedHandler(HttpStatusCode.PaymentRequired, """{"detail":"Insufficient credits"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Nvidia(handler).CompleteAsync("sys", "user", CancellationToken.None));

        Assert.Contains("402", ex.Message);
        Assert.Contains("Insufficient credits", ex.Message);
    }

    [Fact]
    public async Task Nvidia_MissingKey_FailsWithTheRemedy()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Nvidia(new ScriptedHandler(HttpStatusCode.OK, OkBody), new FakeKeyStore(nvidiaKey: null))
                .CompleteAsync("sys", "user", CancellationToken.None));

        Assert.Contains("/admin/ai-supervisor", ex.Message);
        Assert.Contains("NVIDIA_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Nvidia_EmptyContent_IsAnExplainedError_NotAnEmptyAdvisory()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":""},"finish_reason":"length"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Nvidia(handler).CompleteAsync("sys", "user", CancellationToken.None));

        Assert.Contains("MaxTokens", ex.Message);
    }

    // ------------------------------------------------------------------ classificazione NVIDIA nel guard

    [Theory]
    [InlineData("NVIDIA HTTP 503: {\"error\":\"limit reached\"}", true, "server")]
    [InlineData("NVIDIA HTTP 429: {}", true, "rate-limit")]
    [InlineData("NVIDIA HTTP 402: {}", true, "credito API")]
    [InlineData("NVIDIA HTTP 401: {}", true, "credenziali")]
    [InlineData("NVIDIA HTTP 400: {}", false, "richiesta non valida")]
    public void Guard_ClassifiesNvidiaErrors_BySameTaxonomy(string message, bool retryable, string cause)
    {
        // Il 503 del free tier ("Worker local total request limit reached") è stato visto dal vivo
        // il 2026-08-01: prima di questa classificazione cadeva in "inatteso" (non ritentabile) e
        // il run finiva in errore invece di essere rinviato.
        var (r, c) = LlmCallGuard.Classify(new InvalidOperationException(message));
        Assert.Equal(retryable, r);
        Assert.Equal(cause, c);
    }

    // ------------------------------------------------------------------ DelegatingLlmClient

    [Fact]
    public async Task Delegating_RoutesByProvider_HotReload()
    {
        var options = new LlmOptions { Provider = AiProviders.Nvidia, NvidiaModel = "meta/llama-3.3-70b-instruct" };
        var handler = new ScriptedHandler(HttpStatusCode.OK, OkBody);
        var anthropic = new AnthropicLlmClient(options.AsMonitor(), NullLogger<AnthropicLlmClient>.Instance, new FakeKeyStore());
        var delegating = new DelegatingLlmClient(anthropic, Nvidia(handler, options: options), options.AsMonitor());

        // Provider=Nvidia: modello e chiamata vanno al client NVIDIA.
        Assert.Equal("meta/llama-3.3-70b-instruct", delegating.Model);
        Assert.Equal("OK meta/llama-3.3-70b-instruct", await delegating.CompleteAsync("s", "u", CancellationToken.None));

        // Cambio a caldo: stesso oggetto, provider Anthropic → modello Anthropic, e IsConfigured
        // riflette il fatto che il FakeKeyStore non ha una chiave Anthropic.
        options.Provider = AiProviders.Anthropic;
        Assert.Equal(options.Model, delegating.Model);
        Assert.False(delegating.IsConfigured);
    }

    // ------------------------------------------------------------------ AiKeyStore (Postgres)

    [Collection("Postgres")]
    public sealed class AiKeyStoreTests : IAsyncDisposable
    {
        private readonly string _connString;
        private ServiceProvider? _provider;

        public AiKeyStoreTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

        private sealed class PassthroughEncryption : IEncryptionService
        {
            public string Encrypt(string plaintext) => plaintext;
            public string Decrypt(string ciphertext) => ciphertext;
        }

        private async Task<AiKeyStore> BuildAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEncryptionService, PassthroughEncryption>();
            services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
            _provider = services.BuildServiceProvider();
            var dbf = _provider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await dbf.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            return new AiKeyStore(dbf, NullLogger<AiKeyStore>.Instance);
        }

        [Fact]
        public async Task SetGetRemove_Roundtrip_WithSourceReporting()
        {
            var store = await BuildAsync();

            Assert.Null(await store.GetKeyAsync(AiProviders.Nvidia));
            Assert.Equal(AiKeySource.None, store.GetCachedSource(AiProviders.Nvidia));

            await store.SetKeyAsync(AiProviders.Nvidia, "nvapi-secret");
            Assert.Equal("nvapi-secret", await store.GetKeyAsync(AiProviders.Nvidia));
            Assert.Equal(AiKeySource.Database, store.GetCachedSource(AiProviders.Nvidia));

            // Sostituzione (upsert sulla stessa riga, indice unico sul provider).
            await store.SetKeyAsync(AiProviders.Nvidia, "nvapi-nuova");
            Assert.Equal("nvapi-nuova", await store.GetKeyAsync(AiProviders.Nvidia));

            await store.RemoveKeyAsync(AiProviders.Nvidia);
            Assert.Null(await store.GetKeyAsync(AiProviders.Nvidia));
        }

        [Fact]
        public async Task DatabaseKey_SurvivesProcessRestart_ViaReload()
        {
            // Il punto della persistenza: un secondo store (= processo nuovo) rilegge la chiave dal DB.
            var first = await BuildAsync();
            await first.SetKeyAsync(AiProviders.Anthropic, "sk-ant-xyz");

            var second = new AiKeyStore(
                _provider!.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>(),
                NullLogger<AiKeyStore>.Instance);
            Assert.Equal("sk-ant-xyz", await second.GetKeyAsync(AiProviders.Anthropic));
        }

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null) await _provider.DisposeAsync();
        }
    }
}
