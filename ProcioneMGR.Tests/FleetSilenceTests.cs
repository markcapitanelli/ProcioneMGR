using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Llm.Committee;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I8] <b>Perché la flotta e il comitato tacciono.</b>
///
/// Il difetto che copre non è un guasto ma un buco di prodotto, misurato il 2026-08-18: in quindici
/// giorni l'orchestratore ha prodotto 83 proposte grigie e <b>zero assegnazioni</b>, e il comitato —
/// acceso, con tre provider e le chiavi a posto — non ha emesso <b>un solo voto</b>. Nessuna
/// superficie diceva perché, e i flag dicevano tutti «a posto».
///
/// <para>La causa è a monte e in un punto solo: un menù (e quindi una domanda per il comitato) nasce
/// solo con almeno due candidati in banda «pass» in coda <b>e</b> una corsia libera. Con la coda
/// sempre vuota non c'è pareggio, quindi nessuna domanda. Un comitato acceso che non vota non è
/// guasto: non gli si sta chiedendo nulla — ed è esattamente ciò che serviva poter leggere.</para>
/// </summary>
public class FleetSilenceTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    private static FleetLaneState Lane(int id, bool running = false, string mode = "Paper",
        bool quarantined = false, bool campaignOwned = false, bool emergency = false) =>
        new(id, running, mode, IsConfigured: true, quarantined, campaignOwned, emergency,
            RealizedSharpe: 0m, TradeCount: 0, Observation: TimeSpan.Zero,
            Symbol: "BTC/USDT", Timeframe: "1h");

    private static FleetCandidate Candidate(string band, decimal tradesPerMonth = 5m, bool handled = false) =>
        new(Guid.NewGuid(), Now.AddDays(-1), band, tradesPerMonth, "1h", "Composite BTC/USDT", handled);

    private static FleetState State(IEnumerable<FleetLaneState> lanes, IEnumerable<FleetCandidate> candidates, int footprint = 3) =>
        new()
        {
            Lanes = lanes.ToList(),
            Candidates = candidates.ToList(),
            FootprintLanes = footprint,
            ExposureGuardEnabled = true,
            NowUtc = Now,
        };

    private static FleetOptions Opt() => new() { MinTradesPerMonth = 1m };

    /// <summary>
    /// <b>Lo stato reale misurato il 2026-08-18</b>: cinque corsie sotto governo tutte occupate,
    /// zero candidati «pass», molti grigi. È il caso che ha prodotto sedici giorni di silenzio
    /// inspiegato, e la diagnosi deve nominarlo.
    /// </summary>
    [Fact]
    public void StatoRealeDel18Agosto_NessunCandidatoPass_ELoDice()
    {
        var lanes = Enumerable.Range(0, 8).Select(i => Lane(i, running: i >= 3));
        var candidati = Enumerable.Range(0, 4).Select(_ => Candidate("grey"));

        var s = FleetOrchestrator.Explain(State(lanes, candidati), Opt());

        Assert.Equal(0, s.PassCandidatesQueued);
        Assert.Equal(4, s.GreyCandidates);
        Assert.Equal(0, s.FreeFleetLanes);
        Assert.Equal(5, s.LanesUnderGovernance);
        Assert.False(s.CommitteeCouldBeAsked);
        Assert.Contains("nessun candidato in banda «pass»", s.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>La proprietà che chiude il buco</b>: con due candidati idonei e una corsia libera il
    /// comitato PUÒ essere interrogato. Senza questo complemento, «non può ricevere domande»
    /// sarebbe soddisfatto anche da una diagnosi che dice sempre di no.
    /// </summary>
    [Fact]
    public void DueCandidatiPassEUnaCorsiaLibera_IlComitatoPuoEssereInterrogato()
    {
        var lanes = new[] { Lane(0), Lane(1), Lane(2), Lane(3, running: false), Lane(4, running: true) };
        var candidati = new[] { Candidate("pass"), Candidate("pass") };

        var s = FleetOrchestrator.Explain(State(lanes, candidati), Opt());

        Assert.Equal(2, s.PassCandidatesQueued);
        Assert.Equal(1, s.FreeFleetLanes);
        Assert.True(s.CommitteeCouldBeAsked);
    }

    /// <summary>Un solo candidato: l'assegnazione è determinata, non c'è pareggio da arbitrare.</summary>
    [Fact]
    public void UnSoloCandidato_NonEUnPareggio()
    {
        var lanes = new[] { Lane(0), Lane(1), Lane(2), Lane(3) };
        var s = FleetOrchestrator.Explain(State(lanes, [Candidate("pass")]), Opt());

        Assert.Equal(1, s.PassCandidatesQueued);
        Assert.False(s.CommitteeCouldBeAsked);
        Assert.Contains("non c'è pareggio", s.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// La diagnosi conta le corsie che l'orchestratore può DAVVERO toccare: quarantenate, di
    /// campagna, in emergency stop e in modalità protetta non contano. Se contasse quelle, il
    /// pannello prometterebbe un governo che non c'è.
    /// </summary>
    [Fact]
    public void CorsieIntoccabili_NonContanoComeSottoGoverno()
    {
        var lanes = new[]
        {
            Lane(0), Lane(1), Lane(2),                      // impronta auto-apply
            Lane(3, quarantined: true),
            Lane(4, campaignOwned: true),
            Lane(5, emergency: true),
            Lane(6, mode: "Live"),
            Lane(7),                                        // l'unica governabile
        };

        var s = FleetOrchestrator.Explain(State(lanes, []), Opt());

        Assert.Equal(1, s.LanesUnderGovernance);
        Assert.Equal(1, s.FreeFleetLanes);
    }

    /// <summary>
    /// <b>Il controllo sul rumore</b>: zero corsie oltre l'impronta ⇒ la ragione è quella, non
    /// «mancano candidati». Il primo vincolo che chiude la strada è quello che l'operatore deve
    /// conoscere: dirne un altro lo manderebbe a cercare candidati che non servirebbero comunque.
    /// </summary>
    [Fact]
    public void SenzaCorsieOltreLImpronta_LaRagioneEQuellaNonICandidati()
    {
        var lanes = new[] { Lane(0), Lane(1), Lane(2) };
        var s = FleetOrchestrator.Explain(State(lanes, [Candidate("pass"), Candidate("pass")]), Opt());

        Assert.Equal(0, s.LanesUnderGovernance);
        Assert.Contains("nessuna corsia oltre l'impronta", s.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// I candidati già gestiti e quelli sotto la frequenza minima NON entrano in coda: la diagnosi
    /// deve contare esattamente ciò che la decisione guarda, altrimenti spiegherebbe un silenzio
    /// diverso da quello vero.
    /// </summary>
    [Fact]
    public void LaCodaUsaGliStessiFiltriDellaDecisione()
    {
        var lanes = new[] { Lane(0), Lane(1), Lane(2), Lane(3) };
        var candidati = new[]
        {
            Candidate("pass", handled: true),          // già gestito
            Candidate("pass", tradesPerMonth: 0.1m),   // sotto MinTradesPerMonth
            Candidate("pass"),                          // l'unico che conta
        };

        var s = FleetOrchestrator.Explain(State(lanes, candidati), new FleetOptions { MinTradesPerMonth = 1m });

        Assert.Equal(1, s.PassCandidatesQueued);
    }

    // --- [I8] «default» collassava tre cause diverse --------------------------------------------

    private static CommitteeVote Vote(string provider, bool valid) =>
        new(provider, valid ? "abc" : null, valid ? 0.8 : null, valid ? "ok" : "timeout", valid);

    /// <summary>
    /// Il quorum ha scelto: la fonte è il comitato, e resta la parola che il journal usava.
    /// </summary>
    [Fact]
    public void QuorumRaggiunto_LaFonteEIlComitato()
        => Assert.Equal("committee", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", ByQuorum: true, [Vote("nvidia", true), Vote("groq", true)])));

    /// <summary>
    /// <b>Le tre cause che prima erano una parola sola.</b> La differenza che conta è fra «il
    /// comitato ha deliberato e la maggioranza non si è formata» e «il comitato non ha funzionato»:
    /// nel primo caso il default è la risposta, nel secondo è un ripiego su un guasto. Sedici giorni
    /// di righe identiche non dicevano quale dei due fosse.
    /// </summary>
    [Fact]
    public void NessunVoto_SignificaNonInterrogato_NonQuorumMancato()
        => Assert.Equal("default:non-interrogato", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", ByQuorum: false, [])));

    [Fact]
    public void TuttiAstenuti_SiDistingueDaUnQuorumMancato()
        => Assert.Equal("default:tutti-astenuti", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", ByQuorum: false, [Vote("nvidia", false), Vote("groq", false)])));

    [Fact]
    public void HannoRispostoMaSenzaMaggioranza_EQuorumMancato()
        => Assert.Equal("default:quorum-mancato", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", ByQuorum: false, [Vote("nvidia", true), Vote("groq", false)])));

    /// <summary>
    /// Le tre cause devono restare DISTINGUIBILI fra loro: se due collassassero, il difetto
    /// tornerebbe in forma ridotta.
    /// </summary>
    [Fact]
    public void LeTreCauseSonoTutteDiverse()
    {
        var cause = new[]
        {
            FleetOrchestratorWorker.DescribeAssignSource(new CommitteeVerdict("a", false, [])),
            FleetOrchestratorWorker.DescribeAssignSource(new CommitteeVerdict("a", false, [Vote("x", false)])),
            FleetOrchestratorWorker.DescribeAssignSource(new CommitteeVerdict("a", false, [Vote("x", true)])),
        };

        Assert.Equal(3, cause.Distinct().Count());
        Assert.All(cause, c => Assert.StartsWith("default:", c, StringComparison.Ordinal));
    }
}
