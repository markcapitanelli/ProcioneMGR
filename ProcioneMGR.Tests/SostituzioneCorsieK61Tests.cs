using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K61, 2026-09-04, richiesta del proprietario] <b>La sostituzione di una corsia INERTE.</b>
///
/// <para>Il fatto che l'ha motivata, misurato sul database vivo il 2026-09-04:
/// <c>Fleet:GreyAutoDeploy</c> è ACCESO, 19 candidati grigi sono schierabili, e le cinque corsie di
/// flotta (3-7) sono tutte occupate senza che <b>nessuna</b> possa liberarsi — il ritiro per Sharpe
/// pretende 20 trade (le corsie ne hanno 0, 0, 1, 3, 4) e quello per inedia pretende un ritmo atteso
/// dichiarato, che sulla corsia 5 è <c>null</c>. Il braccio automatico girava a vuoto scrivendo
/// «nessuna corsia di flotta libera».</para>
///
/// <para>Le prove qui dentro coprono i due lati dello standard: che la regola scatti dove deve
/// (corsia muta, rimpiazzo stabile) e soprattutto che <b>NON</b> scatti dove non deve — la corsia
/// lenta che rispetta il proprio ritmo, quella con una posizione aperta, quella appena schierata,
/// il rimpiazzo non giudicabile, l'interruttore spento.</para>
/// </summary>
public sealed class SostituzioneCorsieK61Tests
{
    private static readonly DateTime Adesso = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    private static FleetLaneState Corsia(
        int id = 5, bool running = true, int osservazioneGiorni = 30, double? silenzioGiorni = 20,
        decimal? attesiAlMese = null, int posizioniAperte = 0, string mode = "Paper",
        bool quarantined = false, bool campaign = false, bool emergency = false, bool? grigia = true)
        => new(id, running, mode, IsConfigured: true, quarantined, campaign, emergency,
            RealizedSharpe: 0.5m, TradeCount: 3, Observation: TimeSpan.FromDays(osservazioneGiorni),
            Symbol: "UNI/USDT", Timeframe: "4h",
            ExpectedTradesPerMonth: attesiAlMese, GreySourced: grigia, Unreadable: false,
            RealizedSharpePerTrade: 0.1m,
            LastTradeUtc: silenzioGiorni is double g ? Adesso.AddDays(-g) : null,
            OpenPositions: posizioniAperte);

    private static FleetCandidate Rimpiazzo(
        string identita = "MacdTrend AAVE/USDT 4h #f523b2ee", decimal mediana = 3.98m, int misure = 5,
        string band = "grey", decimal tpm = 11.1m, bool handled = false)
        => new(Guid.NewGuid(), Adesso.AddDays(-1), band, tpm, "4h", "MacdTrend AAVE/USDT 4h",
            AlreadyHandled: handled, AlreadyProposed: false, Identity: identita,
            StabilityMedian: mediana, StabilityMeasures: misure, StabilitySpread: 0.21m);

    private static FleetOptions Opzioni(bool accesa = true) => new()
    {
        ReplaceIdleLanes = accesa,
        ReplaceIdleDays = 10,
        ReplaceIdleExpectedMultiple = 2.0m,
        ReplaceMinLaneDays = 7,
        ReplaceMinCandidateMedian = 1.0m,
        ReplaceMinCandidateMeasures = 5,
        MaxReplacementsPerTick = 1,
        MinTradesPerMonth = 0.5m,
        // Le corsie di prova non devono cadere nei criteri di RITIRO, che hanno la precedenza.
        RetireMinTrades = 20,
        RetireMinWeeks = 3,
        StarvationMinDays = 10,
    };

    private static FleetState Stato(IReadOnlyList<FleetLaneState> corsie, IReadOnlyList<FleetCandidate> candidati)
        => new()
        {
            Lanes = corsie,
            Candidates = candidati,
            FootprintLanes = 3,
            ExposureGuardEnabled = true,
            NowUtc = Adesso,
        };

    // ---------------------------------------------------------------- il predicato, riga per riga

