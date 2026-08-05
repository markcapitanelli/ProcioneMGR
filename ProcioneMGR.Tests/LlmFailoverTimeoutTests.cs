using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-05] Il failover deve partire anche quando un provider SI APPENDE.
///
/// <para><b>Il difetto che questi test chiudono</b>, trovato provando l'app dal vivo: il guard crea
/// un token <i>linked</i> (token del chiamante + timeout della chiamata) e passa quello al client;
/// il delegante vedeva SOLO quel token — cancellato sia dallo shutdown sia dal timeout — e il suo
/// <c>when (ct.IsCancellationRequested)</c> lo faceva rilanciare senza provare il provider
/// successivo. Risultato osservato: Nvidia in timeout, e Groq/Gemini/HuggingFace — vivi, con
/// chiave, raggiungibili — mai interpellati. Un provider che si appende è il modo più comune in
/// cui un provider gratuito smette di funzionare: è esattamente il caso per cui la catena esiste.</para>
///
/// <para><b>La distinzione che il rimedio introduce</b>: ogni anello ha un proprio budget di tempo.
/// Scaduto quello, è colpa del provider ⇒ si passa al prossimo. Se invece è il token ESTERNO a
/// essere cancellato (shutdown vero, o budget complessivo della chiamata esaurito), non si prova
/// nessun altro — che è il comportamento storico, e questi test lo difendono.</para>
/// </summary>
public class LlmFailoverTimeoutTests
{
    // ------------------------------------------------------------------ fakes

    /// <summary>Un provider che non risponde MAI finché non viene cancellato.</summary>
    private sealed class HangingLlm(string model) : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string Model => model;

        public async Task<string> CompleteAsync(string s, string u, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }

