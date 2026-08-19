using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del Campaign Planner (Fase 1, PRD Autonomia Operativa §4) con motore pipeline FAKE
/// (stesso approccio dei PipelineSchedulerWorkerTests): rotazione su 0 sopravvissuti, backoff,
/// stop-su-successo (Observing + avvio corsie Paper), rotazione-esaurita → WaitingForTrigger,
/// ripresa-su-wake con trigger "Event", gate globale e per-campagna, slot singolo occupato.
/// </summary>
[Collection("Postgres")]
public sealed class CampaignPlannerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public CampaignPlannerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    private sealed class ScriptedPipelineEngine : IPipelineEngine
    {
        public List<(int ConfigId, string Trigger, string? UserId)> Started { get; } = new();
        public bool SlotBusy { get; set; }
        public Guid NextRunId { get; set; } = Guid.NewGuid();

        public Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
        {
            if (SlotBusy) throw new InvalidOperationException("Un run è già in corso.");
            Started.Add((configurationId, trigger, userId));
            return Task.FromResult(NextRunId);
        }

        public Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public void RequestPause(Guid runId) => throw new NotImplementedException();
        public void Cancel(Guid runId) => throw new NotImplementedException();
        public PipelineLiveStatus? GetLiveStatus() => null;
        public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages) => [];
        public Task<int> RecoverOrphanedRunsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ScriptedApplyEvaluator : IRunApplyEvaluator
    {
        public RunApplyOutcome Outcome { get; set; } = new() { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "applicato (fake)" };
        public List<Guid> Evaluated { get; } = new();

        public Task<RunApplyOutcome> EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default)
        {
            Evaluated.Add(runId);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<(ProcioneMGR.Services.Notifications.NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    /// <summary>Motore corsia fake: registra l'avvio Paper, può fingersi già in esecuzione o in quarantena.</summary>
    private sealed class RecordingLaneEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public bool IsRunning { get; set; }
        public bool IsEmergencyStopped { get; set; }
        public TradingMode StatusMode { get; set; } = TradingMode.Paper;
        public bool ThrowOnStart { get; set; }
        public TradingMode? StartedWith { get; private set; }
        public int StartCalls { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { IsRunning = IsRunning, Mode = StatusMode, IsEmergencyStopped = IsEmergencyStopped });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default)
        {
            if (ThrowOnStart) throw new InvalidOperationException("Corsia 0 in QUARANTENA (fake).");
            StartedWith = mode;
            StartCalls++;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<(CampaignPlanner Planner, ScriptedPipelineEngine Engine, ScriptedApplyEvaluator Evaluator,
        IDbContextFactory<ApplicationDbContext> DbFactory, RecordingLaneEngine[] Lanes)> BuildAsync(
        bool enabled = true, ProcioneMGR.Services.Notifications.INotifier? notifier = null,
        CampaignOptions? campaignOptions = null, AutoReapplyOptions? autoReapplyOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var lanes = new RecordingLaneEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            lanes[lane] = new RecordingLaneEngine(lane);
            services.AddKeyedSingleton<ITradingEngine>(lane, lanes[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new ScriptedPipelineEngine();
        var evaluator = new ScriptedApplyEvaluator();
        var planner = new CampaignPlanner(
            dbFactory, engine, evaluator, provider,
            (campaignOptions ?? new CampaignOptions { Enabled = enabled }).AsMonitor(),
            // [I7] Il percorso campagna rispetta il gate della ri-applica: nei test resta ACCESO per
            // default, così le prove esistenti esercitano il percorso di schieramento di sempre.
            (autoReapplyOptions ?? new AutoReapplyOptions { Enabled = true }).AsMonitor(),
            NullLogger<CampaignPlanner>.Instance,
            notifier);
        return (planner, engine, evaluator, dbFactory, lanes);
    }

    private static async Task<(int CampaignId, int Config1, int Config2)> SeedCampaignAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, bool campaignEnabled = true, int backoffHours = 12,
        bool autoStartPaper = true, string executionMode1 = "Paper")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cfg1 = new PipelineConfiguration { Name = "Caccia A", CreatedBy = "u1", ExecutionMode = executionMode1 };
        var cfg2 = new PipelineConfiguration { Name = "Caccia B", CreatedBy = "u1", ExecutionMode = "Paper" };
        db.PipelineConfigurations.AddRange(cfg1, cfg2);
        await db.SaveChangesAsync();

        var campaign = new VettingCampaign
        {
            Name = "Test",
            CreatedBy = "u1",
            Enabled = campaignEnabled,
            BackoffHours = backoffHours,
            AutoStartPaperLanes = autoStartPaper,
            ConfigStatesJson = CampaignPlanner.SerializeConfigStates(
            [
                new CampaignConfigState { ConfigurationId = cfg1.Id },
                new CampaignConfigState { ConfigurationId = cfg2.Id },
            ]),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.VettingCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        return (campaign.Id, cfg1.Id, cfg2.Id);
    }

    private static async Task<VettingCampaign> LoadAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VettingCampaigns.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async Task CompletePendingRunAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int campaignId, int survivors, string status = "Completed")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var campaign = await db.VettingCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        var recommendation = new PipelineRecommendation();
        for (var i = 0; i < survivors; i++)
        {
            recommendation.EnsembleLegs.Add(new ProposedLeg
            {
                StrategyName = "RsiOversold", DisplayName = $"Leg {i}", Symbol = "BTC/USDT", Timeframe = "1h",
                WeightPercent = 100m / Math.Max(1, survivors), HoldoutSharpe = 1.2m,
            });
        }
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = campaign.PendingRunId!.Value,
            ConfigurationId = 0,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = status == "Completed" ? DateTime.UtcNow : null,
            Status = status,
            Trigger = "Campaign",
            RecommendationJson = JsonSerializer.Serialize(recommendation),
        });
        await db.SaveChangesAsync();
    }

    // --- Gate --------------------------------------------------------------------------------

    [Fact]
    public async Task Tick_GlobalGateOff_DoesNothing()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync(enabled: false);
        await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();

        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task Tick_CampaignDisabled_DoesNothing()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        await SeedCampaignAsync(dbFactory, campaignEnabled: false);

        await planner.TickAsync();

        Assert.Empty(engine.Started);
    }

    // --- Rotazione ---------------------------------------------------------------------------

    [Fact]
    public async Task Tick_StartsFirstConfig_WithCampaignTrigger_AndSetsPending()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();

        var started = Assert.Single(engine.Started);
        Assert.Equal(config1, started.ConfigId);
        Assert.Equal("Campaign", started.Trigger);
        Assert.Equal("u1", started.UserId);

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(engine.NextRunId, campaign.PendingRunId);
        var state = CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson).Single(s => s.ConfigurationId == config1);
        Assert.Equal(1, state.Attempts);
        Assert.Equal(engine.NextRunId, state.LastRunId);
    }

    [Fact]
    public async Task Tick_PendingRunStillRunning_Waits()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Running");

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();

        Assert.Single(engine.Started); // nessun secondo run
        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.NotNull(campaign.PendingRunId);
    }

    [Fact]
    public async Task NoSurvivors_RotatesToNextConfig_ThenExhaustion_WaitsForTrigger()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        // Run 1 (config A): 0 sopravvissuti.
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync(); // valuta l'esito, libera il pending
        var afterFirst = await LoadAsync(dbFactory, campaignId);
        Assert.Null(afterFirst.PendingRunId);
        Assert.Equal(CampaignStatus.Rotating, afterFirst.Status);
        Assert.Equal("NoSurvivors", CampaignPlanner.ParseConfigStates(afterFirst.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);

        // Run 2 (config B, la A è in backoff): 0 sopravvissuti.
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();

        // Rotazione esaurita (entrambe in backoff 12h) → in attesa di trigger.
        await planner.TickAsync();
        var exhausted = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.WaitingForTrigger, exhausted.Status);
        Assert.Equal(2, engine.Started.Count); // nessun terzo run
    }

    [Fact]
    public async Task Survivors_Applied_StopsRotation_Observing_StartsOnlyStoppedPaperLanes()
    {
        var (planner, engine, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 2, Message = "ok" };
        lanes[0].IsRunning = true; // corsia 0 già in esecuzione: NON va toccata

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 3);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Observing, campaign.Status);
        Assert.Null(campaign.PendingRunId);
        Assert.Equal(2, campaign.ObservedLanes); // stato ATTESO di flotta per il riallineamento C3
        Assert.Equal("Applied", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);
        Assert.Single(evaluator.Evaluated);

        Assert.Null(lanes[0].StartedWith);                    // già running: mai riavviata
        Assert.Equal(TradingMode.Paper, lanes[1].StartedWith); // avviata in Paper
        Assert.Null(lanes[2].StartedWith);                    // fuori da LanesUsed

        // In osservazione: nessun nuovo run ai tick successivi.
        await planner.TickAsync();
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task Survivors_QuarantinedLane_ApplyProceeds_StartFailureIsNotFatal()
    {
        var (planner, _, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "ok" };
        lanes[0].ThrowOnStart = true; // es. quarantena Fase 0-A3

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 1);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Observing, campaign.Status); // la campagna non muore per l'avvio fallito
        Assert.Null(lanes[0].StartedWith);
    }

    [Fact]
    public async Task Survivors_NotApplied_RotationContinues()
    {
        var (planner, engine, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = false, Vetoed = true, Message = "VETO del supervisore AI." };

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 2);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Rotating, campaign.Status); // scostamento documentato dal PRD: senza schieramento la caccia continua
        Assert.Equal("NotApplied", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);
        Assert.All(lanes, l => Assert.Null(l.StartedWith));

        // Il giro dopo parte la config B.
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
    }

    [Fact]
    public async Task FailedRun_MarksConfig_AndRotationContinues()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Failed");
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Null(campaign.PendingRunId);
        Assert.Equal("Failed", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
    }

    // --- [I7] Annullamento umano: un ORDINE, non un esito --------------------------------------

    /// <summary>
    /// Il difetto che copre, trovato leggendo il codice il 2026-08-18: un run annullato a mano da
    /// <c>/pipeline</c> finiva nello stesso ramo di uno <c>Failed</c>, la config veniva marcata
    /// fallita e la rotazione passava alla successiva — che, mai eseguita in questo ciclo, è
    /// <b>sempre eleggibile</b>. Entro un tick da 60 secondi partiva un altro run automatico:
    /// <b>chi annullava otteneva il contrario di ciò che voleva</b>, e l'unico modo di fermare la
    /// campagna era disabilitarla.
    /// </summary>
    [Fact]
    public async Task RunAnnullato_MetteLaCampagnaInPausa_ENonFaRipartireLaRotazione()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Cancelled");
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Null(campaign.PendingRunId);
        Assert.NotNull(campaign.PausedUntilUtc);
        Assert.True(campaign.PausedUntilUtc > DateTime.UtcNow.AddMinutes(50));
        // L'annullamento NON è un fallimento della config: distinguerli è il punto.
        Assert.Equal("Cancelled", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);

        // E soprattutto: il tick successivo NON avvia niente.
        var avviatiPrima = engine.Started.Count;
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(avviatiPrima, engine.Started.Count);
    }

    /// <summary>
    /// <b>Il controllo sul rumore</b>: con la pausa a zero il comportamento è quello storico. Senza
    /// questo test, «la pausa funziona» sarebbe soddisfatto anche da un planner che non riparte mai.
    /// </summary>
    [Fact]
    public async Task PausaAZero_ComportamentoStorico_LaRotazioneProsegue()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync(
            campaignOptions: new CampaignOptions { Enabled = true, CancelPauseMinutes = 0 });
        var (campaignId, _, config2) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Cancelled");
        await planner.TickAsync();

        Assert.Null((await LoadAsync(dbFactory, campaignId)).PausedUntilUtc);

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
    }

    /// <summary>Un run FALLITO resta un esito, non un ordine: nessuna pausa, comportamento invariato.</summary>
    [Fact]
    public async Task RunFallito_NonMetteInPausa()
    {
        var (planner, _, _, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Failed");
        await planner.TickAsync();

        Assert.Null((await LoadAsync(dbFactory, campaignId)).PausedUntilUtc);
    }

    // --- [I7] Il gate della ri-applica vale anche per la campagna -------------------------------

    /// <summary>
    /// Il difetto: <c>AutoReapply:Enabled</c> è letto solo dallo scheduler, quindi con la ri-applica
    /// SPENTA la campagna schierava lo stesso. Un interruttore che chiude una porta e ne lascia
    /// aperta un'altra è la stessa forma dei pannelli che scrivevano sul processo sbagliato — e qui
    /// la porta aperta <b>riscrive corsie</b>.
    /// </summary>
    [Fact]
    public async Task RiApplicaSpenta_ISopravvissutiNonVengonoSchierati_MaRestanoRegistrati()
    {
        var (planner, _, evaluator, dbFactory, _) = await BuildAsync(
            autoReapplyOptions: new AutoReapplyOptions { Enabled = false });
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 2, status: "Completed");
        await planner.TickAsync();

        // L'applier non viene MAI invocato: è la prova che il gate morde.
        Assert.Empty(evaluator.Evaluated);

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal("NotApplied", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);
        Assert.Contains("ri-applica automatica", campaign.LastOutcome);
        // La campagna resta in rotazione: i sopravvissuti non si perdono, aspettano un click umano.
        Assert.Equal(CampaignStatus.Rotating, campaign.Status);
    }

    /// <summary>
    /// Il complemento: con la ri-applica ACCESA il percorso è quello di sempre. Senza, il test sopra
    /// sarebbe soddisfatto anche da una campagna che non schiera mai.
    /// </summary>
    [Fact]
    public async Task RiApplicaAccesa_ISopravvissutiPassanoDallApplier()
    {
        var (planner, _, evaluator, dbFactory, _) = await BuildAsync(
            autoReapplyOptions: new AutoReapplyOptions { Enabled = true });
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 2, status: "Completed");
        await planner.TickAsync();

        Assert.NotEmpty(evaluator.Evaluated);
    }

    // --- Wake (trigger contestuale / operatore) ------------------------------------------------

    [Fact]
    public async Task Wake_ResumesWaitingCampaign_NextRunHasEventTrigger_AndBypassesBackoff()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        // Esaurisce la rotazione (2 config, 0 sopravvissuti ciascuna).
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        await planner.TickAsync();
        Assert.Equal(CampaignStatus.WaitingForTrigger, (await LoadAsync(dbFactory, campaignId)).Status);

        // Il trigger contestuale (Fase 2) sveglia il planner.
        var woken = await planner.WakeAsync("Cambio regime K-means: cluster 2 → 0");
        Assert.Equal(1, woken);

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();

        var lastStart = engine.Started[^1];
        Assert.Equal("Event", lastStart.Trigger);           // run visibile con ⚡ nello storico
        Assert.Equal(config1, lastStart.ConfigId);          // round-robin: si riparte dalla successiva all'ultima (B) → A
        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Rotating, campaign.Status);
        Assert.Null(campaign.PendingWakeReason);            // consumato
        Assert.NotNull(campaign.PendingRunId);
        _ = config2;
    }

    [Fact]
    public async Task Wake_DoesNotTouchObservingCampaigns()
    {
        var (planner, _, evaluator, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "ok" };
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 1);
        await planner.TickAsync();
        Assert.Equal(CampaignStatus.Observing, (await LoadAsync(dbFactory, campaignId)).Status);

        var woken = await planner.WakeAsync("Cambio regime");

        Assert.Equal(0, woken);
        Assert.Equal(CampaignStatus.Observing, (await LoadAsync(dbFactory, campaignId)).Status);
    }

    // --- Riallineamento post-riavvio (Fase 3-C3) ----------------------------------------------

    private static async Task SeedObservingCampaignAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        int campaignId, int observedLanes)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var campaign = await db.VettingCampaigns.SingleAsync(c => c.Id == campaignId);
        campaign.Status = CampaignStatus.Observing;
        campaign.ObservedLanes = observedLanes;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Realign_StoppedPaperLane_RestartedOnce_EmergencyLaneOnlyNotified()
    {
        var notifier = new RecordingNotifier();
        var (planner, _, _, dbFactory, lanes) = await BuildAsync(notifier: notifier);
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        await SeedObservingCampaignAsync(dbFactory, campaignId, observedLanes: 2);

        lanes[0].IsRunning = false;                 // caso pulito: ferma in Paper → riallineata
        lanes[1].IsRunning = false;
        lanes[1].IsEmergencyStopped = true;         // emergency: MAI riavviata, solo notificata

        await planner.TickAsync();

        Assert.Equal(TradingMode.Paper, lanes[0].StartedWith);
        Assert.Null(lanes[1].StartedWith);
        Assert.Contains(notifier.Sent, n => n.Title.Contains("corsia 0 riallineata"));
        Assert.Contains(notifier.Sent, n =>
            n.Severity == ProcioneMGR.Services.Notifications.NotificationSeverity.Warning && n.Title.Contains("corsia 1 divergente"));

        // Check UNA volta per processo: il tick successivo non combatte l'operatore.
        lanes[0].IsRunning = false;
        await planner.TickAsync();
        Assert.Equal(1, lanes[0].StartCalls);
    }

    [Fact]
    public async Task Realign_NonPaperStoppedLane_NotifiedNotRestarted()
    {
        var notifier = new RecordingNotifier();
        var (planner, _, _, dbFactory, lanes) = await BuildAsync(notifier: notifier);
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        await SeedObservingCampaignAsync(dbFactory, campaignId, observedLanes: 1);

        lanes[0].IsRunning = false;
        lanes[0].StatusMode = TradingMode.Testnet; // riavviarla in Paper la retrocederebbe in silenzio

        await planner.TickAsync();

        Assert.Null(lanes[0].StartedWith);
        Assert.Contains(notifier.Sent, n => n.Title.Contains("corsia 0 divergente"));
    }

    [Fact]
    public async Task Realign_RunningLanes_NoActionNoNoise()
    {
        var notifier = new RecordingNotifier();
        var (planner, _, _, dbFactory, lanes) = await BuildAsync(notifier: notifier);
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        await SeedObservingCampaignAsync(dbFactory, campaignId, observedLanes: 2);
        lanes[0].IsRunning = true;
        lanes[1].IsRunning = true;

        await planner.TickAsync();

        Assert.All(lanes, l => Assert.Null(l.StartedWith));
        Assert.Empty(notifier.Sent);
    }

    // --- Notifiche (Fase 4) ------------------------------------------------------------------

    [Fact]
    public async Task Notifications_EmittedOnApplied_AndOnExhaustion()
    {
        var notifier = new RecordingNotifier();
        var (planner, engine, evaluator, dbFactory, _) = await BuildAsync(notifier: notifier);
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);

        // Esaurimento: 2 run a 0 sopravvissuti → Warning "rotazione esaurita".
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        await planner.TickAsync();
        Assert.Contains(notifier.Sent, n =>
            n.Severity == ProcioneMGR.Services.Notifications.NotificationSeverity.Warning && n.Title.Contains("rotazione esaurita"));

        // Applica riuscita: Info "ensemble schierato".
        await planner.WakeAsync("test");
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "ok" };
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 2);
        await planner.TickAsync();
        Assert.Contains(notifier.Sent, n =>
            n.Severity == ProcioneMGR.Services.Notifications.NotificationSeverity.Info && n.Title.Contains("ensemble schierato"));
    }

    // --- Difese ------------------------------------------------------------------------------

    [Fact]
    public async Task SlotBusy_RetriesOnNextTick()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        engine.SlotBusy = true;

        await planner.TickAsync();
        Assert.Empty(engine.Started);
        Assert.Null((await LoadAsync(dbFactory, campaignId)).PendingRunId);

        engine.SlotBusy = false;
        await planner.TickAsync();
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task LiveConfig_IsSkipped_NextConfigUsed()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (_, _, config2) = await SeedCampaignAsync(dbFactory, executionMode1: "Live");

        await planner.TickAsync();

        var started = Assert.Single(engine.Started);
        Assert.Equal(config2, started.ConfigId); // la config Live non parte MAI da un automatismo
    }
}
