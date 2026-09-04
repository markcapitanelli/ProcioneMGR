using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF2] Il core puro della Queen Bee, attaccato come la promozione: fuzz 20k stati sugli
/// invarianti (mai un'azione su impronta/Live/Testnet/quarantena/campagne/emergency, mai due
/// assegnazioni sulla stessa corsia, mai un candidato senza frequenza sopra soglia) + scenari a
/// mano per il comportamento voluto + la proprietà di quiete (stato sano ⇒ nessuna azione).
/// </summary>
public sealed class FleetOrchestratorTests
{
    private static FleetLaneState Lane(int id, bool running = false, string mode = "Paper",
        bool configured = false, bool quarantined = false, bool campaign = false, bool emergency = false,
        decimal sharpe = 0m, int trades = 0, int observationDays = 0)
        // [K44, 2026-09-01] Il giudizio legge lo Sharpe PER OPERAZIONE, non quello annualizzato:
        // la stessa soglia sul secondo valeva quattro cose diverse a seconda del timeframe. Qui la
        // gamba dichiara lo stesso valore su entrambi — queste prove parlano dei CANCELLI del
        // ritiro, non dell'unità — ma deve dichiararlo: senza, il criterio si astiene.
        => new(id, running, mode, configured, quarantined, campaign, emergency,
            sharpe, trades, TimeSpan.FromDays(observationDays), configured ? "BTC/USDT" : "", configured ? "1h" : "",
            ExpectedTradesPerMonth: null, GreySourced: null, Unreadable: false, RealizedSharpePerTrade: sharpe);

    private static FleetCandidate Pass(Guid? id = null, decimal tpm = 8m, int ageDays = 1, bool handled = false)
        => new(id ?? Guid.NewGuid(), DateTime.UtcNow.AddDays(-ageDays), "pass", tpm, "1h", "test-candidate", handled);

    private static FleetCandidate Grey(Guid? id = null, bool handled = false)
        => new(id ?? Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "grey", 5m, "15m", "grey-candidate", handled);

    private static FleetState State(IReadOnlyList<FleetLaneState> lanes, IReadOnlyList<FleetCandidate>? candidates = null,
        int footprint = 3, bool guardOn = true)
        => new() { Lanes = lanes, Candidates = candidates ?? [], FootprintLanes = footprint, ExposureGuardEnabled = guardOn, NowUtc = DateTime.UtcNow };

    // ------------------------------------------------------------------ fuzz

