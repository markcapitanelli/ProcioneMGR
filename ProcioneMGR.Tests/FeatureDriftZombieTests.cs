using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA FOTOGRAFIA CHE NON MORIVA.
///
/// <para>Al 2026-08-24 la Home annunciava «156 modelli ML con feature in deriva». Il tick che li
/// aveva prodotti era del <b>2026-08-19 08:16</b>, cinque giorni prima; nel frattempo [I6c] aveva
/// ristretto la sorveglianza a Champion e Challenger, e il registro conteneva 176 modelli
/// <b>tutti in Staging</b> — cioè zero modelli sorvegliati. La scheda mostrava il verdetto su una
/// popolazione che il monitor aveva deliberatamente smesso di guardare.</para>
///
/// <para>Non era una scheda ferma: era <b>bistabile</b>. L'idratatore, registrato senza condizioni,
/// la rimetteva 10 secondi dopo ogni avvio; un eventuale tick la svuotava solo in memoria e usciva
/// PRIMA di scrivere righe, quindi non lasciava traccia; il riavvio successivo la resuscitava
/// identica. E il guscio si riavvia più volte al giorno. Non poteva convergere.</para>
///
/// <para>Questi test difendono la riga che rompe il ciclo: un tick <b>senza soggetto</b> deve
/// lasciare una traccia persistente, perché è l'unica cosa che distingue «ho girato e non avevo
/// niente da guardare» da «non sto girando» — il principio che questa stessa classe dichiarava e
/// applicava ovunque tranne che nell'unico caso che si verificava davvero.</para>
/// </summary>
[Collection("Postgres")]
public sealed class FeatureDriftZombieTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FeatureDriftZombieTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Monitor che non deve mai essere chiamato: se lo fosse, il test sta misurando altro.</summary>
    private sealed class NeverCalledMonitor : IFeatureDriftMonitor
    {
        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles,
            DriftThresholds? thresholds = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Il monitor non doveva essere invocato: nessun modello è sorvegliato.");
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildFactoryAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static async Task<int> AddModelAsync(IDbContextFactory<ApplicationDbContext> factory, ModelStage stage)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = new ApplicationUser { UserName = $"u_{Guid.NewGuid():N}", Email = "t@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var model = new SavedMlModel
        {
            UserId = user.Id, Name = $"m_{Guid.NewGuid():N}", ModelType = "Linear",
            Symbol = "BTCUSDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [1],
            DeflatedSharpe = 0.9, Stage = stage,
            PromotedAtUtc = stage == ModelStage.Champion ? DateTime.UtcNow : null,
        };
        db.SavedMlModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>Il tick del 19 agosto, riprodotto: righe di allarme su modelli in Staging.</summary>
    private static async Task SeedVecchioTickAsync(
        IDbContextFactory<ApplicationDbContext> factory, DateTime at, params int[] modelIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var id in modelIds)
        {
            db.DriftCheckResults.Add(new DriftCheckResult
            {
                CheckedAtUtc = at,
                ModelId = id,
                ModelName = $"Pipeline {id}",
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                TotalFeatures = 4,
                DriftingFeatures = 4,
                AlertFeatures = 4,
                Overall = DriftSeverity.Alert,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Worker con gli stage sorvegliati di oggi: Champion e Challenger (lista vuota ⇒ default).</summary>
    private static FeatureDriftWorker Worker(
        IDbContextFactory<ApplicationDbContext> factory,
        FeatureDriftSnapshot? snapshot = null,
        DriftMonitorOptions? opt = null)
        => new(factory, new NeverCalledMonitor(),
            new ModelRegistry(factory, new ModelRegistryOptions(), NullLogger<ModelRegistry>.Instance),
            (opt ?? new DriftMonitorOptions()).AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance,
            metrics: null,
            snapshot: snapshot);

    // ----------------------------------------------------------------------------------------
    //  1. Il tick senza soggetto lascia traccia
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TickSenzaSoggetto_PersisteUnaRigaSENTINELLA_NonEsceInSilenzio()
    {
        var factory = await BuildFactoryAsync();
        await AddModelAsync(factory, ModelStage.Staging);   // esiste, ma non è sorvegliato

        await Worker(factory).TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var row = Assert.Single(await db.DriftCheckResults.ToListAsync());

        // Prima della correzione questa tabella restava VUOTA, e l'assenza di righe era
        // indistinguibile da «il worker non sta girando» — che è precisamente la distinzione che
        // questa tabella esiste per rendere possibile.
        Assert.Equal(0, row.ModelId);
        Assert.False(row.IsVerdict);
        Assert.NotNull(row.SkipReason);
        Assert.Contains("nessun modello negli stage sorvegliati", row.SkipReason);
        Assert.Contains("Champion", row.SkipReason);
        // Il denominatore vero, che il worker calcolava già e buttava in un log.
        Assert.Contains("1 salvati", row.SkipReason);
    }

    [Fact]
    public async Task LaSentinella_UccideLaFotografiaZOMBIE()
    {
        // È il test della regressione vera, riprodotta nella sua forma originale.
        var factory = await BuildFactoryAsync();
        var staging = await AddModelAsync(factory, ModelStage.Staging);
        var vecchio = new DateTime(2026, 8, 19, 8, 16, 14, DateTimeKind.Utc);
        await SeedVecchioTickAsync(factory, vecchio, staging);

        // Prima: l'idratazione ripescava il tick del 19 agosto, con il suo allarme.
        var prima = new FeatureDriftSnapshot();
        await prima.HydrateAsync(factory);   // senza filtro per stage: il comportamento storico
        Assert.Single(prima.Alerts);
        Assert.Equal(vecchio, prima.LastRunUtc);

        // Un tick senza soggetto, e poi un riavvio (= una nuova idratazione).
        await Worker(factory).TickAsync(CancellationToken.None);

        var dopo = new FeatureDriftSnapshot();
        await dopo.HydrateAsync(factory, new DriftMonitorOptions().EffectiveStages());

        Assert.Empty(dopo.Alerts);
        Assert.NotEqual(vecchio, dopo.LastRunUtc);
        Assert.All(dopo.All, m => Assert.True(m.IsSentinel));
    }

    [Fact]
    public async Task TickSenzaSoggetto_POTAAncheLoStoricoOltreLaRetention()
    {
        // Il prune è l'unico DELETE su questa tabella in tutta la codebase, e viveva dentro il
        // ramo che scrive righe: senza soggetto non girava, quindi lo storico non scadeva MAI.
        var factory = await BuildFactoryAsync();
        var staging = await AddModelAsync(factory, ModelStage.Staging);
        var vecchissimo = DateTime.UtcNow.AddDays(-(FeatureDriftWorker.ResultRetentionDays + 5));
        await SeedVecchioTickAsync(factory, vecchissimo, staging);

        await Worker(factory).TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.DriftCheckResults.ToListAsync();
        Assert.Single(rows);                       // resta solo la sentinella
        Assert.Equal(0, rows[0].ModelId);
    }

    [Fact]
    public async Task ConUnModelloSORVEGLIATO_NessunaSentinella()
    {
        // Il controllo nella direzione opposta: la sentinella non deve comparire quando il monitor
        // ha davvero un soggetto, altrimenti sporcherebbe ogni tick vero.
        var factory = await BuildFactoryAsync();
        await AddModelAsync(factory, ModelStage.Champion);

        var opt = new DriftMonitorOptions { RecentCandles = 20, RetireChampionOnAlert = false };
        // Il monitor finto lancerebbe: qui il modello non ha candele recenti, quindi il worker
        // dichiara il check SALTATO prima di invocarlo — ed è comunque una riga su un modello VERO.
        await Worker(factory, opt: opt).TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var row = Assert.Single(await db.DriftCheckResults.ToListAsync());
        Assert.NotEqual(0, row.ModelId);
    }

    // ----------------------------------------------------------------------------------------
    //  2. L'idratazione non riporta in Home modelli che nessuno sorveglia più
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Idratazione_ScartaIModelliFuoriDagliStageSorvegliati_ELoDICE()
    {
        var factory = await BuildFactoryAsync();
        var staging = await AddModelAsync(factory, ModelStage.Staging);
        var champion = await AddModelAsync(factory, ModelStage.Champion);
        await SeedVecchioTickAsync(factory, new DateTime(2026, 8, 19, 8, 16, 0, DateTimeKind.Utc), staging, champion);

        var snap = new FeatureDriftSnapshot();
        await snap.HydrateAsync(factory, new DriftMonitorOptions().EffectiveStages());

        // Resta solo il Champion: il modello in Staging non è sorvegliato, e riportarlo in Home
        // sarebbe un allarme su una popolazione che il monitor ha smesso di guardare.
        var restato = Assert.Single(snap.All);
        Assert.Equal(champion, restato.ModelId);
        // E lo scarto si DICHIARA: una fotografia che si assottiglia in silenzio è un allarme che
        // sparisce senza che nessuno abbia deciso nulla.
        Assert.Equal(1, snap.DroppedNotMonitored);
    }

    [Fact]
    public async Task Idratazione_ScartaIModelliCHENONESISTONOPIU()
    {
        var factory = await BuildFactoryAsync();
        await SeedVecchioTickAsync(factory, DateTime.UtcNow.AddDays(-3), 4242);   // id mai esistito

        var snap = new FeatureDriftSnapshot();
        await snap.HydrateAsync(factory, new DriftMonitorOptions().EffectiveStages());

        Assert.Empty(snap.All);
        Assert.Equal(1, snap.DroppedNotMonitored);
    }

    [Fact]
    public async Task Idratazione_DichiaraLaDimensioneVERADelRegistro()
    {
        // «Copertura: 158 modelli, tutti giudicati» contava le righe del tick, non il registro:
        // al 2026-08-24 i modelli salvati erano 176, quindi «tutti» copriva 158/176 — e lo scarto
        // cresceva a ogni modello nuovo, in silenzio.
        var factory = await BuildFactoryAsync();
        var champion = await AddModelAsync(factory, ModelStage.Champion);
        await AddModelAsync(factory, ModelStage.Staging);
        await AddModelAsync(factory, ModelStage.Staging);
        await SeedVecchioTickAsync(factory, DateTime.UtcNow.AddHours(-2), champion);

        var snap = new FeatureDriftSnapshot();
        await snap.HydrateAsync(factory, new DriftMonitorOptions().EffectiveStages());

        Assert.Single(snap.All);
        Assert.Equal(3, snap.RegistrySize);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
