using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il guardiano di profondità delle serie-patrimonio deve accorgersi DA SOLO di una storia che si
/// accorcia — il funding dal 2019 è sparito DUE volte (2026-07-24 e 2026-08-11) con l'esenzione
/// dalla purge al suo posto e carry/backtest che leggevano ~14 mesi credendoli 7 anni — e dirlo
/// UNA volta per transizione, non una per giro. Il complemento (livello 2 dello standard): su
/// serie profonde non deve inventare nulla.
/// </summary>
public sealed class SentimentHeritageSnapshotTests
{
    [Fact]
    public void UnoSnapshotVuoto_NonHaViolazioniNeRun()
    {
        var snapshot = new SentimentHeritageSnapshot();

        Assert.Empty(snapshot.All);
        Assert.Empty(snapshot.Violations);
        Assert.Null(snapshot.LastRunUtc);
    }

    [Fact]
    public void Violations_MetteLeSerieAssentiPerPrime()
    {
        // Una serie ASSENTE è la perdita più grave: in una lista lunga deve stare in cima.
        var snapshot = new SentimentHeritageSnapshot();
        var now = DateTime.UtcNow;
        snapshot.Replace(
        [
            new HeritageSeriesDepth("a", "corta", new DateTime(2025, 6, 1), 100, "…", "profondità persa"),
            new HeritageSeriesDepth("b", "sana", new DateTime(2019, 9, 1), 7000, "…", null),
            new HeritageSeriesDepth("c", "assente", null, 0, "…", "serie ASSENTE"),
        ], now);

        Assert.Equal(2, snapshot.Violations.Count);
        Assert.Equal("assente", snapshot.Violations[0].DisplayName);
        Assert.Equal("corta", snapshot.Violations[1].DisplayName);
        Assert.Equal(now, snapshot.LastRunUtc);
    }

    [Fact]
    public void SimboliFunding_VuotoUsaIDefaultIncorporati()
    {
        // La lista è vuota nel POCO per la trappola del binder (che APPENDE ai default): i default
        // veri vivono in DefaultFundingSymbols e valgono quando la config non dice nulla.
        var options = new SentimentHeritageGuardOptions();
        Assert.Equal(["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"], options.EffectiveFundingSymbols);

        options.FundingSymbols = ["BTC"];
        Assert.Equal(["BTC"], options.EffectiveFundingSymbols);
    }
}

