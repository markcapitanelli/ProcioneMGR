using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K22 + K27, PRD autonomia-piena — Fase 2, 2026-09-01] <b>Due difetti che si sono presi per mano.</b>
///
/// <para>Il 2026-08-31 <c>GridMeanReversion DOGE/USDT 15m</c>, stessi parametri, stesso
/// <c>ExpectedSharpe</c> a ventotto cifre, stesso <c>ExpectedTradesSource</c>, è finita sulle corsie
/// <b>4 e 6</b>: 20.000 USDT nominali su una stima da 14 trade di holdout, e due slot del tetto
/// grigio consumati da una prova sola. Per riuscirci le sono servite due cose insieme:</para>
///
/// <list type="number">
/// <item><b>Nessuno confrontava una corsia con le altre.</b> L'unico controllo di duplicazione
/// esistente (<c>EnsemblePageService.AddFromGreyAsync</c>) guarda DENTRO la stessa corsia, e
/// <c>StrategyId</c> è un <c>Guid.NewGuid()</c> coniato a ogni costruzione della gamba: due
/// schieramenti della stessa ipotesi hanno due identità diverse.</item>
/// <item><b>Il tetto grigio perdeva pezzi del proprio denominatore.</b> <c>greyRunning</c> contava
/// dentro <c>FleetLanes</c>, che esclude le corsie intoccabili — e <c>FleetStateReader</c> marca
/// <c>EmergencyStopped</c> ogni corsia di cui non riesce a leggere lo stato. Una corsia illeggibile
/// usciva dal conteggio, e il tetto si allargava da solo. È l'unica combinazione compatibile con i
/// due fatti persistiti di quella sera: il ledger della corsia 4 azzerato in quel tick (quindi il
/// lettore l'aveva raggiunta) e lo schieramento finito sulla 6 (quindi la 4 non era né contata né
/// libera).</item>
/// </list>
///
/// <para>I due test sotto difendono le due metà. Entrambi hanno il loro <b>nullo</b>, come pretende
/// il livello 2 dello standard di verifica: una guardia che blocca sempre e un tetto che conta
/// sempre passerebbero i casi positivi ed sarebbero sbagliati.</para>
/// </summary>
public class FleetIpotesiDoppiaK22Tests
{
    private static readonly Dictionary<string, decimal> Grid =
        new() { ["Direction"] = 1m, ["EntryRungs"] = 1m, ["StepPercent"] = 2m, ["AnchorPeriod"] = 20m };

    private static string Chiave(string strategia = "GridMeanReversion", string simbolo = "DOGE/USDT",
        string timeframe = "15m", Dictionary<string, decimal>? par = null)
        => ProcioneMGR.Services.Pipeline.PipelineCandidateKey.Build(strategia, simbolo, timeframe, par ?? Grid);

    private static LaneSummary Corsia(int id, string? chiave, bool inCorsa = true) => new(
        id, "DOGE/USDT", "15m", "Paper", inCorsa,
        ExpectedTradesPerMonth: 3.80m,
        ActiveStrategyIds: null,
        HasGreyLegs: true,
        ActiveCandidateKeys: chiave is null ? null : [chiave]);

    // ---------------------------------------------------------------- K22: la guardia

    [Fact]
    public void ReplicaESATTA_suUnAltraCorsia_vieneRIFIUTATA()
    {
        var chiave = Chiave();
        var esito = HypothesisGuard.Check([Corsia(4, chiave), Corsia(6, null)], targetLane: 6, chiave);

        Assert.True(esito.Blocked);
        Assert.Contains("corsia 4", esito.Reason);
        Assert.Contains("ESATTAMENTE", esito.Reason);
    }

    [Fact]
    public void IlNULLO_dellaGuardia_unIpotesiDIVERSAnonVieneBloccata()
    {
        // Senza questo, una guardia che rifiuta tutto passerebbe il test qui sopra. Simbolo diverso
        // = ipotesi diversa: è il caso NORMALE, ed è quello che una guardia troppo larga romperebbe.
        var esito = HypothesisGuard.Check(
            [Corsia(4, Chiave(simbolo: "SHIB/USDT"))], targetLane: 6, Chiave());

        Assert.False(esito.Blocked);
        Assert.Null(esito.Reason);
    }

