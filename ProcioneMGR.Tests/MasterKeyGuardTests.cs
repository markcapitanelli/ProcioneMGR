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
/// Guard della master key (bundle sicurezza audit 2026-07): con la chiave placeholder del
/// template (committata su git) i segreti "cifrati" sono decifrabili da chiunque legga il
/// repository. Due linee di difesa, entrambe testate qui:
///   1. <see cref="AesGcmEncryptionService.IsDefaultDevKey"/> riconosce il placeholder
///      confrontando lo SHA-256 (il test con la stringa vera valida anche l'hash costante);
///   2. <see cref="TradingEngine.StartAsync"/> rifiuta il LIVE se la chiave è il placeholder
///      (Paper/Testnet restano permessi: comodi in sviluppo, nessun denaro vero in gioco).
/// </summary>
public sealed class MasterKeyDetectionTests
{
    /// <summary>Stessa stringa committata in appsettings.json.example ("Security:MasterKey").</summary>
    private const string DevPlaceholder = "__CHANGE_ME_BASE64_32_BYTES__";

    private static AesGcmEncryptionService Build(string masterKey)
    {
        // La costruzione legge PRIMA la env PROCIONE_MGR_MASTER_KEY: va neutralizzata per la
        // durata del test, altrimenti su una macchina configurata il config in-memory è ignorato.
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

    [Fact]
    public void PlaceholderKey_IsDetected()
    {
        // Valida anche la costante SHA-256 hard-coded nel servizio: se qualcuno cambia il
        // placeholder nel template senza aggiornare l'hash, questo test fallisce.
        var svc = Build(DevPlaceholder);

        Assert.True(svc.IsDefaultDevKey);
    }

    [Fact]
    public void RealBase64Key_IsNotFlagged()
    {
        var realKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var svc = Build(realKey);

        Assert.False(svc.IsDefaultDevKey);

        // Sanity: il servizio resta pienamente funzionante (roundtrip autenticato).
        var roundtrip = svc.Decrypt(svc.Encrypt("api-secret-di-prova"));
        Assert.Equal("api-secret-di-prova", roundtrip);
    }

    [Fact]
    public void ArbitraryPassphrase_IsNotFlagged()
    {
        // Una passphrase qualunque (derivata via SHA-256) non è il placeholder: nessun falso positivo.
        var svc = Build("una-passphrase-scelta-dall-utente");

        Assert.False(svc.IsDefaultDevKey);
    }
}

/// <summary>
/// Gate LIVE del motore: vedi <see cref="MasterKeyDetectionTests"/> per il razionale.
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineMasterKeyGateTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineMasterKeyGateTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes (stesso pattern di TradingEngineSizingTests) ------------------------------------

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

    private sealed class ThrowingExchangeClientFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
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

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeMasterKeyStatus(bool isDefault) : IMasterKeyStatus
    {
        public bool IsDefaultDevKey => isDefault;
    }

    // --- Setup ---------------------------------------------------------------------------------

    private async Task<TradingEngine> BuildAsync(bool devKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            IsFutures = false, Leverage = 1,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Hold", DisplayName = "Hold", IsActive = true }],
        };

        return new TradingEngine(
            0, dbFactory, new HoldStrategyFactory(), new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            masterKeyStatus: new FakeMasterKeyStatus(devKey));
    }

    // --- Test ----------------------------------------------------------------------------------

    [Fact]
    public async Task Live_WithPlaceholderKey_IsBlockedBeforeAnythingElse()
    {
        var engine = await BuildAsync(devKey: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Live));

        Assert.Contains("LIVE bloccato", ex.Message);
        Assert.Contains("placeholder", ex.Message);
        var status = await engine.GetStatusAsync();
        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task Live_WithRealKey_PassesTheGate()
    {
        // Con chiave reale il gate NON scatta: il Live prosegue e si ferma DOPO, sulla mancanza
        // di credenziali (messaggio diverso) — prova che a bloccare prima era solo la master key.
        var engine = await BuildAsync(devKey: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Live));

        Assert.DoesNotContain("LIVE bloccato", ex.Message);
        Assert.Contains("credenziale", ex.Message);
    }

    [Fact]
    public async Task Paper_WithPlaceholderKey_StartsNormally()
    {
        // Il guard riguarda SOLO i soldi veri: il Paper con chiave placeholder resta permesso.
        var engine = await BuildAsync(devKey: true);

        await engine.StartAsync(TradingMode.Paper);

        var status = await engine.GetStatusAsync();
        Assert.True(status.IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
