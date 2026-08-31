namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Opzioni dell'orchestratore di flotta (sezione <c>Fleet</c>). Default: SPENTO, e anche da
/// acceso parte in DryRun (solo journal, zero azioni) — l'ordine degli incrementi è parte del
/// contratto: prima si osserva il journal per giorni, poi si toglie il dry-run apposta.
/// </summary>
public sealed class FleetOptions
{
    public bool Enabled { get; set; }

    /// <summary>Finché è true (default), l'orchestratore DECIDE e SCRIVE il journal ma non esegue nulla.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Cadenza del tick in minuti.</summary>
    public int TickMinutes { get; set; } = 15;

    // --- Ritiro dei forward test perdenti (corsie di flotta, mai l'impronta storica) ---

    /// <summary>Sharpe realizzato sotto cui un forward test è un perdente da ritirare.</summary>
    public decimal RetireSharpeThreshold { get; set; }

    /// <summary>Settimane minime di osservazione prima che un ritiro sia un giudizio e non rumore.</summary>
    public int RetireMinWeeks { get; set; } = 3;

    /// <summary>Trade minimi prima che un ritiro sia un giudizio e non rumore.</summary>
    public int RetireMinTrades { get; set; } = 20;

    /// <summary>
    /// Tick CONSECUTIVI in cui il verdetto di ritiro deve ripetersi prima di agire (isteresi:
    /// uno Sharpe che oscilla attorno alla soglia non deve produrre stop a raffica).
    /// </summary>
    public int RetireConfirmTicks { get; set; } = 2;

    // --- [I12] Ritiro per INEDIA: la corsia che non opera ---

    /// <summary>
    /// [I12] Frazione del ritmo ATTESO sotto cui una corsia è dichiarata in inedia e ritirata
    /// (0,2 = ha prodotto meno del 20% dei trade che l'holdout prometteva nel tempo trascorso).
    /// <b>0 = criterio spento</b>, comportamento identico a prima di I12.
    ///
    /// <para><b>Perché serve un secondo criterio di ritiro.</b> Quello per Sharpe pretende
    /// <see cref="RetireMinTrades"/> trade, e chi non opera non ci arriva mai: al 2026-08-19 le
    /// corsie di flotta 3-7 avevano chiuso <b>da uno a sei trade ciascuna</b> sul simbolo attuale
    /// (5, 1, 5, 6, 3 in 6-16 giorni, misurato sul database vero): mai vicino ai venti, quindi non
    /// erano ritirabili per nessuna via. Una corsia che non si libera mai blocca la flotta, e a
    /// monte il comitato — che riceve una domanda solo quando esiste una corsia libera con due
    /// candidati che se la contendono.</para>
    ///
    /// <para>Il confronto è col ritmo atteso della corsia, non con un conteggio assoluto: 30
    /// trade/mese fermi da due settimane sono un guasto, 2 trade/mese con un trade in due settimane
    /// sono la norma. E se il ritmo atteso non è noto — gambe configurate a mano, ensemble creati
    /// prima di I11 — <b>non si condanna</b>. Vedi <see cref="TradeFrequency.IsStarving"/>.</para>
    ///
    /// <para>Il default 0,2 è deliberatamente prudente: a un quinto dell'atteso non c'è più margine
    /// di lettura alternativa, mentre soglie vicine a 1 ritirerebbero corsie semplicemente lente.</para>
    /// </summary>
    public decimal StarvationFraction { get; set; } = 0.2m;

    /// <summary>
    /// [I12] Giorni minimi di osservazione prima che l'inedia sia un giudizio. Più corto delle tre
    /// settimane del ritiro per Sharpe di proposito: qui non si stima una performance, si constata
    /// un'assenza di operazioni, e constatarla richiede meno storia. Sotto questa soglia si tace,
    /// anche a zero trade, altrimenti una corsia appena avviata verrebbe ritirata prima di aver
    /// avuto occasione di operare.
    /// </summary>
    public int StarvationMinDays { get; set; } = 10;

    // --- [AF2b] Il braccio esecutivo: SOLO il ritiro, e solo dove è scritto ---

