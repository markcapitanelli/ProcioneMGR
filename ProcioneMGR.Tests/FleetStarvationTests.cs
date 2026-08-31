using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I12] <b>Il ritiro per inedia</b>: il secondo criterio, quello che esiste perché il primo non
/// può mordere.
///
/// <para>Il ritiro per Sharpe pretende <c>RetireMinTrades</c> trade (20 di default) e chi non opera
/// non ci arriva <b>mai</b>: al 2026-08-19 le corsie di flotta 3-7 avevano chiuso un trade ciascuna
/// o zero in 13-15 giorni. Non erano ritirabili per nessuna via, e una corsia che non si libera mai
/// blocca la flotta — e a monte il comitato, che riceve una domanda solo quando c'è una corsia
/// libera con due candidati che se la contendono. Sedici giorni senza un voto avevano lì la loro
/// causa.</para>
/// </summary>
public sealed class FleetStarvationTests
{
    private static FleetLaneState Lane(int id, bool running = true, string mode = "Paper",
        bool quarantined = false, bool campaign = false, bool emergency = false,
        decimal sharpe = 0.5m, int trades = 0, int observationDays = 14, decimal? expectedPerMonth = null)
        => new(id, running, mode, IsConfigured: true, quarantined, campaign, emergency,
            sharpe, trades, TimeSpan.FromDays(observationDays), "ADA/USDT", "4h", expectedPerMonth);

    private static FleetState State(IReadOnlyList<FleetLaneState> lanes, int footprint = 3)
        => new() { Lanes = lanes, Candidates = [], FootprintLanes = footprint, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow };

    private static FleetOptions Options(decimal fraction = 0.2m, int minDays = 10)
        => new() { StarvationFraction = fraction, StarvationMinDays = minDays, RetireSharpeThreshold = -99m };

    private static int[] Retired(FleetPlan plan) =>
        plan.Actions.OfType<StopAndFreeLane>().Select(a => a.LaneId).OrderBy(x => x).ToArray();

    /// <summary>
    /// <b>L1 con lo stato reale della flotta.</b> Corsie 3-7 in corsa su un atteso di 12 trade/mese
    /// (~5,6 nel periodo di 14 giorni):
    /// <list type="bullet">
    /// <item>3, 5, 6, 7 hanno prodotto 0 o 1 trade ⇒ <b>ritirate</b>;</item>
    /// <item>4 è in corsa da 3 giorni ⇒ sotto l'osservazione minima, <b>non si giudica</b>;</item>
    /// <item>0-2 sono l'impronta dell'auto-apply ⇒ <b>intoccabili</b>, per quanto ferme siano;</item>
    /// <item>8 è in quarantena, 9 è di una campagna ⇒ <b>intoccabili</b>.</item>
    /// </list>
    /// Le ultime tre righe non sono contorno: il criterio nuovo eredita i confini del vecchio, e un
    /// ritiro per inedia sull'impronta riscriverebbe corsie che l'orchestratore non governa.
    /// </summary>
    [Fact]
    public void L1_StatoReale_RitiraLeAffamateENonLeAltre()
    {
        var plan = FleetOrchestrator.Decide(State([
            // L'impronta: affamatissime, e comunque intoccabili.
            Lane(0, trades: 0, expectedPerMonth: 12m),
            Lane(1, trades: 0, expectedPerMonth: 12m),
            Lane(2, trades: 0, expectedPerMonth: 12m),
            // La flotta.
            Lane(3, trades: 1, expectedPerMonth: 12m),
            Lane(4, trades: 0, expectedPerMonth: 12m, observationDays: 3),   // troppo giovane
            Lane(5, trades: 0, expectedPerMonth: 12m),
            Lane(6, trades: 1, expectedPerMonth: 12m),
            Lane(7, trades: 0, expectedPerMonth: 12m),
            // Vincolate.
            Lane(8, trades: 0, expectedPerMonth: 12m, quarantined: true),
            Lane(9, trades: 0, expectedPerMonth: 12m, campaign: true),
        ]), Options());

        Assert.Equal([3, 5, 6, 7], Retired(plan));
    }

