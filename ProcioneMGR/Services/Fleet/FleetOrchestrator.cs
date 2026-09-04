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
            // [K44, 2026-09-01] SI GIUDICA SULLO SHARPE PER OPERAZIONE, non su quello annualizzato.
            //
            // `RealizedSharpe` è annualizzato sui rendimenti di BARRA, quindi porta un fattore
            // √PeriodsPerYear che vale 46,8 a 4h e 187,2 a 15m: la STESSA soglia era quattro soglie
            // diverse a seconda del timeframe della corsia, e nessuna superficie lo diceva. Lo
            // Sharpe per trade non ha quell'annualizzazione, ed è lo STESSO test
            // (t = Sharpe × √N è un'identità algebrica): non cambia il verdetto, toglie l'ambiguità
            // su cosa il verdetto significhi.
            //
            // `null` = non disponibile (meno di due trade, o un motore con un'immagine precedente
            // al campo). In quel caso NON si giudica: l'ignoranza non condanna, ed è la stessa
            // politica del ritmo atteso e della provenienza.
            if (enoughHistory && lane.RealizedSharpePerTrade is decimal sharpeTrade
                && sharpeTrade < opt.RetireSharpeThreshold)
            {
                actions.Add(new StopAndFreeLane(lane.LaneId,
                    $"Forward test perdente: Sharpe per trade {sharpeTrade:F3} < {opt.RetireSharpeThreshold:F2} " +
                    $"su {lane.TradeCount} trade in {lane.Observation.TotalDays:F0}gg ({lane.Symbol} {lane.Timeframe}). " +
                    $"Sharpe annualizzato per riferimento: {lane.RealizedSharpe:F2} — non è su quello che si giudica, " +
                    "perché dipende dal timeframe."));
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

            // [K61b, 2026-09-04] Per MERITO invece che per data, se lo si è chiesto.
            //
            // GreyAccorpati ordina per CompletedAtUtc crescente: con 19 candidati schierabili e uno
            // slot, la Regina prende il più vecchio. La stabilità K57 — quale ipotesi regge alle
            // rimisurazioni — viveva solo nell'ordinamento della lista che legge un umano, mentre il
            // braccio automatico sceglieva per data: due superfici, due criteri, stessa domanda.
            // I non giudicabili restano in coda nell'ordine di prima, mai davanti a chi ha una misura.
            if (opt.PreferStableGrey)
            {
                eligible = [.. eligible
                    .OrderByDescending(c => c.StabilityMeasures >= Math.Max(1, opt.ReplaceMinCandidateMeasures) ? 1 : 0)
                    .ThenByDescending(c => c.StabilityMedian ?? decimal.MinValue)
                    .ThenBy(c => c.CompletedAtUtc)
                    .ThenBy(c => c.RunId)];
            }

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

        // --- 4. [K61] SOSTITUZIONE: un candidato al posto di un occupante INERTE ----------------
        //
        // Ultima risorsa, e si vede dall'ordine: prima si riempiono le corsie libere (blocchi 2 e 3),
        // e solo se non ne è rimasta nessuna si guarda chi sta occupando uno slot senza produrre
        // niente. Il vincolo misurato il 2026-09-04 è esattamente questo: 19 grigi schierabili, cinque
        // corsie occupate, e NESSUNA che possa liberarsi (zero corsie arrivano ai 20 trade del
        // giudizio per Sharpe, e la corsia 5 non ha ritmo atteso dichiarato quindi non può nemmeno
        // andare in inedia). Il braccio automatico girava a vuoto scrivendo «nessuna corsia libera».
        if (opt.ReplaceIdleLanes)
        {
            var libereRimaste = freeLanes.Count - passAssigned - greyAssignedRuns.Count;
            actions.AddRange(Sostituzioni(state, opt, fleetLanes, actions, libereRimaste, exposureBlocked, greyAssignedRuns));
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

    // === [K61, 2026-09-04] LA SOSTITUZIONE ======================================================

    /// <summary>
    /// [K61] Da quanto tempo la corsia <b>non chiude un'operazione</b>. Se non ne ha mai chiusa, il
    /// silenzio è tutta la sua osservazione cumulata.
    ///
    /// <para>Il tempo trascorso dall'ultima operazione è di PARETE, mentre l'osservazione è cumulata
    /// (non scorre mentre la corsia è ferma). Sono due orologi diversi e qui si prende il <b>minore</b>:
    /// una corsia spenta per due giorni non deve accumulare silenzio mentre nessuno la fa operare.
    /// È la stessa prudenza del ritiro — l'ignoranza non condanna.</para>
    /// </summary>
    internal static TimeSpan Silenzio(FleetLaneState lane, DateTime nowUtc)
    {
        if (lane.LastTradeUtc is not DateTime ultima) return lane.Observation;
        var parete = nowUtc - ultima;
        if (parete < TimeSpan.Zero) parete = TimeSpan.Zero;
        return parete < lane.Observation ? parete : lane.Observation;
    }

    /// <summary>
    /// [K61] La soglia di silenzio DI QUESTA corsia. Con un ritmo atteso dichiarato è il massimo fra
    /// il pavimento in giorni e il multiplo dell'intervallo medio fra due operazioni attese; senza,
    /// è il solo pavimento.
    ///
    /// <para>Senza la scala, una soglia secca punirebbe le corsie lente: la corsia 4 (XLM/USDT 4h,
    /// 1,65 trade/mese attesi = una ogni 18,4 giorni) risulterebbe «inerte» a 10 giorni mentre sta
    /// rispettando il proprio ritmo.</para>
    /// </summary>
    internal static TimeSpan SogliaSilenzio(FleetLaneState lane, FleetOptions opt)
    {
        var pavimento = TimeSpan.FromDays(Math.Max(1, opt.ReplaceIdleDays));
        if (lane.ExpectedTradesPerMonth is not decimal attesi || attesi <= 0m) return pavimento;

        var multiplo = opt.ReplaceIdleExpectedMultiple;
        if (multiplo <= 0m) return pavimento;

        // 30,44 giorni per mese: la stessa costante che TradeFrequency usa per convertire il ritmo.
        var intervalloMedioGiorni = 30.44m / attesi;
        var scalata = TimeSpan.FromDays((double)(intervalloMedioGiorni * multiplo));
        return scalata > pavimento ? scalata : pavimento;
    }

    /// <summary>
    /// [K61] <b>La corsia è INERTE</b>: sta occupando uno slot senza produrre prove, e può essere
    /// sostituita. Puro, e condiviso fra la decisione e la sua spiegazione.
    ///
    /// <para>Le quattro condizioni, tutte necessarie:</para>
    /// <list type="number">
    /// <item>la corsia gira (una ferma è già libera: la riempie il braccio normale);</item>
    /// <item><b>nessuna posizione aperta</b> — non è prudenza generica: <c>StopAsync</c> lascia le
    /// posizioni aperte e il successivo <c>StartAsync</c> in Paper le CANCELLA con
    /// <c>ExecuteDelete</c> senza scrivere alcun <c>TradeRecord</c>. Sostituire sopra una posizione
    /// viva la farebbe sparire dalla storia invece di chiuderla — il danno K36, già avvenuto il
    /// 2026-08-31 sulla corsia 6 (short DOGE/USDT, 799 USDT di nozionale). Il sorvegliante che
    /// grida non basterebbe: se stop e schieramento cadono nello stesso giro, la sua finestra di
    /// osservazione può non aprirsi mai;</item>
    /// <item>osservazione cumulata oltre il <b>pavimento di residenza</b>: non si uccide un
    /// esperimento che non ha ancora avuto occasione di operare;</item>
    /// <item>silenzio oltre la soglia della corsia.</item>
    /// </list>
    ///
    /// <para><b>Perché non è un doppione dell'inedia.</b> <see cref="IsStarving"/> è un test di TASSO
    /// cumulato che pretende un ritmo atteso dichiarato e si astiene senza; questo è un test di
    /// RECENZA che non ne ha bisogno. Il chiamante lo applica <b>solo alle corsie che il ritiro non
    /// ha condannato</b>: dove l'inedia parla, la sostituzione tace.</para>
    /// </summary>
    internal static bool IsIdle(FleetLaneState lane, FleetOptions opt, DateTime nowUtc) =>
        lane.IsRunning
        && lane.OpenPositions == 0
        && lane.Observation >= TimeSpan.FromDays(Math.Max(1, opt.ReplaceMinLaneDays))
        && Silenzio(lane, nowUtc) >= SogliaSilenzio(lane, opt);

    /// <summary>
    /// [K61] I candidati ammessi a SOSTITUIRE, ordinati per merito. Non per data: con 19 schierabili
    /// e uno slot, la data non è un ordinamento neutro rispetto alla domanda «quale ipotesi regge».
    ///
    /// <para>Ammessi solo i <b>giudicabili</b> (abbastanza rimisurazioni) sopra la mediana minima:
    /// fail-closed, perché non sapere se un'ipotesi è stabile non è una ragione per preferirla a una
    /// corsia già in corsa. Misurato il 2026-09-04: <c>EventTrigger GRT/USDT 4h</c> porta 3,91 nel run
    /// più recente ma ha mediana 2,79 con ventaglio 3,26 su 3 trade di holdout, contro
    /// <c>MacdTrend AAVE/USDT 4h</c> con mediana 3,98, ventaglio 0,21 e 52 trade.</para>
    /// </summary>
    internal static List<FleetCandidate> RimpiazziAmmessi(FleetState state, FleetOptions opt) => state.Candidates
        .Where(c => !c.AlreadyHandled)
        .Where(c => !string.IsNullOrEmpty(c.Identity))
        .Where(c => c.TradesPerMonth >= opt.MinTradesPerMonth)
        .Where(c => c.StabilityMeasures >= Math.Max(1, opt.ReplaceMinCandidateMeasures))
        .Where(c => c.StabilityMedian is decimal m && m >= opt.ReplaceMinCandidateMedian)
        // Una identità sola, anche se più run l'hanno ritrovata: si tiene la rimisurazione più recente.
        .GroupBy(c => c.Identity!, StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(c => c.CompletedAtUtc).ThenBy(c => c.RunId).First())
        .OrderByDescending(c => c.StabilityMedian!.Value)
        .ThenBy(c => c.CompletedAtUtc)
        .ThenBy(c => c.RunId)
        .ToList();

    /// <summary>
    /// [K61] Le corsie inerti, dalla più muta. Solo quelle che il ritiro NON ha già condannato in
    /// questo giro: due azioni sulla stessa corsia romperebbero l'invariante del piano, e un ritiro
    /// già deciso libera comunque lo slot al giro dopo.
    /// </summary>
    internal static List<FleetLaneState> CorsieInerti(
        IEnumerable<FleetLaneState> fleetLanes, IEnumerable<FleetAction> giaDecise, FleetOptions opt, DateTime nowUtc)
    {
        var condannate = giaDecise.OfType<StopAndFreeLane>().Select(a => a.LaneId).ToHashSet();
        return [.. fleetLanes
            .Where(l => !condannate.Contains(l.LaneId))
            .Where(l => IsIdle(l, opt, nowUtc))
            .OrderByDescending(l => Silenzio(l, nowUtc))
            .ThenBy(l => l.LaneId)];
    }

    /// <summary>
    /// [K61] Il ramo di sostituzione: dichiara sempre perché NON sostituisce, come fa il ramo grigio
    /// dopo K12. Un ramo che tace per cinque ragioni diverse e non ne nomina nessuna è
    /// indistinguibile da un ramo spento.
    /// </summary>
    private static List<FleetAction> Sostituzioni(
        FleetState state, FleetOptions opt, List<FleetLaneState> fleetLanes, List<FleetAction> giaDecise,
        int libereRimaste, bool exposureBlocked, HashSet<Guid> giaAssegnati)
    {
        var esito = new List<FleetAction>();
        var inerti = CorsieInerti(fleetLanes, giaDecise, opt, state.NowUtc);
        if (inerti.Count == 0) return esito;

        // C'è ancora uno slot vuoto: si riempie quello, che non costa niente a nessuno.
        if (libereRimaste > 0)
        {
            esito.Add(new FleetNoOp(
                $"{inerti.Count} corsie inerti, ma ci sono ancora {libereRimaste} corsie LIBERE da riempire: "
                + "la sostituzione è l'ultima risorsa e non anticipa il braccio normale."));
            return esito;
        }

        if (exposureBlocked)
        {
            // Una sostituzione non allarga la flotta, ma cambia il SIMBOLO della corsia — cioè la
            // matrice di correlazione su cui la guardia ragiona. Fail-closed, come l'assegnazione.
            esito.Add(new FleetNoOp(
                $"{inerti.Count} corsie inerti sostituibili ma Trading:CorrelatedExposure SPENTO: una sostituzione "
                + "non allarga la flotta ma ne cambia i simboli, cioè la matrice di correlazione (AF4b)."));
            return esito;
        }

        var ammessi = RimpiazziAmmessi(state, opt).Where(c => !giaAssegnati.Contains(c.RunId)).ToList();
        if (ammessi.Count == 0)
        {
            var giudicabili = state.Candidates.Count(c => c.StabilityMeasures >= Math.Max(1, opt.ReplaceMinCandidateMeasures));
            esito.Add(new FleetNoOp(
                $"{inerti.Count} corsie inerti ma NESSUN rimpiazzo ammesso: servono almeno "
                + $"{Math.Max(1, opt.ReplaceMinCandidateMeasures)} rimisurazioni e una mediana K57 ≥ "
                + $"{opt.ReplaceMinCandidateMedian:F2} (candidati giudicabili in finestra: {giudicabili}). "
                + "Non sapere se un'ipotesi regge non è una ragione per preferirla a una corsia in corsa."));
            return esito;
        }

        // Il tetto grigio: sostituire una corsia NON grigia con un candidato grigio allarga
        // l'esposizione della fascia grigia, sostituirne una grigia la lascia dov'è.
        var greyRunning = GreyOccupied(state).Count;
        var budget = Math.Max(1, opt.MaxReplacementsPerTick);

        foreach (var (corsia, candidato) in inerti.Zip(ammessi))
        {
            if (budget <= 0) break;

            var candidatoGrigio = candidato.Band == "grey";
            var corsiaGrigia = corsia.GreySourced != false;
            if (candidatoGrigio && !corsiaGrigia && greyRunning + 1 > Math.Max(0, opt.MaxGreyLanes))
            {
                esito.Add(new FleetNoOp(
                    $"Corsia {corsia.LaneId} inerte e rimpiazzabile, ma metterci un grigio al posto di una gamba "
                    + $"dichiarata sopravvissuta porterebbe le corsie grigie a {greyRunning + 1} sul tetto di "
                    + $"{opt.MaxGreyLanes}."));
                continue;
            }

            esito.Add(new ReplaceLaneOccupant(candidato.RunId, candidato.Identity!, corsia.LaneId,
                $"[Sostituzione] Corsia {corsia.LaneId} INERTE su {corsia.Symbol} {corsia.Timeframe}: "
                + $"nessuna operazione da {Silenzio(corsia, state.NowUtc).TotalDays:F1} giorni "
                + $"(soglia {SogliaSilenzio(corsia, opt).TotalDays:F1}, "
                + (corsia.ExpectedTradesPerMonth is decimal a
                    ? $"ritmo atteso {a:F2}/mese"
                    : "ritmo atteso NON dichiarato: il ritiro per inedia non può esprimersi")
                + $"), nessuna posizione aperta, {corsia.Observation.TotalDays:F1} giorni osservati. "
                + $"Al suo posto: {candidato.Summary} — mediana K57 {candidato.StabilityMedian:F2} su "
                + $"{candidato.StabilityMeasures} rimisurazioni"
                + (candidato.StabilitySpread is decimal v ? $" (ventaglio {v:F2})" : "")
                + $", ~{candidato.TradesPerMonth:F1} trade/mese."));

            if (candidatoGrigio && !corsiaGrigia) greyRunning++;
            budget--;
        }

        return esito;
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

    /// <summary>
    /// [K55, 2026-09-02] Le corsie <b>d'impronta</b> (0..FootprintLanes−1) che portano gambe di
    /// provenienza grigia o ignota — cioè la stessa esposizione che <see cref="GreyOccupied"/>
    /// governa sulle corsie di flotta, ma su un percorso che <c>MaxGreyLanes</c> <b>non copre</b>.
    ///
    /// <para><b>Perché esiste, e da quando.</b> Il 2026-09-02 il proprietario ha portato
    /// <c>AutoReapply:MaxGreyLegs</c> da 0 a 2: da quel momento l'auto-apply può mettere gambe di
    /// fascia grigia sulle corsie d'impronta. Sono <b>due tetti scollegati sullo stesso rischio</b>
    /// — «ipotesi non promosse in forward test» si accumula su due percorsi contati separatamente,
    /// e prima di questa riga <b>nessuna superficie sommava i due</b>.</para>
    ///
    /// <para>Non entra nel tetto: <c>MaxGreyLanes</c> significa «corsie di FLOTTA grigie», e
    /// cambiarne il denominatore in silenzio bloccherebbe schieramenti per una ragione che nessuno
    /// ha scelto. Qui si <b>conta e si dichiara</b>; se poi debba diventare un vincolo è una
    /// decisione del proprietario, e va presa guardando questo numero.</para>
    ///
    /// <para>Stesso criterio fail-closed di K38: <c>GreySourced != false</c>, quindi l'ignoto conta
    /// come grigio. Non sapere non allarga il permesso.</para>
    /// </summary>
    internal static int GreyOnFootprintLanes(FleetState state) => state.Lanes
        .Where(l => l.LaneId < state.FootprintLanes)
        .Count(l => l.IsRunning && l.GreySourced != false);

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

        // [K61] Il cancello della sostituzione si CONTA sempre, anche a interruttore spento: è così
        // che si decide se accenderlo, ed è la lezione del «gate senza strumento» — una soglia senza
        // la superficie che dice quante corsie ci arriverebbero è un criterio di cui nessuno sa se è
        // severo o finto. Si esclude chi il ritiro ha già condannato, come fa la decisione vera.
        var condannate = fleetLanes.Where(l => IsStarving(l, opt)).Select(l => l.LaneId).ToHashSet();
        var inerti = fleetLanes.Count(l => !condannate.Contains(l.LaneId) && IsIdle(l, opt, state.NowUtc));
        var rimpiazzi = RimpiazziAmmessi(state, opt).Count;

        return new FleetSilence(queue.Count, grey, free.Count, fleetLanes.Count, reason, starving, illeggibili,
            GreyOccupied(state).Count, GreyOnFootprintLanes(state), inerti, rimpiazzi);
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
    int UnreadableLanes = 0,
    /// <summary>[K55] Corsie di FLOTTA che occupano il tetto grigio in questo giro.</summary>
    int GreyFleetLanes = 0,
    /// <summary>
    /// [K55, 2026-09-02] Corsie d'IMPRONTA che portano gambe grigie (o di provenienza ignota).
    /// <b>Non sono contate da <c>MaxGreyLanes</c></b>: sono lo stesso rischio su un secondo
    /// percorso, aperto dal 2026-09-02 quando <c>AutoReapply:MaxGreyLegs</c> è passato da 0 a 2.
    /// </summary>
    int GreyFootprintLanes = 0,
    /// <summary>
    /// [K61] Corsie che arriverebbero al cancello della SOSTITUZIONE: in corsa, senza posizioni
    /// aperte, oltre il pavimento di residenza e oltre la propria soglia di silenzio.
    ///
    /// <para>Sta qui per la lezione del «gate senza strumento»: una soglia va scritta insieme alla
    /// superficie che dice quante corsie ci arriverebbero, altrimenti resta un criterio di cui
    /// nessuno sa se è severo o spento. Il conteggio si fa <b>sempre</b>, anche a interruttore
    /// spento: è così che si decide se accenderlo.</para>
    /// </summary>
    int IdleLanes = 0,
    /// <summary>[K61] Candidati ammessi a sostituire: giudicabili e sopra la mediana minima.</summary>
    int ReplacementsReady = 0)
{
    /// <summary>Vero quando esistono le condizioni perché il comitato riceva una domanda.</summary>
    public bool CommitteeCouldBeAsked => PassCandidatesQueued >= 2 && FreeFleetLanes > 0;

    /// <summary>
    /// [K55] L'esposizione TOTALE a ipotesi non promosse, sui due percorsi sommati. È il numero che
    /// nessuna superficie mostrava, ed è quello che descrive il rischio davvero corso.
    /// </summary>
    public int GreyLanesTotal => GreyFleetLanes + GreyFootprintLanes;
}
