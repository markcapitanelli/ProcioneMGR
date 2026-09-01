using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K40, PRD autonomia-piena — Fase 3, 2026-09-01] <b>«Non so in che stato sono» non è
/// «le corsie sono impegnate».</b>
///
/// <para><b>Il fatto.</b> Al tick del 2026-09-01 11:30 UTC il journal della flotta ha scritto:
/// <i>«16 candidati grigi schierabili ma NESSUNA corsia di flotta libera (6 attive): il vincolo sono
/// le corsie, non i candidati»</i>. Nello stesso istante <c>FleetLaneObservations.LastTickUtc</c>
/// era fermo alle 11:15 per <b>tutte e cinque</b> le corsie: il lettore non ne aveva raggiunta
/// nessuna, perché <c>GetStatusAsync</c> aveva lanciato per tutte.</para>
///
/// <para>Due difetti che si sommano: una corsia illeggibile esce da <c>FleetLanes</c> (quindi non
/// risulta «libera»), mentre il conteggio delle attive viene dal <b>database</b>, che risponde
/// sempre. Una singola frase mescola un numero preso dal DB con una libertà presa dal motore, e
/// trasforma un guasto di lettura in un problema di capienza — che ha il rimedio opposto: guardare
/// perché il motore non risponde, non liberare una corsia.</para>
///
/// <para><b>È l'ironia esatta di K38.</b> Lì si è reso fail-closed il <i>denominatore</i> del tetto
/// grigio; ma il ramo che sceglie <i>quale spiegazione stampare</i> era rimasto fail-open e viene
/// prima nella catena, quindi il messaggio corretto di K38 non poteva essere raggiunto proprio
/// quando serviva di più.</para>
/// </summary>
public class SuperficiCheMentonoK39K40Tests
{
    private static FleetLaneState Corsia(
        int id, bool inCorsa = true, bool emergency = false, bool illeggibile = false) =>
        new(id, inCorsa, "Paper", IsConfigured: true, Quarantined: false, CampaignOwned: false,
            EmergencyStopped: emergency, RealizedSharpe: 0m, TradeCount: 0, Observation: TimeSpan.Zero,
            Symbol: "DOGE/USDT", Timeframe: "15m", ExpectedTradesPerMonth: 3.8m, GreySourced: true,
            Unreadable: illeggibile);

    private static FleetState Stato(params FleetLaneState[] lanes) => new()
    {
        Lanes = lanes,
        Candidates =
        [
            new FleetCandidate(Guid.NewGuid(), new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
                "grey", 4m, "15m", "un grigio", AlreadyHandled: false,
                Identity: "GridMeanReversion DOGE/USDT 15m #5d0ed1f8"),
        ],
        FootprintLanes = 3,
        ExposureGuardEnabled = true,
        NowUtc = new DateTime(2026, 9, 1, 11, 30, 0, DateTimeKind.Utc),
    };

    private static readonly FleetOptions Opt = new()
    {
        Enabled = true, GreyAutoDeploy = true, MaxGreyLanes = 3, MinTradesPerMonth = 1m,
    };

    [Fact]
    public void TutteLeCorsieILLEGGIBILI_ilMotivoDICEcheNonSiSaLEGGERE()
    {
        // LA REGRESSIONE, con lo stato esatto del 2026-09-01 11:30 UTC: cinque corsie, tutte mute.
        var stato = Stato(
            Corsia(3, emergency: true, illeggibile: true),
            Corsia(4, emergency: true, illeggibile: true),
            Corsia(5, emergency: true, illeggibile: true),
            Corsia(6, emergency: true, illeggibile: true),
            Corsia(7, inCorsa: false, emergency: true, illeggibile: true));

        var noop = Assert.Single(FleetOrchestrator.Decide(stato, Opt).Actions.OfType<FleetNoOp>());

        Assert.Contains("NON SONO LEGGIBILI", noop.Reason);
        Assert.Contains("5 corsie", noop.Reason);
        // E soprattutto: NON deve dire che il vincolo sono le corsie.
        Assert.DoesNotContain("il vincolo sono le corsie", noop.Reason);
    }

    [Fact]
    public void IlNULLO_diK40_corsieTUTTEleggibili_eDAVVEROpiene_dannoLaColpaAlleCorsie()
    {
        // Senza questo, un ramo che dà sempre la colpa all'illeggibilità passerebbe il test qui
        // sopra e nasconderebbe il caso vero — che esiste ed è quello per cui la frase è nata.
        var stato = Stato(Corsia(3), Corsia(4), Corsia(5), Corsia(6), Corsia(7));

        var noop = Assert.Single(FleetOrchestrator.Decide(stato, Opt).Actions.OfType<FleetNoOp>());

        Assert.DoesNotContain("NON SONO LEGGIBILI", noop.Reason);
        Assert.Contains("il vincolo sono le corsie", noop.Reason);
    }

    [Fact]
    public void UnaCorsiaLIBERAeLEGGIBILE_faVincereIlTETTOgrigio_nonLilleggibilita()
    {
        // L'ordine dei rami conta: se c'è una corsia libera davvero, il vincolo è il tetto (K38) e
        // il messaggio deve essere quello — anche se un'altra corsia è illeggibile.
        var stato = Stato(
            Corsia(3), Corsia(4), Corsia(5),
            Corsia(6, emergency: true, illeggibile: true),
            Corsia(7, inCorsa: false));

        var noop = Assert.Single(FleetOrchestrator.Decide(stato, Opt).Actions.OfType<FleetNoOp>());

        Assert.Contains("tetto", noop.Reason);
        Assert.Contains("INTOCCABILI", noop.Reason);
    }

    [Fact]
    public void Explain_METTElilleggibilitaPRIMAdiTUTTO_eLaCONTA()
    {
        // La scheda del silenzio è ciò che l'operatore legge davvero. Sopra zero corsie illeggibili
        // ogni altro numero è costruito su un denominatore incompleto, e va detto per primo.
        var stato = Stato(
            Corsia(3, emergency: true, illeggibile: true),
            Corsia(4), Corsia(5), Corsia(6), Corsia(7, inCorsa: false));

        var silenzio = FleetOrchestrator.Explain(stato, Opt);

        Assert.Equal(1, silenzio.UnreadableLanes);
        Assert.Contains("NON SONO LEGGIBILI", silenzio.Reason);
    }

    [Fact]
    public void IlNULLO_diExplain_senzaIlleggibili_ilMotivoEquelloDIsempre()
    {
        var stato = Stato(Corsia(3), Corsia(4), Corsia(5), Corsia(6), Corsia(7, inCorsa: false));

        var silenzio = FleetOrchestrator.Explain(stato, Opt);

        Assert.Equal(0, silenzio.UnreadableLanes);
        Assert.DoesNotContain("NON SONO LEGGIBILI", silenzio.Reason);
    }

    [Fact]
    public void LImprontaILLEGGIBILEnonCONTA()
    {
        // Le corsie 0..FootprintLanes-1 non sono governate dalla flotta: contarle produrrebbe un
        // allarme permanente su un territorio che non le appartiene (la corsia 0 è in quarantena
        // dal 2026-08-25 e resterebbe «illeggibile» per sempre nella scheda della Regina).
        var stato = Stato(
            Corsia(0, emergency: true, illeggibile: true),
            Corsia(3), Corsia(4), Corsia(5), Corsia(6), Corsia(7, inCorsa: false));

        Assert.Equal(0, FleetOrchestrator.Explain(stato, Opt).UnreadableLanes);
    }
}