    [Fact]
    public void Decide_Fuzz20k_NeverTouchesProtectedLanes_NeverDoubleAssigns()
    {
        var rnd = new Random(20260802);
        var modes = new[] { "Paper", "Testnet", "Live" };

        for (var i = 0; i < 20_000; i++)
        {
            var laneCount = rnd.Next(1, 13);
            var footprint = rnd.Next(0, laneCount + 1);
            var lanes = Enumerable.Range(0, laneCount).Select(id => Lane(
                id,
                running: rnd.Next(2) == 0,
                mode: modes[rnd.Next(modes.Length)],
                configured: rnd.Next(2) == 0,
                quarantined: rnd.Next(4) == 0,
                campaign: rnd.Next(4) == 0,
                emergency: rnd.Next(6) == 0,
                sharpe: (decimal)(rnd.NextDouble() * 8 - 4),
                trades: rnd.Next(0, 500),
                observationDays: rnd.Next(0, 200)) with
                {
                    // [K61] Il fuzz deve poter produrre corsie inerti, altrimenti la quarta azione
                    // non nascerebbe mai e le sue invarianti passerebbero inosservate.
                    LastTradeUtc = rnd.Next(3) == 0 ? null : DateTime.UtcNow.AddDays(-rnd.Next(0, 60)),
                    OpenPositions = rnd.Next(4) == 0 ? rnd.Next(1, 4) : 0,
                    // [K61] Il ritmo atteso entra nel fuzz con valori ASSURDI compresi: un ritmo
                    // minuscolo produce un intervallo medio enorme, e la soglia scalata deve
                    // limitarsi invece di far traboccare l'aritmetica dentro una funzione pura.
                    ExpectedTradesPerMonth = rnd.Next(4) switch
                    {
                        0 => null,
                        1 => 0m,
                        2 => (decimal)Math.Pow(10, -rnd.Next(0, 26)),
                        _ => (decimal)(rnd.NextDouble() * 40 - 5),
                    },
                }).ToList();

            var candidates = Enumerable.Range(0, rnd.Next(0, 6)).Select(_ => new FleetCandidate(
                Guid.NewGuid(), DateTime.UtcNow.AddDays(-rnd.Next(0, 20)),
                rnd.Next(2) == 0 ? "pass" : "grey",
                (decimal)(rnd.NextDouble() * 40 - 5),   // anche frequenze negative assurde
                "1h", "fuzz", rnd.Next(3) == 0) with
                {
                    // [K61] Identità e stabilità: senza identità nessun candidato è sostituibile, e
                    // senza misure nessuno è giudicabile. Il fuzz deve attraversare tutti e tre i casi.
                    Identity = rnd.Next(4) == 0 ? null : $"k{rnd.Next(0, 5)}",
                    StabilityMeasures = rnd.Next(0, 20),
                    StabilityMedian = rnd.Next(5) == 0 ? null : (decimal)(rnd.NextDouble() * 6 - 2),
                }).ToList();

            var opt = new FleetOptions
            {
                RetireSharpeThreshold = (decimal)(rnd.NextDouble() * 2 - 1),
                RetireMinWeeks = rnd.Next(-1, 6),
                RetireMinTrades = rnd.Next(-5, 50),
                MaxAssignmentsPerTick = rnd.Next(-1, 4),
                MinTradesPerMonth = (decimal)(rnd.NextDouble() * 5),
                MaxLanesWithoutExposureGuard = rnd.Next(-1, 6),
                // [K61] Anche la sostituzione entra nel fuzz, con soglie assurde comprese.
                ReplaceIdleLanes = rnd.Next(2) == 0,
                ReplaceIdleDays = rnd.Next(-2, 30),
                ReplaceIdleExpectedMultiple = (decimal)(rnd.NextDouble() * 4 - 1),
                ReplaceMinLaneDays = rnd.Next(-2, 30),
                ReplaceMinCandidateMedian = (decimal)(rnd.NextDouble() * 4 - 2),
                ReplaceMinCandidateMeasures = rnd.Next(-2, 12),
                MaxReplacementsPerTick = rnd.Next(-1, 4),
                PreferStableGrey = rnd.Next(2) == 0,
            };

            var plan = FleetOrchestrator.Decide(State(lanes, candidates, footprint, guardOn: rnd.Next(2) == 0), opt);

            var byId = lanes.ToDictionary(l => l.LaneId);
            var touchedLanes = new List<int>();
            foreach (var action in plan.Actions)
            {
                switch (action)
                {
                    case AssignCandidateToLane a:
                        touchedLanes.Add(a.LaneId);
                        var lane = byId[a.LaneId];
                        Assert.True(a.LaneId >= footprint, $"iter {i}: assegnazione sull'impronta (corsia {a.LaneId})");
                        Assert.False(lane.IsRunning, $"iter {i}: assegnazione su corsia attiva");
                        Assert.False(lane.Quarantined || lane.CampaignOwned || lane.EmergencyStopped,
                            $"iter {i}: assegnazione su corsia vincolata");
                        Assert.Equal("Paper", lane.Mode);
                        var cand = candidates.Single(c => c.RunId == a.RunId);
                        Assert.Equal("pass", cand.Band);
                        Assert.False(cand.AlreadyHandled);
                        Assert.True(cand.TradesPerMonth >= opt.MinTradesPerMonth,
                            $"iter {i}: candidato sotto la frequenza minima assegnato");
                        break;

                    case StopAndFreeLane s:
                        touchedLanes.Add(s.LaneId);
                        var r = byId[s.LaneId];
                        Assert.True(s.LaneId >= footprint, $"iter {i}: ritiro sull'impronta (corsia {s.LaneId})");
                        Assert.True(r.IsRunning, $"iter {i}: ritiro di una corsia ferma");
                        Assert.False(r.Quarantined || r.CampaignOwned || r.EmergencyStopped,
                            $"iter {i}: ritiro di una corsia vincolata");
                        Assert.Equal("Paper", r.Mode); // mai Live/Testnet, nemmeno oltre l'impronta
                        break;

                    // [K61] La quarta azione. Senza questo ramo la sostituzione attraverserebbe
                    // 20.000 stati senza che nessuna invariante la guardi — e il conteggio di
                    // touchedLanes non la vedrebbe nemmeno come «azione sulla corsia».
                    case ReplaceLaneOccupant rep:
                        touchedLanes.Add(rep.LaneId);
                        var sostituita = byId[rep.LaneId];
                        Assert.True(opt.ReplaceIdleLanes, $"iter {i}: sostituzione a interruttore SPENTO");
                        Assert.True(rep.LaneId >= footprint, $"iter {i}: sostituzione sull'impronta (corsia {rep.LaneId})");
                        Assert.True(sostituita.IsRunning, $"iter {i}: sostituzione di una corsia ferma (era libera: bastava assegnarla)");
                        Assert.Equal(0, sostituita.OpenPositions); // il danno K36: mai sopra una posizione viva
                        Assert.False(sostituita.Quarantined || sostituita.CampaignOwned || sostituita.EmergencyStopped,
                            $"iter {i}: sostituzione su corsia vincolata");
                        Assert.Equal("Paper", sostituita.Mode);
                        var rimpiazzo = candidates.Single(c => c.RunId == rep.RunId);
                        Assert.False(rimpiazzo.AlreadyHandled, $"iter {i}: rimpiazzo già gestito");
                        Assert.False(string.IsNullOrEmpty(rimpiazzo.Identity), $"iter {i}: rimpiazzo senza identità");
                        Assert.Equal(rimpiazzo.Identity, rep.CandidateKey);
                        Assert.True(rimpiazzo.StabilityMeasures >= Math.Max(1, opt.ReplaceMinCandidateMeasures),
                            $"iter {i}: rimpiazzo non giudicabile");
                        Assert.True(rimpiazzo.StabilityMedian >= opt.ReplaceMinCandidateMedian,
                            $"iter {i}: rimpiazzo sotto la mediana minima");
                        break;

                    case ProposeGreyCandidate g:
                        Assert.Equal("grey", candidates.Single(c => c.RunId == g.RunId).Band);
                        break;
                }
            }

            // Mai due azioni sulla stessa corsia nello stesso piano.
            Assert.Equal(touchedLanes.Count, touchedLanes.Distinct().Count());
        }
    }

