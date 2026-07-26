using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R4 — ROADMAP-RENDIMENTO] Lo smoother sticky-HMM. Le proprietà difese sono quelle che rendono
/// la misura del gate credibile: il rumore isolato sparisce, i cambi VERI sopravvivono, ρ più alto
/// significa tratti più lunghi, e la variante causale non guarda avanti — perché se lo facesse, la
/// persistenza misurata sarebbe un altro numero travestito da quello giusto.
/// </summary>
public class StickyHmmSmootherTests
{
    [Fact]
    public void ConstantSequence_StaysConstant()
    {
        var raw = Enumerable.Repeat(2, 500).ToList();

        var decoded = StickyHmmSmoother.Decode(raw, k: 4, rho: 0.99, emissionAccuracy: 0.75);

        Assert.All(decoded, s => Assert.Equal(2, s));
    }

    [Fact]
    public void IsolatedNoise_IsRemoved()
    {
        // Stato 1 con il 10% di barre sporcate a caso: la decodifica deve restituire lo stato vero,
        // non inseguire ogni flip — è l'intera ragione per cui l'HMM esiste.
        var rnd = new Random(11);
        var raw = Enumerable.Range(0, 1000)
            .Select(_ => rnd.NextDouble() < 0.10 ? rnd.Next(4) : 1)
            .ToList();

        var decoded = StickyHmmSmoother.Decode(raw, k: 4, rho: 0.995, emissionAccuracy: 0.75);

        Assert.True(decoded.Count(s => s == 1) >= 990,
            $"il rumore isolato doveva sparire: {decoded.Count(s => s != 1)} barre fuori stato");
    }

    [Fact]
    public void AGenuineSwitch_Survives()
    {
        // 500 barre di stato 0, poi 500 di stato 3: un cambio VERO deve restare — uno smoother che
        // cancella anche i cambi reali non allunga la persistenza, cancella l'informazione.
        var raw = Enumerable.Repeat(0, 500).Concat(Enumerable.Repeat(3, 500)).ToList();

        var decoded = StickyHmmSmoother.Decode(raw, k: 4, rho: 0.995, emissionAccuracy: 0.75);
        var runs = StickyHmmSmoother.RunLengths(decoded);

        Assert.Equal(2, runs.Count);
        Assert.Equal(0, decoded[0]);
        Assert.Equal(3, decoded[^1]);
    }

    [Fact]
    public void HigherRho_NeverIncreasesTransitions()
    {
        // Monotonia del parametro che governa il gate: più sticky = tratti uguali o più lunghi.
        var rnd = new Random(23);
        var raw = new List<int>();
        var state = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (rnd.NextDouble() < 0.02) state = rnd.Next(4);
            raw.Add(rnd.NextDouble() < 0.15 ? rnd.Next(4) : state);
        }

        int Transitions(double rho) =>
            StickyHmmSmoother.RunLengths(StickyHmmSmoother.Decode(raw, 4, rho, 0.75)).Count;

        Assert.True(Transitions(0.998) <= Transitions(0.99),
            "ρ più alto non può produrre PIÙ transizioni di ρ più basso");
    }

    [Fact]
    public void CausalDecode_DoesNotLookAhead()
    {
        // La proprietà che permette al router di usarla: lo stato alla barra t non può cambiare se
        // cambia il FUTURO. Si decodificano i primi 300 punti da soli e dentro una sequenza più
        // lunga il cui seguito è tutto di un altro stato: i primi 300 devono coincidere.
        var raw = Enumerable.Repeat(1, 300).Concat(Enumerable.Repeat(2, 300)).ToList();

        var soloPrefisso = StickyHmmSmoother.DecodeCausal(raw.Take(300).ToList(), 4, 0.995, 0.75);
        var conFuturo = StickyHmmSmoother.DecodeCausal(raw, 4, 0.995, 0.75);

        Assert.Equal(soloPrefisso, conFuturo.Take(300));
    }

    [Fact]
    public void CausalDecode_ReactsToARealSwitch_WithFiniteDelay()
    {
        // Il filtro causale paga il ritardo di conferma: deve agganciare il nuovo stato entro un
        // ritardo finito e piccolo rispetto a 1/(1-rho), non restare incollato per sempre.
        var raw = Enumerable.Repeat(0, 400).Concat(Enumerable.Repeat(3, 400)).ToList();

        var decoded = StickyHmmSmoother.DecodeCausal(raw, 4, 0.995, 0.75);

        var switchIndex = Array.FindIndex(decoded, s => s == 3);
        Assert.InRange(switchIndex, 400, 430);   // aggancio entro ~30 barre dal cambio vero
        Assert.All(decoded.Skip(switchIndex), s => Assert.Equal(3, s));
    }

    [Fact]
    public void RunLengths_MeasuresWhatTheGateNeeds()
    {
        Assert.Equal([3, 2, 1], StickyHmmSmoother.RunLengths([5, 5, 5, 1, 1, 5]));
        Assert.Empty(StickyHmmSmoother.RunLengths([]));
    }

    [Fact]
    public void GarbageIn_FailsLoudly()
    {
        Assert.Throws<ArgumentException>(() => StickyHmmSmoother.Decode([0, -1, 2], 4, 0.99, 0.75));
        Assert.Throws<ArgumentOutOfRangeException>(() => StickyHmmSmoother.Decode([0, 1], 4, 1.0, 0.75));
        Assert.Throws<ArgumentOutOfRangeException>(() => StickyHmmSmoother.Decode([0, 1], 4, 0.99, 0.25)); // <= 1/k
    }
}
