using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// R1.4 — numero EFFETTIVO di tentativi per il Deflated Sharpe: i tentativi con rendimenti correlati
/// (griglia fitta di parametri, simboli gemelli) vengono clusterizzati e contano una volta sola, così
/// la soglia SR* non sovrastima il test multiplo. Verifica: serie indipendenti ⇒ N effettivo = N
/// nominale; gruppi correlati ⇒ N effettivo = numero di gruppi; soglia 1 disattiva; e la conseguenza
/// a valle (DSR con N effettivo &lt; DSR con N nominale gonfiato) — il gate diventa correttamente meno
/// severo quando i tentativi sono ridondanti.
/// </summary>
public class EffectiveTrialsTests
{
    /// <summary>Serie sinusoidale + rumore deterministico: base per costruire gruppi (anti-)correlati.</summary>
    private static double[] Wave(int n, double phase, double noiseScale, int seed)
    {
        var rnd = new Random(seed);
        var s = new double[n];
        for (var i = 0; i < n; i++)
            s[i] = Math.Sin(0.3 * i + phase) + (rnd.NextDouble() - 0.5) * noiseScale;
        return s;
    }

    [Fact]
    public void IndependentSeries_EffectiveEqualsNominal()
    {
        // 6 serie di rumore indipendente (seed distinti) ⇒ correlazioni ~0 ⇒ nessun collasso.
        var trials = Enumerable.Range(0, 6)
            .Select(k => (IReadOnlyList<double>)Wave(200, phase: 0, noiseScale: 100, seed: k + 1))
            .ToList();

        Assert.Equal(6, EffectiveTrials.Count(trials, correlationThreshold: 0.5));
    }

    [Fact]
    public void ThreeCorrelatedGroups_CollapseToThree()
    {
        // 9 serie in 3 gruppi: dentro un gruppo condividono la stessa fase (quasi-identiche, ρ≈1),
        // fra gruppi le fasi differiscono ⇒ correlazione bassa. Atteso: 3 trial effettivi.
        var trials = new List<IReadOnlyList<double>>();
        double[] phases = [0.0, 2.0, 4.0];
        foreach (var ph in phases)
            for (var rep = 0; rep < 3; rep++)
                trials.Add(Wave(300, ph, noiseScale: 0.05, seed: 1000 + (int)(ph * 10) + rep));

        Assert.Equal(3, EffectiveTrials.Count(trials, correlationThreshold: 0.7));
    }

    [Fact]
    public void ThresholdOne_DisablesCollapsing()
    {
        // Stesse serie correlate del test precedente ma con soglia 1 (disattivo) ⇒ resta N nominale.
        var trials = new List<IReadOnlyList<double>>();
        double[] phases = [0.0, 2.0, 4.0];
        foreach (var ph in phases)
            for (var rep = 0; rep < 3; rep++)
                trials.Add(Wave(300, ph, noiseScale: 0.05, seed: 2000 + (int)(ph * 10) + rep));

        Assert.Equal(9, EffectiveTrials.Count(trials, correlationThreshold: 1.0));
    }

    [Fact]
    public void DegenerateAndShortSeries_StayDistinct()
    {
        // Varianza nulla e serie troppo corte non correlano con nessuno ⇒ tentativi distinti (conservativo).
        var trials = new List<IReadOnlyList<double>>
        {
            new double[50],                 // tutta zero (varianza nulla)
            new double[] { 1.0 },           // troppo corta
            Wave(50, 0, 1, 7),
        };

        Assert.Equal(3, EffectiveTrials.Count(trials));
    }

    [Fact]
    public void SingleOrEmpty_ReturnsCount()
    {
        Assert.Equal(0, EffectiveTrials.Count([]));
        Assert.Equal(1, EffectiveTrials.Count([new double[] { 0.1, 0.2, 0.3 }]));
    }

    [Fact]
    public void EffectiveTrials_RaisesDeflatedSharpe_VsInflatedNominal()
    {
        // Con N nominale gonfiato la soglia SR* del massimo atteso è più alta ⇒ il DSR è troppo severo;
        // con N effettivo (pochi cluster) SR* cala e il DSR cresce. Input scalari espliciti (Sharpe
        // osservato modesto, T contenuto) così il PSR resta strettamente in (0,1) e la direzione è netta.
        const double observed = 0.15, skew = 0.0, kurt = 3.0, variance = 0.0004; // sigma trial = 0.02
        const int observations = 200;

        var dsrNominal = DeflatedSharpeRatio.Deflated(observed, observations, skew, kurt, variance, trials: 40);
        var dsrEffective = DeflatedSharpeRatio.Deflated(observed, observations, skew, kurt, variance, trials: 5);

        Assert.InRange(dsrNominal, 0.001, 0.999);   // non saturato
        Assert.InRange(dsrEffective, 0.001, 0.999); // non saturato
        Assert.True(dsrEffective > dsrNominal,
            $"N effettivo deve alzare il DSR: eff={dsrEffective:F4} > nom={dsrNominal:F4}");
    }
}
