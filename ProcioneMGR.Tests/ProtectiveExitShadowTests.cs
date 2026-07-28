using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [B3, sentinella] Con le uscite protettive NON guidate dai tick, il tick OSSERVA: registra che
/// avrebbe fatto scattare un'uscita, e quando il percorso a candele la fa scattare davvero ne nasce
/// un confronto. Serve a vedere il caso singolo che il replay offline non poteva vedere — un crollo
/// con gap — non a produrre una media, che su tre corsie richiederebbe anni.
///
/// Il test che conta più di tutti è <see cref="Il_tick_osserva_e_non_tocca_nulla"/>: la sentinella
/// deve essere INERTE. Se osservando cambiasse anche solo il best-since-entry del trailing, il feed
/// avrebbe acquisito potere sulle uscite dalla porta di servizio — senza che nessun toggle lo dica e
/// senza che nessuno se ne accorga, perché l'effetto si vedrebbe solo come uno stop che scatta
/// "un po' prima" del previsto.
/// </summary>
[Collection("Postgres")]
public sealed class ProtectiveExitShadowTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ProtectiveExitShadowTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    private sealed class CapturingRecorder : IProtectiveExitShadowRecorder
    {
        public List<ProtectiveExitShadow> Captured { get; } = [];
        public Task RecordAsync(ProtectiveExitShadow c, CancellationToken ct = default)
        {
            Captured.Add(c);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<string> Titles { get; } = [];
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Titles.Add(title);
            return Task.CompletedTask;
        }
    }

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

    private sealed class ThrowingExchangeFactory : IExchangeClientFactory
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
        public IDisposable OnChange(Action<T, string> listener) => Null.Instance;
        private sealed class Null : IDisposable { public static readonly Null Instance = new(); public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        if (_provider is null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEncryptionService, PassthroughEncryption>();
            services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
            _provider = services.BuildServiceProvider();

            var f = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await f.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }
        return _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    }

    private async Task<TradingEngine> BuildAsync(
        IProtectiveExitShadowRecorder? recorder,
        bool driveProtectiveExits,
        decimal? trailingPercent = null)
    {
        var dbFactory = await DbAsync();

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            Strategies = [new EnsembleStrategy
            {
                StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted",
                IsActive = true, StopLossPercent = 5m, TrailingStopPercent = trailingPercent,
            }],
        };

        return new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(i => i == 4 ? Signal.Long : Signal.Hold),
            new TechnicalIndicatorsService(),
            new ThrowingExchangeFactory(),        // Paper: nessun exchange viene mai toccato
            new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0, PositionSizePercent = 8m }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            realtimeOptions: new StaticOptionsMonitor<RealtimeFeedOptions>(
                new RealtimeFeedOptions { Enabled = true, DriveProtectiveExits = driveProtectiveExits }),
            shadowRecorder: recorder);
    }

    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = T0.AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    /// <summary>Apre la posizione long a 100 (stop al 5% ⇒ 95) e la lascia aperta.</summary>
    private static async Task OpenAsync(TradingEngine engine)
    {
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
    }

    // --- Il test che conta di più ------------------------------------------------------------

    /// <summary>
    /// LA SENTINELLA È INERTE. I tick sotto lo stop non chiudono niente, e — la parte insidiosa —
    /// non toccano <c>BestPriceSinceEntry</c>. Se lo aggiornassero, il livello di trailing del
    /// percorso a candele si sposterebbe col ritmo dei tick: il feed deciderebbe le uscite senza che
    /// nessun toggle lo dica, e l'effetto si vedrebbe solo come uno stop scattato "un po' prima" del
    /// previsto — cioè non si vedrebbe affatto.
    /// </summary>
    [Fact]
    public async Task Il_tick_osserva_e_non_tocca_nulla()
    {
        var recorder = new CapturingRecorder();
        var engine = await BuildAsync(recorder, driveProtectiveExits: false, trailingPercent: 3m);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAsync(engine);

        var before = (await engine.GetOpenPositionsAsync()).Single();
        var bestBefore = before.BestPriceSinceEntry;

        // Tick MOLTO più in alto: con le uscite guidate dai tick il trailing ratchetterebbe a 120.
        for (var i = 0; i < 20; i++) await engine.ProcessPriceTickAsync(120m, T0.AddHours(4).AddSeconds(i));
        // ...e tick sotto lo stop: con le uscite guidate dai tick la posizione sarebbe chiusa.
        for (var i = 0; i < 20; i++) await engine.ProcessPriceTickAsync(94m, T0.AddHours(4).AddMinutes(30).AddSeconds(i));

        var after = (await engine.GetOpenPositionsAsync()).Single();
        Assert.Equal(bestBefore, after.BestPriceSinceEntry);
        Assert.Empty(recorder.Captured);   // nessun confronto finché la candela non chiude davvero
    }

    // --- Il confronto ------------------------------------------------------------------------

    /// <summary>
    /// Il confronto nasce quando il percorso a candele chiude davvero. Numeri a mano: ingresso 100,
    /// stop 95; il tick a 94 riempirebbe a 94 (peggiore fra livello e apertura della barra degenere),
    /// la candela a 90 riempie a 90 ⇒ 400 punti base a favore del feed, con 30 minuti di anticipo.
    /// </summary>
    [Fact]
    public async Task Il_confronto_nasce_quando_la_candela_chiude_davvero()
    {
        var recorder = new CapturingRecorder();
        var engine = await BuildAsync(recorder, driveProtectiveExits: false);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAsync(engine);

        await engine.ProcessPriceTickAsync(94m, T0.AddHours(4).AddMinutes(30));
        Assert.Single(await engine.GetOpenPositionsAsync());   // il tick non chiude
        Assert.Empty(recorder.Captured);

        await engine.ProcessCandleAsync(Candle(5, 90m));       // ora chiude la candela
        Assert.Empty(await engine.GetOpenPositionsAsync());

        var c = Assert.Single(recorder.Captured);
        Assert.Equal("StopLoss", c.DetectedReason);
        Assert.Equal("StopLoss", c.ActualReason);
        Assert.Equal(94m, c.ShadowFillPrice);
        Assert.Equal(90m, c.ActualFillPrice);
        Assert.Equal(1800d, c.LeadSeconds);
        Assert.Equal(400d, c.DelayCostBps, 6);
        Assert.Equal(0, c.LaneId);
        Assert.Equal(TradingMode.Paper, c.Mode);
    }

    /// <summary>
    /// Il segno segue la STESSA convenzione dell'analizzatore offline: negativo = aspettare la
    /// chiusura è convenuto. Serve che i due numeri siano confrontabili, perché il senso della
    /// sentinella è dire se il mercato continua a comportarsi come nel replay — con due convenzioni
    /// diverse il confronto sarebbe un esercizio di traduzione, cioè un errore in attesa.
    /// </summary>
    [Fact]
    public async Task Quando_il_ritardo_conviene_il_segno_e_negativo()
    {
        var recorder = new CapturingRecorder();
        var engine = await BuildAsync(recorder, driveProtectiveExits: false);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAsync(engine);

        // Il tick buca lo stop (94), poi il prezzo rientra e la barra chiude a 96: la candela vede
        // comunque il minimo? No — la barra è degenere a 96, quindi lo stop NON scatta e la
        // posizione resta aperta. Serve una barra che tocchi il livello ma chiuda meglio del tick.
        await engine.ProcessPriceTickAsync(94m, T0.AddHours(4).AddMinutes(10));

        await engine.ProcessCandleAsync(new OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = T0.AddHours(5),
            Open = 96m, High = 97m, Low = 94m, Close = 96m, Volume = 100m,
        });

        var c = Assert.Single(recorder.Captured);
        // Candela: lo stop a 95 è toccato dal minimo 94, fill = min(95, apertura 96) = 95.
        // Tick: fill 94. Per un long uscire a 94 è PEGGIO che a 95 ⇒ −100 bps.
        Assert.Equal(95m, c.ActualFillPrice);
        Assert.Equal(-100d, c.DelayCostBps, 6);
    }

    /// <summary>
    /// Una sola rilevazione per posizione. Su un mercato che rompe il livello e ci resta, un tick al
    /// secondo ne produrrebbe migliaia — tutte della stessa identica uscita.
    /// </summary>
    [Fact]
    public async Task Una_raffica_di_tick_produce_un_solo_confronto()
    {
        var recorder = new CapturingRecorder();
        var engine = await BuildAsync(recorder, driveProtectiveExits: false);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAsync(engine);

        for (var i = 0; i < 200; i++) await engine.ProcessPriceTickAsync(94m - i * 0.01m, T0.AddHours(4).AddSeconds(i));
        await engine.ProcessCandleAsync(Candle(5, 90m));

        var c = Assert.Single(recorder.Captured);
        // Il PRIMO tick, non l'ultimo: è il momento in cui il feed se ne sarebbe accorto.
        Assert.Equal(T0.AddHours(4), c.DetectedAtUtc);
        Assert.Equal(94m, c.DetectedPrice);
    }

    // --- Regressione: l'assetto acceso non cambia -------------------------------------------

    /// <summary>
    /// Con le uscite guidate dai tick il comportamento è quello di sempre — il tick chiude — e NON
    /// si registra alcun confronto: non c'è ritardo da misurare quando i due lati coincidono.
    /// </summary>
    [Fact]
    public async Task Con_le_uscite_guidate_dai_tick_il_comportamento_e_quello_di_prima()
    {
        var recorder = new CapturingRecorder();
        var engine = await BuildAsync(recorder, driveProtectiveExits: true);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAsync(engine);

        await engine.ProcessPriceTickAsync(90m, T0.AddHours(4).AddMinutes(30));

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Empty(recorder.Captured);
    }

    // --- La soglia della sentinella ---------------------------------------------------------

    /// <summary>
    /// Il senso della sentinella è la soglia: sopra, un caso SOLO è una notizia; sotto, è il
    /// verdetto già noto e notificarlo trasformerebbe l'allarme in rumore. Si allerta solo quando il
    /// ritardo COSTA — il caso opposto conferma il verdetto di B3 e non è una novità.
    /// </summary>
    [Theory]
    [InlineData(500d, true)]     // sopra soglia e a sfavore del ritardo ⇒ allarme
    [InlineData(50d, false)]     // sotto soglia ⇒ silenzio
    [InlineData(-900d, false)]   // enorme ma A FAVORE del ritardo ⇒ è il verdetto noto, silenzio
    public async Task La_sentinella_allerta_solo_sopra_soglia_e_solo_a_sfavore(double costBps, bool expectAlert)
    {
        var dbFactory = await DbAsync();
        var notifier = new RecordingNotifier();
        var recorder = new ProtectiveExitShadowRecorder(
            dbFactory,
            new StaticOptionsMonitor<ProtectiveExitShadowOptions>(new ProtectiveExitShadowOptions { AlertAboveBps = 200d }),
            NullLogger<ProtectiveExitShadowRecorder>.Instance,
            notifier);

        await recorder.RecordAsync(new ProtectiveExitShadow
        {
            LaneId = 0, Symbol = "BTC/USDT", Mode = TradingMode.Paper,
            PositionId = Guid.NewGuid().ToString("N"), Side = OrderSide.Buy, EntryPrice = 100m,
            DetectedAtUtc = T0, DetectedPrice = 94m, DetectedReason = "StopLoss", ShadowFillPrice = 94m,
            ActualExitAtUtc = T0.AddMinutes(30), ActualFillPrice = 90m, ActualReason = "StopLoss",
            LeadSeconds = 1800d, DelayCostBps = costBps,
        });

        Assert.Equal(expectAlert ? 1 : 0, notifier.Titles.Count);

        // In ogni caso la riga è persistita: l'allarme decide cosa si legge subito, non cosa si tiene.
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.NotEmpty(await db.ProtectiveExitShadows.AsNoTracking().ToListAsync());
    }
}
