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
/// [2026-09-06] <b>Una corsia che riprende dopo un riavvio deve poter ancora DECIDERE.</b>
/// È il difetto D1 (<see cref="RipresaDopoRiavvioTests"/>) un piano più sotto: quello restaurava le
/// gambe, questo restaura le barre.
///
/// <para><b>Il fatto.</b> <c>_buffer</c> vive solo in memoria e lo riempie unicamente
/// <c>ProcessCandleAsync</c>. Dopo un riavvio del processo la corsia riprendeva dal segnalibro,
/// marcava a mercato e onorava gli stop — <c>ApplyProtectiveExitsAsync</c> sta prima del cancello —
/// ma il <c>if (closes.Count &gt;= 5)</c> le impediva di interrogare una sola strategia finché non
/// aveva accumulato barre NUOVE. E cinque non bastano: gli indicatori veri ne vogliono 14
/// (Supertrend), ~29 (MacdTrend), 60 (GridMeanReversion con ancora 60). Su una corsia a 4 ore sono
/// dieci giorni di processo vivo senza interruzioni, mentre il pod si rischiera a ogni merge.</para>
///
/// <para><b>Il costo misurato il 2026-09-06.</b> Sei corsie su otto con 1-4 barre in memoria contro
/// le cinque del cancello; zero ordini in tutta la flotta da quaranta ore; le corsie 2, 3, 4 e 5
/// senza nemmeno un <c>TradeRecord</c> da 14, 7, 5 e 14 giorni. Tutte e otto verdi ovunque: i chip e
/// la Home leggono <c>IsRunning</c>, il battito di <c>/trading</c> conta le candele CONSEGNATE, e la
/// sonda E6 misura lo stesso campo — che si chiama «ultima candela valutata» ma è scritto PRIMA del
/// cancello. Una corsia accesa che non decide niente era invisibile per costruzione.</para>
///
/// <para>Come in D1, la simulazione del riavvio è la cosa vera: una <b>seconda istanza</b> di
/// <see cref="TradingEngine"/> sullo stesso database, senza che <c>StartAsync</c> venga mai chiamato
/// su di essa.</para>
/// </summary>
[Collection("Postgres")]
public class RiscaldamentoDopoRiavvioTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RiscaldamentoDopoRiavvioTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
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
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, ConfigWriteContext writtenBy, CancellationToken ct = default) => throw new NotImplementedException();
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
        public IDisposable OnChange(Action<T, string> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private IDbContextFactory<ApplicationDbContext>? _dbFactory;
    private readonly EnsembleConfiguration _config = new()
    {
        ExchangeName = "Binance",
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TotalCapital = 10_000m,
        Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Gamba s1", IsActive = true }],
    };

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

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Candela(int i, decimal close = 100m) => new()
    {
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TimestampUtc = T0.AddHours(i),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 100m,
    };

    /// <summary>Le candele che l'ingestione ha in archivio: è da qui che il buffer si ricostruisce.</summary>
    private async Task InArchivioAsync(int da, int a)
    {
        var dbFactory = await DbAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        for (var i = da; i <= a; i++) db.OhlcvData.Add(Candela(i));
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ il difetto, e la correzione

    /// <summary>
    /// IL TEST CHE INCHIODA LA CORREZIONE: dopo il riavvio la corsia decide sulla PRIMA candela
    /// nuova, non dopo cinque. Prima della correzione il buffer ripartiva vuoto e questa apertura
    /// non avveniva.
    /// </summary>
    [Fact]
    public async Task DopoIlRiavvio_LaCorsiaDECIDE_SullaPrimaCandelaNuova()
    {
        await InArchivioAsync(0, 20);

        // Sessione 1: avviata, cinque candele, nessun segnale. Poi il processo "muore".
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await primo.ProcessCandleAsync(Candela(i));

        // Sessione 2: NUOVA istanza, nessuno StartAsync. Il buffer si ricostruisce dall'archivio.
        // La gamba apre alla prima candela che riesce a valutare, qualunque sia il suo indice.
        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Long);
        var prima = await dopoRiavvio.GetStatusAsync();
        Assert.True(prima.IsRunning);
        Assert.Equal(5, prima.BufferedBars);                 // <- prima della correzione era 0
        Assert.Null(prima.LastStrategyEvaluationUtc);        // in questo avvio non ha ancora deciso nulla

        await dopoRiavvio.ProcessCandleAsync(Candela(5));

        Assert.Single(await dopoRiavvio.GetOpenPositionsAsync());
        var dopo = await dopoRiavvio.GetStatusAsync();
        Assert.Equal(T0.AddHours(5), dopo.LastStrategyEvaluationUtc);
        Assert.Equal(dopo.LastProcessedCandleUtc, dopo.LastStrategyEvaluationUtc);
    }

    /// <summary>
    /// LA SICUREZZA DELLA CORREZIONE: il riempimento si ferma al SEGNALIBRO. Le candele arrivate
    /// mentre il processo era giù devono ancora passare da <c>ProcessCandleAsync</c>, che è l'unico
    /// posto dove uno stop rimasto indietro può scattare. Riempire fino a «adesso» sposterebbe in
    /// avanti la frontiera anti-replay e quelle uscite andrebbero perse in silenzio.
    /// </summary>
    [Fact]
    public async Task IlRiempimentoSiFermaAlSegnalibro_LeCandelePerseSonoAncoraValutabili()
    {
        await InArchivioAsync(0, 20);   // l'archivio ha molto più del segnalibro

        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await primo.ProcessCandleAsync(Candela(i));

        // Il processo resta giù mentre passano le candele 5..9.
        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Hold);
        var stato = await dopoRiavvio.GetStatusAsync();
        Assert.Equal(5, stato.BufferedBars);   // 0..4, NON 0..20: il taglio è il segnalibro

        // La candela 5 non è «già vista»: viene valutata, come prima della correzione.
        await dopoRiavvio.ProcessCandleAsync(Candela(5));
        var dopo = await dopoRiavvio.GetStatusAsync();
        Assert.Equal(T0.AddHours(5), dopo.LastProcessedCandleUtc);
        Assert.Equal(6, dopo.BufferedBars);
    }

    /// <summary>
    /// Senza candele in archivio non si inventa nulla: il comportamento resta quello di prima del
    /// 2026-09-06, e lo stato lo dichiara con un buffer vuoto invece di far credere il contrario.
    /// </summary>
    [Fact]
    public async Task SenzaArchivio_NienteRiempimento_ELoStatoLoDICE()
    {
        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        await primo.ProcessCandleAsync(Candela(0));   // la candela NON è in archivio

        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Long);
        var stato = await dopoRiavvio.GetStatusAsync();

        Assert.Equal(0, stato.BufferedBars);
        Assert.Null(stato.LastStrategyEvaluationUtc);
    }

    /// <summary>
    /// LA FIRMA DEL GUASTO INVISIBILE, resa visibile: con poche barre in memoria la corsia riceve
    /// candele (<c>LastProcessedCandleUtc</c> avanza) e non interroga nessuna strategia
    /// (<c>LastStrategyEvaluationUtc</c> resta null). È lo stato in cui erano sei corsie su otto il
    /// 2026-09-06, e in cui nessun battito sapeva distinguerle da una corsia sana.
    /// </summary>
    [Fact]
    public async Task ConsegnaSenzaDecisione_ISueDueBattitiDIVERGONO()
    {
        var primo = await NuovaIstanzaAsync(_ => Signal.Long);
        await primo.StartAsync(TradingMode.Paper);

        // Nessun archivio: il buffer riparte vuoto anche dopo il riavvio.
        var dopoRiavvio = await NuovaIstanzaAsync(_ => Signal.Long);
        await dopoRiavvio.ProcessCandleAsync(Candela(0));
        await dopoRiavvio.ProcessCandleAsync(Candela(1));

        var stato = await dopoRiavvio.GetStatusAsync();
        Assert.Equal(T0.AddHours(1), stato.LastProcessedCandleUtc);   // le candele arrivano
        Assert.Null(stato.LastStrategyEvaluationUtc);                 // le strategie no
        Assert.Equal(2, stato.BufferedBars);                          // e si vede perché: 2 < 5
        Assert.Empty(await dopoRiavvio.GetOpenPositionsAsync());
    }

    /// <summary>
    /// Il riempimento è per SERIE DELLA CORSIA: le candele di un'altra coppia o di un altro
    /// timeframe non entrano. Sembra ovvio e non lo è — la corsia 4 ha già operato su due serie
    /// diverse nella stessa vita (XRP fino al 31/08, poi DOGE), e un buffer misto darebbe indicatori
    /// calcolati su due mercati.
    /// </summary>
    [Fact]
    public async Task IlRiempimentoPrendeSoloLaSerieDellaCorsia()
    {
        await InArchivioAsync(0, 4);
        var dbFactory = await DbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i <= 4; i++)
            {
                var altra = Candela(i, 999m);
                altra.Symbol = "ETH/USDT";
                db.OhlcvData.Add(altra);
                var altroTf = Candela(i, 888m);
                altroTf.Timeframe = "4h";
                db.OhlcvData.Add(altroTf);
            }
            await db.SaveChangesAsync();
        }

        var primo = await NuovaIstanzaAsync(_ => Signal.Hold);
        await primo.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await primo.ProcessCandleAsync(Candela(i));

        var stato = await (await NuovaIstanzaAsync(_ => Signal.Hold)).GetStatusAsync();

        Assert.Equal(5, stato.BufferedBars);   // 5, non 15
    }
}