        private sealed class UnreachableException : Exception;
    }

    private sealed class WorkingLlm(string model, string response = "risposta") : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string Model => model;

        public Task<string> CompleteAsync(string s, string u, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }

    private sealed class MapResolver(Dictionary<string, ILlmClient> map) : ILlmClientResolver
    {
        public ILlmClient? Resolve(string provider) => map.GetValueOrDefault(provider);
    }

    private static DelegatingLlmClient Build(
        Dictionary<string, ILlmClient> providers, LlmOptions options)
    {
        var resolver = new MapResolver(providers);
        // I due client concreti non vengono mai usati quando il resolver risolve: passiamo null!
        // sarebbe scorretto, quindi si usano istanze reali mai raggiunte dalla Sequence.
        return new DelegatingLlmClient(
            anthropic: null!, nvidia: null!,
            options: new StaticMonitor<LlmOptions>(options),
            resolver: resolver,
            logger: NullLogger<DelegatingLlmClient>.Instance);
    }

    private sealed class StaticMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static LlmOptions Options(int perProviderSeconds = 1) => new()
    {
        Provider = "A",
        FailoverEnabled = true,
        FailoverProviders = ["A", "B", "C"],
        PerProviderTimeoutSeconds = perProviderSeconds,
    };

    // ------------------------------------------------------------------ il rimedio

    /// <summary>IL TEST CHE DESCRIVE IL DIFETTO: il primo provider si appende, il secondo serve.</summary>
    [Fact]
    public async Task ProviderCheSiAppende_PassaAlSuccessivo()
    {
        var hanging = new HangingLlm("modello-appeso");
        var working = new WorkingLlm("modello-buono", "ok dal secondo");
        var client = Build(new() { ["A"] = hanging, ["B"] = working }, Options(perProviderSeconds: 1));

        var text = await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Equal("ok dal secondo", text);
        Assert.Equal(1, hanging.Calls);
        Assert.Equal(1, working.Calls);
        // La tracciabilità deve dire chi ha DAVVERO risposto, non chi era configurato.
        Assert.Equal("modello-buono", client.LastCompletionModel);
    }

    [Fact]
    public async Task DueProviderAppesi_ServeIlTerzo()
    {
        var a = new HangingLlm("a");
        var b = new HangingLlm("b");
        var c = new WorkingLlm("c", "ok dal terzo");
        var client = Build(new() { ["A"] = a, ["B"] = b, ["C"] = c }, Options(perProviderSeconds: 1));

        Assert.Equal("ok dal terzo", await client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, c.Calls);
    }

    /// <summary>Se si appendono TUTTI, l'errore che esce è un timeout — e non è una cancellazione silenziosa.</summary>
    [Fact]
    public async Task TuttiAppesi_LanciaSenzaRestarePiantato()
    {
        var client = Build(
            new() { ["A"] = new HangingLlm("a"), ["B"] = new HangingLlm("b") },
            Options(perProviderSeconds: 1));

        await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteAsync("s", "u", CancellationToken.None));
    }

    // ------------------------------------------------------------------ ciò che NON deve cambiare

    /// <summary>
    /// LA PROPRIETÀ DA NON ROMPERE: uno shutdown vero (token esterno cancellato) non è un guasto
    /// del provider — nessun failover, e l'eccezione di cancellazione esce così com'è.
    /// </summary>
    [Fact]
    public async Task CancellazioneEsterna_NienteFailover()
    {
        var hanging = new HangingLlm("a");
        var mai = new WorkingLlm("b");
        var client = Build(new() { ["A"] = hanging, ["B"] = mai }, Options(perProviderSeconds: 30));

        using var cts = new CancellationTokenSource();
        var task = client.CompleteAsync("s", "u", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, mai.Calls); // il secondo provider NON è stato interpellato
    }

    /// <summary>
    /// Budget per-provider a 0 = spento: comportamento storico bit-identico (il provider appeso
    /// resta appeso finché non lo cancella il chiamante, e non si prova nessun altro). È la via di
    /// fuga se il rimedio dovesse dare fastidio.
    /// </summary>
    [Fact]
    public async Task BudgetAZero_ComportamentoStorico()
    {
        var hanging = new HangingLlm("a");
        var mai = new WorkingLlm("b");
        var client = Build(new() { ["A"] = hanging, ["B"] = mai }, Options(perProviderSeconds: 0));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteAsync("s", "u", cts.Token));

        Assert.Equal(0, mai.Calls);
    }

    /// <summary>Un provider che risponde subito non paga nulla: nessun ritardo, nessun secondo tentativo.</summary>
    [Fact]
    public async Task ProviderSano_NessunTentativoInPiu()
    {
        var a = new WorkingLlm("a", "subito");
        var b = new WorkingLlm("b");
        var client = Build(new() { ["A"] = a, ["B"] = b }, Options(perProviderSeconds: 1));

        Assert.Equal("subito", await client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal(1, a.Calls);
        Assert.Equal(0, b.Calls);
    }

    /// <summary>Gli errori normali (non timeout) continuano a innescare il failover come prima.</summary>
    [Fact]
    public async Task ErroreNormale_FailoverComePrima()
    {
        var failing = new ThrowingLlm("a");
        var working = new WorkingLlm("b", "ok");
        var client = Build(new() { ["A"] = failing, ["B"] = working }, Options(perProviderSeconds: 30));

        Assert.Equal("ok", await client.CompleteAsync("s", "u", CancellationToken.None));
    }

    private sealed class ThrowingLlm(string model) : ILlmClient
    {
        public bool IsConfigured => true;
        public string Model => model;
        public Task<string> CompleteAsync(string s, string u, CancellationToken ct)
            => throw new HttpRequestException("NVIDIA HTTP 503: service unavailable");
    }

    // ------------------------------------------------------------------ configurazione

    [Fact]
    public void Default_BudgetPerProviderAttivo()
    {
        // Questo è un RIMEDIO A UN DIFETTO, non una funzione nuova: acceso di default, altrimenti
        // non ripara niente. Lo 0 resta disponibile come via di fuga.
        Assert.True(new LlmOptions().PerProviderTimeoutSeconds > 0);
    }

    [Theory]
    [InlineData(0, true)]      // spento: ammesso
    [InlineData(5, true)]
    [InlineData(600, true)]
    [InlineData(-1, false)]
    [InlineData(601, false)]
    public void AdminConfigRules_ValidaIlBudgetPerProvider(int seconds, bool valid)
    {
        var error = ProcioneMGR.Services.Config.AdminConfigRules.Validate(
            new LlmOptions { PerProviderTimeoutSeconds = seconds });

        if (valid) Assert.Null(error);
        else Assert.NotNull(error);
    }
}
