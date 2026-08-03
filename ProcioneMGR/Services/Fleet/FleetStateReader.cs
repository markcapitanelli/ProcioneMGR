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

                var verdict = Evaluate(recommendation, validated, rangesByConfig.GetValueOrDefault(run.ConfigurationId));
                if (verdict is not { } v) continue;

                list.Add(new FleetCandidate(
                    run.Id, run.CompletedAt ?? now, v.Band, v.TradesPerMonth, v.Timeframe, v.Summary,
                    AlreadyHandled: handledByReapply.Contains(run.Id) || handledByFleet.Contains(run.Id)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Run {Run} escluso dalla coda candidati (dati illeggibili).", run.Id);
            }
        }
        return list;
    }

    internal readonly record struct CandidateVerdict(string Band, decimal TradesPerMonth, string Timeframe, string Summary);

    /// <summary>
    /// Il verdetto di un run come candidato di flotta.
    /// "pass" = almeno un sopravvissuto E gambe schierabili: frequenza dalla gamba più RADA (min).
    /// "grey" (F5) = zero sopravvissuti ma bocciature per SOLA finestra corta — classe ContoTrade
    /// ("Solo N trade in holdout") o DSR in [0.80, 0.95) — CON Sharpe holdout positivo: un grigio
    /// che perde non è grigio, è bocciato nel merito. La frequenza del grigio viene dal SUO
    /// HoldoutTrades, non dalle gambe della raccomandazione: con zero sopravvissuti le gambe non
    /// esistono, ed è proprio il caso per cui la fascia grigia esiste (primo journal vuoto del
    /// 2026-08-03: i grigi non entravano MAI in coda — il lettore chiedeva la frequenza a una
    /// lista vuota).
    /// null = non candidato (bocciato nel merito, o finestra/frequenza non derivabili).
    /// </summary>
    internal static CandidateVerdict? Evaluate(
        PipelineRecommendation recommendation, List<ValidatedCandidate> validated, string? dateRangesJson)
    {
        if (HoldoutMonths(dateRangesJson) is not decimal months) return null;

        var survivors = validated.Count(v => v.Survived);
        if (survivors > 0 && recommendation.EnsembleLegs.Count > 0)
        {
            var minTrades = recommendation.EnsembleLegs.Min(l => l.HoldoutTrades);
            return new CandidateVerdict("pass",
                Math.Round(minTrades / months, 2),
                recommendation.EnsembleLegs[0].Timeframe,
                $"{recommendation.BestCandidate} ({survivors} sopravvissuti su {recommendation.CandidatesEvaluated})");
        }

        var grey = validated
            .Where(IsGrey)
            .OrderByDescending(candidate => candidate.HoldoutSharpe)
            .ToList();
        if (grey.Count == 0) return null;

        var best = grey[0];
        return new CandidateVerdict("grey",
            Math.Round(best.HoldoutTrades / months, 2),
            best.Timeframe,
            $"{best.StrategyName} {best.Symbol} {best.Timeframe}: Sharpe holdout {best.HoldoutSharpe:F2} su {best.HoldoutTrades} trade"
            + (grey.Count > 1 ? $" (+{grey.Count - 1} altri in fascia grigia)" : ""));
    }

    /// <summary>
    /// IL filtro della fascia grigia — l'unica definizione, condivisa fra il lettore (le proposte)
    /// e il GreyDeployer (il click umano): bocciato per SOLA finestra corta (ContoTrade "Solo N
    /// trade…" o DSR in [0.80, 0.95)) CON Sharpe holdout positivo e almeno un trade. Un grigio che
    /// perde non è grigio: è bocciato nel merito.
    /// </summary>
    internal static bool IsGrey(ValidatedCandidate candidate) =>
        !candidate.Survived
        && candidate.HoldoutSharpe > 0m
        && candidate.HoldoutTrades > 0
        && ((candidate.RejectReason?.StartsWith("Solo ", StringComparison.Ordinal) ?? false)
            || candidate.DeflatedSharpe is >= GreyDsrFloor and < 0.95);

    /// <summary>
    /// Mesi della finestra holdout della config. Null se non derivabile (e allora il run non è un
    /// candidato: senza finestra la frequenza è un'illusione). La durata mediana delle posizioni
    /// invece NON esiste a livello di run (trade list non persistita): la misura il forward test.
    /// </summary>
    private static decimal? HoldoutMonths(string? dateRangesJson)
    {
        if (string.IsNullOrWhiteSpace(dateRangesJson)) return null;
        try
        {
            var ranges = JsonSerializer.Deserialize<PipelineDateRanges>(dateRangesJson);
            if (ranges is null) return null;
            var days = (ranges.HoldoutTo - ranges.HoldoutFrom).TotalDays;
            return days < 7 ? null : (decimal)(days / 30.44);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
