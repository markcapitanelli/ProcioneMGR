using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da <c>Pipeline.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): bozze dell'editor (nuova/da esistente col merge delle fasi nuove),
/// catena di validazione del salvataggio, CRUD config, controllo run, dettaglio con confronto e
/// decisione di ri-applica, export markdown — prima tutto nel <c>@code</c> del componente, senza
/// test indipendenti da Blazor. Motore e applier sono fake che catturano le chiamate (il motore
/// vero ha i propri test); catalogo fasi fake minimale; config/run/artifact su Postgres reale.
/// </summary>
[Collection("Postgres")]
public sealed class PipelinePageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PipelinePageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes ---------------------------------------------------------------------------------

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeStage(string name, int order, IReadOnlyList<StageParameterDefinition>? defs = null) : IPipelineStage
    {
        public string Name => name;
        public string DisplayName => name + " (fase)";
        public string Description => "fake";
        public int DefaultOrder => order;
        public IReadOnlyList<StageDependency> Dependencies => [];
        public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => defs ?? [];
        public string? ValidateInput(PipelineContext ctx) => null;
        public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct) => throw new NotImplementedException();
        public StageSummary Summarize(PipelineContext ctx) => throw new NotImplementedException();
    }

    private sealed class FakeCatalog : IPipelineStageCatalog
    {
        public List<IPipelineStage> Stages { get; } =
        [
            new FakeStage("Alpha", 1, [new StageParameterDefinition("Window", "Finestra", "20", "hint")]),
            new FakeStage("Beta", 2),
        ];
        public IReadOnlyList<IPipelineStage> Prototypes => Stages;
        public IPipelineStage Create(IServiceProvider scopedProvider, string name) => throw new NotImplementedException();
        public List<StageConfig> DefaultStages() => Stages
            .Select(s => new StageConfig
            {
                Type = s.Name, Order = s.DefaultOrder, Enabled = true,
                Parameters = s.ParameterDefinitions.ToDictionary(d => d.Key, d => d.DefaultValue),
            })
            .ToList();
    }

    private sealed class FakeEngine : IPipelineEngine
    {
        public PipelineLiveStatus? LiveToReturn { get; set; }
        public List<string> ProblemsToReturn { get; set; } = [];
        public List<string> Calls { get; } = [];

        public Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
        { Calls.Add($"Start:{configurationId}:{trigger}:{userId}"); return Task.FromResult(Guid.NewGuid()); }
        public Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default)
        { Calls.Add($"Resume:{runId}"); return Task.FromResult(runId); }
        public void RequestPause(Guid runId) => Calls.Add($"Pause:{runId}");
        public void Cancel(Guid runId) => Calls.Add($"Cancel:{runId}");
        public PipelineLiveStatus? GetLiveStatus() => LiveToReturn;
        public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages) => ProblemsToReturn;
        public Task<int> RecoverOrphanedRunsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeApplier : IPipelineApplier
    {
        public PipelineRecommendation? LastApplied { get; private set; }
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)
        {
            LastApplied = recommendation;
            return Task.FromResult(new ApplyResult { LanesUsed = 2, Message = "Applicato su 2 corsie." });
        }
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProcioneMGR.Services.Ensemble.EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public ProcioneMGR.Services.Ensemble.EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation) => throw new NotImplementedException();
    }

    // --- Setup ---------------------------------------------------------------------------------

    private FakeEngine _engine = null!;
    private FakeApplier _applier = null!;
    private FakeCatalog _catalog = null!;

    private async Task<(PipelinePageService Svc, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(bool ensureSchema = true)
    {
        _engine = new FakeEngine();
        _applier = new FakeApplier();
        _catalog = new FakeCatalog();

        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (ensureSchema)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        return (new PipelinePageService(_engine, _catalog, dbFactory, _applier), dbFactory);
    }

    private static PipelineConfigDraft ValidDraft(PipelinePageService svc)
    {
        var draft = svc.BuildNewConfigDraft();
        draft.Config.Name = "Config di prova";
        return draft;
    }

    // --- Bozze dell'editor ---------------------------------------------------------------------

    [Fact]
    public async Task BuildNewConfigDraft_HasSafeDefaults()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var draft = svc.BuildNewConfigDraft();

        Assert.Equal("Paper", draft.Config.ExecutionMode);   // mai Live di default
        Assert.Equal("BTC/USDT", Assert.Single(draft.Universe).Symbol);
        Assert.True(draft.Ranges.SelectionTo > draft.Ranges.SelectionFrom);
        Assert.True(draft.Ranges.HoldoutFrom >= draft.Ranges.SelectionTo);   // mai sovrapposti
        Assert.Equal(["Alpha", "Beta"], draft.Stages.Select(s => s.Type));
        Assert.Equal("20", draft.Stages[0].Parameters["Window"]);
    }

    [Fact]
    public async Task BuildEditDraft_MergesNewPrototypesDisabled_AndOrdersByOrder()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var existing = new PipelineConfiguration
        {
            Id = 7, Name = "Vecchia", ExchangeName = "Binance",
            UniverseJson = JsonSerializer.Serialize(new List<SeriesSpec> { new() { Symbol = "ETH/USDT", Timeframe = "4h" } }),
            DateRangesJson = JsonSerializer.Serialize(new PipelineDateRanges { SelectionFrom = new(2024, 1, 1), SelectionTo = new(2025, 1, 1), HoldoutFrom = new(2025, 1, 1), HoldoutTo = new(2025, 6, 1) }),
            // Config salvata quando esisteva solo "Beta": "Alpha" è stata aggiunta DOPO.
            StagesJson = JsonSerializer.Serialize(new List<StageConfig> { new() { Type = "Beta", Order = 5, Enabled = true } }),
        };

        var draft = svc.BuildEditDraft(existing);

        Assert.Equal(7, draft.Config.Id);
        Assert.Equal("ETH/USDT", Assert.Single(draft.Universe).Symbol);
        Assert.Equal(2, draft.Stages.Count);
        var merged = draft.Stages.Single(s => s.Type == "Alpha");
        Assert.False(merged.Enabled);                        // proposta, MAI attivata di nascosto
        Assert.Equal("20", merged.Parameters["Window"]);
        Assert.Equal(["Alpha", "Beta"], draft.Stages.Select(s => s.Type));   // ordinate per Order (1 < 5)
    }

    [Fact]
    public async Task MoveStage_SwapsAndRenumbers_OutOfRangeIsNoOp()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var stages = svc.BuildNewConfigDraft().Stages;

        PipelinePageService.MoveStage(stages, 0, -1);        // fuori range: no-op
        Assert.Equal(["Alpha", "Beta"], stages.Select(s => s.Type));

        PipelinePageService.MoveStage(stages, 0, +1);
        Assert.Equal(["Beta", "Alpha"], stages.Select(s => s.Type));
        Assert.Equal([1, 2], stages.Select(s => s.Order));   // rinumerate 1..N
    }

    // --- Salvataggio: catena di validazione ----------------------------------------------------

    [Fact]
    public async Task SaveConfig_ValidationChain()
    {
        var (svc, _) = await BuildAsync();

        var noName = ValidDraft(svc);
        noName.Config.Name = " ";
        Assert.True((await svc.SaveConfigAsync(noName, "u")).IsError);

        var noUniverse = ValidDraft(svc);
        noUniverse.Universe[0].Symbol = "  ";               // symbol vuoto → rimosso → universo vuoto
        var resU = await svc.SaveConfigAsync(noUniverse, "u");
        Assert.True(resU.IsError);
        Assert.Contains("almeno una serie", resU.Message);

        var badRanges = ValidDraft(svc);
        badRanges.Ranges.SelectionTo = badRanges.Ranges.SelectionFrom;
        Assert.True((await svc.SaveConfigAsync(badRanges, "u")).IsError);

        var overlap = ValidDraft(svc);
        overlap.Ranges.HoldoutFrom = overlap.Ranges.SelectionTo.AddDays(-10);
        var resO = await svc.SaveConfigAsync(overlap, "u");
        Assert.True(resO.IsError);
        Assert.Contains("DOPO la fine della selezione", resO.Message);

        _engine.ProblemsToReturn = ["Fase Beta richiede Alpha"];
        var stageProblem = ValidDraft(svc);
        var resS = await svc.SaveConfigAsync(stageProblem, "u");
        Assert.True(resS.IsError);
        Assert.Equal(["Fase Beta richiede Alpha"], resS.Problems);
    }

    [Fact]
    public async Task SaveConfig_NewRow_Persists_AndReloads()
    {
        var (svc, db) = await BuildAsync();
        var draft = ValidDraft(svc);

        var res = await svc.SaveConfigAsync(draft, "utente-1");

        Assert.False(res.IsError);
        var saved = Assert.Single(svc.Configs);              // Reload interno
        Assert.Equal("Config di prova", saved.Name);
        Assert.Equal("utente-1", saved.CreatedBy);
        await using var ctx = await db.CreateDbContextAsync();
        var row = await ctx.PipelineConfigurations.SingleAsync();
        Assert.Contains("BTC/USDT", row.UniverseJson);
        Assert.Contains("Alpha", row.StagesJson);
    }

    [Fact]
    public async Task SaveConfig_Edit_ScheduleChange_ResetsNextRunAt()
    {
        var (svc, db) = await BuildAsync();
        var draft = ValidDraft(svc);
        draft.Config.Schedule = "0 3 * * *";
        draft.Config.ScheduleEnabled = true;
        await svc.SaveConfigAsync(draft, "u");
        int id;
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var row = await ctx.PipelineConfigurations.SingleAsync();
            row.NextRunAt = new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc);   // occorrenza calcolata
            await ctx.SaveChangesAsync();
            id = row.Id;
        }

        // Modifica SENZA toccare la schedulazione: NextRunAt resta.
        var editSame = svc.BuildEditDraft(svc.Configs.Single(c => c.Id == id));
        editSame.Config.Description = "aggiornata";
        await svc.SaveConfigAsync(editSame, "u");
        await using (var ctx = await db.CreateDbContextAsync())
            Assert.NotNull((await ctx.PipelineConfigurations.SingleAsync()).NextRunAt);

        // Cambio di cron: NextRunAt azzerato → lo scheduler ricalcola dall'espressione NUOVA.
        var editCron = svc.BuildEditDraft(svc.Configs.Single(c => c.Id == id));
        editCron.Config.Schedule = "0 5 * * *";
        await svc.SaveConfigAsync(editCron, "u");
        await using (var ctx2 = await db.CreateDbContextAsync())
            Assert.Null((await ctx2.PipelineConfigurations.SingleAsync()).NextRunAt);
    }

    [Fact]
    public async Task CloneAndDelete_RoundTrip()
    {
        var (svc, _) = await BuildAsync();
        await svc.SaveConfigAsync(ValidDraft(svc), "u");
        var original = svc.Configs.Single();

        var clone = await svc.CloneConfigAsync(original, "u2");
        Assert.False(clone.IsError);
        Assert.Equal(2, svc.Configs.Count);
        Assert.Contains(svc.Configs, c => c.Name == "Config di prova (copia)" && c.CreatedBy == "u2");

        await svc.DeleteConfigAsync(original.Id);
        Assert.Single(svc.Configs);
    }

    // --- Reload + raccomandazione --------------------------------------------------------------

    [Fact]
    public async Task Reload_ParsesLastRecommendation_FromMostRecentCompletedRun()
    {
        var (svc, db) = await BuildAsync();
        var rec = new PipelineRecommendation { RegimeLabel = "trend", Survivors = 3, CandidatesEvaluated = 10 };
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.PipelineRuns.Add(new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = DateTime.UtcNow.AddHours(-2), Status = "Completed", RecommendationJson = "{}" });
            ctx.PipelineRuns.Add(new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = DateTime.UtcNow.AddHours(-1), Status = "Completed", RecommendationJson = JsonSerializer.Serialize(rec) });
            ctx.PipelineRuns.Add(new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = DateTime.UtcNow, Status = "Failed", RecommendationJson = JsonSerializer.Serialize(rec) });
            await ctx.SaveChangesAsync();
        }

        await svc.ReloadAsync();

        Assert.Equal(3, svc.Runs.Count);
        Assert.NotNull(svc.LastRecommendation);              // dal più recente COMPLETATO con json non vuoto
        Assert.Equal("trend", svc.LastRecommendation!.RegimeLabel);
        Assert.Equal(DateTime.UtcNow.AddHours(-1).Hour, svc.LastRecommendationRun!.StartedAt.Hour);
    }

    // --- Dettaglio run + confronto -------------------------------------------------------------

    [Fact]
    public async Task SelectRun_LoadsSummaries_PreviousComparison_AndDecisionArtifact()
    {
        var (svc, db) = await BuildAsync();
        List<StageSummary> Summaries(decimal sharpe) =>
        [
            new StageSummary { DisplayName = "Holdout", Order = 1, Metrics = new Dictionary<string, decimal> { ["Sharpe"] = sharpe } },
        ];
        var prevRun = new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = DateTime.UtcNow.AddDays(-1), Status = "Completed", StageSummariesJson = JsonSerializer.Serialize(Summaries(0.8m)) };
        var currRun = new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = DateTime.UtcNow, Status = "Completed", StageSummariesJson = JsonSerializer.Serialize(Summaries(1.1m)) };
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.PipelineRuns.AddRange(prevRun, currRun);
            ctx.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = currRun.Id, StageName = "AutoReapply", Kind = AutoReapplyArtifactKinds.Decision,
                PayloadJson = JsonSerializer.Serialize(new AutoReapplyDecisionArtifact { Applied = true, Message = "Sostituito." }),
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        await svc.ReloadAsync();

        await svc.SelectRunAsync(svc.Runs.First(r => r.Id == currRun.Id));

        Assert.Equal(currRun.Id, svc.SelectedRun!.Id);
        Assert.Single(svc.SelectedSummaries);
        Assert.Single(svc.PreviousRunSummaries);             // il run COMPLETATO precedente della stessa config
        var (label, prev, curr) = Assert.Single(svc.CompareRuns());
        Assert.Equal("Holdout: Sharpe", label);
        Assert.Equal(0.8m, prev);
        Assert.Equal(1.1m, curr);
        Assert.True(svc.SelectedDecision!.Applied);          // decisione ri-applica caricata

        svc.CloseSelectedRun();
        Assert.Null(svc.SelectedRun);
    }

    // --- Live + controllo run ------------------------------------------------------------------

    [Fact]
    public async Task RefreshLive_SignalsJustFinished_OnlyOnRunningToNullTransition()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);

        Assert.False(svc.RefreshLive());                     // mai stato in run
        _engine.LiveToReturn = new PipelineLiveStatus { RunId = Guid.NewGuid() };
        Assert.False(svc.RefreshLive());                     // appena partito
        _engine.LiveToReturn = null;
        Assert.True(svc.RefreshLive());                      // APPENA finito → ricaricare
        Assert.False(svc.RefreshLive());                     // già gestito
    }

    [Fact]
    public async Task StartAndResume_DelegateToEngine_PauseCancelUseLiveRunId()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var runId = Guid.NewGuid();

        await svc.StartRunAsync(42, "utente");
        Assert.Contains("Start:42:Manual:utente", _engine.Calls);

        await svc.ResumeRunAsync(runId, "utente");
        Assert.Contains($"Resume:{runId}", _engine.Calls);

        svc.PauseLiveRun();                                  // Live null → no-op
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("Pause:"));
        _engine.LiveToReturn = new PipelineLiveStatus { RunId = runId };
        svc.RefreshLive();
        svc.PauseLiveRun();
        svc.CancelLiveRun();
        Assert.Contains($"Pause:{runId}", _engine.Calls);
        Assert.Contains($"Cancel:{runId}", _engine.Calls);
    }

    // --- Applica & export ----------------------------------------------------------------------

    [Fact]
    public async Task ApplyRecommendation_NullOrEmpty_IsSilentNoOp_WithLegs_Delegates()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);

        Assert.Null(await svc.ApplyRecommendationAsync(null));
        Assert.Null(await svc.ApplyRecommendationAsync(new PipelineRecommendation()));   // zero gambe
        Assert.Null(_applier.LastApplied);

        var rec = new PipelineRecommendation { EnsembleLegs = [new ProposedLeg()] };
        var res = await svc.ApplyRecommendationAsync(rec);

        Assert.NotNull(res);
        Assert.Equal("Applicato su 2 corsie.", res!.Message);
        Assert.Same(rec, _applier.LastApplied);
    }

    [Fact]
    public async Task ExportHref_And_UniverseSummary()
    {
        var run = new PipelineRun
        {
            Id = Guid.NewGuid(), StartedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc), Status = "Completed",
            Conclusion = "Tutto bene",
            StageSummariesJson = JsonSerializer.Serialize(new List<StageSummary> { new() { DisplayName = "Holdout", Order = 1, Text = "ok" } }),
        };
        var href = PipelinePageService.ExportHref(run);
        Assert.StartsWith("data:text/markdown", href);
        var md = Uri.UnescapeDataString(href.Split(',', 2)[1]);
        Assert.Contains("Tutto bene", md);
        Assert.Contains("### Holdout", md);
        Assert.Equal("#", PipelinePageService.ExportHref(null));

        var cfg = new PipelineConfiguration
        {
            UniverseJson = JsonSerializer.Serialize(Enumerable.Range(1, 6).Select(i => new SeriesSpec { Symbol = $"S{i}/USDT", Timeframe = "1h" }).ToList()),
        };
        var summary = PipelinePageService.UniverseSummary(cfg);
        Assert.Contains("S1/USDT 1h", summary);
        Assert.EndsWith("+2", summary);                      // 6 serie: 4 mostrate + 2
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
