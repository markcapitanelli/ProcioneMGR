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
/// Test del Model Registry (Fase 2): gate del Deflated Sharpe sulla promozione a Champion, invariante
/// "un solo Champion per (Symbol, Timeframe)", e ciclo chiuso col drift (Champion in Alert → Retired +
/// retrain accodato, mai Live). DB Postgres effimero (Testcontainers) via EnsureCreated.
/// </summary>
[Collection("Postgres")]
public class ModelRegistryTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ModelRegistryTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
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

    private static async Task<string> SeedUserAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<int> AddModelAsync(
        IDbContextFactory<ApplicationDbContext> factory, string userId,
        string symbol, string timeframe, double? deflatedSharpe, ModelStage stage = ModelStage.Staging)
    {
        await using var db = await factory.CreateDbContextAsync();
        var model = new SavedMlModel
        {
            UserId = userId, Name = $"m_{Guid.NewGuid():N}", ModelType = "Linear",
            Symbol = symbol, Timeframe = timeframe, FactorsJson = "[]", ModelBytes = new byte[] { 1 },
            DeflatedSharpe = deflatedSharpe, Stage = stage,
            PromotedAtUtc = stage == ModelStage.Champion ? DateTime.UtcNow : null,
        };
        db.SavedMlModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private static ModelRegistry NewRegistry(IDbContextFactory<ApplicationDbContext> factory, double minDsr = 0.0)
        => new(factory, new ModelRegistryOptions { MinChampionDeflatedSharpe = minDsr }, NullLogger<ModelRegistry>.Instance);

    // --- Gate DSR + unicità del Champion -----------------------------------------------------

    [Fact]
    public async Task Promote_FirstChampion_SucceedsWhenNoIncumbent()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", deflatedSharpe: 0.90);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.True(outcome.Promoted, outcome.Reason);
        var champ = await registry.GetChampionAsync("BTCUSDT", "1h");
        Assert.NotNull(champ);
        Assert.Equal(id, champ!.Id);
    }

    [Fact]
    public async Task Promote_NonDirectionalModel_IsRejectedBySemantics_EvenWithGoodDsr()
    {
        // [1.V fase 2] Gate 0: il Champion alimenta MlStrategy (segnali long/short) — un modello
        // che predice la volatilità non è MAI promuovibile, anche se qualcuno gli mettesse un DSR.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        int volModel;
        await using (var db = await f.CreateDbContextAsync())
        {
            var m = new SavedMlModel
            {
                UserId = user, Name = "vol", ModelType = "Linear", Symbol = "BTCUSDT", Timeframe = "1h",
                FactorsJson = "[]", ModelBytes = [1], DeflatedSharpe = 0.99, TargetKind = "ForwardRealizedVol",
            };
            db.SavedMlModels.Add(m);
            await db.SaveChangesAsync();
            volModel = m.Id;
        }
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(volModel);

        Assert.False(outcome.Promoted);
        Assert.Contains("ForwardRealizedVol", outcome.Reason);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h"));
    }

    [Fact]
    public async Task Promote_LowerDsr_IsRejected_AndIncumbentStays()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.90, ModelStage.Champion);
        var weak = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.80);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(weak);

        Assert.False(outcome.Promoted);
        var current = await registry.GetChampionAsync("BTCUSDT", "1h");
        Assert.Equal(champ, current!.Id); // l'incumbent resta
    }

    [Fact]
    public async Task Promote_HigherDsr_ReplacesIncumbent_AndKeepsSingleChampion()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var oldChamp = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.80, ModelStage.Champion);
        var better = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.95);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(better);

        Assert.True(outcome.Promoted, outcome.Reason);
        Assert.Equal(oldChamp, outcome.DemotedChampionId);

        await using var db = await f.CreateDbContextAsync();
        var champions = await db.SavedMlModels.Where(m => m.Symbol == "BTCUSDT" && m.Timeframe == "1h" && m.Stage == ModelStage.Champion).ToListAsync();
        Assert.Single(champions);                        // invariante: un solo Champion
        Assert.Equal(better, champions[0].Id);
        var demoted = await db.SavedMlModels.FindAsync(oldChamp);
        Assert.Equal(ModelStage.Retired, demoted!.Stage); // il vecchio è Retired
    }

    [Fact]
    public async Task Promote_WithoutDsr_IsRejected()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", deflatedSharpe: null);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.False(outcome.Promoted);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h"));
    }

    [Fact]
    public async Task Champion_IsScopedPerSymbolTimeframe()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var btc = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9);
        var eth = await AddModelAsync(f, user, "ETHUSDT", "1h", 0.5);
        var registry = NewRegistry(f);

        Assert.True((await registry.TryPromoteToChampionAsync(btc)).Promoted);
        Assert.True((await registry.TryPromoteToChampionAsync(eth)).Promoted); // gruppo diverso: non compete col BTC

        Assert.Equal(btc, (await registry.GetChampionAsync("BTCUSDT", "1h"))!.Id);
        Assert.Equal(eth, (await registry.GetChampionAsync("ETHUSDT", "1h"))!.Id);
    }

    [Fact]
    public async Task Retire_WithRetrain_SetsReasonAndRetrainMarker()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);

        await registry.RetireAsync(id, "test reason", requestRetrain: true);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(id);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.Equal("test reason", m.RetiredReason);
        Assert.NotNull(m.RetiredAtUtc);
        Assert.NotNull(m.RetrainRequestedAtUtc);
    }

    // --- Ogni transizione sa dire di no (2026-08-19) ------------------------------------------
    //
    // Prima, PromoteToChallengerAsync no-oppava in silenzio e RetireAsync sovrascriveva senza
    // fiatare: /registry dichiarava successo in entrambi i casi. «Una verifica che non può fallire
    // non è una verifica» (docs/STANDARD-VERIFICA.md) — questi test sono i suoi "no".

    [Theory]
    [InlineData(ModelStage.Challenger)]
    [InlineData(ModelStage.Champion)]
    [InlineData(ModelStage.Retired)]
    public async Task PromoteToChallenger_NonStagingModel_IsRefusedWithAReasonOfItsOwn(ModelStage stage)
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, stage);
        var registry = NewRegistry(f);

        var outcome = await registry.PromoteToChallengerAsync(id);

        Assert.False(outcome.Changed);
        await using var db = await f.CreateDbContextAsync();
        Assert.Equal(stage, (await db.SavedMlModels.FindAsync(id))!.Stage); // e non ha toccato nulla
    }

    [Fact]
    public async Task PromoteToChallenger_StagingModel_ReportsTheChange()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9);
        var registry = NewRegistry(f);

        var outcome = await registry.PromoteToChallengerAsync(id);

        Assert.True(outcome.Changed, outcome.Reason);
        await using var db = await f.CreateDbContextAsync();
        Assert.Equal(ModelStage.Challenger, (await db.SavedMlModels.FindAsync(id))!.Stage);
    }

    [Fact]
    public async Task PromoteToChallenger_UnknownId_IsRefused_WithoutThrowing()
    {
        var f = await BuildFactoryAsync();
        var outcome = await NewRegistry(f).PromoteToChallengerAsync(987654);
        Assert.False(outcome.Changed);
        Assert.Contains("inesistente", outcome.Reason);
    }

    [Fact]
    public async Task Retire_AlreadyRetiredModel_IsRefused_AndTheDiagnosisSurvives()
    {
        // IL caso che serviva davvero: la conferma di ritiro resta aperta su una riga, il ciclo drift
        // ritira il Champion nel frattempo, e il secondo clic cancellava «drift: …» sostituendolo con
        // il motivo manuale — distruggendo la diagnosi e dichiarando successo.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);
        await registry.RetireAsync(id, "drift: 3 feature in alert (Mom1, Rsi14, Atr)", requestRetrain: true);

        var outcome = await registry.RetireAsync(id, "Ritirato manualmente dalla UI.", requestRetrain: false);

        Assert.False(outcome.Changed);
        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(id);
        Assert.Equal("drift: 3 feature in alert (Mom1, Rsi14, Atr)", m!.RetiredReason); // intatta
        Assert.NotNull(m.RetrainRequestedAtUtc);
    }

    [Fact]
    public async Task Retire_UnknownId_IsRefused_WithoutThrowing()
    {
        var f = await BuildFactoryAsync();
        var outcome = await NewRegistry(f).RetireAsync(987654, "motivo", requestRetrain: false);
        Assert.False(outcome.Changed);
        Assert.Contains("inesistente", outcome.Reason);
    }

    // --- Rientro da Retired (2026-08-19) ------------------------------------------------------
    //
    // Prima di questa transizione Retired era senza uscita: un ritiro accidentale — e "Ritira" era a
    // un clic solo — si annullava soltanto scrivendo a mano sul database. Il rientro restituisce
    // l'ELEGGIBILITÀ, non il regno: si atterra su Staging e si ripassa da TUTTI i gate. Questi test
    // fissano proprio quel confine, perché è ciò che rende la reversibilità innocua.

    [Fact]
    public async Task Reinstate_RetiredModel_ReturnsToStaging_AndKeepsTheRetirementScar()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);
        await registry.RetireAsync(id, "drift: 3 feature in alert (Mom1, Rsi14, Atr)", requestRetrain: true);

        var outcome = await registry.ReinstateToStagingAsync(id);

        Assert.True(outcome.Changed, outcome.Reason);
        Assert.Contains("drift: 3 feature in alert", outcome.Reason); // il motivo scavalcato è nell'esito

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(id);
        Assert.Equal(ModelStage.Staging, m!.Stage);
        // La cicatrice resta scritta: è ciò che l'operatore deve rileggere se riproverà a promuoverlo.
        Assert.Equal("drift: 3 feature in alert (Mom1, Rsi14, Atr)", m.RetiredReason);
        Assert.NotNull(m.RetiredAtUtc);
        Assert.NotNull(m.RetrainRequestedAtUtc); // il retrain chiesto NON è stato fatto: la richiesta resta
    }

    [Theory]
    [InlineData(ModelStage.Staging)]
    [InlineData(ModelStage.Challenger)]
    [InlineData(ModelStage.Champion)]
    public async Task Reinstate_NonRetiredModel_IsRefusedWithReason_AndStageIsUntouched(ModelStage stage)
    {
        // [L2] Il rientro deve accendersi SOLO sui Retired. È anche il caso della riga stantia: la
        // conferma resta aperta, il ciclo drift o una seconda scheda cambiano lo stadio sotto, e il
        // secondo clic deve dire di no invece di rassicurare.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, stage);
        var registry = NewRegistry(f);

        var outcome = await registry.ReinstateToStagingAsync(id);

        Assert.False(outcome.Changed);
        Assert.Contains(stage.ToString(), outcome.Reason); // dice quale stadio ha trovato, non "no" e basta

        await using var db = await f.CreateDbContextAsync();
        Assert.Equal(stage, (await db.SavedMlModels.FindAsync(id))!.Stage);
    }

    [Fact]
    public async Task Reinstate_UnknownId_IsRefused_WithoutThrowing()
    {
        var f = await BuildFactoryAsync();
        var registry = NewRegistry(f);

        var outcome = await registry.ReinstateToStagingAsync(987654);

        Assert.False(outcome.Changed);
        Assert.Contains("inesistente", outcome.Reason);
    }

    [Fact]
    public async Task Reinstate_Twice_SecondCallIsRefused()
    {
        // Doppio clic / due schede: la seconda chiamata non deve dire di sì una seconda volta.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Retired);
        var registry = NewRegistry(f);

        Assert.True((await registry.ReinstateToStagingAsync(id)).Changed);
        Assert.False((await registry.ReinstateToStagingAsync(id)).Changed);
    }

    [Fact]
    public async Task Reinstate_DoesNotBypassTheDsrGate()
    {
        // [L2] IL test di sicurezza: il rientro non è un'autorizzazione. Un modello debole ritirato e
        // riportato in Staging resta respinto dal gate esattamente come prima, e l'incumbent non si muove.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.90, ModelStage.Champion);
        var weak = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.60);
        var registry = NewRegistry(f);
        await registry.RetireAsync(weak, "scartato", requestRetrain: false);

        Assert.True((await registry.ReinstateToStagingAsync(weak)).Changed);
        var outcome = await registry.TryPromoteToChampionAsync(weak);

        Assert.False(outcome.Promoted);
        Assert.Equal(champ, (await registry.GetChampionAsync("BTCUSDT", "1h"))!.Id); // l'incumbent resta
    }

    [Fact]
    public async Task Reinstate_ThenFullChain_CanBecomeChampionAgain_WithANewVersion()
    {
        // [L1] Il giro completo Retired → Staging → Challenger → Champion attraverso i gate veri.
        // Fissa anche la scelta di NON azzerare i campi del ritiro alla promozione: la storia
        // sopravvive al rientro, altrimenti un Champion tornato dal drift sembrerebbe immacolato.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.95, ModelStage.Champion);
        var registry = NewRegistry(f);
        await registry.RetireAsync(id, "drift: Mom1 in alert", requestRetrain: true);

        Assert.True((await registry.ReinstateToStagingAsync(id)).Changed);
        await registry.PromoteToChallengerAsync(id);
        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.True(outcome.Promoted, outcome.Reason);
        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(id);
        Assert.Equal(ModelStage.Champion, m!.Stage);
        Assert.True(m.Version > 1, "la Version deve crescere: la cache del motore è per (Id, Version)");
        Assert.Equal("drift: Mom1 in alert", m.RetiredReason); // la cicatrice sopravvive alla promozione
    }

    [Fact]
    public async Task Promote_RetiredModel_IsRefused_AndPointsAtTheReinstatePath()
    {
        // Il messaggio di rifiuto indicava un percorso che non esisteva ("ri-portato a Challenger").
        // Ora deve indicare quello che esiste — se un giorno tornasse a indicare l'irraggiungibile,
        // questo test lo dice.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Retired);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.False(outcome.Promoted);
        Assert.Contains("Staging", outcome.Reason);
        Assert.DoesNotContain("Challenger", outcome.Reason);
    }

    // --- Ciclo chiuso col drift (worker + monitor fittizio + registry reale) -----------------

    private sealed class AlertMonitor : IFeatureDriftMonitor
    {
        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            IReadOnlyList<FactorDriftReport> reports = new[]
            {
                new FactorDriftReport
                {
                    FeatureName = "Mom1",
                    Results = new[] { new DriftResult("Psi", 0.5, null, DriftSeverity.Alert, "shift") },
                },
            };
            return Task.FromResult(reports);
        }
    }

    [Fact]
    public async Task DriftWorker_ChampionInAlert_IsRetiredAndRetrainRequested()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);
        // [I6] Senza candele recenti il check è dichiarato SALTATO e il ciclo chiuso non scatta:
        // questa prova riguarda il RITIRO, quindi parte da un check che può davvero avvenire.
        await DriftTestData.SeedRecentCandlesAsync(f, "BTCUSDT");

        var worker = new FeatureDriftWorker(
            f, new AlertMonitor(), registry,
            new DriftMonitorOptions { Enabled = true, RetireChampionOnAlert = true, MinAlertsToRetire = 1, RecentCandles = DriftTestData.MinimumCandles }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(champ);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.NotNull(m.RetrainRequestedAtUtc);
        Assert.Contains("drift", m.RetiredReason);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h")); // niente Champion drifted attivo
    }

    [Fact]
    public async Task DriftWorker_StagingModelInAlert_IsNotRetired()
    {
        // Solo i Champion vengono ritirati dal ciclo chiuso: uno Staging in drift resta (lo si valuta a mano).
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var staging = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Staging);
        var registry = NewRegistry(f);

        var worker = new FeatureDriftWorker(
            f, new AlertMonitor(), registry, new DriftMonitorOptions { RetireChampionOnAlert = true }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);
        await worker.TickAsync(CancellationToken.None);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(staging);
        Assert.Equal(ModelStage.Staging, m!.Stage);
        Assert.Null(m.RetrainRequestedAtUtc);
    }

    [Fact]
    public async Task DriftWorker_RetiresAgain_AModelThatWasReinstatedAndRePromoted()
    {
        // [L3] Il rientro non disarma il ciclo automatico: se l'operatore scavalca un ritiro da drift
        // e il drift persiste, il check successivo ri-ritira. È il comportamento voluto — la
        // riabilitazione è una rimessa in coda, non un'immunità — ed è il motivo per cui la conferma
        // in /registry lo dice a schermo invece di lasciarlo scoprire dall'esito.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        // [I6] Senza candele il worker dichiara SALTATO e non arriva mai al ritiro: il check dev'essere
        // realistico, non la guardia indebolita.
        await DriftTestData.SeedRecentCandlesAsync(f, "BTCUSDT");
        var registry = NewRegistry(f);
        var worker = new FeatureDriftWorker(
            f, new AlertMonitor(), registry,
            new DriftMonitorOptions { Enabled = true, RetireChampionOnAlert = true, MinAlertsToRetire = 1, RecentCandles = DriftTestData.MinimumCandles }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);                    // 1° ritiro, dal drift
        Assert.True((await registry.ReinstateToStagingAsync(champ)).Changed); // l'operatore scavalca
        await registry.PromoteToChallengerAsync(champ);
        Assert.True((await registry.TryPromoteToChampionAsync(champ)).Promoted);
        await worker.TickAsync(CancellationToken.None);                    // 2° check: il drift c'è ancora

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(champ);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.Contains("drift", m.RetiredReason);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h"));
    }

    /// <summary>
    /// Monitor che, mentre valuta, ritira il modello da sotto: riproduce l'operatore che clicca
    /// «Ritira» su /registry nell'istante fra la lettura dei modelli del worker e la sua scrittura.
    /// </summary>
    private sealed class AlertMonitorThatRetiresBehind(IModelRegistry registry) : IFeatureDriftMonitor
    {
        public async Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            await registry.RetireAsync(model.Id, "ritirato a mano dall'operatore", requestRetrain: false, ct);
            return new[]
            {
                new FactorDriftReport
                {
                    FeatureName = "Mom1",
                    Results = new[] { new DriftResult("Psi", 0.5, null, DriftSeverity.Alert, "shift") },
                },
            };
        }
    }

    [Fact]
    public async Task DriftWorker_DoesNotClaimARetirementThatDidNotHappen()
    {
        // [L3] Il worker leggeva il modello come Champion e dava per scontato che il ritiro
        // riuscisse: scriveva ChampionRetired=true nella tabella d'esito e incrementava il contatore
        // anche quando il ritiro non era avvenuto. Ora legge l'esito. Il motivo dell'operatore resta.
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        await DriftTestData.SeedRecentCandlesAsync(f, "BTCUSDT"); // [I6] altrimenti il check è SALTATO
        var registry = NewRegistry(f);

        var worker = new FeatureDriftWorker(
            f, new AlertMonitorThatRetiresBehind(registry), registry,
            new DriftMonitorOptions { Enabled = true, RetireChampionOnAlert = true, MinAlertsToRetire = 1, RecentCandles = DriftTestData.MinimumCandles }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(champ);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.Equal("ritirato a mano dall'operatore", m.RetiredReason);  // il drift non l'ha sovrascritto
        Assert.Null(m.RetrainRequestedAtUtc);                             // né accodato un retrain mai deciso

        var row = await db.DriftCheckResults.AsNoTracking().SingleAsync(r => r.ModelId == champ);
        Assert.False(row.ChampionRetired); // e la tabella d'esito non se ne attribuisce il merito
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
