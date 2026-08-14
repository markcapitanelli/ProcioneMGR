using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Research;

/// <summary>
/// [R2, PRD memoria-caccia 2026-08-14] UNA riga per candidato della caccia — l'indice a righe dei
/// blob <c>PipelineArtifacts</c> Kind="ValidatedCandidates". I dati esistono da luglio (6.554
/// candidati misurati il 2026-08-06) ma ogni domanda trasversale costava una scansione
/// <c>jsonb_array_elements</c> sull'intero archivio: questa tabella è DERIVATA e ricostruibile
/// dagli artifact in ogni momento (<see cref="IResearchCandidateIndexer.RebuildAsync"/>) — non è
/// una seconda verità, è una vista materializzata a mano. Nessun dato nuovo viene raccolto.
/// </summary>
public class ResearchCandidate
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>Denormalizzato da PipelineRuns.CompletedAt: il filtro temporale della pagina non deve fare join.</summary>
    public DateTime RunCompletedUtc { get; set; }

    public string StrategyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>La chiave identità di <see cref="PipelineCandidateKey"/> — mai ricostruirla inline.</summary>
    public string CandidateKey { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "{}";

    // Metriche di selezione (walk-forward)
    public decimal WalkForwardOosSharpe { get; set; }
    public decimal SelectionSharpe { get; set; }
    public decimal SelectionReturn { get; set; }
    public decimal SelectionMaxDrawdown { get; set; }
    public int SelectionTrades { get; set; }

    // Verdetto holdout (mai usato per la selezione)
    public decimal HoldoutSharpe { get; set; }
    public decimal HoldoutReturn { get; set; }
    public decimal HoldoutMaxDrawdown { get; set; }
    public decimal HoldoutProfitFactor { get; set; }
    public int HoldoutTrades { get; set; }

    public bool Survived { get; set; }
    public string? RejectReason { get; set; }

    public double? DeflatedSharpe { get; set; }
    public double? PanelPbo { get; set; }
    public double? PermutationPValue { get; set; }
    public double? NullTwinPercentile { get; set; }

    public string BestStopVariant { get; set; } = "base";

    /// <summary>
    /// Cache del giudice unico <see cref="GreyZone.IsGrey"/>, calcolata all'indicizzazione:
    /// serve al filtro SQL della pagina. La tabella è derivata, quindi il valore si riallinea a
    /// ogni rebuild — mai una seconda definizione di "grigio".
    /// </summary>
    public bool IsGrey { get; set; }
}

public sealed record ResearchIndexResult(int RunsIndexed, int CandidatesIndexed, int RunsSkipped);

/// <summary>
/// Costruisce e mantiene l'indice: incrementale (solo i run non ancora indicizzati) su richiesta
/// della pagina, ricostruzione totale col bottone dedicato. Difensivo per run, come il lettore di
/// flotta: un payload illeggibile esclude QUEL run con un log, mai l'intera indicizzazione.
/// </summary>
public interface IResearchCandidateIndexer
{
    Task<ResearchIndexResult> IndexNewRunsAsync(CancellationToken ct = default);
    Task<ResearchIndexResult> RebuildAsync(CancellationToken ct = default);
}

public sealed class ResearchCandidateIndexer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ResearchCandidateIndexer> logger) : IResearchCandidateIndexer
{
    /// <summary>Stesso KindTag scritto da PipelineEngine.SaveArtifacts e letto da FleetStateReader.</summary>
    internal const string ArtifactKind = "ValidatedCandidates";

