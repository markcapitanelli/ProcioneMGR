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
        var fleetLanes = FleetLanes(state);

        // I tre predicati qui sotto sono estratti apposta e usati ANCHE da Explain: la diagnosi del
        // silenzio deve contare esattamente ciò che la decisione guarda. Due definizioni di «coda» o
        // di «corsia libera» darebbero un pannello che spiega un silenzio diverso da quello vero —
        // il difetto già pagato in D2 e con SeriesFreshness, nel posto peggiore per ripeterlo.

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
        var queue = AssignmentQueue(state, opt);

        if (queue.Count > 0)
        {
            var freeLanes = FreeFleetLanes(fleetLanes);

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

    // --- Predicati CONDIVISI fra la decisione e la sua spiegazione ------------------------------

    /// <summary>Le corsie su cui l'orchestratore ha davvero potere: oltre l'impronta e non intoccabili.</summary>
    internal static List<FleetLaneState> FleetLanes(FleetState state) => state.Lanes
        .Where(l => l.LaneId >= state.FootprintLanes)
        .Where(l => !l.Quarantined && !l.CampaignOwned && !l.EmergencyStopped)
        .Where(l => !IsProtectedMode(l.Mode))
        .ToList();

    /// <summary>La coda di assegnazione: candidati «pass» non ancora gestiti e abbastanza frequenti.</summary>
    internal static List<FleetCandidate> AssignmentQueue(FleetState state, FleetOptions opt) => state.Candidates
        .Where(c => !c.AlreadyHandled && c.Band == "pass")
        .Where(c => c.TradesPerMonth >= opt.MinTradesPerMonth)
        .OrderBy(c => c.CompletedAtUtc) // il più vecchio per primo: FIFO deterministico
        .ToList();

    /// <summary>Le corsie di flotta ferme, in ordine deterministico.</summary>
    internal static List<FleetLaneState> FreeFleetLanes(IEnumerable<FleetLaneState> fleetLanes) => fleetLanes
        .Where(l => !l.IsRunning)
        .OrderBy(l => l.LaneId)
        .ToList();

    /// <summary>
    /// [I8] <b>Perché l'orchestratore non fa nulla.</b>
    ///
    /// <para>Il comitato AI è rimasto acceso sedici giorni senza emettere un voto e la flotta ha
    /// prodotto 83 proposte grigie e zero assegnazioni, senza che nessuna superficie dicesse il
    /// perché. La causa è a monte e in un punto solo: <see cref="Decide"/> forma un menù — e quindi
    /// una domanda per il comitato — <b>solo</b> quando ci sono almeno due candidati in banda
    /// «pass» in coda e una corsia libera. Con la coda sempre vuota non nasce nessun menù, quindi
    /// nessun pareggio, quindi nessun voto.</para>
    ///
    /// <para>I quattro numeri qui sotto sono contati con gli <b>stessi predicati</b> della
    /// decisione, non con una seconda lettura: un pannello che spiegasse un silenzio diverso da
    /// quello vero sarebbe peggio del silenzio.</para>
    /// </summary>
    public static FleetSilence Explain(FleetState state, FleetOptions opt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(opt);

        var fleetLanes = FleetLanes(state);
        var queue = AssignmentQueue(state, opt);
        var free = FreeFleetLanes(fleetLanes);
        var grey = state.Candidates.Count(c => !c.AlreadyHandled && c.Band == "grey");

        // La ragione si dà nell'ordine in cui morde: il primo vincolo che chiude la strada è quello
        // che l'operatore deve conoscere, e dirli tutti insieme non aiuterebbe a decidere.
        var reason = fleetLanes.Count == 0
            ? "nessuna corsia oltre l'impronta dell'auto-apply: l'orchestratore non ha corsie da governare"
            : queue.Count == 0
                ? $"nessun candidato in banda «pass» in coda ({grey} grigi, che sono solo proposte al click umano): senza candidati non c'è nulla da assegnare"
                : free.Count == 0
                    ? $"{queue.Count} candidati in coda ma nessuna corsia di flotta libera: serve un ritiro"
                    : queue.Count < 2
                        ? "un solo candidato idoneo: l'assegnazione è determinata, non c'è pareggio da arbitrare"
                        : "ci sono le condizioni per un'assegnazione e per un pareggio da arbitrare";

        return new FleetSilence(queue.Count, grey, free.Count, fleetLanes.Count, reason);
    }
}

/// <summary>
/// [I8] I quattro numeri che spiegano il silenzio dell'orchestratore, più la ragione in chiaro.
/// </summary>
/// <param name="PassCandidatesQueued">Candidati «pass» in coda: sotto 2 non nasce alcun pareggio da arbitrare.</param>
/// <param name="GreyCandidates">Candidati in fascia grigia non gestiti: proposte al click umano, mai assegnazioni.</param>
/// <param name="FreeFleetLanes">Corsie di flotta ferme e assegnabili adesso.</param>
/// <param name="LanesUnderGovernance">Corsie su cui l'orchestratore ha potere (oltre l'impronta, non intoccabili).</param>
/// <param name="Reason">Il primo vincolo che chiude la strada, in italiano.</param>
public sealed record FleetSilence(
    int PassCandidatesQueued,
    int GreyCandidates,
    int FreeFleetLanes,
    int LanesUnderGovernance,
    string Reason)
{
    /// <summary>Vero quando esistono le condizioni perché il comitato riceva una domanda.</summary>
    public bool CommitteeCouldBeAsked => PassCandidatesQueued >= 2 && FreeFleetLanes > 0;
}
