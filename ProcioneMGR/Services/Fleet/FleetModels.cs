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

    // --- [K33] La stessa ipotesi non occupa due corsie ---

    /// <summary>
    /// [K33, 2026-09-01] Rifiutare uno schieramento quando un'altra corsia porta la stessa
    /// <b>terna</b> (strategia, coppia, timeframe) con parametri diversi. La replica <i>esatta</i>
    /// (stessa <c>PipelineCandidateKey</c>) è sempre rifiutata e non ha manopola: non ha lettura
    /// alternativa.
    ///
    /// <para><b>Default acceso, e il motivo è misurato.</b> Delle 16 proposte grigie schierabili al
    /// 2026-09-01, una sola collide per identità esatta ma <b>tre</b> collidono per terna: due sono
    /// <c>MacdTrend AAVE/USDT 4h</c> con <c>FastPeriod</c> uguale e <c>SlowPeriod</c> 26 e 31 contro
    /// il 21 già in corsa sulla corsia 3. Con la sola guardia sull'identità esatta, il primo slot
    /// che si apre andrebbe a una taratura vicina di ciò che gira già — cioè il difetto del
    /// 2026-08-31 ripetuto un gradino più in là.</para>
    ///
    /// <para>Spegnerlo è legittimo se si vuole <i>deliberatamente</i> mettere in corsa due tarature
    /// dello stesso segnale come esperimento: in quel caso lo schieramento passa e il motivo resta
    /// scritto nel journal, che è la differenza fra una scelta e un incidente.</para>
    /// </summary>
    public bool BlockDuplicateTriple { get; set; } = true;

    /// <summary>
    /// [K57, revisione 2026-09-04] <b>Da quando contano le rimisurazioni per il gate di stabilità.</b>
    /// La soglia <c>MaxAmpiezzaSuMediana</c> è stata misurata sulle chiavi del <i>motore corrente</i>
    /// (walk-forward sostituito il 2026-08-23), ma il lettore aggregava OGNI riga della chiave: per
    /// le configurazioni 17 e 18 il 70-77 % delle righe è precedente alla sostituzione, e il
    /// ventaglio misurava il cambio di motore, non l'instabilità dell'ipotesi. <c>null</c> = tutte
    /// le righe (comportamento storico, dichiarato).
    /// </summary>
    public DateTime? StabilitaDaUtc { get; set; } = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

    // --- [K61, 2026-09-04] La SOSTITUZIONE: rimpiazzare un occupante inerte -------------------

    /// <summary>
    /// [K61, richiesta del proprietario 2026-09-04] <b>La Regina può schierare un candidato al posto
    /// di un occupante inerte</b>, invece di aspettare che una corsia si liberi da sola.
    ///
    /// <para><b>Perché serve, misurato il 2026-09-04.</b> <see cref="GreyAutoDeploy"/> è ACCESO e 19
    /// candidati grigi sono schierabili, ma le cinque corsie di flotta (3-7) sono tutte occupate e
    /// <b>nessuna può liberarsi</b>: il ritiro per Sharpe pretende <see cref="RetireMinTrades"/>
    /// trade (le corsie ne hanno 0, 0, 1, 3, 4) e il ritiro per inedia pretende un ritmo atteso
    /// dichiarato, che sulla corsia 5 è <c>null</c>. Il braccio automatico gira a vuoto da giorni
    /// scrivendo «nessuna corsia di flotta libera»: il vincolo non sono i candidati, sono le corsie.</para>
    ///
    /// <para><b>Perché NON distrugge il forward test.</b> È l'obiezione seria: il forward test Paper
    /// è l'unico giudice immune al multiple testing, e ricambiare corsie lo consuma. Questa regola
    /// tocca <b>solo corsie che non stanno producendo prove</b> — zero operazioni chiuse oltre la
    /// soglia di silenzio, e nessuna posizione aperta. Una corsia muta non accumula evidenza che si
    /// possa sprecare: occupa uno slot senza dire niente. Sostituire una corsia che <i>opera</i>
    /// sarebbe un'altra cosa, e questa regola non lo fa mai.</para>
    ///
    /// <para><b>Default spento.</b> È un potere nuovo su una corsia in corsa (in Paper) e si accende
    /// apposta, dopo aver guardato nel pannello quante corsie arriverebbero al cancello.</para>
    /// </summary>
    public bool ReplaceIdleLanes { get; set; }

    /// <summary>
    /// [K61] Giorni di <b>silenzio</b> (nessuna operazione chiusa) oltre i quali una corsia diventa
    /// candidata alla sostituzione, quando il suo ritmo atteso NON è dichiarato.
    ///
    /// <para>Default 10, allineato a <see cref="StarvationMinDays"/>: le due regole rispondono alla
    /// stessa domanda da due lati (il tasso cumulato lì, la recenza qui) e due pavimenti diversi
    /// sarebbero una contraddizione che nessuna superficie spiega. La sostituzione si esprime
    /// <b>solo dove il ritiro si astiene</b>, e non duplica mai l'inedia.</para>
    /// </summary>
    public int ReplaceIdleDays { get; set; } = 10;

    /// <summary>
    /// [K61] Quando il ritmo atteso <i>è</i> dichiarato, il silenzio si misura anche in <b>multipli
    /// dell'intervallo medio fra due operazioni attese</b>: la soglia diventa il massimo fra
    /// <see cref="ReplaceIdleDays"/> e questo multiplo.
    ///
    /// <para><b>Senza questa scala una soglia fissa punirebbe le corsie lente.</b> Misurato il
    /// 2026-09-04: la corsia 4 (XLM/USDT 4h) dichiara 1,65 trade/mese, cioè un'operazione ogni 18,4
    /// giorni — con una soglia secca a 10 giorni risulterebbe «inerte» mentre sta rispettando il
    /// proprio ritmo. Con il multiplo 2,0 la sua soglia diventa 36,9 giorni.</para>
    /// </summary>
    public decimal ReplaceIdleExpectedMultiple { get; set; } = 2.0m;

    /// <summary>
    /// [K61] <b>Pavimento di residenza</b>: giorni di osservazione cumulata sotto i quali una corsia
    /// non si tocca, qualunque cosa dica il silenzio. Non si uccide un esperimento appena schierato
    /// che non ha ancora avuto occasione di operare.
    /// </summary>
    public int ReplaceMinLaneDays { get; set; } = 7;

    /// <summary>
    /// [K61] Il rimpiazzo dev'essere un'ipotesi <b>stabile</b>: mediana K57 delle sue rimisurazioni
    /// almeno pari a questo valore. Non lo Sharpe del run migliore — quello è la notte fortunata.
    ///
    /// <para>Misurato il 2026-09-04 sulla fascia grigia: <c>EventTrigger GRT/USDT 4h</c> porta 3,91
    /// nel run più recente ma ha mediana 2,79 con un ventaglio di 3,26 su 20 rimisurazioni e 3 soli
    /// trade di holdout; <c>MacdTrend AAVE/USDT 4h</c> ha mediana 3,98 con ventaglio 0,21 su 52
    /// trade. Ordinare per data — come fa oggi il braccio automatico — non distingue i due.</para>
    /// </summary>
    public decimal ReplaceMinCandidateMedian { get; set; } = 1.0m;

    /// <summary>
    /// [K61] Quante rimisurazioni servono perché la mediana di stabilità sia <b>giudicabile</b>.
    /// Sotto questa soglia il candidato non è ammesso alla sostituzione: fail-closed, perché non
    /// sapere se un'ipotesi regge non è una ragione per preferirla a una corsia già in corsa.
    /// </summary>
    public int ReplaceMinCandidateMeasures { get; set; } = 5;

    /// <summary>
    /// [K61] Quante sostituzioni al massimo per giro. Default 1, come gli altri due budget: una
    /// corsia per volta si distingue da un guasto del lettore di stato, cinque no.
    /// </summary>
    public int MaxReplacementsPerTick { get; set; } = 1;

    /// <summary>
    /// [K61b] <b>Scegliere il grigio per merito invece che per data</b>, anche sul braccio che
    /// riempie le corsie già libere.
    ///
    /// <para>Oggi <c>GreyAccorpati</c> ordina per <c>CompletedAtUtc</c> crescente: con 19 candidati
    /// schierabili e uno slot, la Regina prende il più vecchio. La stabilità K57 — che dice quale
    /// ipotesi regge alle rimisurazioni — vive solo nell'ordinamento della lista che legge un umano.
    /// K57 ha misurato che il 22 % delle chiavi passa il cancello solo col proprio MASSIMO: la data
    /// non è un ordinamento neutro rispetto a questa domanda, è indipendente da essa.</para>
    ///
    /// <para><b>Default spento</b> perché cambia il comportamento di un braccio già in esercizio. Il
    /// pannello mostra affiancati chi verrebbe schierato per data e chi per merito: si accende dopo
    /// aver visto la differenza, non prima.</para>
    /// </summary>
    public bool PreferStableGrey { get; set; }
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
    bool? GreySourced = null,
    /// <summary>
    /// [K40, 2026-09-01] Lo stato di questa corsia <b>non è stato leggibile</b> in questo giro: il
    /// motore non ha risposto. È un sottoinsieme di <see cref="EmergencyStopped"/> — il lettore
    /// marca entrambi con la stessa bandiera — ma le due cose hanno rimedi opposti, e confonderle
    /// produce la frase sbagliata nel posto peggiore.
    ///
    /// <para><b>Il fatto misurato.</b> Al tick delle 2026-09-01 11:30 UTC il journal ha scritto
    /// «16 candidati grigi schierabili ma NESSUNA corsia di flotta libera (6 attive): il vincolo
    /// sono le corsie, non i candidati» — mentre <i>nessuna</i> delle cinque corsie aveva risposto
    /// (il ledger dell'osservazione non è stato toccato per nessuna di esse). Una corsia illeggibile
    /// esce da <see cref="FleetOrchestrator.FleetLanes"/>, quindi non è «libera»; e il conteggio
    /// «attive» viene dal database, che invece risponde sempre. Il risultato è una singola frase che
    /// mescola un numero preso dal DB con una libertà presa dal motore, e trasforma
    /// <b>«non riesco a leggere niente»</b> in <b>«le corsie sono impegnate»</b> — che ha un rimedio
    /// completamente diverso: guardare perché il pod non risponde, non liberare una corsia.</para>
    /// </summary>
    bool Unreadable = false,
    /// <summary>
    /// [K44, 2026-09-01] Sharpe <b>per operazione</b> della finestra ancorata, senza
    /// annualizzazione. <c>null</c> = non disponibile (meno di due trade, o un motore con
    /// un'immagine precedente al campo): in quel caso il criterio per Sharpe <b>si astiene</b>.
    ///
    /// <para><see cref="RealizedSharpe"/> resta, ma non è più ciò su cui si giudica: è annualizzato
    /// sui rendimenti di barra, quindi porta un fattore <c>√PeriodsPerYear</c> che vale 46,8 a 4h e
    /// 187,2 a 15m. Una soglia sola su quel numero è <b>quattro soglie diverse</b>.</para>
    /// </summary>
    decimal? RealizedSharpePerTrade = null,
    /// <summary>
    /// [K61, 2026-09-04] Quando la corsia ha chiuso l'<b>ultima operazione</b>. <c>null</c> = non ne
    /// ha chiusa nessuna nella finestra osservata, e il silenzio si misura allora sull'intera
    /// osservazione cumulata.
    ///
    /// <para><b>Non è <c>LastOrderUtc</c>, di proposito.</b> Quel campo lo scrive solo l'APERTURA di
    /// una posizione, mai la chiusura (alimenta l'anti-raffica del <c>SafetyChecker</c>), e per di
    /// più si azzera a ogni <c>StartAsync</c>: una corsia riavviata dal redeploy di un pod sembrerebbe
    /// muta da sempre. Qui si legge l'ultimo <c>ClosedAtUtc</c> di
    /// <c>TradingPerformance.Trades</c>, che arriva già deduplicato e ripulito dal replay (K41) —
    /// un <c>MAX(ClosedAtUtc)</c> scritto a mano leggerebbe l'ultima riga di REPLAY come ultima
    /// operazione.</para>
    ///
    /// <para><b>Limite dichiarato:</b> <c>ClosedAtUtc</c> è un tempo di CANDELA, non di parete. La
    /// misura del silenzio è quindi sfasata di al più una barra — irrilevante contro una soglia in
    /// giorni, e va detto invece di far finta che sia esatta.</para>
    /// </summary>
    DateTime? LastTradeUtc = null,
    /// <summary>
    /// [K61] Posizioni attualmente aperte sulla corsia. <b>Una sostituzione non si fa mai sopra una
    /// posizione viva</b>: <c>StopAsync</c> lascia le posizioni aperte e il successivo
    /// <c>StartAsync</c> in Paper le cancella senza scrivere alcun <c>TradeRecord</c> — la posizione
    /// sparirebbe dalla storia invece di chiudersi. È anche l'invariante K36 che
    /// <c>LaneInvariantWatchdog</c> sorveglia come allarme Critical.
    /// </summary>
    int OpenPositions = 0,
    /// <summary>
    /// [K61, revisione 2026-09-04] Il ritmo atteso è <b>null perché non confrontabile</b>, non perché
    /// non dichiarato: la configurazione e il motore non concordano sulle gambe attive (I12-rev), e
    /// il lettore lo azzera apposta per far ASTENERE il ritiro per inedia.
    ///
    /// <para>Senza questa distinzione la sostituzione farebbe l'opposto: leggendo lo stesso <c>null</c>
    /// come «ritmo non dichiarato» applicherebbe il pavimento secco al posto della soglia scalata, cioè
    /// un giudizio <b>più severo</b>. Un'ammissione di ignoranza non può diventare un'aggravante — è la
    /// stessa politica del ritiro, e qui va detta esplicitamente perché il campo è condiviso.</para>
    /// </summary>
    bool ExpectedDiverged = false,
    /// <summary>
    /// [K33-bis, 2026-09-05] Le identità canoniche (<c>PipelineCandidateKey</c>) delle gambe attive
    /// in configurazione, dalla directory. <c>null</c> o vuoto = la corsia non dichiara le proprie
    /// gambe (JSON illeggibile, gambe senza chiave): il confronto è parziale, e lo si dice.
    ///
    /// <para><b>Perché il decisore le vede.</b> La guardia dei duplicati (<see cref="HypothesisGuard"/>)
    /// viveva solo nel braccio, DOPO la scelta: il decisore ordinava i rimpiazzi per mediana K57,
    /// metteva in testa un'ipotesi che collideva per terna con una corsia in corsa, il braccio la
    /// rifiutava, e al tick dopo la sceglieva di nuovo — per sempre, con una riga di journal ogni
    /// quindici minuti e la coda che non avanzava mai al secondo candidato. Misurato il 2026-09-05:
    /// MacdTrend AAVE/USDT 4h (mediana 4,01) in testa, corsia 3 con la stessa terna. Con le chiavi
    /// nello stato, il decisore salta ciò che il braccio rifiuterebbe comunque.</para>
    /// </summary>
    IReadOnlyList<string>? ActiveCandidateKeys = null);

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
    string? Identity = null,
    /// <summary>
    /// [K61, 2026-09-04] <b>Mediana K57</b> dello Sharpe di holdout della stessa identità su tutte le
    /// sue rimisurazioni: la stima robusta alla «notte fortunata». <c>null</c> = non giudicabile.
    ///
    /// <para>Serve al braccio AUTOMATICO. Finora la stabilità viveva solo nell'ordinamento della
    /// lista che legge un umano in <c>/admin/autonomy</c>, mentre <c>Decide</c> sceglieva per DATA:
    /// due superfici con due criteri diversi sulla stessa domanda.</para>
    /// </summary>
    decimal? StabilityMedian = null,
    /// <summary>[K61] Quante rimisurazioni compongono <see cref="StabilityMedian"/>.</summary>
    int StabilityMeasures = 0,
    /// <summary>[K61] Ventaglio (massimo − minimo) delle rimisurazioni: quanto è ballerina l'ipotesi.</summary>
    decimal? StabilitySpread = null);

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