    [Fact]
    public void StessaTERNAconParametriDIVERSI_vieneRifiutata_seIlFlagEacceso()
    {
        // Il secondo gradino, e la misura che lo motiva: delle 16 proposte grigie schierabili al
        // 2026-09-01, UNA sola collide per identità esatta ma TRE per terna — due sono
        // MacdTrend AAVE/USDT 4h con FastPeriod uguale e SlowPeriod 26 e 31 contro il 21 già in
        // corsa. Con la sola guardia sull'identità, il primo slot libero andrebbe a una taratura
        // vicina di ciò che gira già.
        var altriParametri = new Dictionary<string, decimal>(Grid) { ["AnchorPeriod"] = 60m };
        var esito = HypothesisGuard.Check(
            [Corsia(4, Chiave())], targetLane: 6, Chiave(par: altriParametri), blockOnTriple: true);

        Assert.True(esito.Blocked);
        Assert.Contains("stessa terna", esito.Reason);
    }

    [Fact]
    public void StessaTERNA_conFlagSPENTO_passa_maILMOTIVOsiSCRIVE()
    {
        // Spegnere il flag è legittimo (due tarature dello stesso segnale come esperimento
        // dichiarato), ma il motivo deve restare: una scelta senza traccia è indistinguibile da un
        // incidente — che è precisamente ciò che è successo il 31/08.
        var altriParametri = new Dictionary<string, decimal>(Grid) { ["AnchorPeriod"] = 60m };
        var esito = HypothesisGuard.Check(
            [Corsia(4, Chiave())], targetLane: 6, Chiave(par: altriParametri), blockOnTriple: false);

        Assert.False(esito.Blocked);
        Assert.NotNull(esito.Reason);
        Assert.Contains("stessa terna", esito.Reason);
    }

    [Fact]
    public void LaCorsiaBERSAGLIOnonBloccaSeStessa()
    {
        // Riscrivere la stessa ipotesi sulla corsia che già la porta non è una duplicazione.
        var chiave = Chiave();
        var esito = HypothesisGuard.Check([Corsia(4, chiave)], targetLane: 4, chiave);

        Assert.False(esito.Blocked);
    }

    [Fact]
    public void UnaCorsiaFERMAnonBlocca_maSeLaSiVUOLEcontare_blocca()
    {
        // Una corsia ferma porta ancora la sua configurazione ma non spende né osservazione né
        // capitale: rifiutare per lei impedirebbe il caso normale «fermo la corsia A e riprendo la
        // stessa ipotesi sulla B». Resta però esprimibile il confronto totale.
        var chiave = Chiave();
        Assert.False(HypothesisGuard.Check([Corsia(4, chiave, inCorsa: false)], 6, chiave).Blocked);
        Assert.True(HypothesisGuard.Check([Corsia(4, chiave, inCorsa: false)], 6, chiave, onlyRunning: false).Blocked);
    }

    [Fact]
    public void CorsieCheNONdichiaranoLeGambe_nonBloccano_maSiDICHIARANO()
    {
        // L'ignoto qui NON blocca (bloccare fermerebbe ogni schieramento appena una config si
        // corrompe) ma viene DETTO. È il verso opposto al tetto grigio, dove l'ignoto conta come
        // grigio, e la differenza è deliberata: là l'ignoto restringe un permesso, qui lo
        // negherebbe del tutto.
        var esito = HypothesisGuard.Check([Corsia(4, null), Corsia(5, null)], targetLane: 6, Chiave());

        Assert.False(esito.Blocked);
        Assert.Equal(2, esito.UnknownLanes);
        Assert.Contains("confronto e' parziale", esito.Reason);
    }

    [Fact]
    public void LaTerna_siEstraeTogliendoLimprontaDeiParametri()
    {
        Assert.Equal("GridMeanReversion DOGE/USDT 15m", HypothesisGuard.Triple(Chiave()));
        // Un candidato senza parametri non ha impronta: la chiave È già la terna.
        Assert.Equal("X A/B 1h", HypothesisGuard.Triple("X A/B 1h"));
    }

    // ---------------------------------------------------------------- K27: il denominatore

    private static FleetLaneState CorsiaFlotta(
        int id, bool inCorsa = true, bool? grigia = true, bool emergency = false,
        bool quarantena = false, string modo = "Paper") =>
        new(id, inCorsa, modo, IsConfigured: true, Quarantined: quarantena, CampaignOwned: false,
            EmergencyStopped: emergency, RealizedSharpe: 0m, TradeCount: 0, Observation: TimeSpan.Zero,
            Symbol: "DOGE/USDT", Timeframe: "15m", ExpectedTradesPerMonth: 3.8m, GreySourced: grigia);