/// <summary>Il worker sul database vero: misure, transizioni, riarmo.</summary>
[Collection("Postgres")]
public sealed class SentimentHeritageGuardWorkerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SentimentHeritageGuardWorkerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Body)> Sent { get; } = new();
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Soglie di test PICCOLE (decine di punti, non migliaia): quello che si prova è il confronto
    /// misurato-contro-dichiarato, non la scala. L'àncora è il 2024-01-01.
    /// </summary>
    private static SentimentHeritageGuardOptions TestThresholds() => new()
    {
        FundingSymbols = ["BTC", "ETH"],
        FundingMinStartUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FundingMinEventsPerSymbol = 50,
        FearGreedMinStartUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FearGreedMinPoints = 30,
        LiquidationsMinStartUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        LiquidationsMinPoints = 20,
    };

    private async Task<(SentimentHeritageGuardWorker Worker, SentimentHeritageSnapshot Snapshot,
        IDbContextFactory<ApplicationDbContext> Db, RecordingNotifier Notifier)> BuildAsync(
        SentimentHeritageGuardOptions? guard = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var snapshot = new SentimentHeritageSnapshot();
        var notifier = new RecordingNotifier();
        var options = new SentimentOptions { HeritageGuard = guard ?? TestThresholds() };
        var worker = new SentimentHeritageGuardWorker(
            dbFactory, options.AsMonitor(), snapshot,
            NullLogger<SentimentHeritageGuardWorker>.Instance, notifier);
        return (worker, snapshot, dbFactory, notifier);
    }

    private static async Task SeedPointsAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string source, string metric, string symbol, DateTime firstUtc, int count, TimeSpan? step = null)
    {
        var stride = step ?? TimeSpan.FromHours(8); // cadenza del funding, va bene per tutte
        await using var db = await dbFactory.CreateDbContextAsync();
        for (var i = 0; i < count; i++)
        {
            db.SentimentMetricPoints.Add(new SentimentMetricPoint
            {
                Source = source,
                Metric = metric,
                Symbol = symbol,
                TimestampUtc = firstUtc + stride * i,
                Value = 0.01m,
            });
        }
        await db.SaveChangesAsync();
    }

    private static readonly DateTime DeepStart = new(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Porta tutte le serie sopra soglia rispetto a <see cref="TestThresholds"/>.</summary>
    private static async Task SeedAllHealthyAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await SeedPointsAsync(dbFactory, SentimentMetricSources.BinanceFutures, SentimentMetrics.FundingRate, "BTC", DeepStart, 60);
        await SeedPointsAsync(dbFactory, SentimentMetricSources.BinanceFutures, SentimentMetrics.FundingRate, "ETH", DeepStart, 60);
        await SeedPointsAsync(dbFactory, SentimentMetricSources.FearGreed, SentimentMetrics.FearGreedIndex, "", DeepStart, 40, TimeSpan.FromDays(1));
        await SeedPointsAsync(dbFactory, SentimentMetricSources.BinanceLiquidations, SentimentMetrics.LongLiquidationNotional, "BTC",
            new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc), 30, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task SerieProfonde_NessunAllarmeENessunaNotifica()
    {
        // Livello 2 dello standard: la normalità non deve accendere niente, neanche ripetendo.
        var (worker, snapshot, db, notifier) = await BuildAsync();
        await SeedAllHealthyAsync(db);

        for (var i = 0; i < 3; i++)
        {
            Assert.Empty(await worker.RunOnceAsync(CancellationToken.None));
        }

        Assert.Empty(notifier.Sent);
        Assert.Equal(4, snapshot.All.Count); // BTC + ETH funding, F&G, liquidazioni
        Assert.Empty(snapshot.Violations);
        Assert.NotNull(snapshot.LastRunUtc); // il verdetto dichiara quando è stato calcolato
    }

    [Fact]
    public async Task FundingTroncato_ViolazioneNotificataUnaVoltaSola()
    {
        // L'incidente vero del 2026-08-11: restava ~ la finestra della retention (dal 2025-06),
        // conteggio pieno di quella finestra — la profondità è l'unico segno della perdita.
        var (worker, snapshot, db, notifier) = await BuildAsync();
        await SeedAllHealthyAsync(db);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC"
                            && p.TimestampUtc < new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc))
                .ExecuteDeleteAsync();
        }
        // Rimpiazzo: la finestra recente è fitta (conteggio sopra soglia), ma parte dal 2025-06.
        await SeedPointsAsync(db, SentimentMetricSources.BinanceFutures, SentimentMetrics.FundingRate, "BTC",
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), 60);

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        var violation = Assert.Single(newly);
        Assert.Equal("Funding:BTC", violation.Key);
        Assert.Contains("profondità persa", violation.Problem);
        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Critical, alert.Severity);
        Assert.Contains("serie-patrimonio", alert.Title);
        Assert.Contains("Funding BTC", alert.Body);
        Assert.Contains("fundingbackfill", alert.Body); // la notifica dice anche COME si ripara

        // Secondo e terzo giro: violata e già segnalata, silenzio (ma l'Error nei log continua).
        Assert.Empty(await worker.RunOnceAsync(CancellationToken.None));
        Assert.Empty(await worker.RunOnceAsync(CancellationToken.None));
        Assert.Single(notifier.Sent);
        Assert.Single(snapshot.Violations);
    }

    [Fact]
    public async Task SerieAssente_EViolazione_NonUnCasoDaSaltare()
    {
        // La trappola di B2.a vista dal lato patrimonio: zero righe NON vale «niente da controllare»
        // — un null trattato da via-libera è esattamente come il funding è sparito senza rumore.
        var (worker, snapshot, db, notifier) = await BuildAsync();
        await SeedAllHealthyAsync(db);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "ETH")
                .ExecuteDeleteAsync();
        }

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        var violation = Assert.Single(newly);
        Assert.Equal("Funding:ETH", violation.Key);
        Assert.Contains("ASSENTE", violation.Problem);
        Assert.Null(violation.OldestUtc);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task ConteggioSottoSoglia_EViolazioneAncheConStoriaProfonda()
    {
        // Una serie che parte dal 2019 ma con 12 punti non è una storia: è un guscio vuoto che
        // farebbe passare il check sulla sola data.
        var (worker, _, db, _) = await BuildAsync();
        await SeedAllHealthyAsync(db);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            // BTC: si tolgono tutti i punti tranne i primi 10 (profondo ma rado).
            var keep = await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC")
                .OrderBy(p => p.TimestampUtc).Take(10).Select(p => p.Id).ToListAsync();
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC" && !keep.Contains(p.Id))
                .ExecuteDeleteAsync();
        }

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        var violation = Assert.Single(newly);
        Assert.Contains("solo 10", violation.Problem);
        Assert.Contains("50", violation.Problem); // la soglia dichiarata sta nel messaggio
    }

    [Fact]
    public async Task AltreMetricheEAltriSimboli_NonContanoPerLaSoglia()
    {
        // Il filtro è Source+Metric+Symbol: mille punti di OpenInterest non sono storia di funding,
        // e un simbolo fuori dalla lista sorvegliata non compare nella fotografia.
        var (worker, snapshot, db, _) = await BuildAsync();
        await SeedAllHealthyAsync(db);
        await SeedPointsAsync(db, SentimentMetricSources.BinanceFutures, SentimentMetrics.OpenInterest, "BTC", DeepStart, 200);
        await SeedPointsAsync(db, SentimentMetricSources.BinanceFutures, SentimentMetrics.FundingRate, "SOL", DeepStart, 200);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC")
                .ExecuteDeleteAsync();
        }

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        // BTC funding è sparito: i 200 punti di OpenInterest non lo salvano.
        var violation = Assert.Single(newly);
        Assert.Equal("Funding:BTC", violation.Key);
        // SOL non è in FundingSymbols=[BTC, ETH]: niente riga per lui.
        Assert.DoesNotContain(snapshot.All, d => d.Key == "Funding:SOL");
    }

    [Fact]
    public async Task Rientro_RiarmaLAllarme_EUnaNuovaPerditaSuonaDiNuovo()
    {
        // Il ciclo dell'incidente reale: perdita → ripristino con fundingbackfill → (mesi dopo)
        // seconda perdita. La seconda DEVE suonare: senza riarmo sarebbe già segnalata per sempre.
        var (worker, snapshot, db, notifier) = await BuildAsync();
        await SeedAllHealthyAsync(db);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC")
                .ExecuteDeleteAsync();
        }

        Assert.Single(await worker.RunOnceAsync(CancellationToken.None)); // perdita → allarme
        Assert.Single(notifier.Sent);

        // Ripristino (l'equivalente del fundingbackfill).
        await SeedPointsAsync(db, SentimentMetricSources.BinanceFutures, SentimentMetrics.FundingRate, "BTC", DeepStart, 60);
        Assert.Empty(await worker.RunOnceAsync(CancellationToken.None)); // rientro: riarmo silenzioso
        Assert.Empty(snapshot.Violations);
        Assert.Single(notifier.Sent);

        // Seconda perdita.
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == "BTC")
                .ExecuteDeleteAsync();
        }
        Assert.Single(await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task PiuSerieViolateInsieme_UnaNotificaAggregata()
    {
        // Una perdita della TABELLA colpisce tutte le serie insieme: deve produrre un messaggio,
        // non uno per serie (il rate-limit del canale scarterebbe gli altri in silenzio).
        var (worker, _, _, notifier) = await BuildAsync(); // database completamente vuoto

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(4, newly.Count); // BTC, ETH, F&G, liquidazioni: tutte assenti
        var alert = Assert.Single(notifier.Sent);
        Assert.Contains("4 serie-patrimonio", alert.Title);
        Assert.Contains("Funding BTC", alert.Body);
        Assert.Contains("Fear & Greed", alert.Body);
        Assert.Contains("Liquidazioni", alert.Body);
    }

    [Fact]
    public async Task LiquidazioniNonSorvegliate_MisurateMaSenzaAllarme()
    {
        // Il caso trovato dal collaudo a browser (2026-08-13): dalle postazioni EEA lo stream
        // futures e' muto (MiCA) e l'accumulo resta a zero per costruzione — l'allarme sarebbe
        // perpetuo, cioe' mai letto. Con l'interruttore spento la riga resta MISURATA e presente
        // (mai un OK finto: Enforced=false, non Problem=null-e-basta), ma non genera allarmi.
        var guard = TestThresholds();
        guard.LiquidationsEnforced = false;
        var (worker, snapshot, db, notifier) = await BuildAsync(guard);
        await SeedAllHealthyAsync(db);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            await ctx.SentimentMetricPoints
                .Where(p => p.Source == SentimentMetricSources.BinanceLiquidations)
                .ExecuteDeleteAsync();
        }

        var newly = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(newly);
        Assert.Empty(notifier.Sent);
        var row = Assert.Single(snapshot.All, d => d.Key == "Liquidations");
        Assert.False(row.Enforced);
        Assert.False(row.Violated);
        Assert.Equal(0, row.Count);   // la misura resta onesta: zero punti, dichiarati
        Assert.Empty(snapshot.Violations);
    }

    [Fact]
    public void LaSorveglianzaDelleLiquidazioni_EAccesaDiDefault()
        // Lo spegnimento e' una scelta consapevole per postazioni bloccate, non il default.
        => Assert.True(new SentimentHeritageGuardOptions().LiquidationsEnforced);
}
