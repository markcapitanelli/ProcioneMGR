using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione per un bug reale trovato durante il lavoro sulla schedulazione automatica del
/// pipeline: <see cref="PipelineEngine.StartRunAsync"/> persisteva un <see cref="PipelineRun"/>
/// con Status="Running" PRIMA di controllare se un run era già in corso (il controllo viveva
/// solo dentro LaunchBackground, chiamato DOPO il salvataggio). Con un solo utente che clicca a
/// mano la race era quasi impossibile da osservare, ma lo scheduler introduce chiamate
/// concorrenti reali (due config dovute nello stesso tick, o lo scheduler che corre con un clic
/// manuale) — il secondo StartRunAsync concorrente creava una riga "Running" orfana per sempre,
/// perché il suo lancio in background falliva ma la riga restava già salvata. Fix: la guardia
/// "un run è già in corso" ora gira PRIMA di qualunque scrittura sul DB.
///
/// Per rendere la race deterministica (non affidata ai tempi macchina) il primo run usa UNA fase
/// finta che resta bloccata finché il test non la libera esplicitamente, tenendo lo slot globale
/// occupato per tutta la durata del secondo tentativo.
/// </summary>
[Collection("Postgres")]
public class PipelineEngineConcurrencyTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PipelineEngineConcurrencyTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Fase finta che resta ferma su ExecuteAsync finché il test non completa il TaskCompletionSource — tiene lo slot globale del motore occupato in modo deterministico.</summary>
    private sealed class BlockingStage(TaskCompletionSource release) : IPipelineStage
    {
        public string Name => "Blocking";
        public string DisplayName => "Blocking";
        public string Description => "";
        public int DefaultOrder => 1;
        public IReadOnlyList<StageDependency> Dependencies => [];
        public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];
        public string? ValidateInput(PipelineContext ctx) => null;
        public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct) => await release.Task;
        public StageSummary Summarize(PipelineContext ctx) => new() { StageName = Name, DisplayName = DisplayName };
    }

    private sealed class SingleStageCatalog(IPipelineStage stage) : IPipelineStageCatalog
    {
        public IReadOnlyList<IPipelineStage> Prototypes => [stage];
        public IPipelineStage Create(IServiceProvider scopedProvider, string name) => stage;
        public List<StageConfig> DefaultStages() => [];
    }

    private async Task<(PipelineEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(IPipelineStage stage)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new PipelineEngine(
            dbFactory,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new SingleStageCatalog(stage),
            new ProcioneMGR.Services.Experiments.ExperimentTracker(dbFactory),
            NullLogger<PipelineEngine>.Instance);
        return (engine, dbFactory);
    }

    private static async Task<int> SeedConfigAsync(IDbContextFactory<ApplicationDbContext> dbFactory, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cfg = new PipelineConfiguration
        {
            Name = name,
            CreatedBy = "user-1",
            ExecutionMode = "Paper",
            UniverseJson = "[]",
            DateRangesJson = "{}",
            StagesJson = """[{"Type":"Blocking","Order":1,"Enabled":true,"Parameters":{}}]""",
        };
        db.PipelineConfigurations.Add(cfg);
        await db.SaveChangesAsync();
        return cfg.Id;
    }

    [Fact]
    public async Task ConcurrentStartRunAsync_SecondCallThrows_WithoutPersistingOrphanedRun()
    {
        var release = new TaskCompletionSource();
        var (engine, dbFactory) = await BuildAsync(new BlockingStage(release));

        var configId1 = await SeedConfigAsync(dbFactory, "Config A");
        await engine.StartRunAsync(configId1, "Manual", "user-1"); // prende lo slot, resta bloccato sulla fase finta

        var configId2 = await SeedConfigAsync(dbFactory, "Config B");
        var ex = await Record.ExceptionAsync(() => engine.StartRunAsync(configId2, "Manual", "user-1"));

        release.SetResult(); // libera il primo run per non lasciare un Task.Run appeso a fine test

        Assert.IsType<InvalidOperationException>(ex);
        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var runsForSecondConfig = await verifyDb.PipelineRuns.Where(r => r.ConfigurationId == configId2).ToListAsync();
        Assert.Empty(runsForSecondConfig); // la riga orfana del bug originale non deve esistere
    }

    [Fact]
    public async Task RecoverOrphanedRuns_TurnsInheritedRunningRows_IntoResumablePaused()
    {
        // Il caso che questo copre è il riavvio del processo (in K8s: OGNI deploy del pod, strategy
        // Recreate) con un run in corso: la riga resta "Running" sul DB ma nessuno la sta più
        // eseguendo, e ResumeRunAsync la rifiuta credendola viva — bloccata per sempre. La bonifica
        // a startup la porta a "Paused", cioè ESATTAMENTE riprendibile.
        var (engine, dbFactory) = await BuildAsync(new BlockingStage(new TaskCompletionSource()));

        var configId = await SeedConfigAsync(dbFactory, "Config orfana");
        Guid orphanId, completedId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // Riga "Running" scritta a mano: simula l'eredità di un processo morto — questo engine
            // appena costruito ha lo slot in-memory vuoto, come dopo un riavvio vero.
            var orphan = new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = configId, Status = "Running", Trigger = "Scheduled", StartedAt = DateTime.UtcNow.AddHours(-2) };
            var completed = new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = configId, Status = "Completed", Trigger = "Manual", StartedAt = DateTime.UtcNow.AddHours(-3), CompletedAt = DateTime.UtcNow.AddHours(-2.5) };
            db.PipelineRuns.AddRange(orphan, completed);
            await db.SaveChangesAsync();
            (orphanId, completedId) = (orphan.Id, completed.Id);
        }

        var recovered = await engine.RecoverOrphanedRunsAsync();

        Assert.Equal(1, recovered);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var orphan = await db.PipelineRuns.SingleAsync(r => r.Id == orphanId);
            Assert.Equal("Paused", orphan.Status);                    // riprendibile, non Failed
            Assert.Null(orphan.CompletedAt);
            Assert.Contains("riavvio", orphan.ErrorLog);              // l'operatore sa perché è in pausa
            var completed = await db.PipelineRuns.SingleAsync(r => r.Id == completedId);
            Assert.Equal("Completed", completed.Status);              // gli altri stati non si toccano
        }

        // La prova che "Paused" era la scelta giusta: il run bonificato si può davvero riprendere.
        // (Riparte dalla fase Blocking mai completata; Cancel evita di lasciare un Task.Run appeso.)
        var resumedId = await engine.ResumeRunAsync(orphanId);
        Assert.Equal(orphanId, resumedId);
        engine.Cancel(orphanId);
    }

    [Fact]
    public async Task RecoverOrphanedRuns_WithNothingToRecover_IsANoOp()
    {
        var (engine, dbFactory) = await BuildAsync(new BlockingStage(new TaskCompletionSource()));
        var configId = await SeedConfigAsync(dbFactory, "Config pulita");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.PipelineRuns.Add(new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = configId, Status = "Completed", StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await engine.RecoverOrphanedRunsAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
