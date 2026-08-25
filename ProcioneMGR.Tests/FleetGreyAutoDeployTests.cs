using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J13/J14, PRD autonomia-operativa 2026-08-25] Il braccio di assegnazione e il rovesciamento di
/// F5 coi freni. Questi test pinnano il core PURO (<see cref="FleetOrchestrator.Decide"/>): flag
/// spento = comportamento storico identico (solo proposte); flag acceso = assegnazione grigia
/// dentro il tetto <c>MaxGreyLanes</c> (dove l'IGNOTO conta come grigio), con la banda «pass» che
/// ha la precedenza su corsie e budget, la guardia di esposizione che vale anche qui, e le
/// proposte al click umano che restano per tutto ciò che non è stato assegnato. E il gating del
/// worker: <see cref="FleetOrchestratorWorker.WhyNotExecutedAssignment"/>.
/// </summary>
public class FleetGreyAutoDeployTests
{
    private static FleetLaneState Lane(int id, bool running = true, bool? greySourced = null,
        decimal? expectedPerMonth = 12m)
        => new(id, running, "Paper", IsConfigured: true, Quarantined: false, CampaignOwned: false,
            EmergencyStopped: false, RealizedSharpe: 0.5m, TradeCount: 5,
            Observation: TimeSpan.FromDays(14), "ADA/USDT", "4h", expectedPerMonth, greySourced);

    private static FleetCandidate Grey(string key, double daysAgo = 2, decimal tradesPerMonth = 5m)
        => new(Guid.NewGuid(), DateTime.UtcNow.AddDays(-daysAgo), "grey", tradesPerMonth, "4h",
            $"{key}: Sharpe holdout 1,10 su 12 trade", AlreadyHandled: false, Identity: key);

    private static FleetState State(IReadOnlyList<FleetLaneState> lanes, IReadOnlyList<FleetCandidate> candidates,
        bool exposureGuard = true)
        => new() { Lanes = lanes, Candidates = candidates, FootprintLanes = 3, ExposureGuardEnabled = exposureGuard, NowUtc = DateTime.UtcNow };

    private static FleetOptions Options(bool greyAuto = true, int maxGreyLanes = 3, int maxPerTick = 1)
        => new() { GreyAutoDeploy = greyAuto, MaxGreyLanes = maxGreyLanes, MaxAssignmentsPerTick = maxPerTick, MinTradesPerMonth = 1m };

