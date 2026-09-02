using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>L'esito per gamba: cosa è stato ricostruito, o perché no. Si mostra, non si inghiotte.</summary>
public sealed record BackfillLegOutcome(int LaneId, string Leg, bool Updated, string Detail);

public sealed record BackfillReport(bool DryRun, IReadOnlyList<BackfillLegOutcome> Legs)
{
    public int Updated => Legs.Count(l => l.Updated);
}

/// <summary>
/// [J9, PRD autonomia-operativa 2026-08-25] <b>Rompe la circolarità dell'inedia.</b>
///
/// Il ritiro per inedia (I12) pretende <c>ExpectedTradesPerMonth</c>, che è scritto SOLO da un
/// nuovo schieramento (PipelineApplier e GreyDeployer, da I11 in poi) — e le corsie schierate
/// PRIMA di I11 hanno il campo null su ogni gamba. Il cerchio: per liberare una corsia serve un
/// ritiro → per il ritiro per inedia serve l'atteso → per l'atteso serve uno schieramento → per lo
/// schieramento serve una corsia libera. Al 2026-08-25, null su OGNI gamba di OGNI corsia:
/// «l'ignoranza non condanna» era diventato «nessuno viene mai ritirato».
///
/// <para>La ricostruzione usa la STESSA aritmetica dello schieramento nuovo
/// (<see cref="HoldoutWindow.ForCandidateAsync"/>, quindi <c>PipelineDateRanges.HoldoutMonths</c> e
/// <c>TradeFrequency.PerMonth</c>): il candidato di provenienza si ritrova per
/// <see cref="PipelineCandidateKey"/> — l'identità canonica, non il DisplayName — nell'archivio
/// <c>ResearchCandidates</c>, prendendo la registrazione più recente. Dove il candidato non si
/// trova, la gamba resta com'è e il PERCHÉ va nel report: ricostruire per forza inventerebbe un
/// denominatore, e un verdetto di inedia su un numero inventato è peggio di nessun verdetto.</para>
///
/// <para>La scrittura passa da <see cref="IEnsembleManager.UpdateConfigurationAsync"/> — lo stesso
/// unico scrittore delle pagine e dei deployer: una seconda serializzazione della configurazione
/// sarebbe una seconda verità.</para>
/// </summary>
public sealed class ExpectedFrequencyBackfill(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    ILogger<ExpectedFrequencyBackfill> logger)
{
    public async Task<BackfillReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var outcomes = new List<BackfillLegOutcome>();

        for (var laneId = 0; laneId < TradingLanes.Count; laneId++)
        {
            ct.ThrowIfCancellationRequested();
            EnsembleConfiguration cfg;
            IEnsembleManager manager;
            try
            {
                manager = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(laneId);
                cfg = await manager.GetConfigurationAsync(ct);
            }
            catch (Exception ex)
            {
                outcomes.Add(new(laneId, "—", false, $"configurazione non leggibile: {ex.Message}"));
                continue;
            }

            var changed = false;
            foreach (var leg in cfg.Strategies.Where(s => s.IsActive && s.ExpectedTradesPerMonth is null))
            {
                var (updated, detail, perMonth, source) = await ReconstructAsync(cfg, leg, ct);
                outcomes.Add(new(laneId, $"{leg.StrategyName} {cfg.Symbol} {cfg.Timeframe}", updated, detail));
                if (updated && !dryRun)
                {
                    leg.ExpectedTradesPerMonth = perMonth;
                    leg.ExpectedTradesSource = source;
                    changed = true;
                }
            }

            if (changed)
            {
                await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create(ConfigWriteSources.Backfill,
                    "J9: ricostruzione della frequenza attesa (ExpectedTradesPerMonth)"), ct);
                logger.LogInformation("Corsia {Lane}: frequenze attese ricostruite e salvate (J9).", laneId);
            }
        }

        return new BackfillReport(dryRun, outcomes);
    }

    private async Task<(bool Updated, string Detail, decimal? PerMonth, string? Source)> ReconstructAsync(
        EnsembleConfiguration cfg, EnsembleStrategy leg, CancellationToken ct)
    {
        var key = PipelineCandidateKey.Build(leg.StrategyName, cfg.Symbol, cfg.Timeframe, leg.Parameters);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ResearchCandidates.AsNoTracking()
            .Where(c => c.CandidateKey == key)
            .OrderByDescending(c => c.RunCompletedUtc)
            .Select(c => new { c.RunId, c.HoldoutTrades })
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
        {
            return (false, $"candidato «{key}» non trovato nell'archivio: l'atteso resta ignoto (l'ignoranza non condanna, ma va detta)", null, null);
        }

        var (perMonth, source) = await HoldoutWindow.ForCandidateAsync(dbFactory, candidate.RunId, candidate.HoldoutTrades, ct);
        if (perMonth is null)
        {
            return (false, $"candidato trovato (run {candidate.RunId.ToString()[..8]}) ma finestra di holdout non derivabile: l'atteso resta ignoto", null, null);
        }

        // La provenienza dichiara che è una RICOSTRUZIONE a posteriori, non il numero scritto allo
        // schieramento: chi legge deve poter distinguere le due origini.
        return (true, $"ricostruito: ~{perMonth:0.#} trade/mese ({source})", perMonth, $"[J9, ricostruito {DateTime.UtcNow:yyyy-MM-dd}] {source}");
    }
}
