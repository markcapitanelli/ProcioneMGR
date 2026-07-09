using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Tests;

/// <summary>
/// P0-3: il gate anti-overfitting universale applicato in HoldoutValidation. Verifica i tre
/// comportamenti-cardine su dati sintetici (nessun DB/backtest): rumore/edge debole ⇒ scartato via
/// Deflated Sharpe; edge forte con pochi tentativi ⇒ sopravvive; pannello di rumore ⇒ PBO calcolato
/// e tutti i candidati filtrati.
/// </summary>
public class OverfittingGateTests
{
    private static ValidatedCandidate Candidate(string sym, decimal selectionSharpe, bool survived = true)
        => new()
        {
            StrategyName = "Composite",
            Symbol = sym,
            Timeframe = "1d",
            SelectionSharpe = selectionSharpe,
            Survived = survived,
        };

    [Fact]
    public void Apply_FlatHoldout_RejectsOnLowDeflatedSharpe()
    {
        // Un solo candidato vivo con rendimenti holdout piatti (media ~0) ⇒ un trial ⇒ SR*=0 ⇒
        // DSR = PSR(0) ≈ 0.5 ≤ 0.95 ⇒ scartato dal gate.
        var validated = new List<ValidatedCandidate> { Candidate("BTCUSDT", 1.0m) };
        var flat = new double[8];
        for (var i = 0; i < flat.Length; i++) flat[i] = i % 2 == 0 ? 0.001 : -0.001;

        var result = OverfittingGate.Apply(validated, [flat], minDeflatedSharpe: 0.95, maxPbo: 0.5);

        Assert.False(validated[0].Survived);
        Assert.Contains("DSR", validated[0].RejectReason);
        Assert.Equal(0, result.Survivors);
    }

    [Fact]
    public void Apply_StrongSteadyHoldoutFewTrials_Survives()
    {
        var validated = new List<ValidatedCandidate>
        {
            Candidate("BTCUSDT", 1.0m),
            Candidate("ETHUSDT", 1.1m, survived: false),
            Candidate("SOLUSDT", 0.9m, survived: false),
        };
        // Rendimenti holdout forti e stabili; serie corta (<10) ⇒ nessun PBO a interferire.
        var strong = new double[9];
        for (var i = 0; i < strong.Length; i++) strong[i] = 0.003 + (i % 2 == 0 ? 0.0005 : -0.0005);
        var returns = new List<double[]> { strong, new double[9], new double[9] };

        var result = OverfittingGate.Apply(validated, returns, 0.95, 0.5);

        Assert.True(validated[0].Survived, $"DSR={validated[0].DeflatedSharpe}");
        Assert.NotNull(validated[0].DeflatedSharpe);
        Assert.True(validated[0].DeflatedSharpe > 0.95);
        Assert.Equal(1, result.Survivors);
    }

    [Fact]
    public void Apply_LongNoisePanel_ComputesPbo_AndFiltersAll()
    {
        var rng = new Random(42);
        var validated = new List<ValidatedCandidate>();
        var returns = new List<double[]>();
        for (var s = 0; s < 12; s++)
        {
            validated.Add(Candidate($"SYM{s}", (decimal)(rng.NextDouble() * 2 - 0.5)));
            var series = new double[200];
            for (var i = 0; i < series.Length; i++) series[i] = 0.01 * NextGaussian(rng);
            returns.Add(series);
        }

        var result = OverfittingGate.Apply(validated, returns, 0.95, 0.5);

        Assert.NotNull(result.PanelPbo);                        // PBO calcolato (serie ≥ 10 punti)
        Assert.All(validated, v => Assert.NotNull(v.PanelPbo)); // memorizzato su ogni candidato
        Assert.All(validated, v => Assert.False(v.Survived));   // rumore ⇒ tutti filtrati
        Assert.Equal(0, result.Survivors);
    }

    /// <summary>Normale standard via Box–Muller (deterministica dato il seed).</summary>
    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
