using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K12+K14, PRD autonomia-piena — Fase 1, 2026-08-31] <b>Proporre a un umano non è schierare.</b>
///
/// <para><b>K14, il difetto misurato.</b> <c>FleetStateReader</c> metteva <c>Assign</c> e
/// <c>ProposeGrey</c> nello stesso insieme «già gestito», e quello stato si eredita per identità:
/// bastava una NOTIFICA a un umano perché il braccio automatico considerasse quel candidato gestito
/// per sempre, dentro la finestra dei trenta giorni. Al 2026-08-31 le identità della finestra erano
/// <b>18 su 18 gestite</b> e la coda dei grigi era vuota <b>per costruzione</b>: il migliore
/// disponibile — MacdTrend AAVE/USDT 4h, Sharpe holdout 3,66 su 55 trade, trovato quella mattina —
/// era soppresso perché la stessa chiave era stata <i>proposta</i> due giorni prima.
/// <c>Fleet:GreyAutoDeploy</c> era acceso su un serbatoio che si svuotava da sé.</para>
///
/// <para><b>K12, il difetto gemello.</b> Il ramo «pass» aveva il suo <c>FleetNoOp</c>; il ramo
/// grigio no. In 115 decisioni journalizzate: <b>zero righe <c>Blocked</c></b>. Chi guardava non
/// poteva distinguere «tetto grigio saturo» da «nessuna corsia libera» da «coda vuota» — tre
/// vincoli con tre rimedi diversi. Un ramo che tace per tre ragioni e non ne nomina nessuna è
/// indistinguibile da un ramo spento.</para>
/// </summary>
public sealed class FleetGrigiK12K14Tests
{
    private static FleetLaneState Lane(int id, bool running) =>
        new(id, running, "Paper", true, false, false, false, 0m, 0, TimeSpan.FromDays(1), "BTC/USDT", "1h");

    private static FleetCandidate Grigio(string identita, bool schierato = false, bool proposto = false) =>
        new(Guid.NewGuid(), DateTime.UtcNow.AddHours(-2), "grey", 5m, "4h",
            $"grey {identita}", AlreadyHandled: schierato, AlreadyProposed: proposto, Identity: identita);

    private static FleetState Stato(IReadOnlyList<FleetLaneState> lanes, IReadOnlyList<FleetCandidate> cand) =>
        new() { Lanes = lanes, Candidates = cand, FootprintLanes = 3, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow };

    private static FleetOptions Opzioni(bool autoDeploy = true, int maxGrey = 3) => new()
    {
        GreyAutoDeploy = autoDeploy,
        MaxGreyLanes = maxGrey,
        MinTradesPerMonth = 0.5m,
        MaxAssignmentsPerTick = 1,
        MaxLanesWithoutExposureGuard = 3,
    };

    // =============================================================================================
    //  K14 — una proposta non consuma il braccio automatico
    // =============================================================================================

    [Fact]
    public void UnGrigioGiaPROPOSTO_restaSCHIERABILE()
    {
        // IL test di K14. Prima di oggi questo candidato era invisibile all'automatismo perché
        // qualcuno gli aveva mandato una notifica.
        var stato = Stato([Lane(3, running: false), Lane(4, running: false), Lane(5, running: false)],
                          [Grigio("aave-4h", proposto: true)]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.Contains(piano.Actions.OfType<AssignGreyCandidateToLane>(), a => a.CandidateKey == "aave-4h");
    }

    [Fact]
    public void UnGrigioGiaPROPOSTO_non_si_RIPROPONE()
    {
        // Il complemento, che è la ragione per cui quello stato esiste: l'anti-raffica delle
        // notifiche resta intatto. Con l'auto-deploy spento non deve uscire una nuova proposta.
        var stato = Stato([Lane(3, running: false)], [Grigio("aave-4h", proposto: true)]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni(autoDeploy: false));

        Assert.DoesNotContain(piano.Actions, a => a is ProposeGreyCandidate);
    }

    [Fact]
    public void UnGrigioGiaSCHIERATO_non_si_schiera_ne_si_propone()
    {
        var stato = Stato([Lane(3, running: false), Lane(4, running: false), Lane(5, running: false)],
                          [Grigio("aave-4h", schierato: true)]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.DoesNotContain(piano.Actions, a => a is AssignGreyCandidateToLane);
        Assert.DoesNotContain(piano.Actions, a => a is ProposeGreyCandidate);
    }

    [Fact]
    public void UnGrigioMAIvisto_si_schiera_e_non_si_propone_due_volte()
    {
        var stato = Stato([Lane(3, running: false), Lane(4, running: false), Lane(5, running: false)],
                          [Grigio("nuovo-1h")]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.Single(piano.Actions.OfType<AssignGreyCandidateToLane>());
        // Assegnato in questo giro ⇒ niente proposta al click umano per la stessa cosa.
        Assert.DoesNotContain(piano.Actions, a => a is ProposeGreyCandidate);
    }

    // =============================================================================================
    //  K12 — il ramo grigio dichiara perché tace
    // =============================================================================================

    [Fact]
    public void NessunaCorsiaLIBERA_lo_DICE()
    {
        var stato = Stato([Lane(3, running: true), Lane(4, running: true), Lane(5, running: true)],
                          [Grigio("nuovo-1h")]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.Contains(piano.Actions.OfType<FleetNoOp>(), n => n.Reason.Contains("NESSUNA corsia"));
    }

    [Fact]
    public void TettoGRIGIOsaturo_lo_DICE_col_conteggio()
    {
        // Tre corsie in corsa di provenienza ignota (che conta come grigia) contro MaxGreyLanes=3,
        // e una libera: il vincolo è il tetto, non le corsie.
        var stato = Stato(
            [Lane(3, running: true), Lane(4, running: true), Lane(5, running: true), Lane(6, running: false)],
            [Grigio("nuovo-1h")]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni(maxGrey: 3));

        var noop = Assert.Single(piano.Actions.OfType<FleetNoOp>(), n => n.Reason.Contains("tetto"));
        Assert.Contains("3", noop.Reason);
    }

    [Fact]
    public void CodaVUOTAperche_TUTTIGIASCHIERATI_lo_DICE()
    {
        // La distinzione che mancava: «non ci sono grigi» e «ci sono ma li ho già schierati tutti»
        // mandano a guardare posti diversi.
        var stato = Stato([Lane(3, running: false), Lane(4, running: false), Lane(5, running: false)],
                          [Grigio("a", schierato: true), Grigio("b", schierato: true)]);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.Contains(piano.Actions.OfType<FleetNoOp>(), n => n.Reason.Contains("tutti gia'"));
    }

    [Fact]
    public void NessunGrigioAffatto_NON_produce_rumore()
    {
        // Il controllo sul rumore: senza grigi non c'è niente da dichiarare, e una riga «non ho
        // fatto nulla» a ogni tick a macchina sana insegna a non leggere il journal.
        var stato = Stato([Lane(3, running: false), Lane(4, running: false), Lane(5, running: false)], []);

        var piano = FleetOrchestrator.Decide(stato, Opzioni());

        Assert.DoesNotContain(piano.Actions.OfType<FleetNoOp>(), n => n.Reason.Contains("grigi"));
    }
}