    /// <summary>
    /// La tabella di verità dell'inerzia. Ogni riga è un caso che deve o non deve passare, e le
    /// righe negative sono la parte che conta: una regola che ritira sempre è inutile quanto una
    /// che non ritira mai.
    /// </summary>
    [Theory]
    // caso normale: muta da 20 giorni, nessuna posizione, ben oltre la residenza minima
    [InlineData(30, 20, null, 0, true, "corsia muta da venti giorni senza ritmo dichiarato")]
    // il silenzio non basta: sotto il pavimento di residenza non si tocca
    [InlineData(3, 20, null, 0, false, "appena schierata: non ha ancora avuto occasione di operare")]
    // silenzio sotto la soglia
    [InlineData(30, 4, null, 0, false, "muta da quattro giorni: sotto il pavimento di dieci")]
    // POSIZIONE APERTA: mai, per nessun motivo (danno K36)
    [InlineData(30, 20, null, 1, false, "ha una posizione aperta: sta operando, e fermarla la cancellerebbe")]
    // corsia lenta che rispetta il proprio ritmo: 1,65 trade/mese = uno ogni 18,4 giorni, soglia 36,9
    [InlineData(60, 20, 1.65, 0, false, "corsia lenta dentro il proprio ritmo dichiarato")]
    // la stessa corsia lenta, ma muta da 40 giorni: oltre anche la sua soglia scalata
    [InlineData(60, 40, 1.65, 0, true, "corsia lenta muta oltre il doppio del suo intervallo atteso")]
    // corsia veloce: 11,11 trade/mese = uno ogni 2,7 giorni, soglia resta il pavimento di 10
    [InlineData(30, 12, 11.11, 0, true, "corsia veloce muta da dodici giorni")]
    [InlineData(30, 8, 11.11, 0, false, "corsia veloce muta da otto giorni: sotto il pavimento")]
    public void LaTABELLAdellINERZIA(
        int osservazione, double silenzio, double? attesi, int posizioni, bool atteso, string caso)
    {
        var corsia = Corsia(
            osservazioneGiorni: osservazione, silenzioGiorni: silenzio,
            attesiAlMese: attesi is double a ? (decimal)a : null, posizioniAperte: posizioni);

        Assert.Equal(atteso, FleetOrchestrator.IsIdle(corsia, Opzioni(), Adesso));
    }

    /// <summary>
    /// Una corsia che non ha MAI operato: il silenzio è tutta la sua osservazione. È il caso della
    /// corsia 4 e della corsia 7 al 2026-09-04, e non deve dipendere da un timestamp che non esiste.
    /// </summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(3, false)]
    public void MAIoperato_ilSILENZIOeTUTTAlOSSERVAZIONE(int osservazione, bool atteso)
        => Assert.Equal(atteso,
            FleetOrchestrator.IsIdle(Corsia(osservazioneGiorni: osservazione, silenzioGiorni: null), Opzioni(), Adesso));

    /// <summary>
    /// I due orologi non si sommano. Una corsia spenta per giorni non deve accumulare silenzio
    /// mentre nessuno la fa operare: si prende il minore fra il tempo di parete dall'ultima
    /// operazione e l'osservazione cumulata.
    /// </summary>
    [Fact]
    public void IlSILENZIOnonSUPERAlOSSERVAZIONEcumulata()
    {
        // Ultima operazione 40 giorni fa, ma la corsia è stata osservata solo 12 giorni: per 28
        // giorni era ferma, e quel tempo non è silenzio suo.
        var corsia = Corsia(osservazioneGiorni: 12, silenzioGiorni: 40);
        Assert.Equal(TimeSpan.FromDays(12), FleetOrchestrator.Silenzio(corsia, Adesso));
    }

    /// <summary>Un'ora di orologio storto (ultima operazione nel futuro) non produce silenzio negativo.</summary>
    [Fact]
    public void UnULTIMAoperazioneNELfuturo_nonPRODUCEsilenzioNEGATIVO()
        => Assert.Equal(TimeSpan.Zero, FleetOrchestrator.Silenzio(Corsia(silenzioGiorni: -2), Adesso));

