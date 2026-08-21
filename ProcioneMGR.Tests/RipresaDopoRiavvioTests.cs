using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D1, 2026-08-21] <b>Una corsia che riprende dopo un riavvio deve poter ancora aprire.</b>
///
/// <para>Il motore fotografava le gambe attive in <c>StartAsync</c> e le teneva solo in memoria.
/// <c>EnsureLoadedAsync</c> — la strada di ogni riavvio del processo — restaurava stato, posizioni e
/// piani di esecuzione, ma non quella lista: la corsia ripartiva con <c>IsRunning = true</c>,
/// riceveva candele, marcava a mercato e onorava gli stop delle posizioni già aperte, e
/// <b>non poteva aprire più nulla</b>, per sempre, senza un errore.</para>
///
/// <para>Il costo misurato: il 2026-08-21 cinque corsie Paper risultavano «in esecuzione» col feed
/// all'ultima candela e <b>un solo ordine in tutta la flotta in sette giorni</b>; sulla sola corsia 1
/// (RsiOversold DOT 15m, soglia 20) l'RSI a 14 era sceso sotto soglia <b>57 volte</b> nei 25 giorni
/// dall'avvio. Nessun ordine. Il fatto non compariva da nessuna parte: <c>/trading</c> mostrava le
/// corsie verdi, e il confronto di <c>/ensemble</c> che avrebbe potuto vederlo leggeva «in corsa +
/// zero gambe» come «non determinabile».</para>
///
/// <para>Qui la simulazione del riavvio è la cosa vera: una <b>seconda istanza</b> di
/// <see cref="TradingEngine"/> sullo stesso database, senza che <c>StartAsync</c> venga mai chiamato
/// su di essa — esattamente ciò che accade quando il processo riparte.</para>
/// </summary>
[Collection("Postgres")]
public class RipresaDopoRiavvioTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RipresaDopoRiavvioTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => script(index);
    }

    private sealed class ScriptedStrategyFactory(Func<int, Signal> script) : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new ScriptedStrategy(script);
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
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private IDbContextFactory<ApplicationDbContext>? _dbFactory;
    private EnsembleConfiguration _config = new();

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        if (_dbFactory is not null) return _dbFactory;
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return _dbFactory;
    }

    /// <summary>Una NUOVA istanza di motore sullo stesso database: è la simulazione del riavvio.</summary>
    private async Task<TradingEngine> NuovaIstanzaAsync(Func<int, Signal> script)
    {
        var dbFactory = await DbAsync();
        return new TradingEngine(
            0,
            dbFactory,
            new ScriptedStrategyFactory(script),
            new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(),
            new FakeEnsembleManager(_config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory(),
            NullLogger<TradingEngine>.Instance);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 100m,
    };

    private void ConfiguraCorsia(params EnsembleStrategy[] gambe) => _config = new EnsembleConfiguration
    {
        ExchangeName = "Binance",
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TotalCapital = 10_000m,
        Strategies = [.. gambe],
    };

    private static EnsembleStrategy Gamba(string id, bool attiva = true) => new()
    {
        StrategyId = id, StrategyName = "Scripted", DisplayName = $"Gamba {id}", IsActive = attiva,
    };

    // ------------------------------------------------------------------ il difetto, e la correzione

    [Fact]
    public async Task DopoIlRiavvio_LaCorsiaAPRE_ANCORA()
    {
        ConfiguraCorsia(Gamba("s1"));

        // Sessione 1: avviata, scaldata, nessun segnale. Poi il processo "muore".
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        for (var i = 0; i < 5; i++) await primo.ProcessCandleAsync(Candle(i, 100m));
        Assert.Empty(await primo.GetOpenPositionsAsync());

        // Sessione 2: NUOVA istanza, nessuno StartAsync. Il DB dice IsRunning = true.
        var dopoRiavvio = await NuovaIstanzaAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        var stato = await dopoRiavvio.GetStatusAsync();
        Assert.True(stato.IsRunning);
        Assert.Single(stato.RunningStrategyIds);   // <- prima era VUOTA: il motore era inerte e non lo diceva

        // Cinque candele nuove (il buffer riparte vuoto), la quinta apre.
        for (var i = 5; i < 10; i++) await dopoRiavvio.ProcessCandleAsync(Candle(i, 100m));

        Assert.Single(await dopoRiavvio.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task DopoIlRiavvio_LeGambeSonoQUELLE_DELLA_SESSIONE_NonQuelleDellaConfigurazioneRiscritta()
    {
        // È la ragione per cui la fotografia si CONGELA invece di rileggere la configurazione viva:
        // l'auto-apply della flotta, o un semplice Salva da /ensemble, riscrivono le gambe mentre la
        // corsia opera. Riprendere di lì farebbe operare gambe che nessun backtest ha validato per
        // questa sessione — «si valida una strategia e se ne opera un'altra».
        ConfiguraCorsia(Gamba("originale"));
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);

        ConfiguraCorsia(Gamba("riscritta-dopo-lavvio"));   // la configurazione cambia sotto la sessione viva

        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Hold);
        var stato = await dopoRiavvio.GetStatusAsync();

        Assert.Equal(["originale"], stato.RunningStrategyIds);
    }

    [Fact]
    public async Task DopoIlRiavvio_SoloLeGambeATTIVE()
    {
        ConfiguraCorsia(Gamba("accesa"), Gamba("spenta", attiva: false));
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);

        var stato = await (await NuovaIstanzaAsync(_ => Signal.Hold)).GetStatusAsync();

        Assert.Equal(["accesa"], stato.RunningStrategyIds);
    }

    [Fact]
    public async Task SessioneSenzaFotografia_RipiegaSullaConfigurazione_E_LO_DICHIARA()
    {
        // Le righe scritte prima del 2026-08-21 non hanno la fotografia. Una corsia viva e muta è
        // peggio di una corsia viva e dichiaratamente approssimata: si riprende dalla configurazione
        // e lo si scrive in audit, perché quella ripresa PUÒ essere sbagliata.
        ConfiguraCorsia(Gamba("s1"));
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);

        var dbFactory = await DbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.TradingEngineStates.Where(s => s.LaneId == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveStrategiesJson, (string?)null));
        }

        var stato = await (await NuovaIstanzaAsync(_ => Signal.Hold)).GetStatusAsync();
        Assert.Single(stato.RunningStrategyIds);   // ripresa: la corsia non resta muta

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.True(await db.TradingAuditLogs.AnyAsync(a => a.Action == "ActiveLegsRestoredFromConfig"),
                "la ripresa approssimata non è stata dichiarata in audit");
        }
    }

    [Fact]
    public async Task FotografiaIlleggibile_NonFaEsplodereLaRipresa()
    {
        ConfiguraCorsia(Gamba("s1"));
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);

        var dbFactory = await DbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.TradingEngineStates.Where(s => s.LaneId == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveStrategiesJson, "{non-json"));
        }

        var stato = await (await NuovaIstanzaAsync(_ => Signal.Hold)).GetStatusAsync();
        Assert.Single(stato.RunningStrategyIds);   // ripiego, non eccezione
    }

    [Fact]
    public async Task CorsiaFERMA_NonRipristinaNulla()
    {
        // Il ripristino vale solo per una sessione viva: su una corsia ferma «zero gambe» è la verità,
        // e inventarne sarebbe la bugia opposta.
        ConfiguraCorsia(Gamba("s1"));
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        await primo.StopAsync();

        var stato = await (await NuovaIstanzaAsync(_ => Signal.Hold)).GetStatusAsync();

        Assert.False(stato.IsRunning);
        Assert.Empty(stato.RunningStrategyIds);
    }

    // ------------------------------------------------------ [D2] il fail-open sulla strada peggiore

    /// <summary>
    /// [D2] <b>Una chiusura Testnet/Live senza credenziali non deve finalizzarsi.</b>
    ///
    /// <para>Il ramo che piazza l'ordine reale era guardato da <c>Mode != Paper &amp;&amp; creds is
    /// TradingCredentials</c>. Con le credenziali <b>null</b> la condizione era falsa e il codice
    /// <i>proseguiva</i>: rimuoveva la posizione locale, scriveva il TradeRecord e registrava
    /// ClosePosition — <b>senza aver chiuso nulla sull'exchange</b>. L'esposizione reale restava
    /// aperta e la piattaforma ne perdeva traccia: fail-OPEN sull'unica strada che non può esserlo.</para>
    ///
    /// <para>Lo stato ci si raggiungeva dal riavvio: <c>_creds</c> lo valorizzava solo
    /// <c>StartAsync</c>, e la ripresa non lo rifaceva (stessa radice di D1).</para>
    /// </summary>
    [Fact]
    public async Task ChiusuraTestnetSenzaCredenziali_NON_SiFinalizza_ELaPosizioneRESTA()
    {
        var dbFactory = await DbAsync();
        var apertura = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Una corsia Testnet dichiarata VIVA dal database, con una posizione reale aperta e nessuna
        // credenziale disponibile in questo processo: esattamente lo stato dopo un riavvio.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.TradingEngineStates.Add(new TradingEngineState
            {
                LaneId = 0,
                Mode = TradingMode.Testnet,
                MarketType = MarketType.Spot,
                Leverage = 1,
                IsRunning = true,
                ExchangeName = "Binance",
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                TotalCapital = 10_000m,
                AvailableCapital = 9_000m,
                PeakEquity = 10_000m,
                DailyAnchorUtc = apertura,
                StartedAtUtc = apertura,
                UpdatedAtUtc = apertura,
                ActiveStrategiesJson = System.Text.Json.JsonSerializer.Serialize(new List<EnsembleStrategy> { Gamba("s1") }),
            });
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0,
                PositionId = "pos-reale",
                StrategyId = "s1",
                Symbol = "BTC/USDT",
                Side = OrderSide.Buy,
                EntryPrice = 100m,
                CurrentPrice = 100m,
                Quantity = 10m,
                StopLoss = 95m,
                OpenedAtUtc = apertura,
                OpenedInMode = TradingMode.Testnet,
                BestPriceSinceEntry = 100m,
            });
            await db.SaveChangesAsync();
        }

        ConfiguraCorsia(Gamba("s1"));
        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Hold);

        // Una candela che buca lo stop: l'uscita protettiva scatta e prova a chiudere.
        await dopoRiavvio.ProcessCandleAsync(Candle(1, 90m));

        // La posizione REALE resta: chiuderla solo qui ne farebbe perdere traccia.
        Assert.Single(await dopoRiavvio.GetOpenPositionsAsync());

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.True(await db.TradingAuditLogs.AnyAsync(a => a.Action == "CloseRefusedNoCredentials"),
                "il rifiuto non è stato dichiarato in audit");
            Assert.False(await db.TradeRecords.AnyAsync(t => t.PositionId == "pos-reale"),
                "è stato scritto un TradeRecord per una chiusura mai avvenuta sull'exchange");
            Assert.True(await db.OpenPositions.AnyAsync(p => p.PositionId == "pos-reale"),
                "la riga della posizione reale è stata cancellata");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
