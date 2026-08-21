using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-21] Lo stage dei dati alternativi non deve poter uccidere una caccia.
///
/// <para>Il caso reale: la prima caccia intraday è fallita al <b>secondo stage su diciassette</b>,
/// con l'ingestione dei prezzi già completata con successo, per una violazione di chiave univoca
/// (<c>23505 … IX_AltDataPoints_DedupeKey</c>) mentre sincronizzava le notizie. Due sync sovrapposte
/// che ingeriscono lo stesso elemento: raro — un run su centosettanta — e proprio per questo
/// insidioso, perché si manifesta quando si lancia una caccia a mano mentre il worker periodico sta
/// già sincronizzando.</para>
///
/// <para>Lo stage aveva <b>metà</b> della dottrina applicata: lo snapshot del mood era già protetto
/// con un commento che dice «non deve mai far fallire lo stage», la sync no. È il rovescio della
/// regola 4 del progetto — fail-closed sulla sicurezza, fail-open sulla <i>diagnostica</i>: un run
/// che perde le notizie di oggi vale ancora, un run che non parte non vale niente.</para>
/// </summary>
[Collection("Postgres")]
public sealed class AltDataSyncStageResilienceTests
{
    private readonly string _connString;

    public AltDataSyncStageResilienceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    /// <summary>Sync che esplode come quella vera: stessa eccezione della violazione di unicità.</summary>
    private sealed class SyncCheEsplode(Exception ex) : IAltDataSyncService
    {
        public int Chiamate { get; private set; }
        public Task<int> SyncAllAsync(CancellationToken ct)
        {
            Chiamate++;
            throw ex;
        }
    }

    private sealed class SyncCheFunziona(int inseriti) : IAltDataSyncService
    {
        public Task<int> SyncAllAsync(CancellationToken ct) => Task.FromResult(inseriti);
    }

    private static PipelineContext Contesto(List<string>? log = null)
    {
        var ctx = new PipelineContext();
        ctx.Universe.Add(new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "5m" });
        if (log is not null) ctx.Log = log.Add;
        return ctx;
    }

    private static StageConfig ConSync(bool sync) =>
        new() { Parameters = new Dictionary<string, string> { ["sync"] = sync ? "true" : "false" } };

    [Fact]
    public async Task SyncCheFallisce_NonFermaLoStage_EIlRunProsegue()
    {
        var factory = await DbAsync();
        var sync = new SyncCheEsplode(new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_AltDataPoints_DedupeKey\""));
        var stage = new AltDataSyncStage(sync, factory);
        var ctx = Contesto();

        // Prima della correzione questa riga lanciava, e con lei moriva l'intera caccia.
        await stage.ExecuteAsync(ctx, ConSync(true), CancellationToken.None);

        Assert.Equal(1, sync.Chiamate);
        Assert.NotNull(ctx.AltData);                 // lo stage ha comunque prodotto il suo output
        Assert.Equal(0, ctx.AltData!.InsertedCount); // zero inseriti, non un numero inventato
    }

    [Fact]
    public async Task IlFallimento_ELoSCRITTO_NonInghiottito()
    {
        // Degradare in silenzio sarebbe l'altro difetto: chi legge il run deve sapere che le
        // notizie di oggi mancano, altrimenti «sentiment 0 news» si legge come un fatto di mercato.
        var factory = await DbAsync();
        var righe = new List<string>();
        var stage = new AltDataSyncStage(new SyncCheEsplode(new InvalidOperationException("23505 duplicate key")), factory);
        var ctx = Contesto(righe);

        await stage.ExecuteAsync(ctx, ConSync(true), CancellationToken.None);

        var log = string.Join("\n", righe);
        Assert.Contains("FALLITA", log);
        Assert.Contains("il run non si ferma", log);
        Assert.Contains("23505", log);   // l'errore vero, non una parafrasi
    }

    [Fact]
    public async Task LaCancellazione_RESTAUnaCancellazione()
    {
        // Fail-open sulla diagnostica non vuol dire inghiottire l'annullamento del run: se
        // l'operatore ferma la caccia, deve fermarsi.
        var factory = await DbAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var stage = new AltDataSyncStage(new SyncCheEsplode(new OperationCanceledException()), factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stage.ExecuteAsync(Contesto(), ConSync(true), cts.Token));
    }

    [Fact]
    public async Task SyncCheFunziona_ContinuaAContareGliInseriti()
    {
        var factory = await DbAsync();
        var stage = new AltDataSyncStage(new SyncCheFunziona(7), factory);
        var ctx = Contesto();

        await stage.ExecuteAsync(ctx, ConSync(true), CancellationToken.None);

        Assert.Equal(7, ctx.AltData!.InsertedCount);
    }

    [Fact]
    public async Task ConSyncSpenta_NonSiTeccaLaRete_ELoStagePassaLoStesso()
    {
        // È la manopola documentata («false = usa solo le notizie già presenti nel DB») ed è quella
        // che ha permesso di far ripartire la caccia intraday prima che questa correzione esistesse.
        var factory = await DbAsync();
        var sync = new SyncCheEsplode(new InvalidOperationException("non deve essere chiamata"));
        var stage = new AltDataSyncStage(sync, factory);
        var ctx = Contesto();

        await stage.ExecuteAsync(ctx, ConSync(false), CancellationToken.None);

        Assert.Equal(0, sync.Chiamate);
        Assert.NotNull(ctx.AltData);
    }
}
