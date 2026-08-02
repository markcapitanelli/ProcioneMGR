using System.Net;
using System.Text;
using System.Text.Json;
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
        /// <summary>Chiavi per-provider oltre a quella Nvidia storica (i test Fase D le riempiono).</summary>
        public Dictionary<string, string?> Keys { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [AiProviders.Nvidia] = nvidiaKey,
        };

        public Task<string?> GetKeyAsync(string provider, CancellationToken ct = default) => Task.FromResult(GetCachedKey(provider));
        public string? GetCachedKey(string provider) => Keys.TryGetValue(provider, out var k) ? k : null;
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

    // ------------------------------------------------------------------ [Fase D] i tre provider nuovi

    private static IAiKeyStore KeysForAll()
    {
        var store = new FakeKeyStore();
        store.Keys[AiProviders.Gemini] = "AIza-test";
        store.Keys[AiProviders.Groq] = "gsk-test";
        store.Keys[AiProviders.HuggingFace] = "hf-test";
        return store;
    }

    public static TheoryData<string> CompatProviders() => new(AiProviders.Gemini, AiProviders.Groq, AiProviders.HuggingFace);

    private static OpenAiCompatibleLlmClient CompatClient(string provider, ScriptedHandler handler, IAiKeyStore keys, LlmOptions options)
    {
        var factory = new SingleClientFactory(handler);
        var monitor = options.AsMonitor();
        return provider switch
        {
            AiProviders.Gemini => new GeminiLlmClient(factory, monitor, keys, NullLogger<GeminiLlmClient>.Instance),
            AiProviders.Groq => new GroqLlmClient(factory, monitor, keys, NullLogger<GroqLlmClient>.Instance),
            AiProviders.HuggingFace => new HuggingFaceLlmClient(factory, monitor, keys, NullLogger<HuggingFaceLlmClient>.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    [Theory]
    [MemberData(nameof(CompatProviders))]
    public async Task NewProviders_SendToTheirDefaultEndpoint_WithTheirModel(string provider)
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ciao"},"finish_reason":"stop"}]}""");
        var options = new LlmOptions();
        var client = CompatClient(provider, handler, KeysForAll(), options);

        var text = await client.CompleteAsync("sys", "user", CancellationToken.None);

        Assert.Equal("ciao", text);
        Assert.EndsWith("/chat/completions", handler.LastUri!.AbsolutePath);
        var (expectedHost, expectedModel) = provider switch
        {
            AiProviders.Gemini => ("generativelanguage.googleapis.com", options.GeminiModel),
            AiProviders.Groq => ("api.groq.com", options.GroqModel),
            _ => ("router.huggingface.co", options.HuggingFaceModel),
        };
        Assert.Equal(expectedHost, handler.LastUri!.Host);
        Assert.Contains($"\"model\":{JsonSerializer.Serialize(expectedModel)}", handler.LastRequestBody);
        Assert.StartsWith("Bearer ", handler.LastAuthorization);
    }

    [Theory]
    [MemberData(nameof(CompatProviders))]
    public async Task NewProviders_MissingKey_FailsWithTheirEnvVarRemedy(string provider)
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, OkBody);
        var client = CompatClient(provider, handler, new FakeKeyStore(nvidiaKey: null), new LlmOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync("sys", "user", CancellationToken.None));

        Assert.Contains("/admin/ai-supervisor", ex.Message);
        Assert.Contains(AiProviders.EnvVarFor(provider), ex.Message);
        Assert.False(client.IsConfigured);
    }

    [Theory]
    [InlineData("GROQ HTTP 429: {\"error\":{\"message\":\"rate limit\"}}", true, "rate-limit")]
    [InlineData("GEMINI HTTP 400: {}", false, "richiesta non valida")]
    [InlineData("GEMINI HTTP 500: {}", true, "server")]
    [InlineData("HUGGINGFACE HTTP 401: {}", true, "credenziali")]
    [InlineData("HUGGINGFACE HTTP 402: {}", true, "credito API")]
    public void Guard_ClassifiesAnyCompatProvider_BySameTaxonomy(string message, bool retryable, string cause)
    {
        var (r, c) = LlmCallGuard.Classify(new InvalidOperationException(message));
        Assert.Equal(retryable, r);
        Assert.Equal(cause, c);
    }

    [Fact]
    public void EnvVarNames_AreTheDocumentedOnes()
    {
        Assert.Equal("GEMINI_API_KEY", AiProviders.EnvVarFor(AiProviders.Gemini));
        Assert.Equal("GROQ_API_KEY", AiProviders.EnvVarFor(AiProviders.Groq));
        Assert.Equal("HUGGINGFACE_API_KEY", AiProviders.EnvVarFor(AiProviders.HuggingFace));
    }

    // ------------------------------------------------------------------ elenco modelli per chiave

    [Fact]
    public async Task ListModels_SendsGetToModelsEndpoint_AndParsesSortedIds()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK,
            """{"object":"list","data":[{"id":"zeta-9b"},{"id":"alpha-70b"},{"id":"med-8b"}]}""");
        var client = CompatClient(AiProviders.Groq, handler, KeysForAll(), new LlmOptions());

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["alpha-70b", "med-8b", "zeta-9b"], models);
        Assert.Equal("api.groq.com", handler.LastUri!.Host);
        Assert.EndsWith("/models", handler.LastUri!.AbsolutePath);
        Assert.StartsWith("Bearer ", handler.LastAuthorization);
    }

    [Fact]
    public async Task ListModels_HttpError_KeepsTheSpeakingContract()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");
        var client = CompatClient(AiProviders.Gemini, handler, KeysForAll(), new LlmOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(CancellationToken.None));

        Assert.StartsWith("GEMINI HTTP 401:", ex.Message);
        // Il contratto d'errore resta classificabile dal guard con la stessa tassonomia.
        Assert.Equal((true, "credenziali"), LlmCallGuard.Classify(ex));
    }

    [Fact]
    public async Task ListModels_Anthropic_UsesItsOwnDialect()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK,
            """{"data":[{"id":"claude-b","display_name":"B"},{"id":"claude-a","display_name":"A"}],"has_more":false}""");
        var keys = KeysForAll();
        ((FakeKeyStore)keys).Keys[AiProviders.Anthropic] = "sk-ant-test";
        var client = new AnthropicLlmClient(new LlmOptions().AsMonitor(),
            NullLogger<AnthropicLlmClient>.Instance, keys, new SingleClientFactory(handler));

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-a", "claude-b"], models);
        Assert.Equal("api.anthropic.com", handler.LastUri!.Host);
        Assert.Null(handler.LastAuthorization); // x-api-key, non Bearer: il dialetto è il suo
    }

    [Fact]
    public async Task Delegating_WithResolver_RoutesToEveryKnownProvider()
    {
        // Ogni provider risponde col proprio host: si prova che il delegante+resolver instrada
        // davvero su TUTTI i provider noti, non solo sui due storici.
        var handler = new ScriptedHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        var options = new LlmOptions { Provider = AiProviders.Groq };
        var keys = KeysForAll();
        var factory = new SingleClientFactory(handler);
        var monitor = options.AsMonitor();

        var anthropic = new AnthropicLlmClient(monitor, NullLogger<AnthropicLlmClient>.Instance, (FakeKeyStore)keys);
        var nvidia = new NvidiaLlmClient(factory, monitor, keys, NullLogger<NvidiaLlmClient>.Instance);
        var gemini = new GeminiLlmClient(factory, monitor, keys, NullLogger<GeminiLlmClient>.Instance);
        var groq = new GroqLlmClient(factory, monitor, keys, NullLogger<GroqLlmClient>.Instance);
        var hf = new HuggingFaceLlmClient(factory, monitor, keys, NullLogger<HuggingFaceLlmClient>.Instance);
        var resolver = new LlmClientResolver(anthropic, nvidia, gemini, groq, hf);
        var delegating = new DelegatingLlmClient(anthropic, nvidia, monitor, resolver);

        Assert.Equal(options.GroqModel, delegating.Model);
        await delegating.CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal("api.groq.com", handler.LastUri!.Host);

        options.Provider = AiProviders.HuggingFace;
        Assert.Equal(options.HuggingFaceModel, delegating.Model);
        await delegating.CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal("router.huggingface.co", handler.LastUri!.Host);

        options.Provider = AiProviders.Gemini;
        await delegating.CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal("generativelanguage.googleapis.com", handler.LastUri!.Host);

        // Provider ignoto → fallback storico (Anthropic), mai un'eccezione di instradamento.
        options.Provider = "Inventato";
        Assert.Equal(options.Model, delegating.Model);
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