/// <summary>
/// [K61, 2026-09-04] <b>Sostituzione</b>: ferma l'occupante INERTE della corsia e schiera al suo
/// posto il candidato indicato, nello stesso giro.
///
/// <para><b>Una sola azione, non due.</b> Il piano ha l'invariante «mai due azioni sulla stessa
/// corsia» (provato dal fuzz su 20.000 stati) e il ritiro ha la regola «un motivo solo per corsia».
/// Esprimere la sostituzione come <see cref="StopAndFreeLane"/> + <see cref="AssignGreyCandidateToLane"/>
/// violerebbe entrambe e, peggio, permetterebbe al worker di eseguirne una sola: una corsia fermata
/// e non riempita è un esito diverso da quello deciso, e nessuno se ne accorgerebbe.</para>
///
/// <para><b>Il journal resta a due righe</b> (<c>Retire</c> e <c>Assign</c>, entrambe con il motivo
/// prefissato da «[Sostituzione]»): i consumatori esistenti filtrano per quei due <c>Kind</c> — in
/// particolare la deduplica dei candidati già gestiti — e un <c>Kind</c> nuovo li renderebbe ciechi,
/// facendo riproporre per sempre lo stesso candidato.</para>
/// </summary>
/// <param name="IsGrey">
/// [K61, revisione 2026-09-04] La banda del rimpiazzo viaggia sull'azione. Il braccio esecutivo la
/// passa a <c>GreyDeployer</c> come <c>allowSurvivor: !IsGrey</c>: un rimpiazzo di banda «pass»
/// schierato col percorso grigio verrebbe <b>sempre</b> rifiutato dal deployer — e la corsia sarebbe
/// già stata fermata, perché lo stop precede lo schieramento.
/// </param>
public sealed record ReplaceLaneOccupant(Guid RunId, string CandidateKey, int LaneId, string Reason, bool IsGrey = true)
    : FleetAction(Reason);

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