    [Fact]
    public void Decide_HealthyQuietState_Produces100TicksOfNothing()
    {
        // La proprietà di quiete (livello 2 dello standard): flotta sana e coda vuota per 100
        // tick ⇒ SOLO piani vuoti. Il rumore non accende niente.
        var lanes = new List<FleetLaneState>
        {
            Lane(0, running: true, configured: true, sharpe: 0.5m, trades: 40, observationDays: 60),
            Lane(1, running: true, configured: true, sharpe: 1.1m, trades: 25, observationDays: 45),
            Lane(2, running: true, configured: true, sharpe: 0.2m, trades: 90, observationDays: 90),
            Lane(3, running: true, configured: true, sharpe: 0.8m, trades: 30, observationDays: 30),
            Lane(4), Lane(5), Lane(6), Lane(7),
        };

        for (var tick = 0; tick < 100; tick++)
        {
            var plan = FleetOrchestrator.Decide(State(lanes), new FleetOptions());
            Assert.Empty(plan.Actions);
        }
    }

    // ------------------------------------------------------------------ scenari a mano

    [Fact]
    public void Assignment_OldestCandidate_GetsTheLowestFreeLane()
    {
        var older = Pass(ageDays: 10);
        var newer = Pass(ageDays: 1);
        var state = State(
            [Lane(0, running: true), Lane(1, running: true), Lane(2, running: true), Lane(3), Lane(4), Lane(5, running: true, configured: true)],
            [newer, older]);

        var plan = FleetOrchestrator.Decide(state, new FleetOptions { MaxAssignmentsPerTick = 1 });

        var assign = Assert.Single(plan.Actions.OfType<AssignCandidateToLane>());
        Assert.Equal(older.RunId, assign.RunId); // FIFO: il più vecchio per primo
        Assert.Equal(3, assign.LaneId);          // la corsia libera con id più basso
    }

