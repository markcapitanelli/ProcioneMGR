using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-07] Collaudo di <see cref="BracketDiagnosisService"/> su un database VERO — il servizio
/// che mette accanto «quanti stop la geometria produce da sola» e «quanti stop sono arrivati».
///
/// <para>Le quattro risposte del servizio sono quattro stati del mondo, non tre risposte più un
/// errore, e ognuna ha qui il proprio caso: <b>come previsto</b>, <b>scarto dal previsto</b>,
/// <b>non giudicabile</b> per campione insufficiente, <b>non misurabile</b> perché la gamba non ha
/// protezioni. Il quinto caso è quello che rende affidabili gli altri quattro: le righe di
/// <c>TradeRecords</c> fabbricate dal replay di candele storiche <b>non devono contare</b>, e lo
/// scarto va dichiarato invece che sottratto in silenzio.</para>
/// </summary>
[Collection("Postgres")]
public class DiagnosiDelBracketTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;
    private IDbContextFactory<ApplicationDbContext>? _dbFactory;

    public DiagnosiDelBracketTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private const string Simbolo = "TEST/USDT";
    private const string Tf = "1h";

    /// <summary>
    /// <b>Le date del seme sono relative ad ADESSO, e non per comodità.</b> <c>RecordedAtUtc</c> è
    /// scritta da Postgres e ha <c>BeforeSaveBehavior.Ignore</c>: nessun chiamante può fabbricarla,
    /// per costruzione (K41). Quindi il ritardo di scrittura di una riga seminata è sempre
    /// «adesso meno il tempo di candela che le ho dato» — e un seme con date fisse produce righe
    /// che il servizio classifica, correttamente, come replay.
    ///
    /// <para>È anche l'unico modo di tenere queste date lontane dalla trappola già pagata: un seme
    /// con date fisse invecchia e un giorno arriva a «oggi», e il test cade da solo.</para>
    /// </summary>
    private static DateTime Adesso => new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);

    /// <summary>Le soglie di default, immobili: il verdetto non deve dipendere dal file di configurazione.</summary>
    private sealed class SogliaFissa<T>(T valore) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = valore;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => new Nulla();
        private sealed class Nulla : IDisposable { public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText;
        public string Decrypt(string cipherText) => cipherText;
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

    /// <summary>Il servizio montato su DI vera, con il manager della corsia registrato keyed come in produzione.</summary>
    private async Task<BracketDiagnosisService> ServizioAsync(EnsembleConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddKeyedSingleton<IEnsembleManager>(0, (_, _) => new FakeEnsembleManager(0, config));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        return new BracketDiagnosisService(_dbFactory, _provider,
            new SogliaFissa<BracketDiagnosisOptions>(new BracketDiagnosisOptions()),
            NullLogger<BracketDiagnosisService>.Instance);
    }

    private static EnsembleConfiguration Config(decimal? stop, decimal? take, decimal? tradeAlMese = 10m) => new()
    {
        ExchangeName = "Binance",
        Symbol = Simbolo,
        Timeframe = Tf,
        Strategies =
        [
            new EnsembleStrategy
            {
                StrategyId = "s1",
                StrategyName = "Scripted",
                DisplayName = "Gamba s1",
                IsActive = true,
                StopLossPercent = stop,
                TakeProfitPercent = take,
                ExpectedTradesPerMonth = tradeAlMese,
            },
        ],
    };

    /// <summary>
    /// Serie deterministica a zig-zag: produce escursioni su entrambi i lati senza favorirne uno.
    /// Nessun <c>Random</c>: il verdetto deve essere lo stesso a ogni corsa.
    /// </summary>
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
                Symbol = Simbolo,
                Timeframe = Tf,
                TimestampUtc = inizio.AddHours(i),
                Open = prezzo,
                High = Math.Max(prezzo, close) + ampiezza * 0.6m,
                Low = Math.Min(prezzo, close) - ampiezza * 0.6m,
                Close = close,
                Volume = 1_000m,
            });
            prezzo = close;
        }
        await db.SaveChangesAsync();
    }

    private async Task StatoCorsiaAsync(bool running = true)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync();
        db.TradingEngineStates.Add(new TradingEngineState
        {
            LaneId = 0,
            Symbol = Simbolo,
            Timeframe = Tf,
            IsRunning = running,
            Mode = TradingMode.Paper,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Aggiunge uscite. Il ritardo di scrittura NON si passa: lo produce la distanza fra l'ora di
    /// candela che assegno qui e l'ora di parete che mette Postgres. <paramref name="vecchieDi"/>
    /// sposta le chiusure indietro nel tempo: null o piccolo = trade vivi (sotto la tolleranza di
    /// tre barre più mezz'ora), giorni = righe che il replay avrebbe fabbricato.
    /// </summary>
    private async Task UsciteAsync(int stop, int take, TimeSpan? vecchieDi = null, int minutoIniziale = 0)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync();
        // Le vive stanno tutte dentro l'ultima ora di candela: spaziarle di un'ora le farebbe
        // scivolare oltre la tolleranza una dopo l'altra, ed è il servizio ad avere ragione.
        var baseChiusura = Adesso - (vecchieDi ?? TimeSpan.Zero);
        for (var i = 0; i < stop + take; i++)
        {
            var chiusura = baseChiusura.AddMinutes(minutoIniziale + i);
            db.TradeRecords.Add(new TradeRecord
            {
                LaneId = 0,
                PositionId = $"p{minutoIniziale}-{i}",
                StrategyId = "s1",
                Symbol = Simbolo,
                Side = OrderSide.Buy,
                EntryPrice = 100m,
                ExitPrice = i < stop ? 99m : 103m,
                Quantity = 1m,
                Pnl = i < stop ? -1m : 3m,
                PnlPercent = i < stop ? -1m : 3m,
                OpenedAtUtc = chiusura.AddHours(-1),
                ClosedAtUtc = chiusura,
                Duration = TimeSpan.FromHours(1),
                ExitReason = i < stop ? "StopLoss" : "TakeProfit",
                Mode = TradingMode.Paper,
                // RecordedAtUtc NON si assegna: la mette Postgres, ed è il punto di K41.
            });
        }
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────── i quattro stati del mondo

    /// <summary>
    /// <b>Come previsto.</b> Bracket 1:3, uscite osservate coerenti col nominale che la geometria
    /// produce sulla stessa serie. Il verdetto deve dire che non c'è niente da correggere — ed è la
    /// risposta alla domanda del proprietario, non un modo di archiviarla.
    /// </summary>
    [Fact]
    public async Task BracketAsimmetrico_ConUsciteCoerenti_EComePrevisto()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();

        // Prima la corsa nominale, per costruire l'osservato COERENTE con essa invece di indovinarlo.
        var nominale = await servizio.DiagnoseAsync(0);
        var quotaAttesa = nominale.Nominale!.StopSharePercent;
        var totale = 40;
        var stop = (int)Math.Round(totale * quotaAttesa / 100m);
        await UsciteAsync(stop, totale - stop);

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(VerdettoBracket.ComePrevisto, d.Verdetto);
        Assert.Contains("niente da correggere", d.Racconto);
        Assert.True(d.Nominale!.StopSharePercent > 80m,
            $"un bracket 1:3 deve produrre da solo oltre l'80% di stop: {d.Nominale.StopSharePercent}%");
    }

    /// <summary>
    /// <b>Scarto dal previsto.</b> Stessa geometria, ma le uscite vere sono metà stop e metà target:
    /// molto meglio del nominale. Il servizio deve accorgersene e dire da che parte pende lo scarto —
    /// perché uno scarto in questa direzione è una buona notizia, e va letta come tale.
    /// </summary>
    [Fact]
    public async Task StessoBracket_MaUsciteMoltoMigliori_EUnoScarto()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 20, take: 20);

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(VerdettoBracket.ScartoDalPrevisto, d.Verdetto);
        Assert.Equal(50m, d.Osservato!.StopSharePercent);
        Assert.Contains("MENO spesso", d.Racconto);
    }

    /// <summary>
    /// <b>Non giudicabile.</b> Con poche uscite la differenza fra l'85% e il 50% non si distingue dal
    /// caso. Il servizio non deve imputare niente: deve dire quante ne mancano e, se le gambe
    /// dichiarano un ritmo, in quanto tempo arriveranno. È la stessa disciplina del gate DSR.
    /// </summary>
    [Fact]
    public async Task PocheUscite_NonSiGiudica_ESiDiceQuantoManca()
    {
        var servizio = await ServizioAsync(Config(1m, 3m, tradeAlMese: 4m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 4, take: 1);

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(VerdettoBracket.NonGiudicabile, d.Verdetto);
        Assert.Contains("5 uscite a barriera vive su 20", d.Racconto);
        Assert.Contains("mesi", d.Racconto);
        // Il nominale però esiste ed è utile anche senza campione: è il denominatore.
        Assert.True(d.Nominale!.Misurabile);
    }

    /// <summary>
    /// <b>Non misurabile.</b> Una gamba senza protezioni non ha una corsa da correre. Il servizio
    /// deve dirlo: un verdetto verde su una corsia senza stop sarebbe la bugia peggiore di tutte.
    /// </summary>
    [Fact]
    public async Task GambaSenzaProtezioni_SiDichiaraNonMisurabile()
    {
        var servizio = await ServizioAsync(Config(null, null));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 30, take: 5);

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(VerdettoBracket.NonMisurabile, d.Verdetto);
        Assert.Contains("stop e target", d.Racconto);
        Assert.Null(d.Nominale);
    }

    // ─────────────────────────────────────────────── il caso che rende affidabili gli altri quattro

    /// <summary>
    /// <b>Il replay non conta, e lo scarto si dichiara.</b> Trenta righe con un ritardo di scrittura
    /// di giorni sono candele storiche rigiocate da un riavvio, non operazioni avvenute. Se
    /// entrassero nel conteggio, una corsia che non ha mai operato risulterebbe giudicabile — ed è
    /// esattamente l'errore per cui i trade di forward test veri risultavano 27 invece di 1.
    /// </summary>
    [Fact]
    public async Task LeRigheDaReplay_NonContano_EIlLoroNumeroSiVede()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 3, take: 2);                                        // vive
        await UsciteAsync(stop: 25, take: 5, vecchieDi: TimeSpan.FromDays(9), minutoIniziale: 200);  // replay

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(35, d.Osservato!.RigheTotali);
        Assert.Equal(30, d.Osservato.ReplayScartati);
        Assert.Equal(5, d.Osservato.UsciteABarriera);
        Assert.Contains("30 da replay", d.Osservato.Scarti);

        // E il verdetto cade su «non giudicabile», che è la verità: cinque uscite non bastano.
        Assert.Equal(VerdettoBracket.NonGiudicabile, d.Verdetto);
    }

    /// <summary>
    /// Le repliche della stessa operazione — chiave d'entità identica, <c>PositionId</c> diverso —
    /// si contano una volta sola, e la differenza si vede. Un totale più basso senza spiegazione si
    /// leggerebbe come un guasto.
    /// </summary>
    [Fact]
    public async Task LeRepliche_SiContanoUnaVoltaSola_ELoScartoSiDichiara()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 6, take: 4);
        await UsciteAsync(stop: 6, take: 4);   // stesse chiavi d'entità: sono le stesse dieci operazioni

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(20, d.Osservato!.RigheTotali);
        Assert.Equal(10, d.Osservato.RepliqueScartate);
        Assert.Equal(10, d.Osservato.UsciteABarriera);
        Assert.Contains("10 repliche", d.Osservato.Scarti);
    }

    /// <summary>
    /// <b>Il caso che c'è davvero sul database.</b> Le righe scritte prima che <c>RecordedAtUtc</c>
    /// esistesse non si possono datare: non si sa se siano avvenute o se le abbia fabbricate un
    /// riavvio rigiocando candele vecchie. Il 2026-09-07 sono il <b>100% del campione</b> delle
    /// corsie 2, 3, 4 e 5, e 159 righe su 164 della corsia 0.
    ///
    /// <para>Contarle produrrebbe una percentuale precisa calcolata su un insieme di cui non si sa
    /// niente — lo stesso errore che faceva risultare 27 trade di forward test dove ne era avvenuto
    /// uno. Restano <b>mostrate</b>, perché nascondere la parte esclusa è peggio che escluderla, e il
    /// motivo distingue le due cause: «ne servono altre» finisce con l'attesa, «non si possono datare»
    /// no.</para>
    /// </summary>
    [Fact]
    public async Task LeRigheSenzaOraDiParete_NonEntranoNelGiudizio_MaSiVedono()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 30, take: 6);

        // Le righe storiche non si possono creare da EF: BeforeSaveBehavior.Ignore impedisce di
        // scrivere la colonna, ed è il punto di K41. Si riproducono azzerandola dopo, in SQL —
        // che è esattamente come sono nate le 371 righe vere.
        await using (var db = await _dbFactory!.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"TradeRecords\" SET \"RecordedAtUtc\" = NULL WHERE \"ExitReason\" = 'StopLoss'");
        }

        var d = await servizio.DiagnoseAsync(0);

        // I sei target databili non bastano: il verdetto non si può dare.
        Assert.Equal(VerdettoBracket.NonGiudicabile, d.Verdetto);
        Assert.Equal(6, d.Osservato!.UsciteABarriera);
        Assert.Equal(30, d.Osservato.UsciteABarrieraNonDatabili);

        // E il motivo distingue le due cause: non è solo «ne servono altre».
        Assert.Contains("aspettare non le renderà databili", d.Racconto);
        Assert.Contains("30 stop e 0 target che NON entrano nella percentuale", d.Osservato.Scarti);

        // Il conteggio grezzo resta visibile: la parte esclusa non sparisce.
        Assert.Equal(36, d.Osservato.RigheTotali);
    }

    /// <summary>
    /// La scansione delle geometrie alternative accompagna ogni corsia misurabile, e la conclusione
    /// sulla leva è scritta a parole. Serve a rispondere alla seconda metà della domanda — «come si
    /// corregge» — senza offrire un bottone che applichi qualcosa: qui non si scrive niente.
    /// </summary>
    [Fact]
    public async Task LaDiagnosi_PortaSempreLeGeometrieAlternative_ELaLoroConclusione()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 30, take: 5);

        var d = await servizio.DiagnoseAsync(0);

        Assert.Equal(BracketRaceAnalyzer.DefaultMultipliers.Count, d.Alternative.Count);
        Assert.All(d.Alternative, a => Assert.True(a.Misurabile));
        Assert.False(string.IsNullOrWhiteSpace(d.Leva));

        // E la quota di stop cresce col rapporto: è l'invariante che rende leggibile la tabella.
        var quote = d.Alternative.Select(a => a.StopSharePercent).ToList();
        Assert.Equal(quote.OrderBy(q => q).ToList(), quote);
    }

    /// <summary>
    /// Una corsia il cui manager non è registrato non fa cadere le altre: la diagnostica è
    /// fail-open. Il difetto si dichiara nella riga di quella corsia, e nessuna eccezione risale.
    /// </summary>
    [Fact]
    public async Task UnaCorsiaIlleggibile_NonAbbatteLaDiagnosiDelleAltre()
    {
        var servizio = await ServizioAsync(Config(1m, 3m));
        await SerieAsync();
        await StatoCorsiaAsync();
        await UsciteAsync(stop: 30, take: 5);

        var tutte = await servizio.DiagnoseAllAsync();

        Assert.NotEmpty(tutte);
        Assert.Equal(VerdettoBracket.ComePrevisto, tutte[0].Verdetto);
        // Le corsie senza manager registrato ci sono e dicono perché, invece di sparire.
        Assert.All(tutte.Skip(1), d => Assert.Equal(VerdettoBracket.NonMisurabile, d.Verdetto));
    }
}
