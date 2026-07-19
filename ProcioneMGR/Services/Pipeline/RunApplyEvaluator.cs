using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Valutazione "vale la pena schierare l'ensemble di questo run?" + eventuale applica, estratta
/// VERBATIM da <see cref="PipelineSchedulerWorker"/> (Fase 1 del PRD Autonomia): la stessa
/// identica catena — supervisore AI (solo veto) → confronto oggettivo con isteresi
/// (<see cref="IEnsembleComparator"/>) → <see cref="IPipelineApplier"/> — è ora usata sia dalla
/// ri-applica automatica dello scheduler sia dal <see cref="CampaignPlanner"/>: una sola
/// implementazione, nessuna deriva tra i due percorsi automatici.
///
/// La decisione resta registrata come <see cref="PipelineArtifact"/> idempotente
/// (<see cref="AutoReapplyArtifactKinds.Decision"/>), qualunque sia il chiamante.
/// </summary>
public interface IRunApplyEvaluator
{
    /// <summary>Valuta un run completato e, se giustificato, ne schiera l'ensemble. Idempotente per run.</summary>
    Task<RunApplyOutcome> EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>Esito della valutazione di un run (per lo scheduler, il planner e i loro log/test).</summary>
public sealed class RunApplyOutcome
{
    /// <summary>false quando il run non esiste o non ha un ensemble applicabile (0 sopravvissuti).</summary>
    public bool HadCandidate { get; init; }

    public bool Applied { get; init; }

    /// <summary>true quando il MOTIVO della mancata applica è il veto del supervisore AI.</summary>
    public bool Vetoed { get; init; }

    /// <summary>Corsie configurate dall'applica (0 se non applicato) — al planner serve per l'avvio Paper.</summary>
    public int LanesUsed { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <inheritdoc cref="IRunApplyEvaluator"/>
public sealed class RunApplyEvaluator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineApplier applier,
    IEnsembleComparator comparator,
    IPipelineSupervisorAgent supervisor,
    ILogger<RunApplyEvaluator> logger) : IRunApplyEvaluator
{
    /// <summary>
    /// Serializza l'applicazione dell'ensemble sulle corsie: atomicità globale tra chiamanti
    /// (scheduler e planner condividono la STESSA istanza singleton, quindi lo stesso gate).
    /// </summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public async Task<RunApplyOutcome> EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default)
    {
        PipelineRun? run;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        }
        if (run is null) return new RunApplyOutcome { Message = $"Run {runId} non trovato." };

        var candidate = DeserializeRecommendation(run.RecommendationJson);
        if (candidate is null || candidate.EnsembleLegs.Count == 0)
        {
            const string noCandidate = "Run senza ensemble applicabile: nessuna azione.";
            await RecordDecisionAsync(runId, applied: false, noCandidate, null, null, ct);
            return new RunApplyOutcome { Message = noCandidate };
        }

        var candidateSummary = applier.SummarizeRecommendation(candidate);
        var currentSummary = await applier.GetCurrentEnsembleSummaryAsync(ct);

        // 1. Supervisore AI (può solo porre un veto; su errore/assenza approva → decidono le metriche).
        var judgment = await supervisor.AnalyzeRunAsync(run, currentSummary, candidateSummary, ct);

        // 2. Confronto oggettivo con hysteresis.
        var comparison = comparator.Compare(currentSummary.IsEmpty ? null : currentSummary, candidateSummary);

        var applied = false;
        var vetoed = false;
        var lanesUsed = 0;
        string message;
        if (comparison.ShouldReplace && judgment.ApproveReplacement)
        {
            await _applyGate.WaitAsync(ct);
            try
            {
                var result = await applier.ApplyRecommendationAsync(candidate, ct);
                applied = true;
                lanesUsed = result.LanesUsed;
                message = $"Ensemble sostituito automaticamente. {comparison.Reason} {result.Message}";
                logger.LogInformation("Applica automatica ESEGUITA per il run {RunId}: {Reason}", runId, comparison.Reason);
            }
            finally { _applyGate.Release(); }
        }
        else if (comparison.ShouldReplace && !judgment.ApproveReplacement)
        {
            vetoed = true;
            message = $"Ensemble corrente mantenuto: VETO del supervisore AI. {judgment.Summary}";
            logger.LogInformation("Applica automatica VETATA dal supervisore per il run {RunId}.", runId);
        }
        else
        {
            message = comparison.Reason;
            logger.LogInformation("Applica automatica NON eseguita per il run {RunId}: {Reason}", runId, comparison.Reason);
        }

        await RecordDecisionAsync(runId, applied, message, comparison, judgment, ct);
        return new RunApplyOutcome { HadCandidate = true, Applied = applied, Vetoed = vetoed, LanesUsed = lanesUsed, Message = message };
    }

    /// <summary>Persiste la decisione come <see cref="PipelineArtifact"/> (marker idempotente + fonte per la UI). Nessuna nuova tabella.</summary>
    private async Task RecordDecisionAsync(Guid runId, bool applied, string message, EnsembleComparison? comparison, SupervisorJudgment? judgment, CancellationToken ct)
    {
        var payload = new AutoReapplyDecisionArtifact
        {
            Applied = applied,
            Message = message,
            Comparison = comparison,
            Judgment = judgment,
            DecidedAtUtc = DateTime.UtcNow,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await db.PipelineArtifacts.AnyAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision, ct);
        if (exists) return; // idempotente
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "AutoReapply",
            Kind = AutoReapplyArtifactKinds.Decision,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    internal static PipelineRecommendation? DeserializeRecommendation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<PipelineRecommendation>(json); }
        catch { return null; }
    }
}
