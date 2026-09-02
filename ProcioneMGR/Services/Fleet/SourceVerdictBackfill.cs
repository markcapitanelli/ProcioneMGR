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
/// <para><b>[Rettifica 2026-09-01]</b> La versione originale di questo commento diceva due cose
/// false, e vale la pena scrivere quali perché sono state usate per decidere. (1) «Il campo
/// <c>SourceVerdict</c> lo scrive <see cref="GreyDeployer"/>»: gli scrittori sono <b>tre</b> —
/// <c>GreyDeployer</c>, <c>EnsemblePageService.AddFromGreyAsync</c> (la terza porta, che non
/// registra nulla a journal) e <c>PipelineApplier</c> per le corsie d'impronta. (2) L'affermazione,
/// pubblicata in <c>docs/audit/36_…</c>, che questo backfill «non avesse trovato il candidato»
/// delle corsie 5 e 7 era una <b>deduzione dallo stato osservato</b>, non una misura, ed era falsa:
/// il backfill <b>non è mai stato eseguito</b> (l'assembly che lo contiene è del 2026-09-01 00:33,
/// posteriore alle etichette), le chiavi combaciano carattere per carattere, e le etichette
/// <c>Grey</c> delle corsie 3, 4 e 6 le ha scritte <c>GreyDeployer</c> all'atto dello schieramento.</para>
///
/// <para>Le gambe schierate prima di [T1] non hanno l'etichetta, e <c>LaneDirectory.HasGreyLegs</c>
/// restituisce <c>null</c>: ignoto. Il consumatore del tetto tratta l'ignoto come grigio, ed è la
/// scelta giusta — non sapere non allarga il permesso — ma <c>greyRunning</c> contava corsie
/// «grigie» senza che nessuna superficie potesse spiegare perché. Il tetto funzionava per la
/// ragione giusta e lo diceva nel modo sbagliato. <b>Etichettarle non libera uno slot</b> (misurato:
/// la provenienza vera della corsia 5 è <c>Grey</c>, 44 righe d'archivio su 44): serve a rendere
/// leggibile un tetto, non ad allargarlo.</para>
///
/// <para><b>La provenienza si DERIVA, non si assume.</b> Stessa forma di [J9]: il candidato di
/// origine si ritrova per <see cref="PipelineCandidateKey"/> — l'identità canonica, non il
/// DisplayName — nell'archivio <c>ResearchCandidates</c>. Dove il candidato non si trova, la gamba
/// <b>resta senza etichetta</b> e il perché va nel report: scrivere «Grey» per default sarebbe
/// comodo e sarebbe un'invenzione, e su un campo che governa un tetto un'invenzione è peggio
/// dell'ignoranza — che almeno è fail-closed.</para>
///
/// <para><b>[K37, 2026-09-01] Tre stati d'archivio, non due — e il run giusto.</b> Il verdetto era
/// <c>Survived ? "Survived" : "Grey"</c>, che schiaccia in due gli stati che l'archivio tiene in
/// tre: un candidato <b>bocciato in pieno</b> (né sopravvissuto né grigio) veniva etichettato
/// «Grey», cioè <i>meglio</i> di come l'archivio lo giudica. Non è ipotetico: è ciò che sarebbe
/// successo alla corsia 7 alla prima esecuzione, perché il suo candidato è stato retrocesso il
/// 2026-08-21 («Sharpe holdout 0,11 &lt; 0,5»).</para>
///
/// <para>Ed è per questo che serve anche il <b>run di schieramento</b>, non l'ultimo run che
/// ricapita sulla stessa chiave: misurato sull'archivio, <b>71 chiavi su 1.028 cambiano
/// <c>IsGrey</c> fra un run e l'altro</b>. «Quale run leggi» sposta l'etichetta, e l'etichetta
/// governa un tetto di rischio. Il run si prende dal journal (<c>OrchestratorDecisions.RunId</c>
/// dell'ultimo <c>Assign</c> applicato di quella corsia); dove il journal tace o è stantio — e
/// tace su 2 corsie di flotta su 5, misurato — la ricerca vincolata non trova nulla e la gamba resta
/// <b>senza etichetta</b>. È il verso voluto: la copertura scende, la fiducia sale.</para>
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
                var (etichetta, dettaglio) = await DeriveAsync(laneId, cfg, leg, ct);
                outcomes.Add(new(laneId, $"{leg.StrategyName} {cfg.Symbol} {cfg.Timeframe}", etichetta is not null, dettaglio));
                if (etichetta is not null && !dryRun)
                {
                    leg.SourceVerdict = etichetta;
                    changed = true;
                }
            }

            if (changed)
            {
                await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create(ConfigWriteSources.Backfill,
                    "K37: ricostruzione della provenienza (SourceVerdict) delle gambe schierate"), ct);
                logger.LogInformation("Corsia {Lane}: provenienza delle gambe ricostruita e salvata (K13).", laneId);
            }
        }

        return new ProvenanceReport(dryRun, outcomes);
    }

    private async Task<(string? Etichetta, string Dettaglio)> DeriveAsync(
        int laneId, EnsembleConfiguration cfg, EnsembleStrategy leg, CancellationToken ct)
    {
        var key = PipelineCandidateKey.Build(leg.StrategyName, cfg.Symbol, cfg.Timeframe, leg.Parameters);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // [K37] Il run DA CUI la gamba è stata schierata, se il journal lo sa. Vincolare la ricerca
        // a quel run è ciò che rende l'etichetta la provenienza e non «l'ultimo giudizio disponibile
        // su un'ipotesi simile»: 71 chiavi su 1.028 cambiano IsGrey fra un run e l'altro.
        var runSchieramento = await db.OrchestratorDecisions.AsNoTracking()
            .Where(d => d.Kind == "Assign" && d.LaneId == laneId && d.Applied && d.RunId != null)
            .OrderByDescending(d => d.AtUtc)
            .Select(d => d.RunId)
            .FirstOrDefaultAsync(ct);

        var q = db.ResearchCandidates.AsNoTracking().Where(c => c.CandidateKey == key);
        if (runSchieramento is not null) q = q.Where(c => c.RunId == runSchieramento);

        var candidate = await q
            .OrderByDescending(c => c.RunCompletedUtc)
            .Select(c => new { c.RunId, c.Survived, c.IsGrey, c.RejectReason })
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
        {
            // Due cause, e vanno distinte: la chiave non esiste affatto, oppure esiste ma non sotto
            // il run che il journal indica — che è il caso delle corsie riassegnate senza lasciare
            // riga (misurato: 2 corsie di flotta su 5). Il secondo NON è un guasto della ricerca:
            // è il journal che non è un registro completo, e l'esito prudente è lo stesso.
            var esisteAltrove = runSchieramento is not null
                && await db.ResearchCandidates.AsNoTracking().AnyAsync(c => c.CandidateKey == key, ct);
            return (null, esisteAltrove
                ? $"candidato «{key}» presente in archivio ma NON sotto il run di schieramento "
                  + $"{runSchieramento.ToString()![..8]} dichiarato a journal: la corsia è stata riassegnata senza "
                  + "lasciare riga, quindi la provenienza non è accertabile e l'etichetta resta ignota "
                  + "(e l'ignoto conta come grigio, che è il verso prudente)"
                : $"candidato «{key}» non trovato nell'archivio: l'etichetta resta ignota "
                  + "(e l'ignoto conta come grigio, che è il verso prudente)");
        }

        // [K37] TRE stati, non due. «Né sopravvissuto né grigio» = bocciato in pieno, e scrivergli
        // «Grey» sarebbe promuoverlo: l'archivio lo giudica peggio di così.
        var etichetta = candidate.Survived ? "Survived" : candidate.IsGrey ? "Grey" : null;
        var origine = runSchieramento is not null ? "run di schieramento" : "run più recente sulla chiave";
        if (etichetta is null)
        {
            return (null, $"il candidato del {origine} {candidate.RunId.ToString()[..8]} è BOCCIATO IN PIENO "
                        + $"(né sopravvissuto né grigio: «{candidate.RejectReason}»): l'etichetta resta ignota, "
                        + "e l'ignoto conta come grigio");
        }
        return (etichetta, $"{etichetta} — dal candidato del {origine} {candidate.RunId.ToString()[..8]}");
    }
}
