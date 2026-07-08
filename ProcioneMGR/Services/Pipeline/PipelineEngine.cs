using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Singleton run orchestrator. One run at a time (the underlying discovery/backtest engines
/// are CPU-heavy); executes in the background on a dedicated scope, checkpoints the context
/// to the DB after every stage, supports graceful pause (at stage boundaries), cancellation,
/// and resume-from-checkpoint. Live progress is polled by the UI (2s timer, same pattern as
/// /trading — Blazor Server already streams the UI over SignalR, a dedicated hub would add
/// moving parts without adding capability).
/// </summary>
public sealed class PipelineEngine(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IPipelineStageCatalog catalog,
    ProcioneMGR.Services.Experiments.IExperimentTracker experimentTracker,
    ILogger<PipelineEngine> logger) : IPipelineEngine
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _gate = new();
    private PipelineLiveStatus? _live;
    private CancellationTokenSource? _cts;
    private volatile bool _pauseRequested;

    public PipelineLiveStatus? GetLiveStatus()
    {
        lock (_gate)
        {
            if (_live is null) return null;
            // Defensive copy: the UI thread must not observe a list being mutated mid-run.
            return new PipelineLiveStatus
            {
                RunId = _live.RunId,
                ConfigurationId = _live.ConfigurationId,
                ConfigurationName = _live.ConfigurationName,
                StartedUtc = _live.StartedUtc,
                CurrentStage = _live.CurrentStage,
                Stages = _live.Stages.Select(s => new StageSummary
                {
                    StageName = s.StageName,
                    DisplayName = s.DisplayName,
                    Order = s.Order,
                    Status = s.Status,
                    StartedUtc = s.StartedUtc,
                    Duration = s.Duration,
                    Text = s.Text,
                    Metrics = new(s.Metrics),
                    Error = s.Error,
                }).ToList(),
                RecentLog = _live.RecentLog.ToList(),
                PauseRequested = _pauseRequested,
            };
        }
    }

    public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages)
        => PipelineDagValidator.Validate(
            stages,
            catalog.Prototypes.ToDictionary(p => p.Name, p => p.Dependencies, StringComparer.OrdinalIgnoreCase),
            catalog.Prototypes.ToDictionary(p => p.Name, p => p.DisplayName, StringComparer.OrdinalIgnoreCase));

    public async Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
    {
        // Guardia anticipata (fail-fast) PRIMA di qualunque scrittura sul DB: senza questa,
        // due StartRunAsync concorrenti (es. lo scheduler e un clic manuale, o due config
        // schedulate dovute nello stesso istante) persisterebbero ENTRAMBI un PipelineRun con
        // Status="Running", e solo il secondo lancio fallirebbe in LaunchBackground — lasciando
        // per sempre nel DB una riga "Running" orfana che nessun task in background completerà
        // mai. Bug preesistente, invisibile con un solo utente che clicca a mano, ma reso
        // concreto dallo scheduler (introduce chiamate concorrenti reali). Il controllo dentro
        // LaunchBackground resta come guardia finale/autoritativa contro la finestra di race
        // residua tra questo controllo e il lancio effettivo.
        lock (_gate)
        {
            if (_live is not null) throw new InvalidOperationException("Un run del pipeline è già in corso: attendere o annullarlo.");
        }

        PipelineConfiguration? config;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            config = await db.PipelineConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == configurationId, ct);
        }
        if (config is null) throw new InvalidOperationException($"Configurazione {configurationId} non trovata.");

        var stages = JsonSerializer.Deserialize<List<StageConfig>>(config.StagesJson) ?? [];
        var problems = ValidateConfiguration(stages);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Configurazione non valida: " + string.Join(" | ", problems));
        }

        var ctx = BuildContext(config, userId);
        var run = new PipelineRun
        {
            Id = ctx.RunId,
            ConfigurationId = config.Id,
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            Trigger = trigger,
            ContextSnapshotJson = JsonSerializer.Serialize(ctx, Json),
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        LaunchBackground(config, stages, ctx, completedStages: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return ctx.RunId;
    }

    public async Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default)
    {
        // Stessa guardia anticipata di StartRunAsync (vedi commento lì) — senza, un tentativo di
        // ripresa mentre un altro run è in corso marcherebbe la riga come "Running" e poi
        // fallirebbe nel lancio, lasciandola orfana.
        lock (_gate)
        {
            if (_live is not null) throw new InvalidOperationException("Un run del pipeline è già in corso: attendere o annullarlo.");
        }

        PipelineRun? run;
        PipelineConfiguration? config;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            config = run is null ? null : await db.PipelineConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == run.ConfigurationId, ct);
        }
        if (run is null || config is null) throw new InvalidOperationException($"Run {runId} (o la sua configurazione) non trovato.");
        if (run.Status == "Running") throw new InvalidOperationException("Il run è già in esecuzione.");
        if (run.Status == "Completed") throw new InvalidOperationException("Il run è già completato.");

        var ctx = JsonSerializer.Deserialize<PipelineContext>(run.ContextSnapshotJson) ?? BuildContext(config, userId);
        ctx.RunId = runId;
        ctx.UserId ??= userId;

        var stages = JsonSerializer.Deserialize<List<StageConfig>>(config.StagesJson) ?? [];
        var completed = ctx.StageSummaries
            .Where(s => s.Status is StageStatus.Completed or StageStatus.Skipped)
            .Select(s => s.StageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.PipelineRuns.FirstAsync(r => r.Id == runId, ct);
            row.Status = "Running";
            row.CompletedAt = null;
            row.ErrorLog = null;
            await db.SaveChangesAsync(ct);
        }

        LaunchBackground(config, stages, ctx, completed);
        return runId;
    }

    public void RequestPause(Guid runId)
    {
        lock (_gate)
        {
            if (_live?.RunId == runId) _pauseRequested = true;
        }
    }

    public void Cancel(Guid runId)
    {
        lock (_gate)
        {
            if (_live?.RunId == runId) _cts?.Cancel();
        }
    }

    // ------------------------------------------------------------------ internals

    private PipelineContext BuildContext(PipelineConfiguration config, string? userId)
        => new()
        {
            RunId = Guid.NewGuid(),
            ExchangeName = config.ExchangeName,
            Universe = JsonSerializer.Deserialize<List<SeriesSpec>>(config.UniverseJson) ?? [],
            Ranges = JsonSerializer.Deserialize<PipelineDateRanges>(config.DateRangesJson) ?? new PipelineDateRanges(),
            InitialCapital = config.InitialCapital,
            Seed = config.Seed,
            UserId = userId ?? config.CreatedBy,
            ExecutionMode = config.ExecutionMode,
        };

    private void LaunchBackground(PipelineConfiguration config, List<StageConfig> stages, PipelineContext ctx, HashSet<string> completedStages)
    {
        lock (_gate)
        {
            if (_live is not null) throw new InvalidOperationException("Un run del pipeline è già in corso: attendere o annullarlo.");
            _pauseRequested = false;
            _cts = new CancellationTokenSource();
            _live = new PipelineLiveStatus
            {
                RunId = ctx.RunId,
                ConfigurationId = config.Id,
                ConfigurationName = config.Name,
                StartedUtc = DateTime.UtcNow,
                Stages = stages.Where(s => s.Enabled).OrderBy(s => s.Order).Select(s => new StageSummary
                {
                    StageName = s.Type,
                    DisplayName = catalog.Prototypes.FirstOrDefault(p => p.Name.Equals(s.Type, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? s.Type,
                    Order = s.Order,
                    Status = completedStages.Contains(s.Type) ? StageStatus.Completed : StageStatus.Pending,
                }).ToList(),
            };
        }

        var token = _cts!.Token;
        _ = Task.Run(() => RunPipelineAsync(config, stages, ctx, completedStages, token), CancellationToken.None);
    }

    private async Task RunPipelineAsync(PipelineConfiguration config, List<StageConfig> stages, PipelineContext ctx, HashSet<string> completedStages, CancellationToken ct)
    {
        var finalStatus = "Completed";
        string? errorLog = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            ctx.Candles = new PipelineCandleCache(scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
            ctx.Log = line =>
            {
                logger.LogInformation("Pipeline {RunId}: {Line}", ctx.RunId, line);
                lock (_gate)
                {
                    if (_live is null) return;
                    _live.RecentLog.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
                    if (_live.RecentLog.Count > 60) _live.RecentLog.RemoveAt(0);
                }
            };

            var ordered = stages.Where(s => s.Enabled).OrderBy(s => s.Order).ToList();
            foreach (var stageConfig in ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (completedStages.Contains(stageConfig.Type))
                {
                    ctx.LogLine($"[{stageConfig.Type}] già completato nel checkpoint: salto.");
                    continue;
                }
                if (_pauseRequested)
                {
                    finalStatus = "Paused";
                    ctx.LogLine("Pausa richiesta: mi fermo al checkpoint corrente.");
                    break;
                }

                var stage = catalog.Create(scope.ServiceProvider, stageConfig.Type);
                UpdateLive(stageConfig.Type, s => { s.Status = StageStatus.Running; s.StartedUtc = DateTime.UtcNow; }, current: stageConfig.Type);

                var stopwatch = Stopwatch.StartNew();
                var validationError = stage.ValidateInput(ctx);
                StageSummary summary;
                if (validationError is not null)
                {
                    // Missing prerequisites = the stage is SKIPPED, not fatal: downstream
                    // stages see the gap and the recommendation stays honest ("0 survivors").
                    summary = new StageSummary
                    {
                        StageName = stage.Name,
                        DisplayName = stage.DisplayName,
                        Order = stageConfig.Order,
                        Status = StageStatus.Skipped,
                        StartedUtc = DateTime.UtcNow,
                        Duration = stopwatch.Elapsed,
                        Text = $"Saltato: {validationError}",
                    };
                    ctx.LogLine($"[{stage.Name}] SALTATO: {validationError}");
                }
                else
                {
                    try
                    {
                        await stage.ExecuteAsync(ctx, stageConfig, ct);
                        summary = stage.Summarize(ctx);
                        summary.Order = stageConfig.Order;
                        summary.Status = StageStatus.Completed;
                        summary.StartedUtc = DateTime.UtcNow - stopwatch.Elapsed;
                        summary.Duration = stopwatch.Elapsed;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        summary = new StageSummary
                        {
                            StageName = stage.Name,
                            DisplayName = stage.DisplayName,
                            Order = stageConfig.Order,
                            Status = StageStatus.Failed,
                            StartedUtc = DateTime.UtcNow - stopwatch.Elapsed,
                            Duration = stopwatch.Elapsed,
                            Text = $"Errore: {ex.Message}",
                            Error = ex.ToString(),
                        };
                        ctx.StageSummaries.Add(summary);
                        UpdateLive(stage.Name, s => Copy(summary, s), current: null);
                        await CheckpointAsync(ctx, CancellationToken.None);
                        throw new InvalidOperationException($"Stage '{stage.DisplayName}' fallito: {ex.Message}", ex);
                    }
                }

                ctx.StageSummaries.RemoveAll(s => s.StageName == summary.StageName);
                ctx.StageSummaries.Add(summary);
                completedStages.Add(stageConfig.Type);
                UpdateLive(stage.Name, s => Copy(summary, s), current: null);
                await CheckpointAsync(ctx, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            finalStatus = "Failed";
            errorLog = ex.ToString();
            logger.LogError(ex, "Pipeline run {RunId} fallito.", ctx.RunId);
        }

        await FinalizeRunAsync(ctx, finalStatus, errorLog);
    }

    private static void Copy(StageSummary from, StageSummary to)
    {
        to.Status = from.Status;
        to.StartedUtc = from.StartedUtc;
        to.Duration = from.Duration;
        to.Text = from.Text;
        to.Metrics = new(from.Metrics);
        to.Error = from.Error;
    }

    private void UpdateLive(string stageName, Action<StageSummary> update, string? current)
    {
        lock (_gate)
        {
            if (_live is null) return;
            var entry = _live.Stages.FirstOrDefault(s => s.StageName.Equals(stageName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) update(entry);
            _live.CurrentStage = current;
        }
    }

    private async Task CheckpointAsync(PipelineContext ctx, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == ctx.RunId, ct);
        if (run is null) return;
        run.ContextSnapshotJson = JsonSerializer.Serialize(ctx, Json);
        run.StageSummariesJson = JsonSerializer.Serialize(ctx.StageSummaries, Json);
        await db.SaveChangesAsync(ct);
    }

    private async Task FinalizeRunAsync(PipelineContext ctx, string status, string? errorLog)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == ctx.RunId, CancellationToken.None);
            if (run is not null)
            {
                run.Status = status;
                run.CompletedAt = DateTime.UtcNow;
                run.ErrorLog = errorLog;
                run.ContextSnapshotJson = JsonSerializer.Serialize(ctx, Json);
                run.StageSummariesJson = JsonSerializer.Serialize(ctx.StageSummaries, Json);
                if (ctx.Recommendation is not null)
                {
                    run.Conclusion = ctx.Recommendation.FullText;
                    run.RecommendationJson = JsonSerializer.Serialize(ctx.Recommendation, Json);
                }

                if (status == "Completed")
                {
                    SaveArtifacts(db, ctx);
                }

                db.TradingAuditLogs.Add(new TradingAuditLog
                {
                    TimestampUtc = DateTime.UtcNow,
                    Action = "PipelineRun",
                    Details = JsonSerializer.Serialize(new
                    {
                        runId = ctx.RunId,
                        status,
                        stages = ctx.StageSummaries.Count,
                        survivors = ctx.Validated.Count(v => v.Survived),
                    }),
                    UserId = ctx.UserId,
                    Mode = TradingMode.Paper,
                });
                await db.SaveChangesAsync(CancellationToken.None);

                // Compone (non sostituisce) un ExperimentRun accanto al PipelineRun, così un run di
                // pipeline compare nella stessa tabella comparativa di backtest/sweep/training.
                // Best-effort: non compromette mai la finalizzazione del run.
                var expRunId = await experimentTracker.SafeStartRunAsync(
                    "Pipeline",
                    $"Pipeline · {ctx.Universe.Count} serie",
                    new
                    {
                        Universe = ctx.Universe,
                        ctx.ExecutionMode,
                        ctx.InitialCapital,
                        ctx.Seed,
                    },
                    createdBy: ctx.UserId);
                await experimentTracker.SafeLogMetricsAsync(expRunId, new Dictionary<string, decimal>
                {
                    ["Stages"] = ctx.StageSummaries.Count,
                    ["Survivors"] = ctx.Validated.Count(v => v.Survived),
                });
                await experimentTracker.SafeCompleteAsync(expRunId, status, errorLog);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finalizzazione del run {RunId} fallita.", ctx.RunId);
        }
        finally
        {
            lock (_gate)
            {
                _live = null;
                _cts?.Dispose();
                _cts = null;
                _pauseRequested = false;
            }
        }
    }

    private static void SaveArtifacts(ApplicationDbContext db, PipelineContext ctx)
    {
        void Add(string stage, string kind, object payload)
            => db.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = ctx.RunId,
                StageName = stage,
                Kind = kind,
                PayloadJson = JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow,
            });

        if (ctx.Features is not null) Add("FeatureEngineering", "FactorIc", ctx.Features);
        if (ctx.Regimes is not null) Add("RegimeAnalysis", "RegimeProfile", ctx.Regimes);
        if (ctx.Validated.Count > 0) Add("HoldoutValidation", "ValidatedCandidates", ctx.Validated);
        if (ctx.MlTraining is not null) Add("MlModelTraining", "FeatureImportance", ctx.MlTraining);
        if (ctx.Ensemble is not null) Add("EnsembleAssembly", "EnsembleProposal", ctx.Ensemble);
        if (ctx.Pairs is not null) Add("PairsScreening", "PairScreen", ctx.Pairs);
    }
}
