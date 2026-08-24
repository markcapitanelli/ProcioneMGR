using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D2, completamento] Verifica della fotografia in memoria e del job che la aggiorna — i due
/// pezzi che il PRD §5e chiedeva ("alert accanto al widget in Home" + "job di calcolo periodico")
/// e che mancavano: il pannello in <c>/feature-selection</c> risponde solo a chi va a cercarlo.
/// </summary>
public class FactorDriftSnapshotTests
{
    private static FactorDriftReport Report(string name, FactorDriftStatus status, double reference, double recent) =>
        new(name, name, 0, reference, recent, 0.04, status, "…", []);

    [Fact]
    public void AnEmptySnapshot_HasNoAlertsAndNoRun()
    {
        var snapshot = new FactorDriftSnapshot();

        Assert.Empty(snapshot.Alerts);
        Assert.Empty(snapshot.All);
        Assert.Null(snapshot.LastRunUtc);
    }

    [Fact]
    public void Alerts_PutTheMostSevereFirst()
    {
        var snapshot = new FactorDriftSnapshot();
        var now = DateTime.UtcNow;
        snapshot.Replace(
        [
            new FactorDriftSeriesSnapshot("BTC/USDT", "1h", now,
            [
                Report("spento", FactorDriftStatus.Weakening, 0.06, 0.01),
                Report("stabile", FactorDriftStatus.Stable, 0.06, 0.06),
                Report("invertito", FactorDriftStatus.SignFlip, 0.06, -0.06),
            ]),
        ], now);

        var alerts = snapshot.Alerts;

        // SignFlip è più grave di Weakening; lo Stable non è un alert e non compare.
        Assert.Equal(2, alerts.Count);
        Assert.Equal("invertito", alerts[0].Report.FeatureName);
        Assert.Equal("spento", alerts[1].Report.FeatureName);
        Assert.Equal(now, snapshot.LastRunUtc);
    }

    [Fact]
    public void Replace_SwapsTheWholePictureInsteadOfMerging()
    {
        // Una fotografia parziale sarebbe peggio di nessuna: se una serie sparisce dalla watchlist,
        // i suoi vecchi allarmi non devono restare appesi.
        var snapshot = new FactorDriftSnapshot();
        snapshot.Replace([new FactorDriftSeriesSnapshot("BTC/USDT", "1h", DateTime.UtcNow,
            [Report("vecchio", FactorDriftStatus.Weakening, 0.06, 0.01)])], DateTime.UtcNow);

        snapshot.Replace([new FactorDriftSeriesSnapshot("ETH/USDT", "4h", DateTime.UtcNow,
            [Report("nuovo", FactorDriftStatus.SignFlip, 0.06, -0.06)])], DateTime.UtcNow);

        Assert.Single(snapshot.All);
        Assert.Equal("nuovo", Assert.Single(snapshot.Alerts).Report.FeatureName);
    }

    [Fact]
    public void SeriesSnapshot_ExposesOnlyItsAlerts()
    {
        var s = new FactorDriftSeriesSnapshot("BTC/USDT", "1h", DateTime.UtcNow,
        [
            Report("a", FactorDriftStatus.Stable, 0.06, 0.06),
            Report("b", FactorDriftStatus.Insufficient, 0, 0),
            Report("c", FactorDriftStatus.Weakening, 0.06, 0.01),
        ]);

        Assert.Equal("c", Assert.Single(s.Alerts).FeatureName);
    }
}