    /// <summary>
    /// Un ritmo atteso ASSURDO non deve far esplodere una funzione pura chiamata a ogni tick:
    /// un'eccezione qui fermerebbe l'intera decisione della flotta, ritiri compresi, per un campo di
    /// configurazione scritto male. La soglia si limita, non trabocca.
    /// </summary>
    [Theory]
    [InlineData(1e-28)]
    [InlineData(1e-10)]
    [InlineData(0.0001)]
    [InlineData(1e+28)]
    public void UnRITMOassurdo_nonFAesplodereLaSOGLIA(double attesi)
    {
        var soglia = FleetOrchestrator.SogliaSilenzio(Corsia(attesiAlMese: (decimal)attesi), Opzioni());

        Assert.True(soglia >= TimeSpan.FromDays(10), "la soglia non scende mai sotto il pavimento");
        Assert.True(soglia <= TimeSpan.FromDays(FleetOrchestrator.MaxSogliaSilenzioGiorni),
            "la soglia si limita invece di traboccare");
    }

    /// <summary>Ritmo atteso zero o negativo: non è un ritmo, e vince il pavimento.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void UnRITMOnonPOSITIVO_valeCOMEnonDICHIARATO(double attesi)
        => Assert.Equal(TimeSpan.FromDays(10),
            FleetOrchestrator.SogliaSilenzio(Corsia(attesiAlMese: (decimal)attesi), Opzioni()));

    /// <summary>Un multiplo a zero o negativo spegne la scala, non la ribalta.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void UnMULTIPLOnonPOSITIVO_spegneLaSCALA(double multiplo)
    {
        var opt = Opzioni();
        opt.ReplaceIdleExpectedMultiple = (decimal)multiplo;

        Assert.Equal(TimeSpan.FromDays(10), FleetOrchestrator.SogliaSilenzio(Corsia(attesiAlMese: 1.65m), opt));
    }

    /// <summary>La soglia scalata sul ritmo atteso, con i numeri veri delle corsie del 2026-09-04.</summary>
    [Theory]
    [InlineData(null, 10.0)]     // niente ritmo dichiarato: il solo pavimento
    [InlineData(1.65, 36.9)]     // corsia 4 XLM/USDT 4h: una operazione ogni 18,4 giorni
    [InlineData(3.29, 18.5)]     // corsia 7 TRX/USDT 4h
    [InlineData(11.11, 10.0)]    // corsia 3 AAVE/USDT 4h: 2,7 giorni × 2 < pavimento, vince il pavimento
    public void LaSOGLIAsiSCALAsulRITMOatteso(double? attesi, double sogliaAttesaGiorni)
    {
        var soglia = FleetOrchestrator.SogliaSilenzio(
            Corsia(attesiAlMese: attesi is double a ? (decimal)a : null), Opzioni());
        Assert.Equal(sogliaAttesaGiorni, soglia.TotalDays, precision: 1);
    }

    // ---------------------------------------------------------------- il piano