    /// <summary>
    /// [AF2b] Le corsie su cui l'orchestratore può <b>agire davvero</b> (fermarle), non solo
    /// scriverne nel journal. <b>Vuota di default = nessuna esecuzione, mai</b>, qualunque sia
    /// <see cref="DryRun"/>.
    ///
    /// <para><b>Perché una lista e non un interruttore.</b> Un booleano «esegui» apre di colpo
    /// tutte le corsie di flotta: il primo tick dopo l'accensione potrebbe fermarne quattro insieme,
    /// e non ci sarebbe modo di provare il braccio su una sola. La lista rende l'ampiezza una
    /// decisione esplicita e reversibile togliendo un numero — e permette il collaudo che il PRD
    /// chiede: <i>una corsia per volta, solo Paper</i>.</para>
    ///
    /// <para>È un <b>permesso</b>, non un bersaglio: essere in lista non fa succedere nulla, toglie
    /// solo il divieto. Le corsie dell'impronta, quarantenate, di campagna, in Live o Testnet
    /// restano intoccabili anche se elencate qui — questo elenco si somma ai confini
    /// dell'orchestratore, non li sostituisce.</para>
    /// </summary>
    public List<int> ExecutionLanes { get; set; } = [];

    /// <summary>
    /// [AF2b] Azioni eseguite al massimo per tick. Uno: il tick dopo si rilegge lo stato e si
    /// rivaluta. Fermare quattro corsie nello stesso giro renderebbe indistinguibile una decisione
    /// giusta da un guasto del lettore di stato.
    /// </summary>
    public int MaxExecutionsPerTick { get; set; } = 1;

    // --- Assegnazione dei candidati ---

    /// <summary>Assegnazioni massime per tick (prudenza: una alla volta, il tick dopo si rivaluta).</summary>
    public int MaxAssignmentsPerTick { get; set; } = 1;

    /// <summary>
    /// Trade/mese minimi dichiarati (derivati dall'holdout) perché un candidato entri in coda.
    /// Preferenza del proprietario: intraday/swing breve — un candidato che non dichiara la sua
    /// frequenza non entra affatto.
    /// </summary>
    public decimal MinTradesPerMonth { get; set; } = 1m;

    /// <summary>Età massima (giorni) di un run perché sia ancora un candidato fresco.</summary>
    public int CandidateMaxAgeDays { get; set; } = 14;

    // --- [AF4b] Guardia di flotta ---

    /// <summary>
    /// Oltre questo numero di corsie ATTIVE, l'orchestratore rifiuta nuove assegnazioni se il
    /// limite di esposizione correlata (<c>Trading:CorrelatedExposure</c>) è spento: una flotta
    /// larga senza guardia trasversale è concentrazione di rischio non misurata.
    /// </summary>
    public int MaxLanesWithoutExposureGuard { get; set; } = 3;

    /// <summary>Notifica se il worker del carry è abilitato ma non decide da più di queste ore.</summary>
    public int CarrySilenceAlertHours { get; set; } = 24;

    /// <summary>
    /// [AF3] Consulta il comitato AI sui PAREGGI (più candidati idonei della stessa assegnazione).
    /// Default false; richiede anche <c>Committee:Enabled</c>. Il comitato sceglie SOLO dentro il
    /// menù che il core ha già validato: una risposta invalida ricade sul default deterministico.
    /// </summary>
    public bool UseCommittee { get; set; }

    // --- [J14] Il rovesciamento di F5: schieramento AUTOMATICO dei grigi, coi freni ---

    /// <summary>
    /// [J14, PRD autonomia-operativa 2026-08-25] <b>Il rovesciamento di F5, per decisione del
    /// proprietario.</b> F5 stabiliva che il grigio si propone al click umano e non si schiera da
    /// solo — perché il forward test Paper è l'unico giudice immune al multiple testing e va speso
    /// con parsimonia. La decisione registrata nel PRD lo rovescia: con questo flag (e SOLO nella
    /// flotta, mai nel percorso campagna→impronta chiuso da J12) l'orchestratore può schierare da
    /// solo un candidato grigio su una corsia libera e autorizzata.
    ///
    /// <para>Default <b>false</b>: è IL cambio di natura della fascia grigia e si accende apposta.
    /// I freni che la campagna non aveva valgono tutti: banda e frequenza filtrate dal core,
    /// tetto <see cref="MaxGreyLanes"/>, corsie solo in <see cref="ExecutionLanes"/>, dry-run,
    /// budget per tick, guardia di esposizione, arbitrato del comitato sui pareggi, e il ritiro
    /// (J8-J10) che libera le corsie — senza un ritiro che funziona si riempiono cinque corsie
    /// una volta sola e non si liberano più.</para>
    /// </summary>
    public bool GreyAutoDeploy { get; set; }

