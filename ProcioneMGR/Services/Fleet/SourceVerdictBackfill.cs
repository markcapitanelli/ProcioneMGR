using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>L'esito per gamba: cosa è stato etichettato, o perché no. Si mostra, non si inghiotte.</summary>
public sealed record ProvenanceLegOutcome(int LaneId, string Leg, bool Updated, string Detail);

public sealed record ProvenanceReport(bool DryRun, IReadOnlyList<ProvenanceLegOutcome> Legs)
{
    public int Updated => Legs.Count(l => l.Updated);
}

/// <summary>
/// [K13, PRD autonomia-piena 2026-08-31] <b>Le gambe schierate prima dell'etichetta dicono da dove
/// vengono.</b>
///
/// <para>Il campo <c>SourceVerdict</c> lo scrive <see cref="GreyDeployer"/> da [T1] in poi. Le
/// gambe schierate PRIMA — le cinque corsie di flotta, tutte da click F5 documentati a journal fra
/// il 3 e il 13 agosto — non ce l'hanno, e <c>LaneDirectory.HasGreyLegs</c> restituisce
/// <c>null</c>: ignoto. Il consumatore del tetto tratta l'ignoto come grigio, ed è la scelta
/// giusta — non sapere non allarga il permesso — ma il risultato è che
/// <c>greyRunning</c> contava cinque corsie «grigie» senza che nessuna superficie potesse spiegare
/// perché. Il tetto funzionava per la ragione giusta e lo diceva nel modo sbagliato.</para>
///
/// <para><b>La provenienza si DERIVA, non si assume.</b> Stessa forma di [J9]: il candidato di
/// origine si ritrova per <see cref="PipelineCandidateKey"/> — l'identità canonica, non il
/// DisplayName — nell'archivio <c>ResearchCandidates</c>, e l'etichetta viene dal campo
/// <c>Survived</c> di quella riga. Dove il candidato non si trova, la gamba <b>resta senza
/// etichetta</b> e il perché va nel report: scrivere «Grey» per default sarebbe comodo e sarebbe
/// un'invenzione, e su un campo che governa un tetto un'invenzione è peggio dell'ignoranza — che
/// almeno è fail-closed.</para>
///
/// <para>La scrittura passa da <see cref="IEnsembleManager.UpdateConfigurationAsync"/>, l'unico
/// scrittore della configurazione: una seconda serializzazione sarebbe una seconda verità.</para>
/// </summary>
public sealed class SourceVerdictBackfill(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    ILogger<SourceVerdictBackfill> logger)
{
    public async Task<ProvenanceReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var outcomes = new List<ProvenanceLegOutcome>();

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
            foreach (var leg in cfg.Strategies.Where(s => s.IsActive && string.IsNullOrEmpty(s.SourceVerdict)))
            {
                var (etichetta, dettaglio) = await DeriveAsync(cfg, leg, ct);
                outcomes.Add(new(laneId, $"{leg.StrategyName} {cfg.Symbol} {cfg.Timeframe}", etichetta is not null, dettaglio));
                if (etichetta is not null && !dryRun)
                {
                    leg.SourceVerdict = etichetta;
                    changed = true;
                }
            }

            if (changed)
            {
                await manager.UpdateConfigurationAsync(cfg, ct);
                logger.LogInformation("Corsia {Lane}: provenienza delle gambe ricostruita e salvata (K13).", laneId);
            }
        }

        return new ProvenanceReport(dryRun, outcomes);
    }

    private async Task<(string? Etichetta, string Dettaglio)> DeriveAsync(
        EnsembleConfiguration cfg, EnsembleStrategy leg, CancellationToken ct)
    {
        var key = PipelineCandidateKey.Build(leg.StrategyName, cfg.Symbol, cfg.Timeframe, leg.Parameters);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.ResearchCandidates.AsNoTracking()
            .Where(c => c.CandidateKey == key)
            .OrderByDescending(c => c.RunCompletedUtc)
            .Select(c => new { c.RunId, c.Survived })
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
        {
            return (null, $"candidato «{key}» non trovato nell'archivio: l'etichetta resta ignota "
                        + "(e l'ignoto conta come grigio, che è il verso prudente)");
        }

        var etichetta = candidate.Survived ? "Survived" : "Grey";
        return (etichetta, $"{etichetta} — dal candidato del run {candidate.RunId.ToString()[..8]}");
    }
}
