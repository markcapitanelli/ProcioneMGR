using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D3, il gate] Verifica del giudice che risponde alla domanda di C5 §3.3: il book aggiunge
/// informazione OLTRE al proxy trade-flow?
///
/// Tre livelli, gli stessi che la piattaforma pretende da qualunque misura nuova (vedi
/// docs/STANDARD-VERIFICA.md):
///  1. RIFERIMENTO INDIPENDENTE — la correlazione parziale calcolata per due strade diverse
///     (formula chiusa vs residui di una regressione) deve dare lo stesso numero;
///  2. EDGE PIANTATO — se l'informazione incrementale c'è, il gate DEVE trovarla, altrimenti un
///     esito negativo su dati veri direbbe solo "il giudice non funziona";
///  3. RUMORE PURO — su tanti semi diversi il gate non deve produrre nemmeno un falso positivo.
/// </summary>
public class IncrementalIcGateTests
{
    /// <summary>
    /// Costruisce n osservazioni dove il rendimento forward dipende dal proxy con peso
    /// <paramref name="proxyWeight"/> e dal candidato con peso <paramref name="candidateWeight"/>.
    /// Il candidato è INDIPENDENTE dal proxy: così "informazione incrementale" ha un valore noto per
    /// costruzione, ed è zero esattamente quando candidateWeight è zero.
    /// </summary>
    private static (double[] Proxy, double[] Candidate, double[] Forward) Build(
        int n, double proxyWeight, double candidateWeight, int seed)
    {
        var rnd = new Random(seed);
        var proxy = new double[n];
        var candidate = new double[n];
        var forward = new double[n];

        for (var i = 0; i < n; i++)
        {
            proxy[i] = Gauss(rnd);
            candidate[i] = Gauss(rnd);
            forward[i] = proxyWeight * proxy[i] + candidateWeight * candidate[i] + Gauss(rnd);
        }
        return (proxy, candidate, forward);
    }