    public async Task<ResearchIndexResult> IndexNewRunsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var alreadyIndexed = await db.ResearchCandidates.AsNoTracking()
            .Select(r => r.RunId).Distinct().ToListAsync(ct);
        return await IndexAsync(db, alreadyIndexed, ct);
    }

    public async Task<ResearchIndexResult> RebuildAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Tabella derivata: cancellare e rifare È il contratto di coerenza col giudice corrente
        // (se la definizione di grigio cambia, il rebuild riallinea il flag su tutto lo storico).
        await db.ResearchCandidates.ExecuteDeleteAsync(ct);
        return await IndexAsync(db, [], ct);
    }

    private async Task<ResearchIndexResult> IndexAsync(ApplicationDbContext db, List<Guid> alreadyIndexed, CancellationToken ct)
    {
        // Gli artifact esistono solo per run "Completed" (PipelineEngine.SaveArtifacts), quindi il
        // join filtra da solo; CompletedAt si denormalizza qui, una volta.
        var sources = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.Kind == ArtifactKind && !alreadyIndexed.Contains(a.RunId))
            .Join(db.PipelineRuns.AsNoTracking(),
                a => a.RunId, r => r.Id,
                (a, r) => new { a.RunId, a.PayloadJson, r.CompletedAt })
            .ToListAsync(ct);

        var runsIndexed = 0;
        var candidatesIndexed = 0;
        var runsSkipped = 0;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            List<ValidatedCandidate> validated;
            try
            {
                validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(source.PayloadJson) ?? [];
            }
            catch (JsonException ex)
            {
                runsSkipped++;
                logger.LogWarning(ex, "Run {Run} escluso dall'indice candidati (payload illeggibile).", source.RunId);
                continue;
            }

            // Dentro un run le chiavi sono uniche per costruzione (fingerprint dei parametri);
            // il GroupBy è la cintura per i payload storici precedenti a quel fix — un duplicato
            // violerebbe l'indice unico e farebbe fallire l'intero giro.
            var rows = validated
                .GroupBy(v => v.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(v => new ResearchCandidate
                {
                    RunId = source.RunId,
                    RunCompletedUtc = source.CompletedAt ?? DateTime.UtcNow,
                    StrategyName = v.StrategyName,
                    Symbol = v.Symbol,
                    Timeframe = v.Timeframe,
                    CandidateKey = v.Key,
                    ParametersJson = JsonSerializer.Serialize(v.Parameters),
                    WalkForwardOosSharpe = v.WalkForwardOosSharpe,
                    SelectionSharpe = v.SelectionSharpe,
                    SelectionReturn = v.SelectionReturn,
                    SelectionMaxDrawdown = v.SelectionMaxDrawdown,
                    SelectionTrades = v.SelectionTrades,
                    HoldoutSharpe = v.HoldoutSharpe,
                    HoldoutReturn = v.HoldoutReturn,
                    HoldoutMaxDrawdown = v.HoldoutMaxDrawdown,
                    HoldoutProfitFactor = v.HoldoutProfitFactor,
                    HoldoutTrades = v.HoldoutTrades,
                    Survived = v.Survived,
                    RejectReason = v.RejectReason,
                    DeflatedSharpe = v.DeflatedSharpe,
                    PanelPbo = v.PanelPbo,
                    PermutationPValue = v.PermutationPValue,
                    NullTwinPercentile = v.NullTwinPercentile,
                    BestStopVariant = v.BestStopVariant,
                    IsGrey = GreyZone.IsGrey(v),
                })
                .ToList();
            if (rows.Count < validated.Count)
            {
                logger.LogWarning("Run {Run}: {Dupes} candidati con chiave duplicata nel payload storico — tenuto il primo di ciascuna.",
                    source.RunId, validated.Count - rows.Count);
            }

            db.ResearchCandidates.AddRange(rows);
            await db.SaveChangesAsync(ct);
            runsIndexed++;
            candidatesIndexed += rows.Count;
        }

        if (runsIndexed > 0)
        {
            logger.LogInformation("Indice candidati: {Runs} run, {Candidates} candidati aggiunti ({Skipped} run illeggibili).",
                runsIndexed, candidatesIndexed, runsSkipped);
        }
        return new ResearchIndexResult(runsIndexed, candidatesIndexed, runsSkipped);
    }
}
