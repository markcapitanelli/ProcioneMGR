namespace ProcioneMGR.Services.Experiments;

/// <summary>
/// Helper "best-effort" per il tracking: incapsulano le chiamate al <see cref="IExperimentTracker"/>
/// in un <c>try/catch</c> così un problema di logging (DB occupato, transitorio) NON fa mai cadere
/// il calcolo osservato. Gli engine restano la fonte di verità; il tracker è un osservatore
/// sacrificabile. Un run non aperto è rappresentato da <see cref="Guid.Empty"/>, che gli altri
/// helper trattano come no-op.
/// </summary>
public static class ExperimentTrackerExtensions
{
    /// <summary>Apre un run senza mai lanciare: ritorna <see cref="Guid.Empty"/> se il logging fallisce.</summary>
    public static async Task<Guid> SafeStartRunAsync(
        this IExperimentTracker tracker,
        string kind,
        string name,
        object? parameters,
        string? symbol = null,
        string? timeframe = null,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        try
        {
            return await tracker.StartRunAsync(kind, name, parameters, symbol, timeframe, createdBy, ct);
        }
        catch
        {
            return Guid.Empty;
        }
    }

    public static async Task SafeLogMetricsAsync(
        this IExperimentTracker tracker, Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) return;
        try { await tracker.LogMetricsAsync(runId, metrics, ct); } catch { /* observer sacrificabile */ }
    }

    public static async Task SafeLogArtifactAsync(
        this IExperimentTracker tracker, Guid runId, string kindTag, object payload, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) return;
        try { await tracker.LogArtifactAsync(runId, kindTag, payload, ct); } catch { /* observer sacrificabile */ }
    }

    public static async Task SafeCompleteAsync(
        this IExperimentTracker tracker, Guid runId, string status, string? errorLog = null, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) return;
        try { await tracker.CompleteAsync(runId, status, errorLog, ct); } catch { /* observer sacrificabile */ }
    }
}
