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
        // non ci arriva mai: al 2026-08-19 le corsie 3-7 avevano da uno a sei trade ciascuna sul simbolo attuale (5, 1, 5, 6, 3) in 6-16 giorni
        // (misurato sul database vero), quindi nessuna era ritirabile — e una corsia che non si libera mai blocca la
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

        // --- 2. Assegnazioni automatiche: PRIMA la banda «pass» --------------------------------
        //
        // [J14] L'ordine è una scelta: i sopravvissuti pieni hanno la precedenza sui grigi per
        // corsie e budget — la fascia grigia riempie lo spazio che la banda «pass» lascia, mai il
        // contrario.
        var queue = AssignmentQueue(state, opt);
        var freeLanes = FreeFleetLanes(fleetLanes);
        var assignBudget = Math.Max(1, opt.MaxAssignmentsPerTick);
        var exposureBlocked = !state.ExposureGuardEnabled && CountActive(state) >= Math.Max(1, opt.MaxLanesWithoutExposureGuard);
        var passAssigned = 0;

        if (queue.Count > 0)
        {
            if (freeLanes.Count == 0)
            {
                actions.Add(new FleetNoOp(
                    $"{queue.Count} candidati in coda ma nessuna corsia di flotta libera: attendo un ritiro o una corsia nuova."));
            }
            else if (exposureBlocked)
            {
                // [AF4b] La flotta non si allarga senza la guardia trasversale.
                actions.Add(new FleetNoOp(
                    $"{CountActive(state)} corsie già attive e Trading:CorrelatedExposure SPENTO: nessuna assegnazione " +
                    $"oltre {opt.MaxLanesWithoutExposureGuard} corsie senza la guardia di esposizione correlata (accendila da /admin/protections)."));
            }
            else
            {
                var assignments = queue.Zip(freeLanes).Take(assignBudget).ToList();
                foreach (var (candidate, lane) in assignments)
                {
                    actions.Add(new AssignCandidateToLane(candidate.RunId, lane.LaneId,
                        $"Candidato validato del {candidate.CompletedAtUtc:yyyy-MM-dd} → corsia {lane.LaneId} in Paper: " +
                        $"{candidate.Summary} (~{candidate.TradesPerMonth:F1} trade/mese, {candidate.Timeframe}).",
                        // [J13] La chiave viaggia sull'azione quando il candidato è a gamba
                        // singola: è ciò che il braccio sa eseguire. Null = multi-gamba, journal-only.
                        CandidateKey: candidate.Identity));
                }
                passAssigned = assignments.Count;

                // [AF3] Il PAREGGIO: più candidati idonei della stessa assegnazione. Il core non
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

        // --- 3. Fascia grigia: assegnazione AUTOMATICA dietro flag (J14), altrimenti proposta ---
        var greyPairs = GreyProposals(state);
        var greyDeployables = GreyDeployables(state);
        var greyAssignedRuns = new HashSet<Guid>();
        if (opt.GreyAutoDeploy && greyDeployables.Count == 0 && greyPairs.Count == 0
            && state.Candidates.Any(c => c.Band == "grey"))
        {
            // [K12] La coda e' vuota ma i grigi ci SONO: sono tutti gia' schierati. E' il caso che
            // non si distingueva da «non ci sono grigi», e i due mandano a guardare posti diversi.
            var grigiTotali = state.Candidates.Count(c => c.Band == "grey");
            actions.Add(new FleetNoOp(
                $"{grigiTotali} candidati grigi nella finestra, tutti gia' schierati in passato: la coda e' vuota " +
                "perche' non c'e' nulla di NUOVO, non perche' non si cerca."));
        }
        if (opt.GreyAutoDeploy && greyDeployables.Count > 0)
        {
            // Il tetto: le corsie grigie IN CORSA più quelle assegnate in questo giro. L'ignoto
            // (GreySourced null) conta come grigio — non sapere non allarga il permesso.
            var greyRunning = GreyOccupied(state).Count;
            var greySlots = Math.Max(0, opt.MaxGreyLanes) - greyRunning;
            var lanesLeft = freeLanes.Skip(passAssigned).ToList();
            var budgetLeft = assignBudget - passAssigned;

            var eligible = greyDeployables
                .Select(p => p.Candidate)
                .Where(c => !string.IsNullOrEmpty(c.Identity))
                .Where(c => c.TradesPerMonth >= opt.MinTradesPerMonth)
                .ToList();

            // [K40, 2026-09-01] L'ILLEGGIBILITA' SI DICHIARA PER PRIMA, PERCHE' IL RIMEDIO E' UN ALTRO.
            //
            // Una corsia che non risponde esce da FleetLanes, quindi non risulta «libera»; e il
            // conteggio delle attive viene dal database, che risponde sempre. Il risultato,
            // misurato al tick del 2026-09-01 11:30 UTC con TUTTE e cinque le corsie mute, era
            // «NESSUNA corsia di flotta libera (6 attive): il vincolo sono le corsie» — una frase
            // che manda a liberare una corsia mentre il problema e' che il motore non risponde.
            //
            // E' l'ironia esatta di K38: li' si e' reso fail-closed il DENOMINATORE del tetto, ma il
            // ramo che sceglie QUALE spiegazione stampare era rimasto fail-open e viene prima nella
            // catena — quindi il messaggio buono di K38 non poteva nemmeno essere raggiunto proprio
            // quando serviva di piu'.
            var illeggibili = state.Lanes.Count(l => l.LaneId >= state.FootprintLanes && l.Unreadable);
            if (eligible.Count > 0 && illeggibili > 0 && lanesLeft.Count == 0)
            {
                actions.Add(new FleetNoOp(
                    $"{eligible.Count} candidati grigi schierabili, ma {illeggibili} corsie di flotta NON SONO " +
                    "LEGGIBILI in questo giro (il motore non ha risposto): non e' «le corsie sono impegnate», e' " +
                    "«non so in che stato sono». Una corsia illeggibile resta INTOCCABILE per prudenza e occupa il " +
                    "tetto grigio. Il rimedio non e' liberare una corsia: e' guardare perche' il motore non risponde " +
                    "(`procione stato`, e i tunnel 18092/18093)."));
            }
            else if (eligible.Count > 0 && exposureBlocked)
            {
                actions.Add(new FleetNoOp(
                    $"{eligible.Count} candidati grigi schierabili ma Trading:CorrelatedExposure SPENTO con " +
                    $"{CountActive(state)} corsie attive: nessuna assegnazione grigia senza la guardia (AF4b)."));
            }
            // [K12, 2026-08-31] IL RAMO GRIGIO DICHIARA PERCHE' NON SCHIERA.
            //
            // Il ramo «pass» aveva il suo FleetNoOp; questo no. Conseguenza misurata: in 115
            // decisioni ZERO righe Blocked, e chi guardava /admin/autonomy vedeva solo proposte
            // senza poter distinguere fra «il tetto grigio e' saturo», «non ci sono corsie libere»
            // e «la coda e' vuota» — tre vincoli diversi con tre rimedi diversi. Un ramo che tace
            // per tre ragioni e non ne nomina nessuna e' indistinguibile da un ramo spento.
            else if (lanesLeft.Count == 0)
            {
                actions.Add(new FleetNoOp(
                    $"{eligible.Count} candidati grigi schierabili ma NESSUNA corsia di flotta libera " +
                    $"({CountActive(state)} attive): il vincolo sono le corsie, non i candidati."));
            }
            else if (greySlots <= 0)
            {
                // [K33] Se fra le grigie ce n'è una che l'orchestratore non può toccare, va DETTO:
                // il rimedio è diverso (liberarla richiede un umano), e un operatore che legge
                // «il tetto è saturo» senza sapere che una delle corsie è intoccabile cerca il
                // rimedio sbagliato.
                var intoccabili = greyRunning - fleetLanes.Count(l => l.IsRunning && l.GreySourced != false);
                actions.Add(new FleetNoOp(
                    $"{eligible.Count} candidati grigi schierabili e {lanesLeft.Count} corsie libere, ma il tetto " +
                    $"grigio e' saturo: {greyRunning} corsie grigie in corsa su un massimo di {opt.MaxGreyLanes} " +
                    "(l'ignoto conta come grigio: non sapere non allarga il permesso)"
                    + (intoccabili > 0
                        ? $"; di queste {intoccabili} sono INTOCCABILI per l'orchestratore (quarantena, emergency, "
                          + "stato non leggibile o modalita' protetta): occupano il tetto e solo un umano puo' liberarle."
                        : ".")));
            }
            else if (eligible.Count == 0)
            {
                actions.Add(new FleetNoOp(
                    $"{greyDeployables.Count} candidati grigi nella finestra ma NESSUNO schierabile: " +
                    $"identita' assente, o sotto Fleet:MinTradesPerMonth ({opt.MinTradesPerMonth:F1} trade/mese)."));
            }
            else
            {
                foreach (var (candidate, lane) in eligible.Zip(lanesLeft))
                {
                    if (budgetLeft <= 0 || greySlots <= 0) break;
                    actions.Add(new AssignGreyCandidateToLane(candidate.RunId, candidate.Identity!, lane.LaneId,
                        $"[J14] Fascia grigia → corsia {lane.LaneId} in Paper: {candidate.Summary} " +
                        $"(~{candidate.TradesPerMonth:F1} trade/mese, {candidate.Timeframe}). " +
                        $"Corsie grigie dopo questa: {greyRunning + greyAssignedRuns.Count + 1}/{opt.MaxGreyLanes}."));
                    greyAssignedRuns.Add(candidate.RunId);
                    budgetLeft--;
                    greySlots--;
                }

                // [AF3] Anche i grigi possono pareggiare: il menù nasce solo se la banda «pass»
                // non ne ha già esposto uno (un tick, una domanda al comitato).
                if (menu is null && greyAssignedRuns.Count == 1 && eligible.Count > 1)
                {
                    var scelto = actions.OfType<AssignGreyCandidateToLane>().First();
                    menu = new FleetAssignmentMenu(scelto.LaneId, eligible.Take(5).ToList(), scelto.RunId);
                }
            }
        }

        // Le proposte al click umano restano per tutto ciò che NON è stato assegnato: flag spento
        // (F5 pieno, comportamento storico), tetto raggiunto, corsie finite, identità assente.
        foreach (var (grey, duplicati) in greyPairs)
        {
            if (greyAssignedRuns.Contains(grey.RunId)) continue;
            actions.Add(new ProposeGreyCandidate(grey.RunId,
                $"Fascia grigia (bocciato solo per finestra corta): {grey.Summary} — ~{grey.TradesPerMonth:F1} trade/mese, " +
                "candidato al forward test Paper con click umano (F5)."
                + (duplicati > 0
                    // [I12] Il numero non si perde: che la stessa cosa sia stata ritrovata da altri
                    // dodici run è un'informazione (è riproducibile), ma è UNA riga, non tredici.
                    ? $" Ritrovato da altri {duplicati} run con parametri identici."
                    : "")));
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

    /// <summary>
    /// [K33, 2026-09-01] <b>Le corsie che stanno occupando il tetto grigio</b> — e sono di più di
    /// quelle su cui l'orchestratore può agire.
    ///
    /// <para><b>Il varco che questa funzione chiude, misurato.</b> Fino a oggi il tetto contava
    /// dentro <see cref="FleetLanes"/>, che esclude le corsie intoccabili — quarantena, campagna,
    /// emergency, e <i>soprattutto</i> quelle che <c>FleetStateReader</c> marca <c>EmergencyStopped</c>
    /// quando non riesce a leggerne lo stato. Conseguenza: <b>una corsia che diventa illeggibile
    /// esce dal denominatore</b>, il tetto si allarga da solo, e nessuno lo dichiara. È il
    /// meccanismo — l'unico compatibile con i due fatti persistiti — per cui il 2026-08-31 alle
    /// 21:12 la seconda copia di <c>GridMeanReversion DOGE/USDT 15m</c> ha trovato uno slot che
    /// secondo la configurazione non c'era: con la corsia 4 contata, <c>greyRunning</c> valeva 3 e
    /// <c>greySlots</c> zero.</para>
    ///
    /// <para><b>Perché il verso giusto è questo.</b> «Intoccabile» e «non conta» sono due cose
    /// diverse e vanno tenute separate: una corsia in quarantena, ferma per emergenza o illeggibile
    /// <i>sta comunque tenendo capitale su un'ipotesi non validata</i>. Non poterla fermare è un
    /// motivo in più per non aggiungerne un'altra, non un motivo per contarne una di meno. È la
    /// stessa politica di <c>GreySourced == null</c> un piano più su: non sapere non allarga il
    /// permesso.</para>
    ///
    /// <para>Le corsie in Live o Testnet <b>contano</b> anche loro: un'ipotesi grigia promossa resta
    /// un'ipotesi grigia con del capitale sopra, e la manopola che governa il rischio dev'essere
    /// consapevole della sua vera larghezza. Le azioni restano quelle di <see cref="FleetLanes"/>.</para>
    /// </summary>
    internal static List<FleetLaneState> GreyOccupied(FleetState state) => state.Lanes
        .Where(l => l.LaneId >= state.FootprintLanes)
        .Where(l => l.IsRunning && l.GreySourced != false)
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
    internal static List<(FleetCandidate Candidate, int Duplicates)> GreyProposals(FleetState state) =>
        GreyAccorpati(state, perSchieramento: false);

    /// <summary>
    /// [K14, 2026-08-31] I grigi SCHIERABILI: stesso accorpamento delle proposte, ma il filtro
    /// guarda solo «gia' schierato». Un candidato gia' PROPOSTO a un umano resta schierabile — sono
    /// due azioni diverse, e fino a oggi la prima consumava la seconda.
    /// </summary>
    internal static List<(FleetCandidate Candidate, int Duplicates)> GreyDeployables(FleetState state) =>
        GreyAccorpati(state, perSchieramento: true);

    private static List<(FleetCandidate Candidate, int Duplicates)> GreyAccorpati(FleetState state, bool perSchieramento)
    {
        var grey = state.Candidates
            .Where(c => c.Band == "grey")
            .Where(c => perSchieramento ? !c.AlreadyHandled : !c.AlreadyHandled && !c.AlreadyProposed)
            .ToList();

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
        // [K40] L'illeggibilità viene PRIMA di tutto: finché non si sa in che stato sono le corsie,
        // ogni altra spiegazione è costruita su un denominatore che non c'è.
        var illeggibili = state.Lanes.Count(l => l.LaneId >= state.FootprintLanes && l.Unreadable);

        var reason = illeggibili > 0
            ? $"{illeggibili} corsie di flotta NON SONO LEGGIBILI (il motore non ha risposto in questo giro): "
              + "restano intoccabili per prudenza e occupano il tetto grigio. Prima di leggere gli altri numeri, "
              + "guarda perché il motore non risponde — non liberare una corsia"
            : fleetLanes.Count == 0
            ? "nessuna corsia oltre l'impronta dell'auto-apply: l'orchestratore non ha corsie da governare"
            : queue.Count == 0
                ? $"nessun candidato in banda «pass» in coda ({grey} grigi, che sono solo proposte al click umano): senza candidati non c'è nulla da assegnare"
                : free.Count == 0
                    ? starving > 0
                        // [I12] Il caso in cui il silenzio ha una fine, e dirlo cambia cosa fa
                        // l'operatore: aspettare, invece di andare a fermare una corsia a mano.
                        //
                        // [I12-rev] MA la fine arriva solo se qualcuno puo' agire. Il verdetto di
                        // inedia e' una DECISIONE; eseguirla richiede il braccio, il dry-run spento e
                        // la corsia autorizzata — tre condizioni che questa funzione pura non conosce
                        // e non deve indovinare. Promettere «il prossimo tick le ritira» in dry-run
                        // era un controllo che rassicura a prescindere dalla realta': l'operatore
                        // avrebbe aspettato un ritiro che non sarebbe mai arrivato.
                        //
                        // Si dice quindi cio' che e' vero in ogni assetto — il verdetto c'e' — e si
                        // rimanda a dove si legge se verra' eseguito.
                        ? $"{queue.Count} candidati in coda e nessuna corsia libera, ma {starving} sono in INEDIA: "
                          + "il verdetto di ritiro c'e' gia'; se venga eseguito dipende da Fleet:DryRun "
                          + "e da Fleet:ExecutionLanes (pannello qui sopra)"
                        : $"{queue.Count} candidati in coda ma nessuna corsia di flotta libera: serve un ritiro"
                    : queue.Count < 2
                        ? "un solo candidato idoneo: l'assegnazione è determinata, non c'è pareggio da arbitrare"
                        : "ci sono le condizioni per un'assegnazione e per un pareggio da arbitrare";

        return new FleetSilence(queue.Count, grey, free.Count, fleetLanes.Count, reason, starving, illeggibili);
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
    int StarvingLanes = 0,
    /// <summary>
    /// [K40] Corsie di flotta di cui NON si è potuto leggere lo stato in questo giro. Sopra zero,
    /// <b>ogni altro numero di questa scheda è costruito su un denominatore incompleto</b>: le
    /// corsie illeggibili non compaiono fra le libere, non compaiono fra quelle sotto governo, e
    /// non possono essere né in inedia né ritirate. È il numero da leggere per primo.
    /// </summary>
    int UnreadableLanes = 0)
{
    /// <summary>Vero quando esistono le condizioni perché il comitato riceva una domanda.</summary>
    public bool CommitteeCouldBeAsked => PassCandidatesQueued >= 2 && FreeFleetLanes > 0;
}