    /// <summary>
    /// <b>L2 — spento lascia il comportamento invariato.</b> Frazione a 0 su cento stati fuzzati:
    /// il piano dev'essere <b>bit-identico</b> a quello prodotto senza il criterio. Senza questo
    /// test «reversibile» sarebbe una promessa; con questo, è una proprietà.
    /// </summary>
    [Fact]
    public void L2_FrazioneAZero_PianoBitIdentico_Su100StatiFuzzati()
    {
        var rnd = new Random(20260819);
        var spento = Options(fraction: 0m);

        for (var i = 0; i < 100; i++)
        {
            var lanes = Enumerable.Range(0, rnd.Next(4, 11)).Select(id => Lane(
                id,
                running: rnd.Next(2) == 0,
                trades: rnd.Next(0, 40),
                observationDays: rnd.Next(0, 60),
                expectedPerMonth: rnd.Next(3) == 0 ? null : (decimal)(rnd.NextDouble() * 40))).ToList();
            var state = State(lanes);

            // Il riferimento: lo stesso stato con il criterio inesistente — cioè con nessuna corsia
            // che dichiari il proprio ritmo, che è la condizione in cui I12 non può esprimersi.
            var senzaRitmo = State(lanes.Select(l => l with { ExpectedTradesPerMonth = null }).ToList());

            Assert.Equal(
                Retired(FleetOrchestrator.Decide(senzaRitmo, spento)),
                Retired(FleetOrchestrator.Decide(state, spento)));
        }
    }

    /// <summary>
    /// <b>L'ignoranza non condanna, e vale per la corsia intera.</b> Ritmo atteso assente (gambe
    /// configurate a mano, corsie nate prima di I11): nessun ritiro, per quanto ferma sia.
    /// </summary>
    [Fact]
    public void SenzaRitmoAtteso_NessunRitiro_PerQuantoFerma()
    {
        var plan = FleetOrchestrator.Decide(
            State([Lane(3, trades: 0, observationDays: 90, expectedPerMonth: null)]), Options());

        Assert.Empty(Retired(plan));
    }

    /// <summary>
    /// <b>Il verso opposto, quello che impedisce al criterio di diventare un tritacarne.</b> Una
    /// corsia da 2 trade/mese con un trade in 14 giorni sta rispettando il proprio contratto: ne
    /// erano attesi ~0,93. Ritirarla perché «ha fatto un solo trade» sarebbe punire la lentezza,
    /// non l'inedia — ed è la lettura sbagliata più facile da fare.
    /// </summary>
    [Fact]
    public void CorsiaLentaMaNellaNorma_NonSiRitira()
    {
        var plan = FleetOrchestrator.Decide(
            State([Lane(3, trades: 1, observationDays: 14, expectedPerMonth: 2m)]), Options());

        Assert.Empty(Retired(plan));
    }

    /// <summary>
    /// Corsia FERMA: non è in inedia, è ferma. Sono due stati diversi e il secondo ha già la sua
    /// strada (è una corsia libera, da assegnare). Ritirare ciò che è già fermo produrrebbe azioni
    /// a vuoto a ogni tick.
    /// </summary>
    [Fact]
    public void CorsiaFerma_NonEInInedia()
    {
        var plan = FleetOrchestrator.Decide(
            State([Lane(3, running: false, trades: 0, expectedPerMonth: 30m)]), Options());

        Assert.Empty(Retired(plan));
    }