    [Fact]
    public void FlagSpento_SoloProposte_ComportamentoStorico()
    {
        // F5 pieno: il grigio si propone al click umano, mai si assegna da solo.
        var state = State([Lane(3, running: false)], [Grey("K1")]);
        var plan = FleetOrchestrator.Decide(state, Options(greyAuto: false));

        Assert.Single(plan.Actions.OfType<ProposeGreyCandidate>());
        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());
    }

    [Fact]
    public void FlagAcceso_AssegnaSuCorsiaLibera_DentroIlTetto()
    {
        var state = State([Lane(3, running: false)], [Grey("K1")]);
        var plan = FleetOrchestrator.Decide(state, Options());

        var assegnata = Assert.Single(plan.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Equal(3, assegnata.LaneId);
        Assert.Equal("K1", assegnata.CandidateKey);
        // Il grigio assegnato non viene ANCHE proposto: una cosa sola, non due.
        Assert.Empty(plan.Actions.OfType<ProposeGreyCandidate>());
    }

    [Fact]
    public void TettoRaggiunto_NonAssegna_MaPropone()
    {
        // Tre corsie grigie già in corsa contro MaxGreyLanes=3: il tetto morde, il grigio nuovo
        // torna alla proposta al click umano — l'operatore può sempre scavalcare il tetto a mano.
        var state = State(
            [Lane(4, greySourced: true), Lane(5, greySourced: true), Lane(6, greySourced: true), Lane(7, running: false)],
            [Grey("K1")]);
        var plan = FleetOrchestrator.Decide(state, Options(maxGreyLanes: 3));

        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Single(plan.Actions.OfType<ProposeGreyCandidate>());
    }

    [Fact]
    public void ProvenienzaIgnota_ContaComeGrigia_PerIlTetto()
    {
        // Due grigie dichiarate + una IGNOTA = 3 ai fini del tetto: non sapere non allarga il permesso.
        var state = State(
            [Lane(4, greySourced: true), Lane(5, greySourced: true), Lane(6, greySourced: null), Lane(7, running: false)],
            [Grey("K1")]);
        var plan = FleetOrchestrator.Decide(state, Options(maxGreyLanes: 3));

        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());

        // Le sopravvissute dichiarate NON contano: con la 6 dichiarata «Survived» lo slot c'è.
        var stateConSurvived = State(
            [Lane(4, greySourced: true), Lane(5, greySourced: true), Lane(6, greySourced: false), Lane(7, running: false)],
            [Grey("K1")]);
        Assert.Single(FleetOrchestrator.Decide(stateConSurvived, Options(maxGreyLanes: 3))
            .Actions.OfType<AssignGreyCandidateToLane>());
    }

    [Fact]
    public void BandaPass_HaLaPrecedenza_SuCorsieEBudget()
    {
        // Un pass e un grigio, una corsia libera, budget 1: passa il pass, il grigio si propone.
        var pass = new FleetCandidate(Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "pass", 6m, "4h",
            "sopravvissuto pieno", AlreadyHandled: false, Identity: "PASS-KEY");
        var state = State([Lane(3, running: false)], [pass, Grey("K1")]);
        var plan = FleetOrchestrator.Decide(state, Options());

        Assert.Single(plan.Actions.OfType<AssignCandidateToLane>());
        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Single(plan.Actions.OfType<ProposeGreyCandidate>());
    }

    [Fact]
    public void GuardiaDiEsposizioneSpenta_BloccaAncheIGrigi()
    {
        // AF4b vale anche per J14: senza la guardia trasversale la flotta non si allarga.
        var lanes = new[] { Lane(3), Lane(4), Lane(5), Lane(6, running: false) };
        var state = State(lanes, [Grey("K1")], exposureGuard: false);
        var plan = FleetOrchestrator.Decide(state, Options());

        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Contains(plan.Actions.OfType<FleetNoOp>(), a => a.Reason.Contains("guardia"));
    }

    [Fact]
    public void FrequenzaSottoSoglia_NonSiAssegna()
    {
        var state = State([Lane(3, running: false)], [Grey("K1", tradesPerMonth: 0.4m)]);
        var plan = FleetOrchestrator.Decide(state, Options()); // MinTradesPerMonth = 1

        Assert.Empty(plan.Actions.OfType<AssignGreyCandidateToLane>());
        Assert.Single(plan.Actions.OfType<ProposeGreyCandidate>());
    }

    [Fact]
    public void PareggioGrigio_EsponeIlMenu_PerIlComitato()
    {
        var state = State([Lane(3, running: false)], [Grey("K1", daysAgo: 5), Grey("K2", daysAgo: 1)]);
        var plan = FleetOrchestrator.Decide(state, Options());

        Assert.NotNull(plan.Menu);
        Assert.Equal(2, plan.Menu!.Eligible.Count);
    }

    // ------------------------------------------------------------------ gating del worker

    private static FleetOptions Armed() => new()
    {
        GreyAutoDeploy = true, DryRun = false, ExecutionLanes = [7], MaxExecutionsPerTick = 1,
    };

    [Fact]
    public void Gating_EseguibileSoloConTuttiIPrerequisiti()
    {
        Assert.Null(FleetOrchestratorWorker.WhyNotExecutedAssignment(Armed(), 7, 1, hasKey: true, hasDeployer: true, isGrey: true));
    }

    [Theory]
    [InlineData(true, false, "deployer")]
    [InlineData(false, true, "multi-gamba")]
    public void Gating_PrerequisitiMancanti_ColMotivoGiusto(bool hasKey, bool hasDeployer, string atteso)
    {
        var perche = FleetOrchestratorWorker.WhyNotExecutedAssignment(Armed(), 7, 1, hasKey, hasDeployer, isGrey: false);
        Assert.NotNull(perche);
        Assert.Contains(atteso, perche);
    }

    [Fact]
    public void Gating_GrigioConFlagSpento_NonEsegue_AncheSeArmato()
    {
        // Hot-reload: il piano può essere nato con GreyAutoDeploy acceso e il flag essere stato
        // spento prima dell'esecuzione — il gate lo riverifica al momento dell'azione.
        var opt = Armed();
        opt.GreyAutoDeploy = false;
        var perche = FleetOrchestratorWorker.WhyNotExecutedAssignment(opt, 7, 1, hasKey: true, hasDeployer: true, isGrey: true);
        Assert.Contains("GreyAutoDeploy", perche);
    }

    [Fact]
    public void Gating_DryRunECorsieRestanoIVincoliDiSempre()
    {
        var dry = Armed(); dry.DryRun = true;
        Assert.Contains("dry-run", FleetOrchestratorWorker.WhyNotExecutedAssignment(dry, 7, 1, true, true, false));

        var nonAutorizzata = Armed();
        Assert.Contains("non autorizzata", FleetOrchestratorWorker.WhyNotExecutedAssignment(nonAutorizzata, 5, 1, true, true, false));

        Assert.Contains("budget", FleetOrchestratorWorker.WhyNotExecutedAssignment(Armed(), 7, 0, true, true, false));
    }
}