/// <summary>Il job che alimenta la fotografia, con Postgres effimero e candele vere.</summary>
[Collection("Postgres")]
public sealed class FactorDriftWorkerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FactorDriftWorkerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(FactorDriftWorker Worker, FactorDriftSnapshot Snapshot, IDbContextFactory<ApplicationDbContext> Db)>
        BuildAsync(Dictionary<string, string?>? settings = null)
    {
        var built = await BuildFullAsync(settings);
        return (built.Worker, built.Snapshot, built.Db);
    }

    /// <summary>Come <see cref="BuildAsync"/> ma espone anche lo store, per i test sulla persistenza.</summary>
    private async Task<(FactorDriftWorker Worker, FactorDriftSnapshot Snapshot,
        IDbContextFactory<ApplicationDbContext> Db, IFactorIcHistoryStore History)>
        BuildFullAsync(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var snapshot = new FactorDriftSnapshot();
        var history = new FactorIcHistoryStore(dbFactory);
        var worker = new FactorDriftWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FactorDriftAnalyzer(),
            new AlphaFactorFactory(),
            history,
            snapshot,
            BindFactorDriftOptions(settings),
            _provider.GetRequiredService<ILogger<FactorDriftWorker>>());

        return (worker, snapshot, dbFactory, history);
    }

    /// <summary>
    /// Un secondo worker sullo STESSO database ma con fotografia vuota: è la simulazione del riavvio
    /// del guscio, che è il caso per cui la persistenza esiste.
    /// </summary>
    private (FactorDriftWorker Worker, FactorDriftSnapshot Snapshot) RestartShell(
        IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        var provider = _provider ?? throw new InvalidOperationException("BuildFullAsync non ancora chiamato.");
        var snapshot = new FactorDriftSnapshot();
        var worker = new FactorDriftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FactorDriftAnalyzer(),
            new AlphaFactorFactory(),
            new FactorIcHistoryStore(dbFactory),
            snapshot,
            BindFactorDriftOptions(null),
            provider.GetRequiredService<ILogger<FactorDriftWorker>>());
        return (worker, snapshot);
    }

    /// <summary>
    /// [M1] I test continuano a dichiarare le impostazioni con le chiavi vere
    /// (<c>FactorDrift:MaxSeries</c>), che è ciò che si vuole provare: il binding dalla sezione al
    /// POCO fa parte del percorso, e passare direttamente un oggetto lo salterebbe.
    /// </summary>
    private static IOptionsMonitor<FactorDriftOptions> BindFactorDriftOptions(Dictionary<string, string?>? settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var options = new FactorDriftOptions();
        config.GetSection("FactorDrift").Bind(options);
        return new StaticOptionsMonitor<FactorDriftOptions>(options);
    }

    /// <summary>
    /// Serie con una relazione fattore→rendimento che si spegne sul CONFINE riferimento/recente:
    /// alert atteso.
    ///
    /// <para>[2026-08-24] La rottura era a metà serie. Con la regola precedente — soglia assoluta —
    /// la posizione non contava; da quando il calo si giudica contro la propria dispersione, sì:
    /// una rottura a metà cade DENTRO il periodo di riferimento, che diventa incoerente con sé
    /// stesso e non può fare da metro. Il decadimento che questo monitor dichiara di trovare è
    /// quello recente, e il test ora lo pianta lì.</para>
    /// </summary>
    private static List<OhlcvData> DecayingSeries(
        int n, string symbol, string timeframe, int seed = 4, double breakFraction = 0.75)
    {
        var rnd = new Random(seed);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var prevReturn = 0d;

        for (var i = 0; i < n; i++)
        {
            // Prima parte: forte momentum (il rendimento continua quello precedente).
            // Coda: rumore puro. Il fattore Momentum deve accorgersene.
            var drift = i < (int)(n * breakFraction)
                ? prevReturn * 0.7 + (rnd.NextDouble() - 0.5) * 0.004
                : (rnd.NextDouble() - 0.5) * 0.02;
            prevReturn = drift;
            var next = Math.Max(1m, price * (1m + (decimal)drift));
            candles.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = start.AddHours(i),
                Open = price,
                High = Math.Max(price, next) * 1.001m,
                Low = Math.Min(price, next) * 0.999m,
                Close = next,
                Volume = 1000m + (decimal)(rnd.NextDouble() * 100),
            });
            price = next;
        }
        return candles;
    }

    private static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, int candleCount, bool enabled = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = ExchangeName.Binance,
            Symbol = symbol,
            Timeframe = timeframe,
            Enabled = enabled,
        });
        db.OhlcvData.AddRange(DecayingSeries(candleCount, symbol, timeframe));
        await db.SaveChangesAsync();
    }

    // --- Il giro del job -------------------------------------------------------------------------

    [Fact]
    public async Task RunOnce_PopulatesTheSnapshotForEachTrackedSeries()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 6000);
        await SeedAsync(db, "ETH/USDT", "1h", 6000);

        await worker.RunOnceAsync();

        Assert.Equal(2, snapshot.All.Count);
        Assert.NotNull(snapshot.LastRunUtc);
        Assert.All(snapshot.All, s => Assert.NotEmpty(s.Reports));
        // Otto fattori scritti a mano monitorati per serie.
        Assert.All(snapshot.All, s => Assert.Equal(8, s.Reports.Count));
    }

    [Fact]
    public async Task RunOnce_OnASeriesWhereAFactorDies_RaisesAnAlert()
    {
        // La serie ha momentum forte nella prima metà e rumore nella seconda: il monitor deve
        // accorgersene. È il controllo che il job misuri davvero, invece di girare a vuoto.
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 8000);

        await worker.RunOnceAsync();

        var alerts = snapshot.Alerts;
        Assert.NotEmpty(alerts);

        // [2026-08-24] Si pretende che il monitor trovi il FENOMENO piantato, non che a trovarlo
        // sia un fattore col nome giusto. Il momentum che si spegne fa collassare tutta la
        // famiglia — MeanReversion, RsiFactor, MacdFactor leggono la stessa struttura — e dopo la
        // correzione per molteplicità (otto fattori giudicati insieme) non è detto che passino
        // tutti: pretendere un nome preciso significherebbe pretendere che BH non faccia il suo
        // mestiere proprio sulla serie in cui c'è più segnale.
        Assert.All(alerts, a =>
        {
            Assert.True(Math.Abs(a.Report.ReferenceIc) > 0.15d,
                $"{a.Report.FeatureName}: il riferimento deve essere chiaramente informativo, era {a.Report.ReferenceIc:F3}");
            Assert.True(Math.Abs(a.Report.RecentIc) < 0.05d,
                $"{a.Report.FeatureName}: il recente deve essersi spento, era {a.Report.RecentIc:F3}");
        });

        // E il fattore Momentum, che è quello direttamente piantato, deve comunque MOSTRARE il
        // crollo nei suoi numeri — anche se la correzione per molteplicità lo trattiene dall'essere
        // un allarme. Il dato non deve sparire con il verdetto.
        var momentum = snapshot.All.SelectMany(s => s.Reports).Single(r => r.FeatureName == "Momentum");
        Assert.True(Math.Abs(momentum.ReferenceIc) > Math.Abs(momentum.RecentIc) * 3d,
            $"Momentum: atteso un crollo, riferimento {momentum.ReferenceIc:F3} contro recente {momentum.RecentIc:F3}");
    }

    [Fact]
    public async Task RunOnce_SkipsSeriesWithTooFewCandles()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "TINY/USDT", "1h", 200); // sotto il minimo di 500

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
        Assert.NotNull(snapshot.LastRunUtc); // il giro è comunque avvenuto
    }

    [Fact]
    public async Task RunOnce_IgnoresDisabledSeries()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "OFF/USDT", "1h", 6000, enabled: false);

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
    }

    [Fact]
    public async Task RunOnce_RespectsTheMaxSeriesCap()
    {
        // Il tetto esiste perché il monitor non diventi un consumo di CPU proporzionale alla
        // watchlist: chi vuole guardare tutto lo fa su richiesta da /feature-selection.
        var (worker, snapshot, db) = await BuildAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "1",
        });
        await SeedAsync(db, "AAA/USDT", "1h", 6000);
        await SeedAsync(db, "BBB/USDT", "1h", 6000);

        await worker.RunOnceAsync();

        Assert.Single(snapshot.All);
    }

    [Fact]
    public async Task RunOnce_WithNoTrackedSeries_LeavesAnEmptyButValidSnapshot()
    {
        var (worker, snapshot, _) = await BuildAsync();

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
        Assert.Empty(snapshot.Alerts);
        Assert.NotNull(snapshot.LastRunUtc);
    }

    // --- La storia registrata (D2, persistenza 2026-07-28) ---------------------------------------

    /// <summary>Serie di puro rumore: nessuna relazione fattore→rendimento da trovare.</summary>
    private static List<OhlcvData> NoiseSeries(int n, string symbol, string timeframe, int seed)
    {
        var rnd = new Random(seed);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < n; i++)
        {
            var next = Math.Max(1m, price * (1m + (decimal)((rnd.NextDouble() - 0.5) * 0.02)));
            candles.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = start.AddHours(i),
                Open = price,
                High = Math.Max(price, next) * 1.001m,
                Low = Math.Min(price, next) * 0.999m,
                Close = next,
                Volume = 1000m + (decimal)(rnd.NextDouble() * 100),
            });
            price = next;
        }
        return candles;
    }

    private static async Task SeedNoiseAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, int candleCount, int seed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = ExchangeName.Binance,
            Symbol = symbol,
            Timeframe = timeframe,
            Enabled = true,
        });
        db.OhlcvData.AddRange(NoiseSeries(candleCount, symbol, timeframe, seed));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RunOnce_WritesTheIcWindowsToTheHistory()
    {
        var (worker, snapshot, db, _) = await BuildFullAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 8000);

        await worker.RunOnceAsync();

        await using var check = await db.CreateDbContextAsync();
        var rows = await check.FactorIcWindows.ToListAsync();

        Assert.NotEmpty(rows);
        // Una riga per finestra per fattore: la fotografia in memoria e la tabella devono raccontare
        // esattamente la stessa cosa, altrimenti il pannello e la Home divergono.
        var expected = snapshot.All.Single().Reports.Sum(r => r.Series.Count);
        Assert.Equal(expected, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("BTC/USDT", r.Symbol);
            Assert.Equal("1h", r.Timeframe);
            Assert.Equal(1, r.ForwardHorizon);
            Assert.True(r.WindowSize >= 250);
            Assert.True(r.WindowEndUtc > r.WindowStartUtc);
        });
    }

    [Fact]
    public async Task RunningTwice_UpdatesTheSameRowsInsteadOfDuplicatingThem()
    {
        // Il worker gira ogni 12 ore sulle stesse candele: senza l'indice unico la tabella
        // crescerebbe di un duplicato per giro, per sempre.
        var (worker, _, db, _) = await BuildFullAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 8000);

        await worker.RunOnceAsync();
        int afterFirst;
        await using (var check1 = await db.CreateDbContextAsync())
        {
            afterFirst = await check1.FactorIcWindows.CountAsync();
        }

        await worker.RunOnceAsync();

        await using var check2 = await db.CreateDbContextAsync();
        Assert.Equal(afterFirst, await check2.FactorIcWindows.CountAsync());
    }

    [Fact]
    public async Task AfterAShellRestart_TheAlertIsAlreadyThereWithoutRecomputing()
    {
        // È il motivo per cui la persistenza esiste: il guscio si riavvia di continuo, e prima
        // l'alert in Home ricompariva solo dopo il primo giro del job (2 minuti + calcolo).
        var (worker, snapshot, db, _) = await BuildFullAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 8000);
        await worker.RunOnceAsync();
        var expected = snapshot.Alerts.Select(a => a.Report.FeatureName).OrderBy(n => n).ToList();
        Assert.NotEmpty(expected);

        var (restarted, freshSnapshot) = RestartShell(db);
        Assert.Empty(freshSnapshot.Alerts); // fotografia vuota, come dopo un riavvio

        await restarted.HydrateAsync();

        var rebuilt = freshSnapshot.Alerts.Select(a => a.Report.FeatureName).OrderBy(n => n).ToList();
        Assert.Equal(expected, rebuilt);
        Assert.Equal("BTC/USDT", freshSnapshot.All.Single().Symbol);
        Assert.NotNull(freshSnapshot.LastRunUtc);
    }

    [Fact]
    public async Task Hydrate_DoesNotResurrectASeriesRemovedFromTheWatchlist()
    {
        // La storia resta in tabella (è un'osservazione vera), ma un allarme su una serie che non si
        // segue più sarebbe rumore in un pannello che deve restare leggibile.
        var (worker, _, db, _) = await BuildFullAsync();
        await SeedAsync(db, "GONE/USDT", "1h", 8000);
        await worker.RunOnceAsync();

        await using (var edit = await db.CreateDbContextAsync())
        {
            var series = await edit.TrackedSeries.SingleAsync(s => s.Symbol == "GONE/USDT");
            series.Enabled = false;
            await edit.SaveChangesAsync();
        }

        var (restarted, freshSnapshot) = RestartShell(db);
        await restarted.HydrateAsync();

        Assert.Empty(freshSnapshot.All);
        await using var check = await db.CreateDbContextAsync();
        Assert.True(await check.FactorIcWindows.AnyAsync(), "la storia deve restare in tabella");
    }

    [Fact]
    public async Task Hydrate_WithAnEmptyHistory_LeavesTheSnapshotEmptyAndSilent()
    {
        var (worker, snapshot, _, _) = await BuildFullAsync();

        await worker.HydrateAsync();

        Assert.Empty(snapshot.All);
        Assert.Null(snapshot.LastRunUtc); // niente storia = nessuna pretesa di aver girato
    }

    [Fact]
    public async Task OnPureNoise_TheHistoryRecordsWindowsButRaisesNoAlert()
    {
        // Controllo sul rumore, lo stesso principio dei 40 semi nell'analizzatore: la persistenza non
        // deve trasformare il caso in un allarme che sopravvive ai riavvii.
        var (worker, snapshot, db, _) = await BuildFullAsync();
        await SeedNoiseAsync(db, "RANDOM/USDT", "1h", 8000, seed: 12345);

        await worker.RunOnceAsync();
        Assert.Empty(snapshot.Alerts);

        var (restarted, freshSnapshot) = RestartShell(db);
        await restarted.HydrateAsync();

        Assert.NotEmpty(freshSnapshot.All);       // la storia c'è
        Assert.Empty(freshSnapshot.Alerts);       // ma non inventa allarmi
    }

    [Theory]
    [InlineData(8000, 750)]      // 800 → quantizzato a 750
    [InlineData(26_929, 2750)]   // il caso reale misurato su BTC/USDT 1h
    [InlineData(27_400, 2750)]   // 500 candele in più: stessa finestra, la serie resta comparabile
    [InlineData(1000, 250)]      // sotto il minimo: pavimento a 250
    [InlineData(90_000, 3000)]   // sopra il massimo: tetto a 3000
    public void WindowSizeFor_IsQuantizedSoTheRecordedSeriesKeepsOneDefinition(int candles, int expected)
    {
        Assert.Equal(expected, FactorDriftWorker.WindowSizeFor(candles));
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(20_000)]
    [InlineData(26_929)]
    public void TheJobAndThePanel_ProposeTheSameWindowOnTheSameSample(int observations)
    {
        // Difetto trovato guardando l'app dal vivo: il job quantizzava e il pannello no, quindi sulla
        // STESSA serie uscivano finestre diverse ⇒ soglie diverse (1,96/√n) ⇒ verdetti che si
        // contraddicevano ("si è spento" contro "non ha mai informato"). Ora la regola è una sola:
        // questo test è il guardiano che resti tale.
        Assert.Equal(FactorDriftAnalyzer.SuggestWindowSize(observations), FactorDriftWorker.WindowSizeFor(observations));
    }

    // --- Rotazione e copertura -------------------------------------------------------------------

    [Fact]
    public async Task TheJobRotates_TheSecondRoundPicksTheSeriesItHasNotSeenYet()
    {
        // Prima si prendevano sempre le prime N per Id: con una watchlist da centinaia di serie il
        // monitor guardava per sempre lo stesso 2%, e "nessun allarme" voleva dire "non ho guardato".
        var (worker, _, db, _) = await BuildFullAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "1",
        });
        await SeedAsync(db, "AAA/USDT", "1h", 6000);
        await SeedAsync(db, "BBB/USDT", "1h", 6000);

        await worker.RunOnceAsync();
        await worker.RunOnceAsync();

        await using var check = await db.CreateDbContextAsync();
        var seen = await check.FactorIcWindows.Select(w => w.Symbol).Distinct().ToListAsync();

        Assert.Equal(2, seen.Count);   // entrambe viste in due giri, non una sola due volte
        Assert.Contains("AAA/USDT", seen);
        Assert.Contains("BBB/USDT", seen);
    }

    [Fact]
    public async Task ASeriesThatCannotBeComputed_DoesNotStarveTheOthers()
    {
        // Una serie con troppe poche candele viene saltata e non scrive nulla in tabella: ordinando
        // per solo "ultimo calcolo" resterebbe in cima alla coda PER SEMPRE e occuperebbe un posto a
        // ogni giro. Con un solo posto per giro, la serie buona non verrebbe mai calcolata.
        var (worker, _, db, _) = await BuildFullAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "1",
        });
        await SeedAsync(db, "AAA-TINY/USDT", "1h", 200);   // sotto il minimo di 500: sempre saltata
        await SeedAsync(db, "BBB/USDT", "1h", 6000);

        await worker.RunOnceAsync();   // tocca la prima (in ordine alfabetico), che viene saltata
        await worker.RunOnceAsync();   // deve passare alla seconda, non riprovare la prima

        await using var check = await db.CreateDbContextAsync();
        var seen = await check.FactorIcWindows.Select(w => w.Symbol).Distinct().ToListAsync();

        Assert.Equal("BBB/USDT", Assert.Single(seen));
    }

    [Fact]
    public async Task AfterARotatedRound_TheSnapshotStillShowsTheSeriesOfPreviousRounds()
    {
        // Con la rotazione, sostituire la fotografia con i soli risultati del giro corrente farebbe
        // sparire dalla Home gli allarmi delle serie viste il giro prima. La fotografia si ricostruisce
        // dalla tabella, che le contiene tutte.
        var (worker, snapshot, db, _) = await BuildFullAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "1",
        });
        await SeedAsync(db, "AAA/USDT", "1h", 8000);
        await SeedAsync(db, "BBB/USDT", "1h", 8000);

        await worker.RunOnceAsync();
        Assert.Single(snapshot.All);

        await worker.RunOnceAsync();

        Assert.Equal(2, snapshot.All.Count);
        Assert.Equal(2, snapshot.TrackedSeriesCount);
    }

    // --- [2.9] I fattori del Champion --------------------------------------------------------------

    /// <summary>Aggiunge un SavedMlModel con i fattori dati (l'utente FK viene creato se manca).</summary>
    private static async Task SeedModelAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, ModelStage stage, string factorsJson, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == "u"))
        {
            db.Users.Add(new ApplicationUser { Id = "u", UserName = "u@t.io" });
        }
        db.SavedMlModels.Add(new SavedMlModel
        {
            UserId = "u",
            Name = name,
            ModelType = "Linear",
            Symbol = symbol,
            Timeframe = timeframe,
            Stage = stage,
            FactorsJson = factorsJson,
            ModelBytes = [],
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RunOnce_WithAChampion_AlsoWatchesItsFactors_ButNotThoseOfStaging()
    {
        // Il perché della 2.9: l'esclusione in blocco di Alpha158 lasciava scoperti proprio i
        // fattori che un modello IN CARICA sta usando. La base resta di 8; il Champion aggiunge i
        // suoi; un fattore già nella base (Momentum) non si conta due volte; un modello Staging non
        // è in carica e non aggiunge nulla.
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 6000);
        await SeedModelAsync(db, "BTC/USDT", "1h", ModelStage.Champion, JsonSerializer.Serialize(
            new List<SavedFactorSpecDto>
            {
                new("A158_ROC_20", "A158_ROC_20", new()),
                new("Momentum", "Momentum", new()),
            }), "Campione");
        await SeedModelAsync(db, "BTC/USDT", "1h", ModelStage.Staging, JsonSerializer.Serialize(
            new List<SavedFactorSpecDto> { new("A158_KLEN", "A158_KLEN", new()) }), "Esperimento");

        await worker.RunOnceAsync();

        var reports = snapshot.All.Single().Reports;
        Assert.Equal(9, reports.Count); // 8 di base + ROC_20 (Momentum non raddoppia)
        Assert.Contains(reports, r => r.FeatureName == "A158_ROC_20");
        Assert.DoesNotContain(reports, r => r.FeatureName == "A158_KLEN");
    }

    [Fact]
    public async Task RunOnce_AChampionOnAnotherSeries_AddsNothingHere()
    {
        // I fattori del Champion valgono per la SUA coppia (Symbol, Timeframe), non per tutte.
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 6000);
        await SeedModelAsync(db, "ETH/USDT", "4h", ModelStage.Champion, JsonSerializer.Serialize(
            new List<SavedFactorSpecDto> { new("A158_ROC_20", "A158_ROC_20", new()) }), "Altrove");

        await worker.RunOnceAsync();

        Assert.Equal(8, snapshot.All.Single().Reports.Count);
    }

    [Fact]
    public async Task RunOnce_BrokenOrUnknownChampionFactors_DegradeWithoutBreakingTheRound()
    {
        // Fail-open (regola 4): il monitor è advisory. Un FactorsJson illeggibile o un fattore che
        // la factory non conosce più si SALTANO con un log; la base di 8 si calcola comunque.
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 6000);
        await SeedModelAsync(db, "BTC/USDT", "1h", ModelStage.Champion, "{non-json", "Rotto");
        await SeedModelAsync(db, "BTC/USDT", "1h", ModelStage.Champion, JsonSerializer.Serialize(
            new List<SavedFactorSpecDto> { new("FattoreScomparso", "FattoreScomparso", new()) }), "Orfano");

        await worker.RunOnceAsync();

        Assert.Equal(8, snapshot.All.Single().Reports.Count);
        Assert.NotNull(snapshot.LastRunUtc);
    }

    [Fact]
    public async Task TheSnapshotDeclaresHowManySeriesAreInTheWatchlist()
    {
        // "1 fattore in deriva" su 5 serie di 228 è vero e fuorviante: la UI deve poter dire su
        // quante serie è basato ciò che mostra.
        var (worker, snapshot, db, _) = await BuildFullAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "2",
        });
        await SeedAsync(db, "AAA/USDT", "1h", 6000);
        await SeedAsync(db, "BBB/USDT", "1h", 6000);
        await SeedAsync(db, "CCC/USDT", "1h", 6000);

        await worker.RunOnceAsync();

        Assert.Equal(2, snapshot.All.Count);          // calcolate in questo giro
        Assert.Equal(3, snapshot.TrackedSeriesCount); // dichiarate in watchlist
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}