    /// <summary>
    /// <b>Un motivo solo per corsia.</b> Una corsia che è insieme perdente E affamata produce UNA
    /// azione, non due: due <c>StopAndFreeLane</c> sulla stessa corsia sarebbero un doppio stop nel
    /// journal e una riga in più da spiegare, per un'unica decisione.
    /// </summary>
    [Fact]
    public void PerdenteEAffamata_UnaSolaAzione_EVinceIlGiudizioSulloSharpe()
    {
        var opt = Options();
        opt.RetireSharpeThreshold = 0.3m;
        opt.RetireMinTrades = 1;
        opt.RetireMinWeeks = 1;

        var plan = FleetOrchestrator.Decide(
            State([Lane(3, sharpe: -1m, trades: 1, observationDays: 21, expectedPerMonth: 30m)]), opt);

        var stops = plan.Actions.OfType<StopAndFreeLane>().ToList();
        Assert.Single(stops);
        Assert.Contains("Forward test perdente", stops[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il motivo si porta i NUMERI: quanti trade sono arrivati, in quanto tempo, contro quanti ne
    /// erano attesi. Un ritiro senza il suo perché è un ordine, non una decisione — e questo va nel
    /// journal, dove qualcuno lo leggerà settimane dopo.
    /// </summary>
    [Fact]
    public void IlMotivoDelRitiroPortaINumeri()
    {
        var plan = FleetOrchestrator.Decide(
            State([Lane(3, trades: 1, observationDays: 14, expectedPerMonth: 30m)]), Options());

        var reason = plan.Actions.OfType<StopAndFreeLane>().Single().Reason;

        Assert.Contains("INEDIA", reason, StringComparison.Ordinal);
        Assert.Contains("ADA/USDT 4h", reason, StringComparison.Ordinal);
        Assert.Contains("1 trade in 14 giorni", reason, StringComparison.Ordinal);

        // [I12-rev] 30/mese x (14 / 30,4375) = 13,8 — con la costante CONDIVISA del mese. Prima
        // erano 14,0 perche' questa meta' del confronto divideva per 30,0 mentre l'atteso nasceva
        // da 30,44: due aritmetiche ai due lati della stessa disuguaglianza.
        // [CI 2026-08-19] Il numero si formatta con la CULTURA DELL'HOST, e l'app non ne fissa
        // nessuna: sulla macchina di sviluppo (it-IT) esce «13,8», sui runner e nei pod Linux
        // «13.8». Inchiodare la forma italiana faceva passare il test qui e fallire in CI —
        // e il test non stava provando la lingua, stava provando il NUMERO.
        // Si costruisce quindi l'atteso con lo STESSO formato del codice sotto test.
        var attesiNelPeriodo = Math.Round(30m * (14m / TradeFrequency.DaysPerMonth), 1);
        Assert.Contains($"contro ~{attesiNelPeriodo:0.#} attesi", reason, StringComparison.Ordinal);
    }

    // --- La diagnosi del silenzio ---------------------------------------------------------------

    /// <summary>
    /// [I8+I12] <b>La spiegazione conta con lo stesso predicato della decisione.</b> Coda piena,
    /// nessuna corsia libera, ma due affamate: il pannello non deve dire «serve un ritiro» — c'è già
    /// un verdetto.
    ///
    /// <para>[I12-rev] Ma non deve nemmeno promettere che il ritiro <i>avverrà</i>: questa funzione è
    /// pura e non conosce né <c>DryRun</c> né <c>ExecutionLanes</c>. La prima versione diceva «il
    /// prossimo tick le ritira e libera il posto» — falso nel default della piattaforma, dove il
    /// dry-run è acceso e l'operatore avrebbe aspettato un ritiro che non sarebbe mai arrivato.
    /// Trovato dalla revisione avversaria: era la classe «controllo che rassicura» dentro la
    /// funzione che serve a NON rassicurare.</para>
    /// </summary>
    [Fact]
    public void Explain_ConAffamate_DichiaraIlVerdettoSenzaPromettereLEsecuzione()
    {
        var state = new FleetState
        {
            Lanes = [Lane(3, trades: 0, expectedPerMonth: 30m), Lane(4, trades: 0, expectedPerMonth: 30m)],
            Candidates =
            [
                new(Guid.NewGuid(), DateTime.UtcNow.AddDays(-2), "pass", 10m, "1h", "c1", false),
                new(Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "pass", 10m, "1h", "c2", false),
            ],
            FootprintLanes = 3, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow,
        };

        var silence = FleetOrchestrator.Explain(state, Options());

        Assert.Equal(2, silence.StarvingLanes);
        Assert.Equal(0, silence.FreeFleetLanes);
        Assert.Contains("INEDIA", silence.Reason, StringComparison.Ordinal);
        // C'e' il verdetto, e si dice DOVE si legge se verra' eseguito...
        Assert.Contains("DryRun", silence.Reason, StringComparison.Ordinal);
        Assert.Contains("ExecutionLanes", silence.Reason, StringComparison.Ordinal);
        // ...ma non si promette che avverra'.
        Assert.DoesNotContain("il prossimo tick le ritira", silence.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il conteggio delle affamate nella spiegazione deve coincidere con i ritiri che la decisione
    /// produce davvero. Due regole per la stessa domanda darebbero un pannello che promette un
    /// ritiro che non arriva — il difetto già pagato in D2 e con <c>SeriesFreshness</c>.
    /// </summary>
    [Fact]
    public void Explain_ContaEsattamenteCioCheDecideRitira()
    {
        var rnd = new Random(20260819);
        var opt = Options();

        for (var i = 0; i < 200; i++)
        {
            var lanes = Enumerable.Range(0, rnd.Next(4, 10)).Select(id => Lane(
                id,
                running: rnd.Next(2) == 0,
                trades: rnd.Next(0, 20),
                observationDays: rnd.Next(0, 40),
                expectedPerMonth: rnd.Next(3) == 0 ? null : (decimal)(rnd.NextDouble() * 40))).ToList();
            var state = State(lanes);

            var ritirate = FleetOrchestrator.Decide(state, opt).Actions
                .OfType<StopAndFreeLane>()
                .Count(a => a.Reason.Contains("INEDIA", StringComparison.Ordinal));

            Assert.Equal(ritirate, FleetOrchestrator.Explain(state, opt).StarvingLanes);
        }
    }

    // --- [I12] Dedup dei grigi per identità ----------------------------------------------------

    private static FleetCandidate GreyRun(string? identity, int ageDays, Guid? id = null)
        => new(id ?? Guid.NewGuid(), DateTime.UtcNow.AddDays(-ageDays), "grey", 5m, "4h",
            $"candidato {identity ?? "senza-identita"}", AlreadyHandled: false, Identity: identity);

    private static FleetState WithCandidates(params FleetCandidate[] candidates)
        => new() { Lanes = [], Candidates = candidates, FootprintLanes = 3, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow };

    /// <summary>
    /// <b>Le quaranta riproposte diventano una.</b> Quaranta run che ritrovano gli stessi parametri
    /// sulla stessa serie sono UNA proposta, e ognuna era una notifica: al 2026-08-18 il journal ne
    /// contava 83 tutte in attesa dello stesso click. Il rumore consuma il budget degli allarmi
    /// veri — lezione già pagata con la staleness a 60s su STX.
    /// </summary>
    [Fact]
    public void QuarantaRiproposteDelloStessoCandidato_DiventanoUna()
    {
        var state = WithCandidates(Enumerable.Range(1, 40)
            .Select(i => GreyRun("Ema|ADA/USDT|4h|p=1", ageDays: i))
            .ToArray());

        var proposte = FleetOrchestrator.Decide(state, Options()).Actions.OfType<ProposeGreyCandidate>().ToList();

        Assert.Single(proposte);
        Assert.Contains("altri 39 run", proposte[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il verso opposto</b>: identità diverse restano proposte diverse. Il dedup deve accorpare
    /// ciò che è uguale, non nascondere ciò che non lo è — un dedup troppo largo farebbe sparire
    /// proposte vere, che è l'errore peggiore dei due.
    /// </summary>
    [Fact]
    public void IdentitaDiverse_RestanoProposteDiverse()
    {
        var state = WithCandidates(
            GreyRun("Ema|ADA/USDT|4h|p=1", 1),
            GreyRun("Ema|ADA/USDT|4h|p=2", 2),
            GreyRun("Rsi|BTC/USDT|1h|p=1", 3));

        Assert.Equal(3, FleetOrchestrator.Decide(state, Options()).Actions.OfType<ProposeGreyCandidate>().Count());
    }

    /// <summary>
    /// <b>Senza identità non si accorpa.</b> Verdetti illeggibili ⇒ nessuna chiave: accorpare per
    /// ignoranza fonderebbe proposte che non si sa se siano la stessa. L'ignoranza non condanna, e
    /// qui «condannare» vuol dire far sparire una proposta.
    /// </summary>
    [Fact]
    public void SenzaIdentita_NessunAccorpamento()
    {
        var state = WithCandidates(GreyRun(null, 1), GreyRun(null, 2), GreyRun("", 3));

        Assert.Equal(3, FleetOrchestrator.Decide(state, Options()).Actions.OfType<ProposeGreyCandidate>().Count());
    }

    /// <summary>
    /// Fra i duplicati sopravvive il run <b>più recente</b>: un grigio è una proposta di forward
    /// test, e il forward test si fa sull'ipotesi vista sui dati più freschi. (È l'opposto della
    /// coda «pass», che è FIFO perché lì il criterio è non far invecchiare nessuno in attesa.)
    /// </summary>
    [Fact]
    public void FraIDuplicati_SopravviveIlPiuRecente()
    {
        var recente = Guid.NewGuid();
        var state = WithCandidates(
            GreyRun("k", ageDays: 9, id: Guid.NewGuid()),
            GreyRun("k", ageDays: 1, id: recente),
            GreyRun("k", ageDays: 5, id: Guid.NewGuid()));

        var proposta = Assert.Single(FleetOrchestrator.Decide(state, Options()).Actions.OfType<ProposeGreyCandidate>());
        Assert.Equal(recente, proposta.RunId);
    }

    /// <summary>
    /// A parità di data vince il RunId minore: il piano è una funzione PURA e deve restare
    /// riproducibile alla riga, altrimenti due tick sullo stesso stato darebbero journal diversi.
    /// </summary>
    [Fact]
    public void APariData_IlPianoRestaDeterministico()
    {
        var stessaData = DateTime.UtcNow.AddDays(-3);
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        FleetState Stato(params FleetCandidate[] c)
            => new() { Lanes = [], Candidates = c, FootprintLanes = 3, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow };

        var uno = new FleetCandidate(a, stessaData, "grey", 5m, "4h", "x", false, Identity: "k");
        var due = new FleetCandidate(b, stessaData, "grey", 5m, "4h", "x", false, Identity: "k");

        Assert.Equal(
            Assert.Single(FleetOrchestrator.Decide(Stato(uno, due), Options()).Actions.OfType<ProposeGreyCandidate>()).RunId,
            Assert.Single(FleetOrchestrator.Decide(Stato(due, uno), Options()).Actions.OfType<ProposeGreyCandidate>()).RunId);
    }

    /// <summary>I candidati già gestiti non si ripropongono, deduplicati o no: il click è già stato dato.</summary>
    [Fact]
    public void GiaGestiti_NonSiRipropongono()
    {
        var state = WithCandidates(
            GreyRun("k", 1) with { AlreadyHandled = true },
            GreyRun("k", 2) with { AlreadyHandled = true });

        Assert.Empty(FleetOrchestrator.Decide(state, Options()).Actions.OfType<ProposeGreyCandidate>());
    }

    /// <summary>
    /// La diagnosi del silenzio conta le proposte DISTINTE, non i run: dire «83 grigi» quando sono
    /// tre cose ripetute descrive un problema che non esiste e nasconde quello vero.
    /// </summary>
    [Fact]
    public void Explain_ContaLeProposteDistinteNonIRun()
    {
        var state = WithCandidates(
            GreyRun("k1", 1), GreyRun("k1", 2), GreyRun("k1", 3),
            GreyRun("k2", 4), GreyRun("k2", 5));

        Assert.Equal(2, FleetOrchestrator.Explain(state, Options()).GreyCandidates);
    }

    // --- [AF2b] Il braccio esecutivo: quando NON esegue -----------------------------------------

    private static FleetOptions Armed(params int[] lanes)
        => new() { DryRun = false, ExecutionLanes = [.. lanes], MaxExecutionsPerTick = 1 };

    /// <summary>
    /// Il caso che esegue: braccio presente, dry-run spento, corsia in lista, budget disponibile.
    /// Serve come termine di paragone — senza, tutti i test qui sotto sarebbero verdi anche se il
    /// braccio non funzionasse mai, che è la definizione di una verifica che non può fallire.
    /// </summary>
    [Fact]
    public void Braccio_ConTutteLeCondizioni_Esegue()
        => Assert.Null(FleetOrchestratorWorker.WhyNotExecuted(Armed(5), laneId: 5, budgetLeft: 1));

    /// <summary>
    /// <b>Le quattro strade per NON eseguire</b>, ognuna col suo motivo in chiaro. La decisione e la
    /// spiegazione escono dalla stessa valutazione: tenerle separate — la condizione nell'if, il
    /// motivo ricalcolato nell'else — darebbe un journal che spiega un rifiuto diverso da quello
    /// avvenuto, nel posto dove qualcuno andrà a cercare cosa è successo.
    /// </summary>
    [Fact]
    public void Braccio_LeQuattroStradePerNonEseguire()
    {
        // 1. dry-run: il default della piattaforma, e resta tale finché non lo si spegne apposta.
        var dryRun = new FleetOptions { DryRun = true, ExecutionLanes = [5] };
        Assert.Equal("dry-run", FleetOrchestratorWorker.WhyNotExecuted(dryRun, 5, 1));

        // 2. dry-run spento ma lista VUOTA: il permesso è per corsia, non globale.
        var senzaLista = new FleetOptions { DryRun = false, ExecutionLanes = [] };
        Assert.Equal("nessuna corsia autorizzata", FleetOrchestratorWorker.WhyNotExecuted(senzaLista, 5, 1));

        // 3. lista non vuota ma senza QUESTA corsia: il collaudo una-alla-volta si regge su questo.
        Assert.Equal("corsia 6 non autorizzata", FleetOrchestratorWorker.WhyNotExecuted(Armed(5), 6, 1));

        // 4. budget del tick esaurito: due condanne nello stesso giro, una sola azione.
        Assert.Equal("budget di esecuzione del tick esaurito", FleetOrchestratorWorker.WhyNotExecuted(Armed(5), 5, 0));
    }

    /// <summary>
    /// <b>Il default della piattaforma non esegue nulla.</b> <c>FleetOptions</c> appena costruito:
    /// dry-run acceso e lista vuota. Chi aggiungesse un default «comodo» qui accenderebbe
    /// l'esecuzione automatica su ogni installazione esistente al primo aggiornamento.
    /// </summary>
    [Fact]
    public void Braccio_IlDefaultDiFabbrica_NonEsegue()
    {
        var fabbrica = new FleetOptions();

        Assert.True(fabbrica.DryRun);
        Assert.Empty(fabbrica.ExecutionLanes);
        Assert.False(FleetOrchestratorWorker.CanExecute(fabbrica));
        Assert.NotNull(FleetOrchestratorWorker.WhyNotExecuted(fabbrica, 5, 1));
    }

    /// <summary>
    /// <b>Entrambi i bracci esistono</b> — nell'ordine deciso dal proprietario: prima il ritiro
    /// (2026-08-19: fermare libera una corsia e si disfa con un click), poi l'avvio
    /// (J13, 2026-08-25: candidato SINGOLO, stesso deployer del click F5, e per i grigi dietro il
    /// flag J14 che rovescia F5 per decisione esplicita). L'esecuzione resta comunque gattata da
    /// dry-run, ExecutionLanes e budget: le costanti dicono cosa il codice SA FARE, i flag cosa è
    /// stato CHIESTO — e i default di fabbrica non eseguono nulla (test sopra).
    /// </summary>
    [Fact]
    public void Braccio_RitiroEAvvio_EntrambiImplementati_MaSpentiDiFabbrica()
    {
        Assert.True(FleetOrchestratorWorker.RetirementArmImplemented);
        Assert.True(FleetOrchestratorWorker.AssignmentArmImplemented);
        // I default di fabbrica NON eseguono: dry-run acceso, nessuna corsia autorizzata, J14 spento.
        var fabbrica = new FleetOptions();
        Assert.NotNull(FleetOrchestratorWorker.WhyNotExecutedAssignment(fabbrica, 5, 1, hasKey: true, hasDeployer: true, isGrey: false));
        Assert.False(fabbrica.GreyAutoDeploy);
    }

    /// <summary>
    /// [revisione algoritmi 2026-08-20] <b>Il dedup vale anche FRA un tick e l'altro.</b>
    ///
    /// <para>La prima versione accorpava le proposte con la stessa identità dentro un tick, e
    /// bastava a non mandare quaranta notifiche insieme. Ma «già gestito» era per RUN: il giorno dopo
    /// un run NUOVO con la stessa coppia strategia/serie/parametri tornava a proporsi come se fosse
    /// la prima volta. È il meccanismo che aveva prodotto le <b>91 proposte per sei cose distinte</b>
    /// misurate il 2026-08-19 — quello dentro il tick non lo tocca.</para>
    ///
    /// <para>Qui l'identità eredita lo stato: se anche uno dei run che la portano è già stato
    /// gestito, lo sono tutti.</para>
    /// </summary>
    [Fact]
    public void UnIdentitaGiaGestita_NonSiRipropone_NemmenoDaUnRunNuovo()
    {
        var gestito = GreyRun("Ema|ADA/USDT|4h|p=1", ageDays: 5) with { AlreadyHandled = true };
        var nuovoStessaCosa = GreyRun("Ema|ADA/USDT|4h|p=1", ageDays: 1);   // run diverso, identità uguale
        var nuovoAltraCosa = GreyRun("Rsi|BTC/USDT|1h|p=1", ageDays: 1);

        // Il lettore marca per identità; qui si simula il suo esito e si verifica la decisione.
        var comeIlLettore = new[] { gestito, nuovoStessaCosa with { AlreadyHandled = true }, nuovoAltraCosa };
        var proposte = FleetOrchestrator.Decide(WithCandidates(comeIlLettore), Options())
            .Actions.OfType<ProposeGreyCandidate>().ToList();

        var sola = Assert.Single(proposte);
        Assert.Equal(nuovoAltraCosa.RunId, sola.RunId);
    }
}
