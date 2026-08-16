using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-15] L'orchestrazione della pagina /market/watchlist dopo la revisione post-incidente
/// (122 serie ferme per 6 ore, worker di sync morto in silenzio). Le cose nuove da provare:
/// il TIMBRO del ciclo con il suo verdetto (vivo/fermo/stimato), il rilevamento del RECUPERO
/// (arretrato che si drena ≠ guasto in corso), la verifica dello stato simboli su exchange con
/// l'annotazione automatica al Disabilita, e i conteggi fuori dal percorso critico.
/// </summary>
[Collection("Postgres")]
public sealed class WatchlistPageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public WatchlistPageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class NoopSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Il timeout HTTP verso il pod ingestion: TaskCanceledException con Token=None.</summary>
    private sealed class NetworkTimeoutSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) =>
            Task.FromException<int>(new TaskCanceledException("timeout HttpClient simulato (Token=None)"));
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Exchange irraggiungibile: il modo di guasto più comune di una chiamata pubblica grossa.</summary>
    private sealed class FailingStatusFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => new FailingClient();
        public IExchangeClient Create(string exchangeName) => new FailingClient();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();

        private sealed class FailingClient : StatusOnlyClientBase
        {
            public override Task<IReadOnlyDictionary<string, string>> GetSymbolStatusesAsync(CancellationToken ct = default) =>
                Task.FromException<IReadOnlyDictionary<string, string>>(
                    new TaskCanceledException("timeout per-tentativo simulato (Token=None)"));
        }
    }

    private sealed class FakeCatalog : ProcioneMGR.Services.MarketData.ISymbolCatalog
    {
        public ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask<IReadOnlyList<ProcioneMGR.Services.MarketData.SeriesKey>> GetKnownSeriesAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ProcioneMGR.Services.MarketData.SeriesKey>>([]);
        public void Invalidate() { }
    }

    /// <summary>Client finto che sa SOLO rispondere sugli stati dei simboli (il resto non serve qui).</summary>
    private sealed class StatusOnlyClient(IReadOnlyDictionary<string, string> statuses) : StatusOnlyClientBase
    {
        public override Task<IReadOnlyDictionary<string, string>> GetSymbolStatusesAsync(CancellationToken ct = default) =>
            Task.FromResult(statuses);
    }

    private abstract class StatusOnlyClientBase : IExchangeClient
    {
        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public abstract Task<IReadOnlyDictionary<string, string>> GetSymbolStatusesAsync(CancellationToken ct = default);
        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StatusOnlyFactory(IReadOnlyDictionary<string, string> statuses) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => new StatusOnlyClient(statuses);
        public IExchangeClient Create(string exchangeName) => new StatusOnlyClient(statuses);
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    private async Task<(WatchlistPageService Service, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(
        IReadOnlyDictionary<string, string>? statuses = null,
        IMarketDataSyncService? sync = null,
        IExchangeClientFactory? exchanges = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MarketData:SyncIntervalMinutes"] = "5" })
            .Build();

        var service = new WatchlistPageService(
            dbFactory,
            sync ?? new NoopSyncService(),
            exchanges ?? new StatusOnlyFactory(statuses ?? new Dictionary<string, string>()),
            new FakeCatalog(),
            new SeriesCandleCountCache(dbFactory, NullLogger<SeriesCandleCountCache>.Instance),
            config,
            NullLogger<WatchlistPageService>.Instance);
        return (service, dbFactory);
    }

    private static async Task<int> SeedSeriesAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, DateTime lastCandleUtc, int candles = 5, bool enabled = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var series = new TrackedSeries
        {
            Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = timeframe, Enabled = enabled,
        };
        db.TrackedSeries.Add(series);
        var step = Timeframes.Supported[timeframe];
        for (var i = 0; i < candles; i++)
        {
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = symbol, Timeframe = timeframe,
                TimestampUtc = lastCandleUtc - step * i,
                Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1m,
            });
        }
        await db.SaveChangesAsync();
        return series.Id;
    }

    private static async Task StampCycleAsync(IDbContextFactory<ApplicationDbContext> dbFactory, DateTime lastUtc,
        string esito = "ciclo ok", int intervalMinutes = 5)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var version = SyncPulse.ComposeOutcome(esito, TimeSpan.FromMinutes(intervalMinutes));
        var row = await db.HostHeartbeats.FirstOrDefaultAsync(h => h.Host == HostHeartbeat.IngestionSyncRole);
        if (row is null)
        {
            db.HostHeartbeats.Add(new HostHeartbeat { Host = HostHeartbeat.IngestionSyncRole, LastUtc = lastUtc, Version = version });
        }
        else
        {
            row.LastUtc = lastUtc;
            row.Version = version;
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Load_FermaEFresca_VerdettoGiustoEConteggiDopo()
    {
        var (service, db) = await BuildAsync();
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));

        await service.LoadAsync();

        Assert.Equal(1, service.StaleEnabledCount());
        // I conteggi NON sono nel percorso critico: dopo LoadAsync la colonna è ancora vuota…
        Assert.All(service.Rows!, r => Assert.Null(r.CandleCount));
        // …e arriva piena con LoadCountsAsync.
        await service.LoadCountsAsync();
        Assert.All(service.Rows!, r => Assert.Equal(5, r.CandleCount));
    }

    [Fact]
    public async Task Pulse_TimbroFresco_Vivo_TimbroStantio_Fermo()
    {
        var (service, db) = await BuildAsync();
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));

        await StampCycleAsync(db, DateTime.UtcNow.AddMinutes(-2));
        await service.LoadAsync();
        Assert.NotNull(service.SyncPulse);
        Assert.False(service.SyncPulse!.Stalled);
        Assert.False(service.SyncPulse.Estimated);
        Assert.StartsWith("ciclo ok", service.SyncPulse.Outcome);

        // Il caso dell'incidente: timbro di 6 ore prima.
        await StampCycleAsync(db, DateTime.UtcNow.AddHours(-6));
        await service.RefreshFreshnessAsync();
        Assert.True(service.SyncPulse!.Stalled);
    }

    [Fact]
    public async Task Pulse_SenzaTimbro_SiStimaDallUltimaSerieSincronizzata()
    {
        // Rollout non atomico: la UI nuova può girare con il pod vecchio che non timbra ancora.
        // Meglio un'ora STIMATA e dichiarata che nessun dato.
        var (service, db) = await BuildAsync();
        var id = await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var s = await ctx.TrackedSeries.FirstAsync(x => x.Id == id);
            s.LastSyncUtc = DateTime.UtcNow.AddMinutes(-3);
            await ctx.SaveChangesAsync();
        }

        await service.LoadAsync();

        Assert.NotNull(service.SyncPulse!.LastCycleUtc);
        Assert.True(service.SyncPulse.Estimated);
        Assert.False(service.SyncPulse.Stalled);
    }

    [Fact]
    public async Task Refresh_ArretratoCheSiDrena_SegnaInRecupero()
    {
        // Dopo un blocco, il sync riparte e l'ultima candela AVANZA pur restando oltre tolleranza:
        // è drenaggio, non guasto — leggerlo come «FERMA» ha quasi fatto disabilitare serie sane
        // nell'incidente. La distinzione richiede memoria (l'osservazione precedente) e sync vivo.
        var (service, db) = await BuildAsync();
        var id = await SeedSeriesAsync(db, "AAA/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await StampCycleAsync(db, DateTime.UtcNow.AddMinutes(-2));

        await service.LoadAsync(); // prima osservazione: ferma, nessun recupero dichiarabile
        Assert.False(service.Rows!.Single().IsRecovering);

        // Il drenaggio: arriva una candela più recente, ma ancora 20 barre indietro.
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OhlcvData.Add(new OhlcvData
            {
                Symbol = "AAA/USDT", Timeframe = "1h",
                TimestampUtc = DateTime.UtcNow.AddHours(-20),
                Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
            });
            await ctx.SaveChangesAsync();
        }

        await service.RefreshFreshnessAsync();

        var row = service.Rows!.Single();
        Assert.True(SeriesFreshness.IsStale(row.Series.Timeframe, row.LastCandleUtc, DateTime.UtcNow)); // ancora ferma…
        Assert.True(row.IsRecovering); // …ma dichiarata in recupero

        // Osservazioni senza progresso: il badge RESISTE per un paio di giri (un drenaggio vero non
        // porta una barra a ogni tick da 60 s: senza grazia lampeggerebbe fra i due stati)…
        await service.RefreshFreshnessAsync();
        Assert.True(service.Rows!.Single().IsRecovering);
        await service.RefreshFreshnessAsync();
        Assert.True(service.Rows!.Single().IsRecovering);

        // …ma non per sempre: un drenaggio piantato torna a dichiararsi FERMA.
        await service.RefreshFreshnessAsync();
        Assert.False(service.Rows!.Single().IsRecovering);
    }

    [Fact]
    public async Task CheckStatuses_FermaInBreak_ContaEAnnotaAlDisabilita()
    {
        // Il flusso MKR/TON, prima manuale (annotazione scritta a mano nel DB il 2026-07-28), ora
        // di pagina: verifica su exchange → badge BREAK → Disabilita annota il motivo da solo.
        var (service, db) = await BuildAsync(new Dictionary<string, string>
        {
            ["MKR/USDT"] = "BREAK",
            ["BTC/USDT"] = "TRADING",
        });
        var mkrId = await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await service.LoadAsync();

        var esito = await service.CheckExchangeStatusesAsync();

        Assert.Equal(1, esito.Suspended); // solo MKR: ferma E non negoziabile
        Assert.Empty(esito.FailedExchanges);
        Assert.NotNull(service.StatusesCheckedAtUtc);
        Assert.Equal("BREAK", service.Rows!.Single(r => r.Series.Symbol == "MKR/USDT").ExchangeStatus);

        var (_, isError) = await service.ToggleAsync(mkrId);
        Assert.False(isError);
        await using var ctx = await db.CreateDbContextAsync();
        var mkr = await ctx.TrackedSeries.FirstAsync(s => s.Id == mkrId);
        Assert.False(mkr.Enabled);
        Assert.StartsWith("disabilitata", mkr.LastSyncStatus);
        Assert.Contains("BREAK", mkr.LastSyncStatus);
    }

    // ------------------------------------------------------------------ correzioni della review 2026-08-15

    [Fact]
    public async Task CheckStatuses_TuttiGliExchangeFalliti_NonRassicuraConZeroSospese()
    {
        // La classe di difetto del Filone E: un controllo che rassicura a prescindere dalla realtà.
        // «Verifica completata: 0 sospese» con TUTTE le chiamate fallite mandava l'utente a cercare
        // il guasto altrove, convinto che i simboli fossero sani.
        var (service, db) = await BuildAsync(exchanges: new FailingStatusFactory());
        await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await service.LoadAsync();

        var esito = await service.CheckExchangeStatusesAsync();

        Assert.True(esito.AllFailed);
        Assert.Equal(0, esito.Checked);
        Assert.NotEmpty(esito.FailedExchanges);
        Assert.Null(service.StatusesCheckedAtUtc); // niente timestamp: non è stato verificato nulla
        Assert.Null(service.Rows!.Single().ExchangeStatus);
    }

    [Fact]
    public async Task CheckStatuses_VerificaFallita_NonLasciaBadgeVecchiSottoTimestampNuovo()
    {
        // Regola 5 nel punto in cui l'utente decide se disabilitare: un BREAK di ieri non deve
        // comparire come esito della verifica di oggi (il simbolo potrebbe essere rientrato).
        var (service, db) = await BuildAsync(new Dictionary<string, string> { ["MKR/USDT"] = "BREAK" });
        await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await service.LoadAsync();
        await service.CheckExchangeStatusesAsync();
        Assert.Equal("BREAK", service.Rows!.Single().ExchangeStatus);

        // Seconda verifica, exchange irraggiungibile: il badge vecchio sparisce.
        var (service2, db2) = (service, db);
        var failing = new WatchlistPageService(
            db2, new NoopSyncService(), new FailingStatusFactory(), new FakeCatalog(),
            new SeriesCandleCountCache(db2, NullLogger<SeriesCandleCountCache>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<WatchlistPageService>.Instance);
        await failing.LoadAsync();
        var esito = await failing.CheckExchangeStatusesAsync();

        Assert.True(esito.AllFailed);
        Assert.Null(failing.Rows!.Single().ExchangeStatus);
        Assert.NotNull(service2.Rows); // il primo service resta com'era: nessun effetto collaterale
    }

    [Fact]
    public async Task SyncNow_TimeoutDiRete_TornaMessaggioDErroreInveceDiUccidereIlCircuito()
    {
        // La TERZA sorgente di OCE, stavolta nel percorso UI: una TaskCanceledException di timeout
        // (Token=None) sfuggita a un filtro per TIPO risalirebbe fino all'event handler Blazor.
        var (service, db) = await BuildAsync(sync: new NetworkTimeoutSyncService());
        var id = await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await service.LoadAsync();

        var (message, isError) = await service.SyncNowAsync(id);

        Assert.True(isError);
        Assert.Contains("Errore sync", message);
    }

    [Fact]
    public async Task Refresh_RileggeLoStatoDiSyncPerSerie_NonSoloLeCandele()
    {
        // Le colonne «Ultima sync» e «Stato» restavano congelate all'apertura della pagina: il
        // worker riscrive LastSyncUtc/LastSyncStatus a ogni ciclo, ma nessuno le rileggeva.
        var (service, db) = await BuildAsync();
        var id = await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await service.LoadAsync();
        Assert.Null(service.Rows!.Single().Series.LastSyncUtc);

        await using (var ctx = await db.CreateDbContextAsync())
        {
            var s = await ctx.TrackedSeries.FirstAsync(x => x.Id == id);
            s.LastSyncUtc = DateTime.UtcNow;
            s.LastSyncStatus = "OK: 12 candele";
            await ctx.SaveChangesAsync();
        }

        await service.RefreshFreshnessAsync();

        Assert.NotNull(service.Rows!.Single().Series.LastSyncUtc);
        Assert.Equal("OK: 12 candele", service.Rows!.Single().Series.LastSyncStatus);
    }

    [Fact]
    public async Task Refresh_SerieAggiuntaDaAltri_EntraNellElencoSenzaRicaricareTutto()
    {
        var (service, db) = await BuildAsync();
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await service.LoadAsync();
        Assert.Single(service.Rows!);

        await SeedSeriesAsync(db, "ETH/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await service.RefreshFreshnessAsync();

        Assert.Equal(2, service.Rows!.Count);
    }

    [Fact]
    public async Task Pulse_WorkerSpento_NonSiLeggeComeGuasto()
    {
        // Spegnere il sync da /admin/autonomy è una scelta, non un guasto: accusare un pod morto
        // che non c'è manda l'operatore a cercare per ore la causa sbagliata.
        var (service, db) = await BuildAsync();
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await StampCycleAsync(db, DateTime.UtcNow.AddHours(-6), esito: "spento");

        await service.LoadAsync();

        Assert.True(service.SyncPulse!.Disabled);
        Assert.False(service.SyncPulse.Stalled); // «spento» non è «fermo»
    }

    [Fact]
    public async Task Pulse_IntervalloDichiaratoNelTimbro_VinceSullaConfigLocale()
    {
        // Il pod ingestion e il guscio hanno appsettings indipendenti: con l'intervallo del pod a
        // 15 min, una soglia calcolata sui 5 min del guscio dichiarerebbe FERMO un sync sano.
        var (service, db) = await BuildAsync(); // config locale: 5 min
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await StampCycleAsync(db, DateTime.UtcNow.AddMinutes(-25), esito: "ciclo ok", intervalMinutes: 15);

        await service.LoadAsync();

        Assert.Equal(TimeSpan.FromMinutes(15), service.SyncPulse!.Interval);
        Assert.False(service.SyncPulse.Stalled); // 25 min < 3×15+2; con i 5 min locali sarebbe stato «fermo»
    }

    [Fact]
    public async Task SerieDaUnMinuto_ConIlNormaleRitardoDelSync_NonRisultaFermaInPagina()
    {
        // [2026-08-16] Il banner rosso e il badge FERMA devono corrispondere a un guasto: con il
        // sync ogni 5 minuti, 4 barre di ritardo su una 1m sono la fisiologia della cadenza.
        var (service, db) = await BuildAsync();
        await SeedSeriesAsync(db, "XRP/USDT", "1m", DateTime.UtcNow.AddMinutes(-5));
        await StampCycleAsync(db, DateTime.UtcNow.AddMinutes(-2));

        await service.LoadAsync();

        Assert.Equal(0, service.StaleEnabledCount());
        Assert.False(service.Rows!.Single().IsStale);

        // Ma un blocco vero resta visibile.
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.OhlcvData.Where(o => o.Symbol == "XRP/USDT").ExecuteDeleteAsync();
            ctx.OhlcvData.Add(new OhlcvData
            {
                Symbol = "XRP/USDT", Timeframe = "1m", TimestampUtc = DateTime.UtcNow.AddMinutes(-30),
                Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
            });
            await ctx.SaveChangesAsync();
        }
        await service.RefreshFreshnessAsync();
        Assert.Equal(1, service.StaleEnabledCount());
    }

    [Fact]
    public async Task Toggle_SerieOperataDaCorsia_AvvisaCheIlTradingNonSiFerma()
    {
        // L'equivoco dell'incidente STX (2026-08-13): disabilitare qui NON ferma la corsia.
        var (service, db) = await BuildAsync();
        var id = await SeedSeriesAsync(db, "STX/USDT", "4h", DateTime.UtcNow.AddHours(-1));
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.TradingEngineStates.Add(new TradingEngineState
            {
                LaneId = 7, IsRunning = true, Symbol = "STX/USDT", Timeframe = "4h",
                ExchangeName = "Binance", Mode = TradingMode.Paper,
            });
            await ctx.SaveChangesAsync();
        }
        await service.LoadAsync();

        var (message, isError) = await service.ToggleAsync(id);

        Assert.True(isError);
        Assert.Contains("NON il trading", message);
        Assert.Contains("corsia 7", message);
    }
}
