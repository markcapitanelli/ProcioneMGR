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
        //
        // [I12] DUE criteri, e uno solo per corsia (il primo che morde vince, e non serve dirne
        // due). Il secondo esiste perché il primo pretende RetireMinTrades trade e chi non opera
        // non ci arriva mai: al 2026-08-19 le corsie 3-7 avevano UN trade ciascuna o zero in 13-15
        // giorni, quindi nessuna era ritirabile — e una corsia che non si libera mai blocca la
        // flotta, e a monte il comitato.
        foreach (var lane in fleetLanes.Where(l => l.IsRunning))
        {
            var enoughHistory = lane.TradeCount >= Math.Max(1, opt.RetireMinTrades)
                                && lane.Observation >= TimeSpan.FromDays(7 * Math.Max(1, opt.RetireMinWeeks));
            if (enoughHistory && lane.RealizedSharpe < opt.RetireSharpeThreshold)
            {
                actions.Add(new StopAndFreeLane(lane.LaneId,
                    $"Forward test perdente: Sharpe {lane.RealizedSharpe:F2} < {opt.RetireSharpeThreshold:F2} " +
                    $"su {lane.TradeCount} trade in {lane.Observation.TotalDays:F0}gg ({lane.Symbol} {lane.Timeframe})."));
                continue;
            }

            if (IsStarving(lane, opt))
            {
                actions.Add(new StopAndFreeLane(lane.LaneId,
                    $"Corsia in INEDIA su {lane.Symbol} {lane.Timeframe}: " +
                    TradeFrequency.DescribeStarvation(
                        lane.ExpectedTradesPerMonth, lane.TradeCount, lane.Observation, opt.StarvationFraction) +
                    ". Non arriverebbe mai ai " + Math.Max(1, opt.RetireMinTrades) +
                    " trade del giudizio per Sharpe: si libera la corsia."));
            }
        }

        // --- 2. Fascia grigia: proposte al click umano (mai auto), UNA per identità -------------
        foreach (var (grey, duplicati) in GreyProposals(state))
        {
            actions.Add(new ProposeGreyCandidate(grey.RunId,
                $"Fascia grigia (bocciato solo per finestra corta): {grey.Summary} — ~{grey.TradesPerMonth:F1} trade/mese, " +
                "candidato al forward test Paper con click umano (F5)."
                + (duplicati > 0
                    // [I12] Il numero non si perde: che la stessa cosa sia stata ritrovata da altri
                    // dodici run è un'informazione (è riproducibile), ma è UNA riga, non tredici.
                    ? $" Ritrovato da altri {duplicati} run con parametri identici."
                    : "")));
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

    /// <summary>
    /// [I12] La corsia è in inedia? Predicato CONDIVISO fra la decisione e la sua spiegazione, per
    /// la stessa ragione degli altri tre: un pannello che contasse le corsie affamate con una
    /// seconda regola spiegherebbe un ritiro diverso da quello che avverrà.
    ///
    /// <para>Il giudizio vero sta in <see cref="TradeFrequency.IsStarving"/>, che è puro e non sa
    /// nulla di corsie: qui si traducono solo le manopole. Chi non è in corsa non è in inedia — è
    /// fermo, che è un altro stato e ha già la sua strada.</para>
    /// </summary>
    internal static bool IsStarving(FleetLaneState lane, FleetOptions opt) =>
        lane.IsRunning && TradeFrequency.IsStarving(
            lane.ExpectedTradesPerMonth, lane.TradeCount, lane.Observation,
            opt.StarvationFraction, TimeSpan.FromDays(Math.Max(1, opt.StarvationMinDays)));

    /// <summary>
    /// [I12] Le proposte grigie da mostrare: <b>una per identità canonica</b>, col numero dei run
    /// che l'hanno ritrovata.
    ///
    /// <para>Le proposte nascono per run, e la caccia rigira gli stessi parametri sugli stessi
    /// mercati: al 2026-08-18 il journal ne contava 83, tutte in attesa dello stesso click e ognuna
    /// una notifica. Un elenco di quaranta righe che sono una cosa sola non è un elenco, è rumore —
    /// e il rumore consuma il budget degli allarmi veri, lezione già pagata con la staleness a 60s
    /// su STX.</para>
    ///
    /// <para><b>Il run che sopravvive è il più RECENTE</b> (a parità, il RunId minore, per
    /// determinismo): un grigio è una proposta di forward test, e il forward test si fa sull'ipotesi
    /// vista sui dati più freschi. È l'opposto della coda «pass», che è FIFO perché lì il criterio è
    /// non far invecchiare nessuno in attesa.</para>
    ///
    /// <para>I candidati SENZA identità non si accorpano mai: accorpare per ignoranza nasconderebbe
    /// proposte diverse, che è l'errore peggiore dei due.</para>
    /// </summary>
    internal static List<(FleetCandidate Candidate, int Duplicates)> GreyProposals(FleetState state)
    {
        var grey = state.Candidates.Where(c => !c.AlreadyHandled && c.Band == "grey").ToList();

        var senzaIdentita = grey
            .Where(c => string.IsNullOrEmpty(c.Identity))
            .Select(c => (c, 0));

        var perIdentita = grey
            .Where(c => !string.IsNullOrEmpty(c.Identity))
            .GroupBy(c => c.Identity!, StringComparer.Ordinal)
            .Select(g => (
                g.OrderByDescending(c => c.CompletedAtUtc).ThenBy(c => c.RunId).First(),
                g.Count() - 1));

        return senzaIdentita.Concat(perIdentita)
            .OrderBy(x => x.Item1.CompletedAtUtc).ThenBy(x => x.Item1.RunId)
            .ToList();
    }

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
        var grey = GreyProposals(state).Count;   // [I12] proposte DISTINTE, non run
        var starving = fleetLanes.Count(l => IsStarving(l, opt));

        // La ragione si dà nell'ordine in cui morde: il primo vincolo che chiude la strada è quello
        // che l'operatore deve conoscere, e dirli tutti insieme non aiuterebbe a decidere.
        var reason = fleetLanes.Count == 0
            ? "nessuna corsia oltre l'impronta dell'auto-apply: l'orchestratore non ha corsie da governare"
            : queue.Count == 0
                ? $"nessun candidato in banda «pass» in coda ({grey} grigi, che sono solo proposte al click umano): senza candidati non c'è nulla da assegnare"
                : free.Count == 0
                    ? starving > 0
                        // [I12] Il caso in cui il silenzio ha una fine DATATA, e dirlo cambia cosa
                        // fa l'operatore: aspettare, invece di andare a fermare una corsia a mano.
                        ? $"{queue.Count} candidati in coda e nessuna corsia libera, ma {starving} in INEDIA: "
                          + "il prossimo tick le ritira e libera il posto"
                        : $"{queue.Count} candidati in coda ma nessuna corsia di flotta libera: serve un ritiro"
                    : queue.Count < 2
                        ? "un solo candidato idoneo: l'assegnazione è determinata, non c'è pareggio da arbitrare"
                        : "ci sono le condizioni per un'assegnazione e per un pareggio da arbitrare";

        return new FleetSilence(queue.Count, grey, free.Count, fleetLanes.Count, reason, starving);
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
/// <param name="StarvingLanes">
/// [I12] Corsie in corsa che non stanno operando quanto promesso e che il prossimo tick ritirerà.
/// Zero non vuol dire "tutte in salute": vuol dire "nessuna condannabile", e comprende le corsie di
/// cui non si conosce il ritmo atteso — l'ignoranza non condanna.
/// </param>
public sealed record FleetSilence(
    int PassCandidatesQueued,
    int GreyCandidates,
    int FreeFleetLanes,
    int LanesUnderGovernance,
    string Reason,
    int StarvingLanes = 0)
{
    /// <summary>Vero quando esistono le condizioni perché il comitato riceva una domanda.</summary>
    public bool CommitteeCouldBeAsked => PassCandidatesQueued >= 2 && FreeFleetLanes > 0;
}