/// <summary>
/// Lo store della storia dell'IC, sul database vero: le due domande che la UI gli fa (la serie di un
/// fattore, la fotografia di una serie) e il caso spinoso della griglia che cambia ampiezza.
/// </summary>
[Collection("Postgres")]
public sealed class FactorIcHistoryStoreTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FactorIcHistoryStoreTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(IFactorIcHistoryStore Store, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
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
        return (new FactorIcHistoryStore(dbFactory), dbFactory);
    }

    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FactorDriftReport ReportWith(string name, int windowSize, int windows, double ic, int dayOffset = 0)
    {
        var series = Enumerable.Range(0, windows)
            .Select(i => new FactorIcPoint(
                Origin.AddDays(dayOffset + i), Origin.AddDays(dayOffset + i + 1), ic, windowSize))
            .ToList();
        return new FactorDriftReport(name, name, ic, ic, ic, 0.04, FactorDriftStatus.Stable, "…", series);
    }

    [Fact]
    public async Task SaveThenLoad_GivesBackTheSeriesInChronologicalOrder()
    {
        var (store, _) = await BuildAsync();
        var report = ReportWith("Momentum", windowSize: 500, windows: 6, ic: 0.05);

        var inserted = await store.SaveAsync("BTC/USDT", "1h", 1, [report], DateTime.UtcNow);
        var series = await store.LoadSeriesAsync("BTC/USDT", "1h", "Momentum");

        Assert.Equal(6, inserted);
        Assert.Equal(6, series.Count);
        Assert.True(series.Zip(series.Skip(1)).All(p => p.First.WindowEndUtc < p.Second.WindowEndUtc));
    }

    [Fact]
    public async Task SavingTheSameWindowTwice_OverwritesTheValueAndInsertsNothing()
    {
        var (store, db) = await BuildAsync();
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 4, 0.05)], DateTime.UtcNow);

        var inserted = await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 4, -0.09)], DateTime.UtcNow);

        Assert.Equal(0, inserted);
        await using var check = await db.CreateDbContextAsync();
        Assert.Equal(4, await check.FactorIcWindows.CountAsync());
        // L'ultimo calcolo vince: se le candele sono state corrette a posteriori, il valore nuovo è
        // quello buono.
        Assert.All(await check.FactorIcWindows.ToListAsync(), r => Assert.Equal(-0.09, r.InformationCoefficient, 12));
    }

    [Fact]
    public async Task WhenTheWindowSizeChanges_OnlyTheMostRecentGridIsReturned()
    {
        // L'ampiezza si adatta ai dati disponibili, quindi crescendo lo storico può cambiare. Le due
        // griglie NON sono comparabili (pavimenti di rumore diversi): mescolarle darebbe una spezzata
        // più lunga e un confronto senza senso.
        var (store, _) = await BuildAsync();
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 5, 0.05)], DateTime.UtcNow);
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 750, 5, 0.02, dayOffset: 10)], DateTime.UtcNow);

        var series = await store.LoadSeriesAsync("BTC/USDT", "1h", "Momentum");

        Assert.Equal(5, series.Count);
        Assert.All(series, p => Assert.Equal(750, p.Observations));
    }

    [Fact]
    public async Task WindowsFromDifferentRounds_AreNeverReturnedOverlapping()
    {
        // Difetto trovato guardando la pagina viva: «18 × 2000» finestre là dove in 20.000 candele ce
        // ne stanno 9. Il job carica le ULTIME N candele, quindi quando ne arrivano di nuove l'inizio
        // della fetta scivola e i confini cadono altrove: due giri scrivono finestre della stessa
        // ampiezza ma sfalsate, che in tabella si accumulano SOVRAPPOSTE. Punti che condividono dati
        // sono correlati per costruzione e fanno sembrare la serie più stabile di quanto è — cioè
        // esattamente ciò che l'analizzatore evita usando finestre non sovrapposte.
        var (store, _) = await BuildAsync();

        // Primo giro: finestre [0,1], [1,2], [2,3] giorni.
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 3, 0.05)], DateTime.UtcNow);

        // Secondo giro, griglia sfalsata di mezza giornata: [0.5,1.5], [1.5,2.5], [2.5,3.5].
        var shifted = new FactorDriftReport("Momentum", "Momentum", 0.02, 0.02, 0.02, 0.04,
            FactorDriftStatus.Stable, "…",
            Enumerable.Range(0, 3)
                .Select(i => new FactorIcPoint(
                    Origin.AddDays(i).AddHours(12), Origin.AddDays(i + 1).AddHours(12), 0.02, 500))
                .ToList());
        await store.SaveAsync("BTC/USDT", "1h", 1, [shifted], DateTime.UtcNow);

        var series = await store.LoadSeriesAsync("BTC/USDT", "1h", "Momentum");

        Assert.Equal(6, await CountRowsAsync());               // in tabella ci sono tutte
        Assert.True(series.Count < 6, "la serie restituita non può contenerle tutte: si sovrappongono");
        for (var i = 1; i < series.Count; i++)
        {
            Assert.True(series[i].WindowStartUtc >= series[i - 1].WindowEndUtc,
                $"finestre sovrapposte: [{series[i - 1].WindowStartUtc:o}, {series[i - 1].WindowEndUtc:o}] e [{series[i].WindowStartUtc:o}, {series[i].WindowEndUtc:o}]");
        }
        // La catena parte dal presente: l'ultima misura c'è sempre, perché è quella su cui si giudica
        // il "recente".
        Assert.Equal(Origin.AddDays(3).AddHours(12), series[^1].WindowEndUtc);
    }

    private async Task<int> CountRowsAsync()
    {
        var dbFactory = _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FactorIcWindows.CountAsync();
    }

    [Fact]
    public async Task LoadSnapshot_ReturnsOnlyTheRequestedSeries()
    {
        var (store, _) = await BuildAsync();
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 6, 0.05)], DateTime.UtcNow);
        await store.SaveAsync("ETH/USDT", "4h", 1, [ReportWith("RsiFactor", 500, 6, 0.05)], DateTime.UtcNow);

        var btc = await store.LoadSnapshotAsync("BTC/USDT", "1h", new FactorDriftConfig());
        var missing = await store.LoadSnapshotAsync("SOL/USDT", "1h", new FactorDriftConfig());

        Assert.NotNull(btc);
        Assert.Equal("BTC/USDT", btc.Symbol);
        Assert.Equal("Momentum", Assert.Single(btc.Reports).FeatureName);
        Assert.Null(missing);
    }

    [Fact]
    public async Task LoadSeries_ForAFactorNeverRecorded_IsEmptyNotAnError()
    {
        var (store, _) = await BuildAsync();

        var series = await store.LoadSeriesAsync("BTC/USDT", "1h", "MaiVisto");

        Assert.Empty(series);
    }

    [Fact]
    public async Task DifferentForwardHorizons_AreDifferentSeriesAndDoNotMix()
    {
        // L'IC a 1 barra e quello a 5 barre sono misure diverse: mescolarle nella stessa spezzata
        // darebbe una serie senza significato, e il verdetto girerebbe su punti incomparabili.
        var (store, _) = await BuildAsync();
        await store.SaveAsync("BTC/USDT", "1h", 1, [ReportWith("Momentum", 500, 6, 0.05)], DateTime.UtcNow);
        await store.SaveAsync("BTC/USDT", "1h", 5, [ReportWith("Momentum", 500, 6, -0.08)], DateTime.UtcNow);

        var h1 = await store.LoadSeriesAsync("BTC/USDT", "1h", "Momentum", forwardHorizon: 1);
        var h5 = await store.LoadSeriesAsync("BTC/USDT", "1h", "Momentum", forwardHorizon: 5);
        var snapshot1 = await store.LoadSnapshotAsync("BTC/USDT", "1h", new FactorDriftConfig { ForwardHorizon = 1 });

        Assert.Equal(6, h1.Count);
        Assert.Equal(6, h5.Count);
        Assert.All(h1, p => Assert.Equal(0.05, p.InformationCoefficient, 12));
        Assert.All(h5, p => Assert.Equal(-0.08, p.InformationCoefficient, 12));
        Assert.Equal(6, Assert.Single(snapshot1!.Reports).Series.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
