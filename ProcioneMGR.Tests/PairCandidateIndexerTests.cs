using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I14] L'indice a righe delle coppie: una tabella DERIVATA dagli artefatti <c>PairScreen</c>, che
/// al 2026-08-06 erano 86 e che <b>nessuna query nel repo aveva mai riletto</b>. Un test di
/// cointegrazione costa, viene rifatto a ogni run, e il risultato finiva in un blob che nessuna
/// superficie apriva.
///
/// <para>Qui si fissa che: (1) la mappatura è fedele campo per campo al blob d'origine, (2)
/// <c>IsTradeable</c> è <b>ricalcolato</b> e non letto dal JSON — la trappola che renderebbe il
/// pannello muto, (3) l'indicizzazione è incrementale e idempotente, (4) l'identità della coppia
/// non dipende dal verso in cui il payload la scrive, (5) un payload illeggibile esclude QUEL run
/// e non l'intero giro.</para>
/// </summary>
[Collection("Postgres")]
public sealed class PairCandidateIndexerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PairCandidateIndexerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private async Task<(IDbContextFactory<ApplicationDbContext> DbFactory, PairCandidateIndexer Indexer)> BuildAsync()
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
        return (dbFactory, new PairCandidateIndexer(dbFactory, NullLogger<PairCandidateIndexer>.Instance));
    }

    private static PairScreenResult Pair(
        string y, string x, bool cointegrated = true, double beta = 1.2, double adf = -4.1, int candles = 900)
        => new()
        {
            SymbolY = y, SymbolX = x, Timeframe = "1h",
            AdfStatistic = adf, IsCointegrated = cointegrated,
            HedgeRatio = beta, IsHedgeRatioPlausible = beta is >= 0.5 and <= 2.0,
            AlignedCandles = candles,
        };

    private static async Task<Guid> SeedRunAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, List<PairScreenResult> pairs,
        DateTime completedAt, string payloadOverride = "", bool completed = true)
    {
        var runId = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId, ConfigurationId = 1,
            StartedAt = completedAt.AddMinutes(-30),
            CompletedAt = completed ? completedAt : null,
            Status = completed ? "Completed" : "Failed",
            Trigger = "Manual",
        });
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId, StageName = "PairsScreening", Kind = PairCandidateIndexer.ArtifactKind,
            PayloadJson = payloadOverride.Length > 0
                ? payloadOverride
                : JsonSerializer.Serialize(new PairsOutput
                {
                    Pairs = pairs,
                    CointegratedCount = pairs.Count(p => p.IsCointegrated),
                    TradeableCount = pairs.Count(p => p.IsTradeable),
                }),
            CreatedAt = completedAt,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    // --- 1. Mappatura fedele ---------------------------------------------------------------------

    /// <summary>Il riferimento indipendente è l'oggetto sorgente: campo per campo, senza scorciatoie.</summary>
    [Fact]
    public async Task Indicizzazione_MappaOgniCampoDalBlobSorgente()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var sorgente = Pair("ETH/USDT", "BTC/USDT", cointegrated: true, beta: 1.37, adf: -3.92, candles: 1234);
        var completato = new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);
        var runId = await SeedRunAsync(dbFactory, [sorgente], completato);

        Assert.Equal(new PairIndexResult(1, 1, 0), await indexer.IndexNewAsync());

        await using var db = await dbFactory.CreateDbContextAsync();
        var riga = await db.PairCandidates.SingleAsync();

        Assert.Equal(runId, riga.RunId);
        Assert.Equal(completato, riga.RunCompletedUtc);
        Assert.Equal("ETH/USDT", riga.SymbolY);
        Assert.Equal("BTC/USDT", riga.SymbolX);
        Assert.Equal("1h", riga.Timeframe);
        Assert.Equal(-3.92, riga.AdfStatistic, 6);
        Assert.True(riga.IsCointegrated);
        Assert.Equal(1.37, riga.HedgeRatio, 6);
        Assert.True(riga.IsHedgeRatioPlausible);
        Assert.Equal(1234, riga.AlignedCandles);
        Assert.Equal(PairKey.Build("ETH/USDT", "BTC/USDT", "1h"), riga.PairKeyValue);
    }

    /// <summary>
    /// <b>La trappola che avrebbe reso il pannello muto.</b> Nel payload <c>IsTradeable</c> è una
    /// property <i>get-only</i>: System.Text.Json la SCRIVE ma la IGNORA in deserializzazione, quindi
    /// un indicizzatore che la mappasse dal blob scriverebbe <c>false</c> su ogni riga — e il filtro
    /// «solo quelle che hanno passato» sarebbe sempre vuoto, con l'aria di funzionare.
    ///
    /// <para>Il test lo prova nei due versi, incluso il caso che li separa: cointegrata ma con β
    /// implausibile ⇒ NON operabile. Se qualcuno sostituisse il ricalcolo con una lettura, il primo
    /// caso diventerebbe rosso.</para>
    /// </summary>
    [Theory]
    [InlineData(true, 1.2, true)]     // cointegrata e β plausibile
    [InlineData(true, 4.0, false)]    // cointegrata ma β fuori banda: NON operabile
    [InlineData(false, 1.2, false)]   // β plausibile ma non cointegrata
    public async Task IsTradeable_ERicalcolato_MaiLettoDalJson(bool cointegrata, double beta, bool operabile)
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT", cointegrata, beta)],
            new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));

        await indexer.IndexNewAsync();

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(operabile, (await db.PairCandidates.SingleAsync()).IsTradeable);
    }

    /// <summary>
    /// La prova diretta che il campo del blob NON è affidabile: si serializza un payload dove
    /// <c>IsTradeable</c> vale <c>true</c>, lo si rilegge, e il valore deserializzato è quello
    /// ricalcolato dalla property — non quello scritto. È il motivo per cui il ricalcolo esiste.
    /// </summary>
    [Fact]
    public void IlCampoIsTradeableDelPayload_NonSopravviveAllaDeserializzazione()
    {
        var originale = Pair("ETH/USDT", "BTC/USDT", cointegrated: true, beta: 1.2);
        Assert.True(originale.IsTradeable);

        var json = JsonSerializer.Serialize(originale);
        Assert.Contains("\"IsTradeable\":true", json, StringComparison.Ordinal); // SCRITTO nel blob...

        // ...ma per rileggerlo servono gli altri due campi: la property non ha setter.
        var riletto = JsonSerializer.Deserialize<PairScreenResult>(json)!;
        Assert.Equal(originale.IsCointegrated && originale.IsHedgeRatioPlausible, riletto.IsTradeable);
    }

    // --- 2. Incrementale e idempotente -----------------------------------------------------------

    /// <summary><b>L2 idempotenza</b>: due ricostruzioni danno le stesse righe, nessun duplicato.</summary>
    [Fact]
    public async Task L2_DueRicostruzioni_StesseRigheNessunDuplicato()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var t = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT"), Pair("SOL/USDT", "BTC/USDT")], t);
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT")], t.AddDays(1));

        var primo = await indexer.RebuildAsync();
        var secondo = await indexer.RebuildAsync();

        Assert.Equal(primo, secondo);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(3, await db.PairCandidates.CountAsync());
    }

    /// <summary>
    /// L'incrementale non ripassa sui run già indicizzati — e non ne perde nessuno. Senza questo,
    /// il pulsante «Indicizza i nuovi run» rileggerebbe l'intero archivio a ogni click, sul Postgres
    /// condiviso con motore e ingestion.
    /// </summary>
    [Fact]
    public async Task Incrementale_IndicizzaSoloIRunNuovi()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var t = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT")], t);

        Assert.Equal(1, (await indexer.IndexNewAsync()).RunsIndexed);
        Assert.Equal(0, (await indexer.IndexNewAsync()).RunsIndexed);   // niente di nuovo

        await SeedRunAsync(dbFactory, [Pair("SOL/USDT", "BTC/USDT")], t.AddDays(1));
        Assert.Equal(1, (await indexer.IndexNewAsync()).RunsIndexed);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.PairCandidates.CountAsync());
    }

    /// <summary>
    /// <b>La gara fra processi.</b> Guscio e pod-ui girano sullo stesso Postgres: se un altro
    /// processo indicizza lo stesso run nella finestra fra la nostra lettura e l'insert, l'indice
    /// unico morde. Quel run è a posto — lo ha scritto qualcun altro — quindi si salta e si prosegue,
    /// invece di far fallire l'intero giro e svuotare il pannello.
    /// </summary>
    [Fact]
    public async Task GaraFraProcessi_IlRunGiaScrittoSiSaltaSenzaFermareGliAltri()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var t = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT")], t);
        await SeedRunAsync(dbFactory, [Pair("SOL/USDT", "BTC/USDT")], t.AddDays(1));

        await indexer.IndexNewAsync();   // entrambi indicizzati da "un altro processo"

        // ...e ora il nostro giro crede che nessuno sia stato fatto: lista stantia, vuota.
        await using var db = await dbFactory.CreateDbContextAsync();
        var esito = await indexer.IndexAsync(db, [], CancellationToken.None);

        Assert.Equal(0, esito.RunsIndexed);
        Assert.Equal(2, esito.RunsSkipped);
        Assert.Equal(2, await db.PairCandidates.CountAsync());   // nessun duplicato
    }

    // --- 3. Identità della coppia ----------------------------------------------------------------

    /// <summary>
    /// <b>La chiave non dipende dal verso.</b> Oggi lo stage genera solo <c>i&lt;j</c>, quindi la
    /// coppia arriva sempre nello stesso ordine — ma è una proprietà di come il ciclo è scritto, non
    /// del dominio. Un universo ordinato diversamente produrrebbe due chiavi per la stessa coppia, e
    /// l'indice unico non le vedrebbe come duplicati: la tabella conterrebbe la stessa relazione due
    /// volte e il pannello la mostrerebbe due volte.
    /// </summary>
    [Fact]
    public void LIdentitaDellaCoppia_NonDipendeDalVerso()
    {
        Assert.Equal(
            PairKey.Build("ETH/USDT", "BTC/USDT", "1h"),
            PairKey.Build("BTC/USDT", "ETH/USDT", "1h"));

        // ...ma il timeframe SI': la stessa coppia su due barre è due relazioni diverse.
        Assert.NotEqual(
            PairKey.Build("ETH/USDT", "BTC/USDT", "1h"),
            PairKey.Build("ETH/USDT", "BTC/USDT", "4h"));
    }

    /// <summary>
    /// Il VERSO però si conserva sulla riga: <c>HedgeRatio</c> è l'elasticità di Y rispetto a X, e
    /// invertirlo cambierebbe il significato del numero. La chiave normalizza l'identità, i due
    /// campi conservano la direzione della regressione.
    /// </summary>
    [Fact]
    public async Task IlVersoDellaRegressione_SiConservaSullaRiga()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [Pair("SOL/USDT", "BTC/USDT", beta: 1.9)],
            new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));

        await indexer.IndexNewAsync();

        await using var db = await dbFactory.CreateDbContextAsync();
        var riga = await db.PairCandidates.SingleAsync();
        Assert.Equal("SOL/USDT", riga.SymbolY);
        Assert.Equal("BTC/USDT", riga.SymbolX);
    }

    // --- 4. Difensività ---------------------------------------------------------------------------

    /// <summary>Un payload illeggibile esclude QUEL run, non l'intero giro.</summary>
    [Fact]
    public async Task PayloadIllegibile_EscludeSoloQuelRun()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var t = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunAsync(dbFactory, [], t, payloadOverride: "{non-json");
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT")], t.AddDays(1));

        var esito = await indexer.IndexNewAsync();

        Assert.Equal(1, esito.RunsIndexed);
        Assert.Equal(1, esito.RunsSkipped);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.PairCandidates.CountAsync());
    }

    /// <summary>
    /// Uno screening senza coppie NON è un guasto: succede quando nessuna serie ha abbastanza
    /// candele allineate.
    ///
    /// <para><b>E si conta a parte, non fra gli indicizzati.</b> Non producendo righe non lascia
    /// traccia nella tabella, quindi l'incrementale lo <b>rilegge a ogni giro</b> — è un fatto, non
    /// una scelta, e la prima versione di questo test lo dichiarava «indicizzato una volta sola»
    /// asserendo una promessa che il codice non manteneva. Contarlo fra i nuovi farebbe dire al
    /// pulsante «indicizzato 1 run» per sempre, su un archivio dove non c'è più niente da fare.</para>
    /// </summary>
    [Fact]
    public async Task ScreeningVuoto_ContatoAParte_ERilettoOgniGiro()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [], new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));

        var primo = await indexer.IndexNewAsync();
        Assert.Equal(0, primo.RunsIndexed);   // nessuna riga: non è "lavoro fatto"
        Assert.Equal(1, primo.RunsEmpty);
        Assert.Equal(0, primo.RunsSkipped);   // e non è nemmeno un errore

        var secondo = await indexer.IndexNewAsync();
        Assert.Equal(0, secondo.RunsIndexed);
        Assert.Equal(1, secondo.RunsEmpty);   // riletto: dichiarato, non nascosto

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.PairCandidates.CountAsync());
    }

    /// <summary>
    /// Coppia duplicata dentro lo stesso payload (possibile nei blob storici): si tiene la prima e
    /// non si fa fallire il run. Senza il collasso, l'indice unico farebbe cadere l'intero giro —
    /// un solo payload malfatto svuoterebbe il pannello.
    /// </summary>
    [Fact]
    public async Task CoppiaDuplicataNelPayload_SiTieneLaPrima()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory,
            [Pair("ETH/USDT", "BTC/USDT", adf: -4.0), Pair("BTC/USDT", "ETH/USDT", adf: -2.0)],
            new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));

        var esito = await indexer.IndexNewAsync();

        Assert.Equal(1, esito.PairsIndexed);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(-4.0, (await db.PairCandidates.SingleAsync()).AdfStatistic, 6);
    }

    /// <summary>
    /// <b>La data denormalizzata è STABILE fra ricostruzioni.</b> Il fallback per un
    /// <c>CompletedAt</c> assente è il <c>CreatedAt</c> dell'artefatto, non <c>DateTime.UtcNow</c>:
    /// quest'ultimo fabbricherebbe una recency diversa a ogni giro, e il pannello — che ordina per
    /// data — mostrerebbe un ordine che cambia da solo.
    /// </summary>
    [Fact]
    public async Task DataDenormalizzata_StabileFraRicostruzioni()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var creato = new DateTime(2026, 7, 2, 9, 30, 0, DateTimeKind.Utc);
        // Run senza CompletedAt: è il caso del fallback. NB — la query NON filtra per Status: gli
        // artefatti si scrivono solo a fine run completato (PipelineEngine), quindi il filtro è un
        // fatto del produttore, non una clausola del lettore. Scoperto scrivendo questo test, che
        // nella prima versione asseriva il contrario.
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT")], creato, completed: false);

        await indexer.RebuildAsync();
        await using var check1 = await dbFactory.CreateDbContextAsync();
        var prima = (await check1.PairCandidates.SingleAsync()).RunCompletedUtc;

        await indexer.RebuildAsync();
        await using var check2 = await dbFactory.CreateDbContextAsync();
        var dopo = (await check2.PairCandidates.SingleAsync()).RunCompletedUtc;

        Assert.Equal(creato, prima);
        Assert.Equal(prima, dopo);
    }

    /// <summary>
    /// [J7, PRD autonomia-operativa 2026-08-25] Il worker che aziona l'indice DA SOLO. L'indice
    /// era costruito, collaudato e mai azionato: 0 righe contro 174 artefatti — uno strumento che
    /// esiste solo dietro un click che nessuno dà. Il tick delega all'incrementale (idempotente)
    /// e non deve morire su un guasto transitorio.
    /// </summary>
    [Fact]
    public async Task Worker_TickOnce_IndicizzaLArretrato_EDueGiriNonDuplicano()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [Pair("ETH/USDT", "BTC/USDT", true, 0.05)], DateTime.UtcNow.AddDays(-2));
        var worker = new ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker(
            indexer, NullLogger<ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker>.Instance);

        await worker.TickOnceAsync(CancellationToken.None);
        await worker.TickOnceAsync(CancellationToken.None); // idempotente: il secondo giro non duplica

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.PairCandidates.CountAsync());
    }

    [Fact]
    public async Task Worker_GuastoDellIndice_NonPropaga()
    {
        // Un guasto transitorio (qui: DB irraggiungibile) non deve uccidere il worker: il
        // prossimo giro riprova. Se questo lanciasse, l'hosted service morirebbe in silenzio.
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(
            "Host=localhost;Port=1;Database=inesistente;Username=x;Password=x;Timeout=1"));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var indexer = new PairCandidateIndexer(
            provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            NullLogger<PairCandidateIndexer>.Instance);
        var worker = new ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker(
            indexer, NullLogger<ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker>.Instance);

        await worker.TickOnceAsync(CancellationToken.None); // non deve lanciare
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