    private static double Gauss(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static IncrementalIcReport Run(double[] proxy, double[] candidate, double[] forward,
        IncrementalIcConfig? config = null, string name = "candidato")
    {
        return IncrementalIcGate.Run(
            proxy,
            new Dictionary<int, IReadOnlyList<double>> { [1] = forward },
            [new IcCandidate(name, candidate)],
            config);
    }

    // --- 1. Riferimento indipendente ---------------------------------------------------------------

    [Fact]
    public void PartialSpearman_TwoIndependentRoutesGiveTheSameNumber()
    {
        // Formula chiusa (ρxy − ρxz·ρyz)/√((1−ρxz²)(1−ρyz²)) contro i residui di una regressione sui
        // ranghi. Sono due strade completamente diverse per la stessa quantità: se coincidono, la
        // probabilità che entrambe siano sbagliate nello stesso modo è trascurabile. È il metodo con
        // cui è stato verificato TreeSHAP in D1 (Shapley per forza bruta).
        var (proxy, candidate, forward) = Build(1500, proxyWeight: 0.6, candidateWeight: 0.3, seed: 11);

        var closedForm = IncrementalIcGate.PartialSpearman(candidate, forward, proxy);
        var byResidual = IncrementalIcGate.PartialSpearmanByResidual(candidate, forward, proxy);

        Assert.Equal(closedForm, byResidual, 10);
        Assert.True(Math.Abs(closedForm) > 0.1, $"l'edge piantato deve vedersi: {closedForm:F4}");
    }

    [Fact]
    public void PartialSpearman_WhenTheCandidateIsTheProxy_IsZeroNotUndefined()
    {
        var (proxy, _, forward) = Build(800, 0.8, 0, seed: 5);

        // Un candidato identico al proxy non ha informazione incrementale: il denominatore degenera e
        // il gate deve leggere zero invece di propagare un NaN che passerebbe qualunque soglia.
        var partial = IncrementalIcGate.PartialSpearman(proxy, forward, proxy);

        Assert.Equal(0d, partial);
        Assert.False(double.IsNaN(partial));
    }

    [Fact]
    public void PartialSpearman_RemovesWhatTheProxyAlreadyExplains()
    {
        // Il candidato è una copia rumorosa del proxy e il rendimento dipende SOLO dal proxy: l'IC
        // grezzo del candidato è alto (eredita quello del proxy), quello parziale deve crollare.
        var rnd = new Random(3);
        var (proxy, _, forward) = Build(2000, proxyWeight: 1.0, candidateWeight: 0, seed: 3);
        var echo = proxy.Select(p => p + 0.2 * Gauss(rnd)).ToArray();

        var raw = Correlation.Spearman(echo, forward);
        var partial = IncrementalIcGate.PartialSpearman(echo, forward, proxy);

        Assert.True(Math.Abs(raw) > 0.25, $"l'IC grezzo deve essere alto: {raw:F4}");
        Assert.True(Math.Abs(partial) < 0.06, $"l'IC parziale deve crollare: {partial:F4}");
    }

    // --- 2. Edge piantato -------------------------------------------------------------------------

    [Fact]
    public void APlantedIncrementalEdge_IsFound()
    {
        var (proxy, candidate, forward) = Build(4000, proxyWeight: 0.5, candidateWeight: 0.35, seed: 21);

        var report = Run(proxy, candidate, forward);

        Assert.True(report.AnyAddsInformation, report.Verdict);
        var outcome = report.Outcomes[0];
        Assert.True(Math.Abs(outcome.PartialIc) > outcome.NullBestPercentile);
        Assert.True(Math.Abs(outcome.PartialIc) >= outcome.Threshold);
        Assert.Contains("AGGIUNGE", outcome.Message);
    }

    [Fact]
    public void ACandidateThatOnlyEchoesTheProxy_IsRejected()
    {
        // Il caso che decide il gate di C5: se il book dice esattamente ciò che dice già il proxy,
        // raccoglierlo è pagare per un doppione. Qui il candidato è il proxy più un filo di rumore.
        var rnd = new Random(9);
        var (proxy, _, forward) = Build(4000, proxyWeight: 0.6, candidateWeight: 0, seed: 9);
        var echo = proxy.Select(p => p + 0.15 * Gauss(rnd)).ToArray();

        var report = Run(proxy, echo, forward, name: "eco-del-proxy");

        Assert.False(report.AnyAddsInformation, report.Verdict);
        Assert.Contains("NEGATIVO", report.Verdict);
        // L'IC grezzo del candidato è alto: è esattamente la trappola che la correlazione parziale
        // esiste per evitare.
        Assert.True(Math.Abs(report.Outcomes[0].RawIc) > 0.15);
    }

    // --- 3. Rumore puro ---------------------------------------------------------------------------

    [Fact]
    public void PureNoise_FalsePositiveRate_StaysAtItsNominalLevel()
    {
        // Si misura il TASSO di falsi positivi su tanti semi, come nel test del rumore del
        // meta-modello (C4.b): una singola estrazione non dice niente sul comportamento del giudice.
        //
        // PERCHÉ LA SOGLIA NON È ZERO. Un test al livello dell'1% PRODUCE falsi positivi nell'1% dei
        // casi: è la sua definizione, non un difetto. Pretendere zero su 30 semi significherebbe
        // pretendere dal giudice più di quanto dichiara, e renderebbe questo test fragile —
        // fallirebbe a caso una volta su quattro. Si controlla invece che il tasso osservato resti
        // compatibile col livello nominale: con 30 semi al 1% ne attende 0,3, e vederne 3 o più
        // avrebbe probabilità sotto l'1% (cioè indicherebbe un giudice davvero rotto).
        //
        // Questo test ha già trovato un difetto vero, che resta corretto nel codice: la prima
        // versione confrontava l'|IC parziale| col 99° percentile di 200 giri — una stima ricavata
        // dal secondo/terzo valore più grande, quindi rumorosissima — e il tasso osservato era
        // 3,3%, oltre tre volte il nominale. Ora la decisione passa dal p-value empirico con
        // correzione +1, che usa tutti i giri.
        var falsePositives = 0;
        for (var seed = 100; seed < 130; seed++)
        {
            var (proxy, candidate, forward) = Build(2000, proxyWeight: 0.4, candidateWeight: 0, seed);
            if (Run(proxy, candidate, forward).AnyAddsInformation) falsePositives++;
        }

        Assert.True(falsePositives <= 2,
            $"tasso di falsi positivi troppo alto su rumore puro: {falsePositives}/30 (atteso ~0,3 al livello dell'1%)");
    }

    [Fact]
    public void AnEdgeTooSmallToPayTheCosts_IsRejectedEvenIfStatisticallyReal()
    {
        // n grande rende il pavimento di rumore minuscolo, quindi un IC di 0,01 può essere
        // statisticamente reale. Il gate lo respinge comunque: sotto il minimo economico non è un
        // segnale, è una curiosità — la stessa regola del pavimento di rilevanza di D4.
        var (proxy, candidate, forward) = Build(40_000, proxyWeight: 0.4, candidateWeight: 0.012, seed: 42);

        var report = Run(proxy, candidate, forward);

        var outcome = report.Outcomes[0];
        Assert.True(Math.Abs(outcome.PartialIc) < 0.02, $"IC parziale atteso piccolo: {outcome.PartialIc:F4}");
        Assert.False(report.AnyAddsInformation);
        Assert.Contains("sotto la soglia operativa", outcome.Message);
    }

    // --- Più controlli: "aggiunge oltre a COSA" ---------------------------------------------------

    [Fact]
    public void PartialSpearmanMulti_WithOneControl_AgreesWithTheTwoSeriesFormula()
    {
        var (proxy, candidate, forward) = Build(1200, 0.5, 0.3, seed: 31);

        var single = IncrementalIcGate.PartialSpearman(candidate, forward, proxy);
        var multi = IncrementalIcGate.PartialSpearmanMulti(candidate, forward, [proxy]);

        Assert.Equal(single, multi, 10);
    }

    [Fact]
    public void WithTwoControls_ACandidateThatIsAMixOfThem_IsRejected()
    {
        // È il caso che ha motivato i controlli multipli: uno sbilanciamento di book può risultare
        // informativo solo perché è il rendimento recente travestito. Qui il candidato è una
        // combinazione dei due controlli più rumore, e il rendimento futuro dipende da entrambi:
        // informazione incrementale vera = zero.
        var rnd = new Random(4);
        var n = 4000;
        var (proxy, pastReturn, _) = Build(n, 0, 0, seed: 4);
        var forward = new double[n];
        for (var i = 0; i < n; i++) forward[i] = 0.4 * proxy[i] + 0.4 * pastReturn[i] + Gauss(rnd);
        var mix = Enumerable.Range(0, n).Select(i => 0.6 * proxy[i] + 0.5 * pastReturn[i] + 0.2 * Gauss(rnd)).ToArray();

        var controls = new List<IcCandidate> { new("proxy", proxy), new("rendimento recente", pastReturn) };
        var forwards = new Dictionary<int, IReadOnlyList<double>> { [1] = forward };

        var withBoth = IncrementalIcGate.Run(controls, forwards, [new IcCandidate("misto", mix)]);
        var withProxyOnly = IncrementalIcGate.Run(proxy, forwards, [new IcCandidate("misto", mix)]);

        Assert.False(withBoth.AnyAddsInformation, withBoth.Verdict);
        // E il punto del test: col solo proxy come controllo il candidato passerebbe, perché la parte
        // che spiega davvero il rendimento futuro è quella del rendimento recente.
        Assert.True(withProxyOnly.AnyAddsInformation,
            $"con un solo controllo l'artefatto deve passare, altrimenti il test non dimostra niente: {withProxyOnly.Verdict}");
    }

    [Fact]
    public void WithTwoControls_AGenuinelyNewSignal_IsStillFound()
    {
        var rnd = new Random(6);
        var n = 4000;
        var (proxy, pastReturn, _) = Build(n, 0, 0, seed: 6);
        var fresh = Enumerable.Range(0, n).Select(_ => Gauss(rnd)).ToArray();
        var forward = new double[n];
        for (var i = 0; i < n; i++) forward[i] = 0.4 * proxy[i] + 0.3 * pastReturn[i] + 0.35 * fresh[i] + Gauss(rnd);

        var report = IncrementalIcGate.Run(
            [new IcCandidate("proxy", proxy), new IcCandidate("rendimento recente", pastReturn)],
            new Dictionary<int, IReadOnlyList<double>> { [1] = forward },
            [new IcCandidate("nuovo", fresh)]);

        Assert.True(report.AnyAddsInformation, report.Verdict);
    }

    [Fact]
    public void WithNoControls_TheGateRefusesToJudge()
    {
        // "Aggiunge informazione oltre a cosa?" senza controlli non è una domanda.
        var (_, candidate, forward) = Build(600, 0.3, 0.3, seed: 2);

        Assert.Throws<ArgumentException>(() => IncrementalIcGate.Run(
            new List<IcCandidate>(),
            new Dictionary<int, IReadOnlyList<double>> { [1] = forward },
            [new IcCandidate("x", candidate)]));
    }

    // --- Struttura del nullo ----------------------------------------------------------------------

    [Fact]
    public void TheNullOfTheBest_IsStricterThanTheNullOfASingleCandidate()
    {
        // Giustificazione del disegno: il massimo di N misure rumorose è più grande di ciascuna.
        // Confrontare il migliore di otto candidati con la soglia di uno solo è il modo classico di
        // fabbricare una scoperta — e la piattaforma ci è già cascata una volta (t = 141 su asset
        // correlati, vedi ricerca-dosaggio).
        var rnd = new Random(77);
        var n = 2000;
        var (proxy, first, forward) = Build(n, 0.4, 0, seed: 77);
        var many = new List<IcCandidate> { new("c0", first) };
        for (var k = 1; k < 8; k++)
        {
            many.Add(new IcCandidate($"c{k}", Enumerable.Range(0, n).Select(_ => Gauss(rnd)).ToArray()));
        }

        var forwards = new Dictionary<int, IReadOnlyList<double>> { [1] = forward };
        var single = IncrementalIcGate.Run(proxy, forwards, [many[0]]);
        var family = IncrementalIcGate.Run(proxy, forwards, many);

        Assert.True(family.NullBestPercentile > single.NullBestPercentile,
            $"la soglia della famiglia ({family.NullBestPercentile:F4}) deve superare quella singola ({single.NullBestPercentile:F4})");
    }

    [Fact]
    public void RowsWhereAnythingIsMissing_AreDroppedForEveryone()
    {
        // Maschera comune: se ogni candidato usasse le proprie barre valide, quello più "presente"
        // vincerebbe in parte per numerosità e i confronti non sarebbero comparabili.
        var (proxy, candidate, forward) = Build(1200, 0.4, 0.3, seed: 13);
        var sparse = candidate.ToArray();
        for (var i = 0; i < sparse.Length; i += 3) sparse[i] = double.NaN;

        var report = IncrementalIcGate.Run(
            proxy,
            new Dictionary<int, IReadOnlyList<double>> { [1] = forward },
            [new IcCandidate("sparso", sparse), new IcCandidate("pieno", candidate)]);

        Assert.All(report.Outcomes, o => Assert.Equal(report.Observations, o.Observations));
        Assert.True(report.Observations < 1200);
    }

    [Fact]
    public void TooFewObservations_DeclaresInsteadOfGuessing()
    {
        var (proxy, candidate, forward) = Build(120, 0.4, 0.4, seed: 1);

        var report = Run(proxy, candidate, forward);

        Assert.False(report.AnyAddsInformation);
        Assert.Empty(report.Outcomes);
        Assert.Contains("insufficienti", report.Verdict);
    }

    [Fact]
    public void TheGateIsDeterministic_SameInputSameVerdict()
    {
        var (proxy, candidate, forward) = Build(2500, 0.4, 0.25, seed: 8);

        var a = Run(proxy, candidate, forward);
        var b = Run(proxy, candidate, forward);

        Assert.Equal(a.NullBestPercentile, b.NullBestPercentile, 12);
        Assert.Equal(a.Outcomes[0].PartialIc, b.Outcomes[0].PartialIc, 12);
        Assert.Equal(a.Verdict, b.Verdict);
    }
}