    /// <summary>
    /// [J14] Tetto di corsie di flotta occupabili da candidati GRIGI contemporaneamente. Il
    /// default 3 su 5 è la raccomandazione del PRD (§8): due corsie restano alla banda «pass» per
    /// il giorno in cui il gate tornerà a produrne. Una corsia dalla provenienza IGNOTA conta come
    /// grigia ai fini del tetto: non sapere non allarga il permesso.
    /// </summary>
    public int MaxGreyLanes { get; set; } = 3;
}

/// <summary>Fotografia di una corsia come la vede l'orchestratore (sola lettura).</summary>
public sealed record FleetLaneState(
    int LaneId,
    bool IsRunning,
    string Mode,
    bool IsConfigured,
    bool Quarantined,
    bool CampaignOwned,
    bool EmergencyStopped,
    decimal RealizedSharpe,
    int TradeCount,
    TimeSpan Observation,
    string Symbol,
    string Timeframe,
    /// <summary>
    /// [I12] Ritmo ATTESO della corsia sul simbolo attuale (somma delle gambe attive), dalla
    /// configurazione dell'ensemble. <c>null</c> = non dichiarato da almeno una gamba, e in quel
    /// caso il ritiro per inedia NON si esprime: l'ignoranza non condanna.
    /// </summary>
    decimal? ExpectedTradesPerMonth = null,
    /// <summary>
    /// [J14] La corsia esegue gambe di fascia GRIGIA? Dalla configurazione (SourceVerdict delle
    /// gambe attive): true = almeno una grigia, false = tutte dichiarate sopravvissute, null =
    /// provenienza ignota — e ai fini del tetto MaxGreyLanes l'ignoto conta come grigio
    /// (fail-closed: non sapere non allarga il permesso).
    /// </summary>
    bool? GreySourced = null);

/// <summary>
/// Un run candidato al forward test. <paramref name="Band"/>: "pass" = sopravvissuti alla
/// validazione piena (assegnabile in automatico); "grey" = bocciato SOLO per finestra corta
/// (ContoTrade/sotto-potenza) — proposto al click umano, mai assegnato da solo (F5).
/// La durata mediana delle posizioni NON è derivabile a livello di run (la trade list dei
/// candidati non è persistita): si dichiara la frequenza (trade/mese) e il timeframe, la durata
/// vera la misurerà il forward test stesso.
/// </summary>
public sealed record FleetCandidate(
    Guid RunId,
    DateTime CompletedAtUtc,
    string Band,
    decimal TradesPerMonth,
    string Timeframe,
    string Summary,
    /// <summary>
    /// <b>SCHIERATO</b>, non «visto»: esiste un Assign di flotta o una decisione di auto-reapply
    /// per questa identità. [K14, 2026-08-31] Fino a quel giorno qui dentro finivano anche le
    /// ProposeGrey — cioè le proposte al click UMANO — e con l'ereditarietà per identità bastava
    /// una notifica perché il braccio AUTOMATICO considerasse il candidato gestito per sempre.
    /// Misura: 18 identità su 18 della finestra a 30 giorni risultavano gestite, e il migliore
    /// disponibile (MacdTrend AAVE/USDT 4h, Sharpe holdout 3,66 su 55 trade) era soppresso perché
    /// la stessa chiave era stata PROPOSTA due giorni prima. Proporre a un umano e schierare in
    /// automatico sono due azioni diverse, e la prima consumava la seconda.
    /// </summary>
    bool AlreadyHandled,

    /// <summary>
    /// [K14] <b>Già proposto al click umano.</b> Serve all'anti-raffica delle notifiche — non si
    /// ripropone quaranta volte la stessa cosa — e a NIENT'ALTRO: non toglie un candidato al
    /// braccio automatico.
    /// </summary>
    bool AlreadyProposed = false,
    /// <summary>
    /// [I12] Identità canonica del candidato (<c>PipelineCandidateKey</c>: strategia + coppia +
    /// timeframe + impronta dei parametri), per NON riproporre quaranta volte la stessa cosa.
    ///
    /// <para>Le proposte grigie nascono per RUN, e la caccia rigira gli stessi parametri sugli
    /// stessi mercati: al 2026-08-18 il journal ne contava 83, tutte in attesa dello stesso click,
    /// e ognuna era una notifica. Un elenco di quaranta righe che sono una cosa sola non è un
    /// elenco, è rumore — e il rumore consuma il budget degli allarmi veri (lezione già pagata con
    /// la staleness a 60s su STX).</para>
    ///
    /// <para><c>null</c> = identità non derivabile (verdetti illeggibili): in quel caso il candidato
    /// NON si deduplica, perché accorpare per ignoranza nasconderebbe proposte diverse.</para>
    /// </summary>
    string? Identity = null);

