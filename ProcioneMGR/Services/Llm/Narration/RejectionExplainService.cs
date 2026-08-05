using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Llm.Narration;

public interface IRejectionExplainService
{
    /// <summary>
    /// Il ritratto DETERMINISTICO delle bocciature di un run, calcolato dall'artifact
    /// <c>ValidatedCandidates</c>. Non chiama l'AI, non costa nulla, funziona sempre —
    /// <see cref="RunRejectionDigest.Empty"/> se il run non ha verdetti leggibili.
    /// </summary>
    Task<RunRejectionDigest> GetDigestAsync(Guid runId, CancellationToken ct = default);

    /// <summary>La narrazione già prodotta per il run, se c'è. Nessuna chiamata all'AI.</summary>
    Task<RejectionNarration?> GetNarrationAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Produce (e persiste) la narrazione del run. Idempotente: se esiste già non richiama l'AI,
    /// a meno di <paramref name="force"/>. Restituisce la narrazione, oppure <c>null</c> se non è
    /// stato possibile produrla — nel qual caso il digest deterministico resta comunque leggibile.
    /// </summary>
    Task<RejectionNarration?> ExplainRunAsync(Guid runId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// I run completati più recenti che hanno verdetti leggibili, col loro digest deterministico e
    /// la narrazione se già prodotta. Serve alla pagina: il digest si vede ANCHE col layer AI
    /// spento, ed è il motivo per cui questa lista non passa dagli advisory.
    /// </summary>
    Task<IReadOnlyList<RunRejectionSummary>> GetRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>Un run con le sue bocciature: numeri sempre, prosa se c'è.</summary>
public sealed record RunRejectionSummary(
    Guid RunId,
    string ConfigurationName,
    DateTime CompletedAtUtc,
    RunRejectionDigest Digest,
    RejectionNarration? Narration);

/// <summary>
/// [G6] Collega il digest deterministico, il narratore AI e la persistenza.
///
/// <para>Confine: legge verdetti di candidati GIÀ respinti e scrive un artifact di testo. Non
/// tocca corsie, ordini, parametri o soglie — non ha nemmeno i servizi per farlo fra le
/// dipendenze.</para>
/// </summary>
public sealed class RejectionExplainService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IRejectionNarrator narrator,
    IOptionsMonitor<LlmOptions> options,
    ILogger<RejectionExplainService> logger) : IRejectionExplainService
{
    /// <summary>Il Kind dell'artifact che porta i verdetti dei candidati (scritto dal motore).</summary>
    private const string ValidatedCandidatesKind = "ValidatedCandidates";

    public async Task<RunRejectionDigest> GetDigestAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await BuildDigestAsync(db, runId, ct);
    }

    private async Task<RunRejectionDigest> BuildDigestAsync(ApplicationDbContext db, Guid runId, CancellationToken ct)
    {
        var payload = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.RunId == runId && a.Kind == ValidatedCandidatesKind)
            .Select(a => a.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return RunRejectionDigest.Empty;

        List<ValidatedCandidate> candidates;
        try
        {
            candidates = JsonSerializer.Deserialize<List<ValidatedCandidate>>(payload) ?? [];
        }
        catch (JsonException ex)
        {
            // Verdetti illeggibili: si dichiara il vuoto, non si inventa un ritratto.
            logger.LogWarning(ex, "Verdetti del run {Run} illeggibili: nessun riassunto delle bocciature.", runId);
            return RunRejectionDigest.Empty;
        }

        return RejectionDigestBuilder.Build(candidates, Math.Max(1, options.CurrentValue.ExplainRejectionsTopN));
    }

    public async Task<RejectionNarration?> GetNarrationAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ReadNarrationAsync(db, runId, ct);
    }

    private static async Task<RejectionNarration?> ReadNarrationAsync(ApplicationDbContext db, Guid runId, CancellationToken ct)
    {
        var payload = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.RunId == runId && a.Kind == LlmArtifactKinds.RejectionExplanation)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try { return JsonSerializer.Deserialize<RejectionNarration>(payload); }
        catch (JsonException) { return null; }
    }

    public async Task<RejectionNarration?> ExplainRunAsync(Guid runId, bool force = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!force)
        {
            var existing = await ReadNarrationAsync(db, runId, ct);
            if (existing is not null) return existing; // idempotente: non si ripaga la stessa prosa
        }

        var digest = await BuildDigestAsync(db, runId, ct);
        if (!digest.HasContent)
        {
            logger.LogDebug("Run {Run}: nessun candidato bocciato, niente da spiegare.", runId);
            return null;
        }

        var narration = await narrator.NarrateAsync(digest, ct);
        if (narration is null) return null;

        // Con force si riscrive: una narrazione vecchia accanto a una nuova non aiuta nessuno.
        if (force)
        {
            var old = await db.PipelineArtifacts
                .Where(a => a.RunId == runId && a.Kind == LlmArtifactKinds.RejectionExplanation)
                .ToListAsync(ct);
            db.PipelineArtifacts.RemoveRange(old);
        }

        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "LlmRejectionExplain",
            Kind = LlmArtifactKinds.RejectionExplanation,
            PayloadJson = JsonSerializer.Serialize(narration),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Spiegazione delle bocciature scritta per il run {Run} ({Notes} note, {Discarded} scartate).",
            runId, narration.Notes.Count, narration.DiscardedNotes);
        return narration;
    }

    public async Task<IReadOnlyList<RunRejectionSummary>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 25);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Solo i run che HANNO verdetti: senza l'artifact non c'è niente da riassumere, e una riga
        // vuota in pagina è peggio di una riga assente.
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt != null)
            .Where(r => db.PipelineArtifacts.Any(a => a.RunId == r.Id && a.Kind == ValidatedCandidatesKind))
            .OrderByDescending(r => r.CompletedAt)
            .Take(limit)
            .Select(r => new { r.Id, r.CompletedAt, r.ConfigurationId })
            .ToListAsync(ct);
        if (runs.Count == 0) return [];

        var configIds = runs.Select(r => r.ConfigurationId).Distinct().ToList();
        var names = await db.PipelineConfigurations.AsNoTracking()
            .Where(c => configIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var list = new List<RunRejectionSummary>(runs.Count);
        foreach (var run in runs)
        {
            var digest = await BuildDigestAsync(db, run.Id, ct);
            if (!digest.HasContent) continue; // run senza bocciati: nulla da spiegare

            var narration = await ReadNarrationAsync(db, run.Id, ct);
            list.Add(new RunRejectionSummary(
                run.Id,
                names.GetValueOrDefault(run.ConfigurationId, $"config {run.ConfigurationId}"),
                run.CompletedAt ?? DateTime.UtcNow,
                digest,
                narration));
        }
        return list;
    }
}
