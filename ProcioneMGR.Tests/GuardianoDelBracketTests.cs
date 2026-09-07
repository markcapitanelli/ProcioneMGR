using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-07] <b>Il guardiano deve parlare quando serve e tacere il resto del tempo.</b>
///
/// <para>Questa piattaforma ha una collezione documentata di meccanismi che misurano bene e non
/// dicono niente: il funding sparito due volte in silenzio, la pagina dei backup cieca per diciotto
/// giorni su dieci dump sani, il comitato AI senza quorum per sedici giorni. Il rischio opposto è
/// altrettanto reale e più insidioso: un avviso che suona a ogni giro diventa rumore, e allora il
/// giorno in cui conta nessuno lo legge. Questi test inchiodano entrambi i lati.</para>
/// </summary>
[Collection("Postgres")]
public class GuardianoDelBracketTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;
    private IDbContextFactory<ApplicationDbContext>? _dbFactory;

    public GuardianoDelBracketTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private const string Simbolo = "TEST/USDT";
    private const string Tf = "1h";

    /// <summary>Vedi <c>DiagnosiDelBracketTests.Adesso</c>: l'ora di parete la mette Postgres, non il seme.</summary>
    private static DateTime Adesso => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText;
        public string Decrypt(string cipherText) => cipherText;
    }

    private sealed class SogliaFissa<T>(T valore) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = valore;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => new Nulla();
        private sealed class Nulla : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeEnsembleManager(int laneId, EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => laneId;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, ConfigWriteContext w, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Raccoglie le notifiche invece di spedirle: è quello che i test devono osservare.</summary>
    private sealed class NotifierDiCarta : INotifier
    {
        public List<(NotificationSeverity Gravita, string Titolo, string Corpo)> Dette { get; } = [];
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Dette.Add((severity, title, body));
            return Task.CompletedTask;
        }
    }

    private NotifierDiCarta _notifier = new();

    private async Task<BracketDiagnosisWorker> GuardianoAsync(EnsembleConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddKeyedSingleton<IEnsembleManager>(0, (_, _) => new FakeEnsembleManager(0, config));
        services.AddSingleton<IOptionsMonitor<BracketDiagnosisOptions>>(
            new SogliaFissa<BracketDiagnosisOptions>(new BracketDiagnosisOptions()));
        services.AddLogging();
        services.AddScoped<BracketDiagnosisService>();
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _notifier = new NotifierDiCarta();
        return new BracketDiagnosisWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new SogliaFissa<BracketDiagnosisOptions>(new BracketDiagnosisOptions()),
            NullLogger<BracketDiagnosisWorker>.Instance,
            _notifier);
    }

    private static EnsembleConfiguration Config(decimal? stop, decimal? take) => new()
    {
        ExchangeName = "Binance",
        Symbol = Simbolo,
        Timeframe = Tf,
        Strategies =
        [
            new EnsembleStrategy
            {
                StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Gamba s1", IsActive = true,
                StopLossPercent = stop, TakeProfitPercent = take, ExpectedTradesPerMonth = 10m,
            },
        ],
    };

    private async Task SerieAsync(int n = 400)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync();
        var inizio = Adesso.AddHours(-n);
        var prezzo = 100m;
        for (var i = 0; i < n; i++)
        {
            var ampiezza = 0.4m + (i % 7) * 0.15m + (i % 13) * 0.05m;
            var close = prezzo + (i % 2 == 0 ? ampiezza : -ampiezza);
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = Simbolo, Timeframe = Tf, TimestampUtc = inizio.AddHours(i),
                Open = prezzo,
                High = Math.Max(prezzo, close) + ampiezza * 0.6m,
                Low = Math.Min(prezzo, close) - ampiezza * 0.6m,
                Close = close, Volume = 1_000m,
            });
            prezzo = close;
        }
        db.TradingEngineStates.Add(new TradingEngineState
        {
            LaneId = 0, Symbol = Simbolo, Timeframe = Tf, IsRunning = true, Mode = TradingMode.Paper,
        });
        await db.SaveChangesAsync();
    }

    private async Task UsciteAsync(int stop, int take, int minutoIniziale = 0)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync();
        for (var i = 0; i < stop + take; i++)
        {
            var chiusura = Adesso.AddMinutes(minutoIniziale + i);
            db.TradeRecords.Add(new TradeRecord
            {
                LaneId = 0, PositionId = $"p{minutoIniziale}-{i}", StrategyId = "s1", Symbol = Simbolo,
                Side = OrderSide.Buy, EntryPrice = 100m, ExitPrice = i < stop ? 99m : 103m, Quantity = 1m,
                Pnl = i < stop ? -1m : 3m, PnlPercent = i < stop ? -1m : 3m,
                OpenedAtUtc = chiusura.AddHours(-1), ClosedAtUtc = chiusura, Duration = TimeSpan.FromHours(1),
                ExitReason = i < stop ? "StopLoss" : "TakeProfit", Mode = TradingMode.Paper,
            });
        }
        await db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────────────────────────────── parlare

    /// <summary>
    /// Una corsia che si scosta dal previsto produce un avviso, e l'avviso <b>dice da che parte</b>
    /// pende lo scarto: meno stop del nominale è una buona notizia, più stop è il segnale che entra
    /// male. Un allarme che non distingue le due cose costringe ad aprire il pannello per capire, e
    /// allora tanto vale non averlo.
    /// </summary>
    [Fact]
    public async Task UnaCorsiaFuoriDalPrevisto_ProduceUnAvvisoCheDiceDaCheParte()
    {
        var g = await GuardianoAsync(Config(1m, 3m));
        await SerieAsync();
        await UsciteAsync(stop: 20, take: 20);   // 50% contro l'~88% nominale di un bracket 1:3

        await g.TickAsync(CancellationToken.None);

        var avviso = Assert.Single(_notifier.Dette, d => d.Titolo.Contains("Corsia 0"));
        Assert.Equal(NotificationSeverity.Warning, avviso.Gravita);
        Assert.Contains("fuori dal previsto", avviso.Titolo);
        Assert.Contains("MENO spesso", avviso.Corpo);
    }

    // ────────────────────────────────────────────────────────────────────────────── tacere

    /// <summary>
    /// Lo stesso verdetto al giro successivo <b>non</b> si ripete. È la differenza fra un guardiano e
    /// una sveglia rotta: chi riceve lo stesso avviso ogni giorno smette di leggerlo, e il giorno in
    /// cui cambia qualcosa non se ne accorge.
    /// </summary>
    [Fact]
    public async Task LoStessoVerdetto_NonSiRipeteAlGiroDopo()
    {
        var g = await GuardianoAsync(Config(1m, 3m));
        await SerieAsync();
        await UsciteAsync(stop: 20, take: 20);

        await g.TickAsync(CancellationToken.None);
        var dopoIlPrimo = _notifier.Dette.Count;
        await g.TickAsync(CancellationToken.None);
        await g.TickAsync(CancellationToken.None);

        Assert.Equal(1, dopoIlPrimo);
        Assert.Equal(dopoIlPrimo, _notifier.Dette.Count);
    }

    /// <summary>
    /// «Non giudicabile» non si notifica. È lo stato in cui quasi tutte le corsie vivranno per mesi —
    /// al ritmo dichiarato dalle gambe servono trimestri per accumulare venti uscite — e un avviso
    /// che descrive la normalità è rumore.
    /// </summary>
    [Fact]
    public async Task UnaCorsiaSenzaCampione_NonProduceNessunAvviso()
    {
        var g = await GuardianoAsync(Config(1m, 3m));
        await SerieAsync();
        await UsciteAsync(stop: 3, take: 1);

        await g.TickAsync(CancellationToken.None);

        Assert.Empty(_notifier.Dette);
    }

    /// <summary>
    /// Il passaggio da «non giudicabile» a «come previsto» si annuncia UNA volta: è il momento in cui
    /// la corsia smette di essere un'ipotesi e comincia a essere una misura, e senza un avviso
    /// passerebbe inosservato dopo mesi di attesa.
    /// </summary>
    [Fact]
    public async Task QuandoUnaCorsiaDiventaGiudicabile_LoDiceUnaVoltaSola()
    {
        var g = await GuardianoAsync(Config(1m, 3m));
        await SerieAsync();
        await UsciteAsync(stop: 3, take: 1);

        await g.TickAsync(CancellationToken.None);
        Assert.Empty(_notifier.Dette);

        // Arriva il campione, e con una composizione coerente col nominale del bracket 1:3.
        await UsciteAsync(stop: 35, take: 5, minutoIniziale: 10);

        await g.TickAsync(CancellationToken.None);
        var avviso = Assert.Single(_notifier.Dette);
        Assert.Equal(NotificationSeverity.Info, avviso.Gravita);
        Assert.Contains("giudicabile", avviso.Titolo);

        await g.TickAsync(CancellationToken.None);
        Assert.Single(_notifier.Dette);
    }

    /// <summary>
    /// Il guardiano spento non misura e non parla. Default acceso: è diagnostica pura, non tocca
    /// niente, e spegnerla è la scelta che va motivata.
    /// </summary>
    [Fact]
    public async Task ConIlGuardianoSpento_NonSuccedeNiente()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddKeyedSingleton<IEnsembleManager>(0, (_, _) => new FakeEnsembleManager(0, Config(1m, 3m)));
        services.AddSingleton<IOptionsMonitor<BracketDiagnosisOptions>>(
            new SogliaFissa<BracketDiagnosisOptions>(new BracketDiagnosisOptions()));
        services.AddLogging();
        services.AddScoped<BracketDiagnosisService>();
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await _dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        await SerieAsync();
        await UsciteAsync(stop: 20, take: 20);

        _notifier = new NotifierDiCarta();
        var spento = new BracketDiagnosisWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new SogliaFissa<BracketDiagnosisOptions>(new BracketDiagnosisOptions { Enabled = false }),
            NullLogger<BracketDiagnosisWorker>.Instance,
            _notifier);

        await spento.TickAsync(CancellationToken.None);

        Assert.Empty(_notifier.Dette);
    }
}
