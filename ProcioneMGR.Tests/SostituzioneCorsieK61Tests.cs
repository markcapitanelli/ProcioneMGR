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
}
