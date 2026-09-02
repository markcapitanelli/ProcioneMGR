using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K55, 2026-09-02] <b>Due tetti scollegati sullo stesso rischio.</b>
///
/// <para><b>Il fatto.</b> Il 2026-09-02 il proprietario ha portato <c>AutoReapply:MaxGreyLegs</c> da
/// 0 a 2: da quel momento l'auto-apply può mettere gambe di fascia grigia sulle corsie d'impronta
/// (0-2). Quelle gambe <b>non entrano</b> nel conteggio di <c>Fleet:MaxGreyLanes</c>, che governa
/// solo le corsie di flotta. «Ipotesi non promossa in forward test» è lo stesso rischio su due
/// percorsi contati separatamente, e prima di K55 <b>nessuna superficie sommava i due</b>.</para>
///
/// <para><b>Perché si conta e non si vincola.</b> Cambiare in silenzio il denominatore di
/// <c>MaxGreyLanes</c> bloccherebbe schieramenti per una ragione che nessuno ha scelto. Il numero
/// serve a decidere se debba diventare un vincolo — e quella è una decisione del proprietario.</para>
/// </summary>
public class DoppioTettoGrigioK55Tests
{
    private static FleetLaneState Corsia(int id, bool running, bool? grey) =>
        new(id, running, "Paper", IsConfigured: true, Quarantined: false, CampaignOwned: false,
            EmergencyStopped: false, RealizedSharpe: 0m, TradeCount: 0, Observation: TimeSpan.Zero,
            Symbol: "BTC/USDT", Timeframe: "4h", GreySourced: grey);

    private static FleetState Stato(params FleetLaneState[] corsie) => new()
    {
        Lanes = [.. corsie],
        Candidates = [],
        FootprintLanes = 3,
        ExposureGuardEnabled = false,
        NowUtc = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void LeGAMBEgrigieDIMPRONTA_sonoCONTATEaPARTE()
    {
        var stato = Stato(
            Corsia(0, running: true, grey: true),     // impronta, grigia
            Corsia(1, running: true, grey: false),    // impronta, promossa
            Corsia(2, running: false, grey: true),    // impronta ma FERMA: non è esposizione
            Corsia(3, running: true, grey: true),     // flotta, grigia
            Corsia(4, running: true, grey: true));    // flotta, grigia

        Assert.Equal(1, FleetOrchestrator.GreyOnFootprintLanes(stato));
        Assert.Equal(2, FleetOrchestrator.GreyOccupied(stato).Count);
    }

    /// <summary>
    /// Stesso criterio fail-closed di K38: la provenienza IGNOTA conta come grigia. Non sapere non
    /// allarga il permesso — ed è il caso reale delle gambe schierate prima che l'etichetta
    /// esistesse.
    /// </summary>
    [Fact]
    public void LaPROVENIENZAignota_CONTAcomeGRIGIA()
        => Assert.Equal(1, FleetOrchestrator.GreyOnFootprintLanes(
            Stato(Corsia(0, running: true, grey: null), Corsia(1, running: true, grey: false))));

    /// <summary>
    /// <b>Il nullo.</b> Senza gambe grigie sulle corsie d'impronta il numero è zero e il riquadro
    /// non compare: il pannello non deve inventarsi un allarme dove non c'è esposizione. È il
    /// comportamento di prima del 2026-09-02, quando <c>MaxGreyLegs</c> era 0.
    /// </summary>
    [Fact]
    public void ILNULLO_senzaGAMBEgrigieDIMPRONTA_eZERO()
        => Assert.Equal(0, FleetOrchestrator.GreyOnFootprintLanes(
            Stato(Corsia(0, running: true, grey: false),
                  Corsia(1, running: true, grey: false),
                  Corsia(3, running: true, grey: true))));   // la flotta grigia non conta qui

    /// <summary>
    /// I due conteggi non devono sovrapporsi: una corsia sta di qua o di là, mai in entrambi. Se si
    /// sovrapponessero, il totale conterebbe due volte lo stesso rischio.
    /// </summary>
    [Fact]
    public void IdueCONTEGGI_nonSISOVRAPPONGONO()
    {
        var stato = Stato(
            Corsia(0, true, true), Corsia(1, true, true), Corsia(2, true, true),
            Corsia(3, true, true), Corsia(4, true, true));

        var impronta = FleetOrchestrator.GreyOnFootprintLanes(stato);
        var flotta = FleetOrchestrator.GreyOccupied(stato).Count;

        Assert.Equal(3, impronta);
        Assert.Equal(2, flotta);
        Assert.Equal(stato.Lanes.Count, impronta + flotta);   // nessuna corsia contata due volte
    }

    [Fact]
    public void ILTOTALE_eLaSOMMAdeiDUE()
        => Assert.Equal(5, new FleetSilence(0, 0, 0, 0, "", 0, 0, GreyFleetLanes: 2, GreyFootprintLanes: 3)
            .GreyLanesTotal);
}