/// <summary>Lo stato complessivo su cui <see cref="FleetOrchestrator.Decide"/> ragiona. Solo dati, nessun servizio.</summary>
public sealed class FleetState
{
    public required IReadOnlyList<FleetLaneState> Lanes { get; init; }

    public required IReadOnlyList<FleetCandidate> Candidates { get; init; }

    /// <summary>
    /// Le prime N corsie (l'impronta storica dell'auto-apply, oggi 3): territorio di
    /// auto-reapply e campagne, MAI dell'orchestratore. La flotta lavora da qui in su.
    /// </summary>
    public required int FootprintLanes { get; init; }

    /// <summary>Il limite di esposizione correlata fra corsie è acceso? (precondizione AF4b)</summary>
    public required bool ExposureGuardEnabled { get; init; }

    public required DateTime NowUtc { get; init; }
}

/// <summary>Le azioni che l'orchestratore può decidere. Chiuse: non esiste un'azione "avvia Live" per costruzione.</summary>
public abstract record FleetAction(string Reason);

/// <summary>
/// Schiera il candidato sulla corsia libera indicata e la avvia in Paper (AF2b; in DryRun solo
/// journal). [J13] <paramref name="CandidateKey"/> è l'identità del candidato da schierare quando
/// la raccomandazione è a gamba SINGOLA; null = ensemble multi-gamba, che il braccio non esegue
/// (lo schieramento di un ensemble su una corsia sola non è definito: una corsia ha un simbolo) —
/// resta di solo journal, col motivo dichiarato.
/// </summary>
public sealed record AssignCandidateToLane(Guid RunId, int LaneId, string Reason, string? CandidateKey = null) : FleetAction(Reason);

/// <summary>
/// [J14] Schiera un candidato GRIGIO sulla corsia libera indicata e la avvia in Paper. Azione
/// distinta da <see cref="AssignCandidateToLane"/> di proposito: il journal e i log devono poter
/// dire «grigio» senza ispezionare nulla, e i freni (tetto, flag) valgono solo qui.
/// </summary>
public sealed record AssignGreyCandidateToLane(Guid RunId, string CandidateKey, int LaneId, string Reason) : FleetAction(Reason);

/// <summary>Ferma un forward test perdente e libera la corsia.</summary>
public sealed record StopAndFreeLane(int LaneId, string Reason) : FleetAction(Reason);

/// <summary>Fascia grigia (F5): si propone al click umano, MAI si assegna da soli.</summary>
public sealed record ProposeGreyCandidate(Guid RunId, string Reason) : FleetAction(Reason);

/// <summary>Nessuna azione, ma con un motivo che PORTA informazione (conflitto, guardia, coda bloccata).</summary>
public sealed record FleetNoOp(string Reason) : FleetAction(Reason);

/// <summary>
/// [AF3] Il PAREGGIO che il comitato può arbitrare: più candidati idonei per la stessa corsia.
/// <paramref name="DefaultRunId"/> è la scelta deterministica (il più vecchio) — quella che vale
/// se il comitato non produce una maggioranza valida, ed è già dentro il piano.
/// </summary>
public sealed record FleetAssignmentMenu(int LaneId, IReadOnlyList<FleetCandidate> Eligible, Guid DefaultRunId);

/// <summary>Il piano di un tick. <see cref="Menu"/> è presente solo quando esiste un pareggio arbitrabile.</summary>
public sealed record FleetPlan(IReadOnlyList<FleetAction> Actions, FleetAssignmentMenu? Menu = null)
{
    public static readonly FleetPlan Empty = new([]);
}
