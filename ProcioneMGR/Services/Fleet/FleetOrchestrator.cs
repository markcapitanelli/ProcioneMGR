namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Il cuore deterministico della "Queen Bee": <see cref="Decide"/> è una funzione PURA —
/// stesso stato, stesso piano, sempre — fuzzabile come la promozione. Le AI non abitano qui: al
/// più, in un pareggio fra opzioni equivalenti, il core produce il pareggio e il worker può
/// chiedere al comitato (AF3) quale scegliere; una risposta invalida ricade sul default
/// deterministico che questo stesso metodo definisce.
///
/// I confini, in ordine di importanza:
/// 1. l'orchestratore NON tocca MAI: corsie dell'impronta storica (0..FootprintLanes-1, territorio
///    di auto-reapply e campagne), corsie Live o Testnet (le gestisce PromotionWorker), corsie in
///    quarantena, corsie in emergency stop, corsie possedute da una campagna;
/// 2. la fascia grigia (F5) si PROPONE al click umano, mai si assegna da soli;
/// 3. [AF4b] con più di MaxLanesWithoutExposureGuard corsie attive e la guardia di esposizione
///    correlata spenta, niente assegnazioni nuove: prima la guardia, poi la larghezza.
/// </summary>
public static class FleetOrchestrator
{
    public static FleetPlan Decide(FleetState state, FleetOptions opt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(opt);

        var actions = new List<FleetAction>();
        FleetAssignmentMenu? menu = null;

        // Le corsie di flotta: oltre l'impronta, e mai intoccabili.
        var fleetLanes = state.Lanes
            .Where(l => l.LaneId >= state.FootprintLanes)
            .Where(l => !l.Quarantined && !l.CampaignOwned && !l.EmergencyStopped)
            .Where(l => !IsProtectedMode(l.Mode))
            .ToList();

        // --- 1. Ritiri: forward test perdenti con storia sufficiente ---------------------------
        foreach (var lane in fleetLanes.Where(l => l.IsRunning))
        {
            var enoughHistory = lane.TradeCount >= Math.Max(1, opt.RetireMinTrades)
                                && lane.Observation >= TimeSpan.FromDays(7 * Math.Max(1, opt.RetireMinWeeks));
            if (enoughHistory && lane.RealizedSharpe < opt.RetireSharpeThreshold)
            {
                actions.Add(new StopAndFreeLane(lane.LaneId,
                    $"Forward test perdente: Sharpe {lane.RealizedSharpe:F2} < {opt.RetireSharpeThreshold:F2} " +
                    $"su {lane.TradeCount} trade in {lane.Observation.TotalDays:F0}gg ({lane.Symbol} {lane.Timeframe})."));
            }
        }

        // --- 2. Fascia grigia: proposte al click umano (mai auto) ------------------------------
        foreach (var grey in state.Candidates.Where(c => !c.AlreadyHandled && c.Band == "grey"))
        {
            actions.Add(new ProposeGreyCandidate(grey.RunId,
                $"Fascia grigia (bocciato solo per finestra corta): {grey.Summary} — ~{grey.TradesPerMonth:F1} trade/mese, " +
                "candidato al forward test Paper con click umano (F5)."));
        }

        // --- 3. Assegnazioni automatiche: solo candidati "pass" --------------------------------
        var queue = state.Candidates
            .Where(c => !c.AlreadyHandled && c.Band == "pass")
            .Where(c => c.TradesPerMonth >= opt.MinTradesPerMonth)
            .OrderBy(c => c.CompletedAtUtc) // il più vecchio per primo: FIFO deterministico
            .ToList();

        if (queue.Count > 0)
        {
            var freeLanes = fleetLanes
                .Where(l => !l.IsRunning)
                .OrderBy(l => l.LaneId) // id più basso per primo: deterministico
                .ToList();

            if (freeLanes.Count == 0)
            {
                actions.Add(new FleetNoOp(
                    $"{queue.Count} candidati in coda ma nessuna corsia di flotta libera: attendo un ritiro o una corsia nuova."));
            }
            else if (!state.ExposureGuardEnabled && CountActive(state) >= Math.Max(1, opt.MaxLanesWithoutExposureGuard))
            {
                // [AF4b] La flotta non si allarga senza la guardia trasversale.
                actions.Add(new FleetNoOp(
                    $"{CountActive(state)} corsie già attive e Trading:CorrelatedExposure SPENTO: nessuna assegnazione " +
                    $"oltre {opt.MaxLanesWithoutExposureGuard} corsie senza la guardia di esposizione correlata (accendila da /admin/protections)."));
            }
            else
            {
                var assignments = queue.Zip(freeLanes).Take(Math.Max(1, opt.MaxAssignmentsPerTick)).ToList();
                foreach (var (candidate, lane) in assignments)
                {
                    actions.Add(new AssignCandidateToLane(candidate.RunId, lane.LaneId,
                        $"Candidato validato del {candidate.CompletedAtUtc:yyyy-MM-dd} → corsia {lane.LaneId} in Paper: " +
                        $"{candidate.Summary} (~{candidate.TradesPerMonth:F1} trade/mese, {candidate.Timeframe})."));
                }

                // [AF3] Il PAREGGIO: più candidati idonei della prima assegnazione. Il core non
                // sceglie "per il comitato": sceglie da regola (il più vecchio) e ESPONE il menù —
                // il worker, se il comitato è attivo, può sostituire la scelta dentro il recinto.
                if (assignments.Count == 1 && queue.Count > 1)
                {
                    menu = new FleetAssignmentMenu(
                        assignments[0].Second.LaneId,
                        queue.Take(5).ToList(),
                        assignments[0].First.RunId);
                }
            }
        }

        return actions.Count == 0 ? FleetPlan.Empty : new FleetPlan(actions, menu);
    }

    /// <summary>Live e Testnet non sono affare dell'orchestratore, nemmeno oltre l'impronta.</summary>
    private static bool IsProtectedMode(string mode) =>
        mode.Equals("Live", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("Testnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Corsie attive in TUTTA la flotta (impronta compresa: l'esposizione correlata è trasversale).</summary>
    private static int CountActive(FleetState state) => state.Lanes.Count(l => l.IsRunning);
}
