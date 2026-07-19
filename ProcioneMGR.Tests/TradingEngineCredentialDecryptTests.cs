using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Bug B2, lato motore: l'avvio Testnet/Live con credenziali cifrate da una master key DIVERSA
/// deve fallire con un <see cref="InvalidOperationException"/> che spiega il rimedio
/// (reinserire in /settings/exchanges), MAI con una AuthenticationTagMismatchException grezza —
/// e se accanto alla riga indecifrabile ce n'è una decifrabile (credenziali reinserite), l'avvio
/// deve riuscire usando quella. Coperto anche il fallback senza reader (vecchi harness), dove
/// l'errore del converter EF va tradotto nello stesso messaggio.
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineCredentialDecryptTests : IAsyncDisposable
{
    private readonly string _connString;
    private readonly List<ServiceProvider> _providers = [];

    public TradingEngineCredentialDecryptTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes (stesso pattern di TradingEngineMasterKeyGateTests) -----------------------------

    private sealed class HoldStrategy : IStrategy
    {
        public string Name => "Hold";
        public string DisplayName => "Hold";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct) => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => Signal.Hold;
    }

    private sealed class HoldStrategyFactory : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new HoldStrategy();
    }

    private sealed class FakeEnsembleManager(EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Client spot minimo per un Avvio Testnet riuscito: solo filtri simbolo + ordini aperti vuoti.</summary>
    private sealed class FiltersOnlySpotClient : IExchangeClient
    {
        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => Task.FromResult(new List<OpenOrder>());
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
    }

    private sealed class FakeExchangeClientFactory(IExchangeClient client) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => client;
        public IExchangeClient Create(string exchangeName) => client;
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    // --- Setup ---------------------------------------------------------------------------------

    private static AesGcmEncryptionService BuildAes(string masterKey)
    {
        var saved = Environment.GetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY");
        Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", null);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:MasterKey"] = masterKey })
                .Build();
            return new AesGcmEncryptionService(config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", saved);
        }
    }

    private static string RandomKey() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <param name="contextEncryption">IEncryptionService del DbContext (usato solo dal converter EF).</param>
    /// <param name="isolateEfModel">true per il test del fallback: forza la RICOSTRUZIONE del model EF
    /// così il converter cattura DAVVERO <paramref name="contextEncryption"/> — il model è cached per
    /// internal service provider e altrimenti potrebbe arrivare da un'altra classe di test
    /// (con la sua PassthroughEncryption), rendendo l'esito dipendente dall'ordine della suite.</param>
    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync(
        IEncryptionService contextEncryption, bool isolateEfModel = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(contextEncryption);
        services.AddDbContextFactory<ApplicationDbContext>(o =>
        {
            o.UseNpgsql(_connString);
            if (isolateEfModel) o.EnableServiceProviderCaching(false);
        });
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
            await db.SaveChangesAsync();
        }
        return dbFactory;
    }

    private static async Task SeedRawAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string label, string apiKeyStored, string apiSecretStored)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""ExchangeCredentials""
                (""UserId"", ""ExchangeName"", ""Label"", ""ApiKey"", ""ApiSecret"", ""Passphrase"", ""IsTestnet"", ""CreatedAt"")
            VALUES ('u1', 'Binance', {label}, {apiKeyStored}, {apiSecretStored}, NULL, TRUE, {DateTime.UtcNow})");
    }

    private static TradingEngine BuildEngine(IDbContextFactory<ApplicationDbContext> dbFactory,
        IExchangeCredentialReader? reader)
    {
        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            IsFutures = false, Leverage = 1,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Hold", DisplayName = "Hold", IsActive = true }],
        };

        return new TradingEngine(
            0, dbFactory, new HoldStrategyFactory(), new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(new FiltersOnlySpotClient()), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            credentialReader: reader);
    }

    // --- Test ----------------------------------------------------------------------------------

    [Fact]
    public async Task StartTestnet_UndecryptableCredential_FailsWithClearRemedy_NotRawCryptoException()
    {
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());
        var dbFactory = await BuildDbAsync(new PassthroughEncryption());
        await SeedRawAsync(dbFactory, "Vecchia", foreignAes.Encrypt("k"), foreignAes.Encrypt("s"));

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var engine = BuildEngine(dbFactory, reader);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Testnet));

        Assert.Contains("NON decifrabile", ex.Message);
        Assert.Contains("/settings/exchanges", ex.Message);
        Assert.Contains("'Vecchia'", ex.Message);
        var status = await engine.GetStatusAsync();
        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task StartTestnet_DecryptableRowNextToTheOldOne_StartsUsingTheGoodRow()
    {
        // Il rimedio promesso dal badge ("reinserire le credenziali") deve bastare anche se la
        // vecchia riga non viene eliminata: l'avvio preferisce la riga decifrabile.
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());
        var dbFactory = await BuildDbAsync(new PassthroughEncryption());
        await SeedRawAsync(dbFactory, "Vecchia", foreignAes.Encrypt("old-k"), foreignAes.Encrypt("old-s"));
        await SeedRawAsync(dbFactory, "Nuova", currentAes.Encrypt("new-k"), currentAes.Encrypt("new-s"));

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var engine = BuildEngine(dbFactory, reader);

        await engine.StartAsync(TradingMode.Testnet);

        var status = await engine.GetStatusAsync();
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task StartTestnet_LegacyPathWithoutReader_TranslatesConverterFailureIntoTheSameClearError()
    {
        // Vecchi harness costruiscono il motore SENZA reader: lì decifra il converter EF dentro
        // la materializzazione. Il fallimento crypto deve comunque uscire tradotto, mai grezzo.
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());
        var dbFactory = await BuildDbAsync(currentAes, isolateEfModel: true);
        await SeedRawAsync(dbFactory, "Vecchia", foreignAes.Encrypt("k"), foreignAes.Encrypt("s"));

        var engine = BuildEngine(dbFactory, reader: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Testnet));

        Assert.Contains("NON decifrabile", ex.Message);
        Assert.Contains("/settings/exchanges", ex.Message);
        var status = await engine.GetStatusAsync();
        Assert.False(status.IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers) await p.DisposeAsync();
    }
}
