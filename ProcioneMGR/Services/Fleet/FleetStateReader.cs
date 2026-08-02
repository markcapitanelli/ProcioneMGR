using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Costruisce il <see cref="FleetState"/> in SOLA lettura: corsie (directory + quarantene +
/// possesso campagne + stato vivo dei motori) e coda candidati (run completati + verdetti di
/// validazione). Difensivo per corsia e per run: un guasto su una corsia la rende INTOCCABILE
/// (mai "libera per errore"), un run illeggibile esce dalla coda con un log — l'orchestratore
/// deve poter ragionare su ciò che sa, non inciampare su ciò che non sa.
/// </summary>
public interface IFleetStateReader
{
    Task<FleetState> ReadAsync(CancellationToken ct = default);
}

public sealed class FleetStateReader(
    ILaneDirectory laneDirectory,
    ILaneQuarantineStore quarantineStore,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    IPipelineApplier applier,
    IOptionsMonitor<Risk.CorrelatedExposureOptions> exposureOptions,
    IOptionsMonitor<FleetOptions> fleetOptions,
    ILogger<FleetStateReader> logger) : IFleetStateReader
{
    /// <summary>Soglia F5: DSR in [GreyDsrFloor, soglia di sopravvivenza) = fascia grigia.</summary>
    private const double GreyDsrFloor = 0.80;

    public async Task<FleetState> ReadAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // --- Corsie -----------------------------------------------------------------------------
        var summaries = await laneDirectory.ListAsync(ct);
        var quarantined = (await quarantineStore.GetAllAsync(ct)).Select(q => q.LaneId).ToHashSet();

        int campaignPrefix;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Il possesso delle campagne è un PREFISSO 0..ObservedLanes-1 (non una lista): si
            // prende il massimo fra le campagne abilitate — conservativo con più campagne.
            campaignPrefix = await db.VettingCampaigns.AsNoTracking()
                .Where(c => c.Enabled)
                .Select(c => (int?)c.ObservedLanes)
                .MaxAsync(ct) ?? 0;
        }

        var lanes = new List<FleetLaneState>(summaries.Count);
        foreach (var s in summaries)
        {
            var quarantinedLane = quarantined.Contains(s.Id);
            var campaignOwned = s.Id < campaignPrefix;

            var running = s.IsRunning;
            var mode = s.Mode;
            var emergency = false;
            var sharpe = 0m;
            var trades = 0;
            var observation = TimeSpan.Zero;

            // Lo stato vivo serve solo alle corsie di flotta potenzialmente toccabili; per le
            // altre bastano directory e vincoli (meno chiamate, meno superfici di guasto).
            if (s.Id >= applier.LaneCount && !quarantinedLane && !campaignOwned)
            {
                try
                {
                    var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(s.Id);
                    var status = await engine.GetStatusAsync(ct);
                    running = status.IsRunning;
                    mode = status.Mode.ToString();
                    emergency = status.IsEmergencyStopped;
                    if (running && status.StartedAtUtc is DateTime started)
                    {
                        observation = now - started;
                        var perf = await engine.GetPerformanceAsync(from: started, ct);
                        sharpe = perf.SharpeRatio;
                        trades = perf.TotalTrades;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Corsia illeggibile = corsia INTOCCABILE, mai "libera per errore".
                    logger.LogWarning(ex, "Stato corsia {Lane} non leggibile: la marco intoccabile per questo tick.", s.Id);
                    emergency = true;
                }
            }

            lanes.Add(new FleetLaneState(
                s.Id, running, mode, s.IsConfigured, quarantinedLane, campaignOwned, emergency,
                sharpe, trades, observation, s.Symbol, s.Timeframe));
        }

        // --- Candidati --------------------------------------------------------------------------
        var candidates = await ReadCandidatesAsync(now, ct);

        return new FleetState
        {
            Lanes = lanes,
            Candidates = candidates,
            FootprintLanes = applier.LaneCount,
            ExposureGuardEnabled = exposureOptions.CurrentValue.Enabled,
            NowUtc = now,
        };
    }

    private async Task<IReadOnlyList<FleetCandidate>> ReadCandidatesAsync(DateTime now, CancellationToken ct)
    {
        var opt = fleetOptions.CurrentValue;
        var minCompleted = now.AddDays(-Math.Max(1, opt.CandidateMaxAgeDays));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt >= minCompleted)
            .Where(r => !string.IsNullOrEmpty(r.RecommendationJson) && r.RecommendationJson != "{}")
            .Select(r => new { r.Id, r.CompletedAt, r.ConfigurationId, r.RecommendationJson })
            .ToListAsync(ct);
        if (runs.Count == 0) return [];

        var runIds = runs.Select(r => r.Id).ToList();

        // Verdetti di validazione per run (artifact "ValidatedCandidates").
        var validatedByRun = (await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.Kind == "ValidatedCandidates" && runIds.Contains(a.RunId))
                .Select(a => new { a.RunId, a.PayloadJson })
                .ToListAsync(ct))
            .ToDictionary(a => a.RunId, a => a.PayloadJson);

        // Run già gestiti: dall'auto-reapply (artifact) o da questo stesso journal.
        var handledByReapply = (await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.Kind == AutoReapplyArtifactKinds.Decision && runIds.Contains(a.RunId))
                .Select(a => a.RunId)
                .ToListAsync(ct))
            .ToHashSet();
        var handledByFleet = (await db.OrchestratorDecisions.AsNoTracking()
                .Where(d => d.RunId != null && runIds.Contains(d.RunId.Value)
                            && (d.Kind == "Assign" || d.Kind == "ProposeGrey"))
                .Select(d => d.RunId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        // Le finestre date per derivare i trade/mese (la config le porta come JSON).
        var configIds = runs.Select(r => r.ConfigurationId).Distinct().ToList();
        var rangesByConfig = (await db.PipelineConfigurations.AsNoTracking()
                .Where(c => configIds.Contains(c.Id))
                .Select(c => new { c.Id, c.DateRangesJson })
                .ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.DateRangesJson);

        var list = new List<FleetCandidate>();
        foreach (var run in runs)
        {
            try
            {
                var recommendation = RunApplyEvaluator.DeserializeRecommendation(run.RecommendationJson);
                if (recommendation is null) continue;

                List<ValidatedCandidate> validated = [];
                if (validatedByRun.TryGetValue(run.Id, out var payload))
                {
                    try { validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(payload) ?? []; }
                    catch (JsonException) { /* verdetti illeggibili: si classifica con quello che c'è */ }
                }

                var survivors = validated.Count(v => v.Survived);
                var band = Classify(recommendation, validated, survivors);
                if (band is null) continue;

                var tradesPerMonth = DeriveTradesPerMonth(recommendation, rangesByConfig.GetValueOrDefault(run.ConfigurationId));
                if (tradesPerMonth is not decimal tpm)
                {
                    // Preferenza vincolante del proprietario: un candidato che non sa dichiarare
                    // la propria frequenza non entra in coda.
                    continue;
                }

                var timeframe = recommendation.EnsembleLegs.FirstOrDefault()?.Timeframe ?? "?";
                var summary = band == "pass"
                    ? $"{recommendation.BestCandidate} ({survivors} sopravvissuti su {recommendation.CandidatesEvaluated})"
                    : $"{recommendation.BestCandidate} (0 sopravvissuti: finestra corta, non mancanza di edge)";

                list.Add(new FleetCandidate(
                    run.Id, run.CompletedAt ?? now, band, tpm, timeframe, summary,
                    AlreadyHandled: handledByReapply.Contains(run.Id) || handledByFleet.Contains(run.Id)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Run {Run} escluso dalla coda candidati (dati illeggibili).", run.Id);
            }
        }
        return list;
    }

    /// <summary>
    /// "pass" = almeno un sopravvissuto alla validazione piena E gambe schierabili.
    /// "grey" (F5) = zero sopravvissuti ma bocciature per SOLA finestra corta: classe ContoTrade
    /// (guadagna ma i trade in holdout sono pochi per la sua frequenza) o DSR in [0.80, soglia).
    /// null = non candidato (bocciato nel merito, o niente da schierare).
    /// </summary>
    private static string? Classify(PipelineRecommendation recommendation, List<ValidatedCandidate> validated, int survivors)
    {
        if (survivors > 0 && recommendation.EnsembleLegs.Count > 0) return "pass";
        if (validated.Count == 0) return null;

        var contoTrade = PipelineEngine.ClassifyRejections(validated).Any(c => c.Classe == "ContoTrade" && c.Quanti > 0);
        var greyDsr = validated.Any(v => !v.Survived && v.DeflatedSharpe is double dsr and >= GreyDsrFloor and < 0.95);
        return contoTrade || greyDsr ? "grey" : null;
    }

    /// <summary>
    /// Trade/mese derivati: gamba più RADA (min) su finestra holdout della config. Null se la
    /// finestra non è derivabile — la durata mediana delle posizioni invece NON esiste a livello
    /// di run (trade list non persistita): la misura il forward test stesso.
    /// </summary>
    private static decimal? DeriveTradesPerMonth(PipelineRecommendation recommendation, string? dateRangesJson)
    {
        if (recommendation.EnsembleLegs.Count == 0 || string.IsNullOrWhiteSpace(dateRangesJson)) return null;
        try
        {
            var ranges = JsonSerializer.Deserialize<PipelineDateRanges>(dateRangesJson);
            if (ranges is null) return null;
            var days = (ranges.HoldoutTo - ranges.HoldoutFrom).TotalDays;
            if (days < 7) return null; // sotto una settimana la frequenza è un'illusione

            var months = (decimal)(days / 30.44);
            var minTrades = recommendation.EnsembleLegs.Min(l => l.HoldoutTrades);
            return Math.Round(minTrades / months, 2);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