    [Fact]
    public void Retirement_RequiresHistory_AndFiresOnLosers()
    {
        var options = new FleetOptions { RetireSharpeThreshold = 0m, RetireMinWeeks = 3, RetireMinTrades = 20 };

        // Perdente con storia: ritiro.
        var loser = State([Lane(3, running: true, configured: true, sharpe: -0.8m, trades: 30, observationDays: 30)]);
        var retire = Assert.Single(FleetOrchestrator.Decide(loser, options).Actions.OfType<StopAndFreeLane>());
        Assert.Equal(3, retire.LaneId);

        // Perdente GIOVANE: nessun giudizio.
        var young = State([Lane(3, running: true, configured: true, sharpe: -3m, trades: 5, observationDays: 4)]);
        Assert.Empty(FleetOrchestrator.Decide(young, options).Actions);
    }

    [Fact]
    public void GreyBand_IsProposed_NeverAssigned()
    {
        var grey = Grey();
        var state = State([Lane(3), Lane(4)], [grey]);

        var plan = FleetOrchestrator.Decide(state, new FleetOptions());

        Assert.Single(plan.Actions.OfType<ProposeGreyCandidate>());
        Assert.Empty(plan.Actions.OfType<AssignCandidateToLane>()); // la fascia grigia non si auto-schiera MAI
    }

    [Fact]
    public void ExposureGuardOff_BlocksNewAssignments_BeyondTheThreshold()
    {
        // [AF4b] 3 corsie già attive, guardia spenta: il candidato resta in coda, con il motivo
        // che dice DOVE si accende la guardia.
        var state = State(
            [Lane(0, running: true), Lane(1, running: true), Lane(2, running: true), Lane(3)],
            [Pass()],
            guardOn: false);

        var plan = FleetOrchestrator.Decide(state, new FleetOptions { MaxLanesWithoutExposureGuard = 3 });

        Assert.Empty(plan.Actions.OfType<AssignCandidateToLane>());
        var blocked = Assert.Single(plan.Actions.OfType<FleetNoOp>());
        Assert.Contains("CorrelatedExposure", blocked.Reason);
    }

    [Fact]
    public void NoFreeLanes_ReportsTheBlock_WithTheQueueSize()
    {
        var state = State(
            [Lane(0, running: true), Lane(1, running: true), Lane(2, running: true), Lane(3, running: true, configured: true, sharpe: 1m, trades: 10, observationDays: 10)],
            [Pass(), Pass()]);

        var plan = FleetOrchestrator.Decide(state, new FleetOptions());

        var blocked = Assert.Single(plan.Actions.OfType<FleetNoOp>());
        Assert.Contains("2 candidati", blocked.Reason);
    }

    [Fact]
    public void MultipleEligibleCandidates_ExposeTheMenu_WithTheRuleChoiceAsDefault()
    {
        // [AF3] Il pareggio arbitrabile: il core sceglie comunque da regola (il più vecchio) e
        // ESPONE il menù; senza pareggio (un solo candidato) il menù non esiste.
        var older = Pass(ageDays: 10);
        var newer = Pass(ageDays: 1);
        var tie = FleetOrchestrator.Decide(State([Lane(0, running: true), Lane(3), Lane(4)], [newer, older]),
            new FleetOptions { MaxAssignmentsPerTick = 1 });

        Assert.NotNull(tie.Menu);
        Assert.Equal(older.RunId, tie.Menu.DefaultRunId);
        Assert.Equal(2, tie.Menu.Eligible.Count);
        Assert.Equal(older.RunId, Assert.Single(tie.Actions.OfType<AssignCandidateToLane>()).RunId);

        var single = FleetOrchestrator.Decide(State([Lane(3)], [older]), new FleetOptions());
        Assert.Null(single.Menu);
    }

    [Fact]
    public void HandledCandidates_AndLowFrequencyOnes_NeverEnterTheQueue()
    {
        var handled = Pass(handled: true);
        var tooSlow = Pass(tpm: 0.5m);
        var state = State([Lane(3), Lane(4)], [handled, tooSlow]);

        var plan = FleetOrchestrator.Decide(state, new FleetOptions { MinTradesPerMonth = 1m });

        Assert.Empty(plan.Actions); // niente da fare, e nemmeno un blocco: la coda EFFETTIVA è vuota
    }
}