    private static FleetState Stato(params FleetLaneState[] lanes) => new()
    {
        Lanes = lanes,
        Candidates = [],
        FootprintLanes = 3,
        ExposureGuardEnabled = true,
        NowUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void UnaCorsiaILLEGGIBILE_CONTAancoraNelTettoGrigio()
    {
        // LA REGRESSIONE. `FleetStateReader` marca EmergencyStopped ogni corsia di cui non riesce a
        // leggere lo stato, e prima di oggi quella corsia usciva dal denominatore: il tetto si
        // allargava da solo, in silenzio, proprio nel momento in cui il sistema sapeva di meno.
        var stato = Stato(
            CorsiaFlotta(3),
            CorsiaFlotta(4, emergency: true),   // illeggibile: intoccabile, ma sta ancora correndo
            CorsiaFlotta(5));

        Assert.Equal(3, FleetOrchestrator.GreyOccupied(stato).Count);
        // ...e resta intoccabile per le AZIONI: le due cose sono separate apposta.
        Assert.DoesNotContain(FleetOrchestrator.FleetLanes(stato), l => l.LaneId == 4);
    }

    [Fact]
    public void IlNULLO_delDenominatore_unaCorsiaSOPRAVVISSUTAnonConta()
    {
        // Senza questo, un denominatore che conta tutto passerebbe il test qui sopra. Solo le
        // corsie con provenienza NON dimostrata occupano il tetto grigio.
        var stato = Stato(
            CorsiaFlotta(3, grigia: false),     // dichiarata Survived
            CorsiaFlotta(4, inCorsa: false),    // ferma: non occupa nulla
            CorsiaFlotta(5, grigia: null));     // ignota: conta come grigia

        var occupate = FleetOrchestrator.GreyOccupied(stato);
        Assert.Single(occupate);
        Assert.Equal(5, occupate[0].LaneId);
    }

    [Fact]
    public void LImprontaNONentraNelTetto()
    {
        // Le corsie 0..FootprintLanes-1 sono territorio dell'auto-apply: il tetto della flotta non
        // le governa e contarle lo saturerebbe per sempre.
        var stato = Stato(CorsiaFlotta(0), CorsiaFlotta(1), CorsiaFlotta(2), CorsiaFlotta(3));

        Assert.Single(FleetOrchestrator.GreyOccupied(stato));
    }

    [Fact]
    public void QuarantenaEmodalitaProtetta_CONTANOnelTetto()
    {
        // Non poter fermare una corsia è un motivo in più per non aggiungerne un'altra, non un
        // motivo per contarne una di meno. Il capitale su un'ipotesi non validata resta lì.
        var stato = Stato(
            CorsiaFlotta(3, quarantena: true),
            CorsiaFlotta(4, modo: "Testnet"),
            CorsiaFlotta(5));

        Assert.Equal(3, FleetOrchestrator.GreyOccupied(stato).Count);
    }

    [Fact]
    public void IlTettoSaturoDICHIARAquanteSonoINTOCCABILI()
    {
        // Il rimedio è diverso — liberarla richiede un umano — quindi il motivo deve distinguerle:
        // un operatore che legge «il tetto è saturo» senza saperlo cerca il rimedio sbagliato.
        var opt = new FleetOptions { Enabled = true, GreyAutoDeploy = true, MaxGreyLanes = 2, MinTradesPerMonth = 1m };
        var stato = new FleetState
        {
            Lanes = [CorsiaFlotta(3), CorsiaFlotta(4, emergency: true), CorsiaFlotta(7, inCorsa: false)],
            Candidates =
            [
                new FleetCandidate(Guid.NewGuid(), new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
                    "grey", 4m, "15m", "un grigio", AlreadyHandled: false, Identity: Chiave()),
            ],
            FootprintLanes = 3,
            ExposureGuardEnabled = true,
            NowUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var piano = FleetOrchestrator.Decide(stato, opt);
        var noop = piano.Actions.OfType<FleetNoOp>().Single();

        Assert.Contains("tetto", noop.Reason);
        Assert.Contains("INTOCCABILI", noop.Reason);
    }
}