    /// <summary>IL NULLO: a interruttore spento la sostituzione non esiste, per quanto inerte sia tutto.</summary>
    [Fact]
    public void AinterruttoreSPENTO_nessunaSOSTITUZIONE()
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia()], [Rimpiazzo()]), Opzioni(accesa: false));

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>Il caso che il proprietario ha chiesto: corsia muta, rimpiazzo stabile, una sola azione.</summary>
    [Fact]
    public void CorsiaINERTEeRIMPIAZZOstabile_UNAsostituzione()
    {
        var piano = FleetOrchestrator.Decide(Stato([Corsia()], [Rimpiazzo()]), Opzioni());

        var azione = Assert.Single(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Equal(5, azione.LaneId);
        Assert.Equal("MacdTrend AAVE/USDT 4h #f523b2ee", azione.CandidateKey);
        Assert.Contains("INERTE", azione.Reason, StringComparison.Ordinal);
        Assert.Contains("mediana K57", azione.Reason, StringComparison.Ordinal);

        // Una sola azione su quella corsia: mai anche un ritiro.
        Assert.Empty(piano.Actions.OfType<StopAndFreeLane>().Where(a => a.LaneId == 5));
    }

    /// <summary>
    /// Dove il ritiro parla, la sostituzione tace. Una corsia in inedia è già condannata: emettere
    /// anche una sostituzione darebbe due azioni sulla stessa corsia, e il worker ne eseguirebbe una
    /// sola senza che nessuno sappia quale.
    /// </summary>
    [Fact]
    public void SeIlRITIROhaGIAcondannato_laSOSTITUZIONEtace()
    {
        // Inedia: ritmo dichiarato 10/mese, 3 trade in 30 giorni osservati = 30% del promesso...
        // con StarvationFraction 0,2 non basta; si abbassa a 0,5 per far mordere l'inedia.
        var opt = Opzioni();
        opt.StarvationFraction = 0.5m;

        var corsia = Corsia(attesiAlMese: 10m, osservazioneGiorni: 30, silenzioGiorni: 20);
        var piano = FleetOrchestrator.Decide(Stato([corsia], [Rimpiazzo()]), opt);

        Assert.Single(piano.Actions.OfType<StopAndFreeLane>());
        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>
    /// Con una corsia LIBERA la sostituzione non anticipa il braccio ordinario: riempire un posto
    /// vuoto non costa niente a nessuno, fermare una corsia sì.
    /// </summary>
    [Fact]
    public void ConUNAcorsiaLIBERA_nonSIsostituisce()
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia(id: 5), Corsia(id: 6, running: false)], [Rimpiazzo()]), Opzioni());

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Contains(piano.Actions.OfType<FleetNoOp>(),
            n => n.Reason.Contains("ultima risorsa", StringComparison.Ordinal));
    }

    /// <summary>
    /// Il rimpiazzo non giudicabile non entra, e il ramo lo DICE: un ramo che tace per cinque ragioni
    /// diverse è indistinguibile da un ramo spento.
    /// </summary>
    [Theory]
    [InlineData(2, 3.98, "poche rimisurazioni")]
    [InlineData(9, 0.40, "mediana sotto la soglia")]
    public void UnRIMPIAZZOnonAMMESSO_nonENTRAeSIdichiara(int misure, double mediana, string caso)
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia()], [Rimpiazzo(mediana: (decimal)mediana, misure: misure)]), Opzioni());

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Contains(piano.Actions.OfType<FleetNoOp>(),
            n => n.Reason.Contains("NESSUN rimpiazzo ammesso", StringComparison.Ordinal));
    }

    /// <summary>Un candidato senza identità non è schierabile: il braccio esegue per chiave, non per run.</summary>
    [Fact]
    public void UnRIMPIAZZOsenzaIDENTITA_nonENTRA()
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia()], [Rimpiazzo() with { Identity = null }]), Opzioni());

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>
    /// La stessa identità ritrovata da più run è UNA proposta, non tre: altrimenti la sostituzione
    /// riempirebbe tre corsie con la stessa ipotesi, che è il difetto K33 in un posto nuovo.
    /// </summary>
    [Fact]
    public void LaSTESSAidentitaDAaltriRUN_eUNArigaSOLA()
    {
        var uguali = new[]
        {
            Rimpiazzo(), Rimpiazzo(), Rimpiazzo(),
        };

        var ammessi = FleetOrchestrator.RimpiazziAmmessi(Stato([Corsia()], uguali), Opzioni());
        Assert.Single(ammessi);
    }

    /// <summary>Il merito, non la data: fra due candidati vince la mediana più alta anche se è il più recente.</summary>
    [Fact]
    public void SiSCEGLIEperMERITO_nonPERdata()
    {
        var vecchioMediocre = new FleetCandidate(
            Guid.NewGuid(), Adesso.AddDays(-15), "grey", 5m, "4h", "EventTrigger GRT/USDT 4h",
            AlreadyHandled: false, AlreadyProposed: false, Identity: "EventTrigger GRT/USDT 4h #69438482",
            StabilityMedian: 2.79m, StabilityMeasures: 20, StabilitySpread: 3.26m);

        var ammessi = FleetOrchestrator.RimpiazziAmmessi(
            Stato([Corsia()], [vecchioMediocre, Rimpiazzo()]), Opzioni());

        Assert.Equal("MacdTrend AAVE/USDT 4h #f523b2ee", ammessi[0].Identity);
    }

    /// <summary>
    /// Il tetto grigio vale anche qui: mettere un grigio al posto di una gamba dichiarata
    /// sopravvissuta allarga l'esposizione della fascia grigia, e va contato.
    /// </summary>
    [Fact]
    public void IlTETTOgrigioVALEancheNELLAsostituzione()
    {
        var opt = Opzioni();
        opt.MaxGreyLanes = 0;

        // La corsia da sostituire NON è grigia: il rimpiazzo grigio porterebbe il conteggio a 1.
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia(grigia: false)], [Rimpiazzo()]), opt);

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Contains(piano.Actions.OfType<FleetNoOp>(),
            n => n.Reason.Contains("tetto di", StringComparison.Ordinal));
    }

    /// <summary>Il budget: una sola sostituzione per giro, anche con due corsie inerti e due rimpiazzi.</summary>
    [Fact]
    public void UNAsolaSOSTITUZIONEperGIRO()
    {
        var piano = FleetOrchestrator.Decide(
            Stato(
                [Corsia(id: 5), Corsia(id: 6, silenzioGiorni: 25)],
                [Rimpiazzo(), Rimpiazzo(identita: "Supertrend TRX/USDT 4h #ba1beca0", mediana: 2.73m, misure: 6)]),
            Opzioni());

        Assert.Single(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>Si comincia dalla più muta: fra due inerti la prima è quella che tace da più tempo.</summary>
    [Fact]
    public void SiCOMINCIAdallaPIUmuta()
    {
        var piano = FleetOrchestrator.Decide(
            Stato(
                [Corsia(id: 5, silenzioGiorni: 12), Corsia(id: 6, silenzioGiorni: 40)],
                [Rimpiazzo()]),
            Opzioni());

        Assert.Equal(6, Assert.Single(piano.Actions.OfType<ReplaceLaneOccupant>()).LaneId);
    }

    /// <summary>
    /// Le corsie intoccabili restano intoccabili: impronta, quarantena, campagna, emergency,
    /// Live e Testnet. È lo stesso recinto del ritiro, e la sostituzione non ne apre una breccia.
    /// </summary>
    [Theory]
    [InlineData(2, "Paper", false, false, false)]      // impronta storica
    [InlineData(5, "Live", false, false, false)]
    [InlineData(5, "Testnet", false, false, false)]
    [InlineData(5, "Paper", true, false, false)]       // quarantena
    [InlineData(5, "Paper", false, true, false)]       // campagna
    [InlineData(5, "Paper", false, false, true)]       // emergency / illeggibile
    public void LeCORSIEintoccabili_restanoINTOCCABILI(
        int id, string mode, bool quarantena, bool campagna, bool emergency)
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia(id: id, mode: mode, quarantined: quarantena, campaign: campagna, emergency: emergency)],
                  [Rimpiazzo()]),
            Opzioni());

        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>
    /// Lo strumento accanto al cancello: il pannello conta le corsie inerti e i rimpiazzi pronti
    /// ANCHE a interruttore spento, perché è guardando quei numeri che si decide se accenderlo.
    /// </summary>
    [Fact]
    public void IlCONTEGGIOdelCANCELLO_esisteANCHEaINTERRUTTOREspento()
    {
        var silenzio = FleetOrchestrator.Explain(
            Stato([Corsia()], [Rimpiazzo()]), Opzioni(accesa: false));

        Assert.Equal(1, silenzio.IdleLanes);
        Assert.Equal(1, silenzio.ReplacementsReady);
    }

    // ------------------------------------------- correzioni dalla revisione avversariale 2026-09-04

    /// <summary>
    /// Il ritmo atteso azzerato perché <b>non confrontabile</b> (configurazione e motore in
    /// disaccordo sulle gambe) non deve rendere la corsia PIÙ facile da sostituire.
    ///
    /// <para>Senza la distinzione, la corsia 4 — 1,65 trade/mese attesi, soglia propria 36,9 giorni —
    /// dopo una modifica di configurazione non riavviata ricadrebbe sul pavimento secco di 10 giorni
    /// e verrebbe sostituita mentre rispetta il proprio ritmo. Un'ammissione di ignoranza non può
    /// diventare un'aggravante.</para>
    /// </summary>
    [Fact]
    public void UnATTESOnonCONFRONTABILE_faASTENERE_nonCONDANNA()
    {
        var divergente = Corsia(osservazioneGiorni: 60, silenzioGiorni: 12, attesiAlMese: null) with
        {
            ExpectedDiverged = true,
        };

        Assert.False(FleetOrchestrator.IsIdle(divergente, Opzioni(), Adesso));

        // Il nullo del nullo: senza divergenza, lo stesso stato È inerte (il pavimento morde).
        Assert.True(FleetOrchestrator.IsIdle(divergente with { ExpectedDiverged = false }, Opzioni(), Adesso));
    }

    /// <summary>
    /// Il secondo mezzo del predicato K57: un'ipotesi col ventaglio più largo della mediana è
    /// INSTABILE, e la lista del clic umano la marca «⚠ INSTABILE». Il braccio automatico non può
    /// ammetterla e chiamarla stabile.
    /// </summary>
    [Theory]
    [InlineData(2.79, 3.26, false, "EventTrigger GRT: ventaglio maggiore della mediana")]
    [InlineData(3.98, 0.21, true, "MacdTrend AAVE: ventaglio stretto")]
    [InlineData(2.00, 2.00, true, "ventaglio pari alla mediana: al limite, ammesso")]
    public void UnIPOTESIinstabileNONeUNrimpiazzo(double mediana, double ventaglio, bool ammesso, string caso)
    {
        var candidato = Rimpiazzo(mediana: (decimal)mediana, misure: 20) with
        {
            StabilitySpread = (decimal)ventaglio,
        };

        var ammessi = FleetOrchestrator.RimpiazziAmmessi(Stato([Corsia()], [candidato]), Opzioni());
        Assert.Equal(ammesso, ammessi.Count == 1);
    }

    /// <summary>
    /// Il tetto grigio conta anche i grigi assegnati NELLO STESSO GIRO. La fotografia non li vede
    /// ancora in corsa, e senza la somma il tetto verrebbe superato di una corsia — proprio nel giro
    /// in cui il ramo grigio consuma l'ultima corsia libera, che è la condizione che apre la
    /// sostituzione.
    /// </summary>
    [Fact]
    public void IlTETTOgrigioCONTAancheIgrigiDIquestoGIRO()
    {
        var opt = Opzioni();
        opt.GreyAutoDeploy = true;
        opt.MaxGreyLanes = 3;
        opt.MaxAssignmentsPerTick = 2;

        var stato = Stato(
            [
                Corsia(id: 3, silenzioGiorni: 0, osservazioneGiorni: 1),   // grigia in corsa, non inerte
                Corsia(id: 4, silenzioGiorni: 0, osservazioneGiorni: 1),   // grigia in corsa, non inerte
                Corsia(id: 5, grigia: false),                              // NON grigia, inerte
                Corsia(id: 6, running: false),                             // libera
            ],
            [
                Rimpiazzo(identita: "A #1"),
                Rimpiazzo(identita: "B #2", mediana: 3.5m, misure: 8),
            ]);

        var piano = FleetOrchestrator.Decide(stato, opt);

        // Il grigio va sulla corsia libera; la sostituzione porterebbe le grigie a 4 su un tetto di 3.
        Assert.Single(piano.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
    }

    /// <summary>Il candidato scelto per la sostituzione non viene ANCHE proposto al clic umano.</summary>
    [Fact]
    public void IlRIMPIAZZOsceltoNONeANCHEunaPROPOSTA()
    {
        var candidato = Rimpiazzo();
        var piano = FleetOrchestrator.Decide(Stato([Corsia()], [candidato]), Opzioni());

        Assert.Single(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Empty(piano.Actions.OfType<ProposeGreyCandidate>().Where(p => p.RunId == candidato.RunId));
    }

    /// <summary>
    /// La banda viaggia sull'azione: un rimpiazzo «pass» va schierato col percorso dei sopravvissuti,
    /// altrimenti il deployer lo rifiuta a corsia già ferma.
    /// </summary>
    [Theory]
    [InlineData("grey", true)]
    [InlineData("pass", false)]
    public void LaBANDAviaggiaSULLazione(string band, bool grigioAtteso)
    {
        var piano = FleetOrchestrator.Decide(
            Stato([Corsia()], [Rimpiazzo(band: band)]), Opzioni());

        Assert.Equal(grigioAtteso, Assert.Single(piano.Actions.OfType<ReplaceLaneOccupant>()).IsGrey);
    }

    /// <summary>
    /// Il conteggio del pannello e la decisione devono contare lo STESSO insieme: una corsia che il
    /// ritiro condanna per Sharpe non è «inerte sostituibile», e mostrarla esporrebbe un cancello
    /// più largo di quello vero.
    /// </summary>
    [Fact]
    public void ExplainNONcontaLEcorsieCHEilRITIROcondanna()
    {
        var opt = Opzioni();
        opt.RetireMinTrades = 1;
        opt.RetireMinWeeks = 1;
        opt.RetireSharpeThreshold = 0.5m;

        // Storia sufficiente e Sharpe per trade sotto soglia: il ritiro la condanna.
        var perdente = Corsia(osservazioneGiorni: 30, silenzioGiorni: 20) with
        {
            TradeCount = 25,
            RealizedSharpePerTrade = 0.1m,
        };

        var piano = FleetOrchestrator.Decide(Stato([perdente], [Rimpiazzo()]), opt);
        var silenzio = FleetOrchestrator.Explain(Stato([perdente], [Rimpiazzo()]), opt);

        Assert.Single(piano.Actions.OfType<StopAndFreeLane>());
        Assert.Empty(piano.Actions.OfType<ReplaceLaneOccupant>());
        Assert.Equal(0, silenzio.IdleLanes);
    }

    /// <summary>
    /// [K61b] Il braccio automatico sceglie il grigio per MERITO quando glielo si chiede. Prova
    /// deterministica: due candidati, quello mediocre è più vecchio — a interruttore spento vince
    /// la data, acceso vince la mediana.
    /// </summary>
    [Theory]
    [InlineData(false, "EventTrigger GRT/USDT 4h #69438482")]
    [InlineData(true, "MacdTrend AAVE/USDT 4h #f523b2ee")]
    public void K61b_perDATAoPERmerito(bool perMerito, string identitaAttesa)
    {
        var opt = Opzioni(accesa: false);
        opt.GreyAutoDeploy = true;
        opt.PreferStableGrey = perMerito;

        var vecchioMediocre = new FleetCandidate(
            Guid.NewGuid(), Adesso.AddDays(-15), "grey", 5m, "4h", "EventTrigger GRT/USDT 4h",
            AlreadyHandled: false, AlreadyProposed: false, Identity: "EventTrigger GRT/USDT 4h #69438482",
            StabilityMedian: 2.79m, StabilityMeasures: 20, StabilitySpread: 0.5m);

        var piano = FleetOrchestrator.Decide(
            Stato([Corsia(id: 5, running: false)], [vecchioMediocre, Rimpiazzo()]), opt);

        var schierato = Assert.Single(piano.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Equal(identitaAttesa, schierato.CandidateKey);
    }
}
