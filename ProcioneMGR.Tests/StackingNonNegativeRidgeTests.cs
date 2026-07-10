using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — meta-learner dello stacking: pesi NON-NEGATIVI (i pesi negativi estrapolano male fuori campione)
/// e λ scelto per cross-validation invece che fisso. Verifica la proiezione di non-negatività (un base
/// che l'OLS peserebbe negativo viene azzerato), il recupero dei pesi su target additivo pulito, e che
/// la CV preferisca più regolarizzazione quando le predizioni base sono rumorose/collineari.
/// </summary>
public class StackingNonNegativeRidgeTests
{
    [Fact]
    public void NonNegativeRidge_ClampsWeightThatOlsWouldMakeNegative()
    {
        // Costruzione "suppressor": base0 = segnale + rumore, base1 = SOLO quel rumore, y = segnale.
        // L'OLS non vincolato porrebbe w0=+1, w1=-1 (sottrae il rumore via base1); il vincolo azzera w1.
        var rnd = new Random(1);
        var n = 500;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = rnd.NextDouble() - 0.5;                 // segnale
            var noise = (rnd.NextDouble() - 0.5) * 0.3;      // rumore
            x[i] = [s + noise, noise];                       // base0 = s+rumore, base1 = rumore
            y[i] = s;
        }

        var (w, _) = StackedReturnPredictor.FitNonNegativeRidge(x, y, lambda: 0.0);

        Assert.True(w[0] >= 0 && w[1] >= 0, $"pesi non-negativi: [{w[0]:F3}, {w[1]:F3}]");
        Assert.Equal(0.0, w[1], 6);                          // il peso che l'OLS renderebbe negativo è azzerato
        Assert.True(w[0] > 0.5, $"w0={w[0]:F3} deve restare positivo");
    }

    [Fact]
    public void NonNegativeRidge_RecoversAdditiveWeights()
    {
        // y = 0.3 + 0.6·base0 + 0.4·base1, base indipendenti ⇒ pesi recuperati (tutti ≥ 0).
        var rnd = new Random(2);
        var n = 600;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var b0 = rnd.NextDouble();
            var b1 = rnd.NextDouble();
            x[i] = [b0, b1];
            y[i] = 0.3 + 0.6 * b0 + 0.4 * b1;
        }

        var (w, intercept) = StackedReturnPredictor.FitNonNegativeRidge(x, y, lambda: 0.0);

        Assert.Equal(0.6, w[0], 2);
        Assert.Equal(0.4, w[1], 2);
        Assert.Equal(0.3, intercept, 2);
    }

    [Fact]
    public void NonNegativeRidge_AllWeightsNonNegative_OnNoisyBases()
    {
        var rnd = new Random(3);
        var n = 300;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = rnd.NextDouble() - 0.5;
            x[i] = [t + (rnd.NextDouble() - 0.5) * 0.4, -t + (rnd.NextDouble() - 0.5) * 0.4, (rnd.NextDouble() - 0.5)];
            y[i] = t;
        }

        var (w, _) = StackedReturnPredictor.FitNonNegativeRidge(x, y, lambda: 1.0);
        Assert.All(w, wi => Assert.True(wi >= 0.0, $"peso negativo: {wi}"));
    }

    [Fact]
    public void SelectLambdaByCv_PrefersMoreRegularization_WhenBasesAreNoisy()
    {
        // Predizioni base puro rumore rispetto al target ⇒ la CV deve preferire λ alta (pesi verso 0)
        // rispetto a λ minima, che sovradatterebbe il rumore.
        var rnd = new Random(4);
        var n = 500;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = [rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5];
            y[i] = rnd.NextDouble() - 0.5; // indipendente dalle base
        }

        var lambda = StackedReturnPredictor.SelectLambdaByCv(x, y, k: 3, fallbackLambda: 1.0);
        Assert.True(lambda >= 1.0, $"su base non informative la CV deve regolarizzare di più (λ={lambda})");
    }

    [Fact]
    public void SelectLambdaByCv_PrefersLessRegularization_WhenBasesAreInformative()
    {
        // Base che spiegano bene il target ⇒ la CV deve preferire λ bassa (lascia usare i pesi).
        var rnd = new Random(5);
        var n = 500;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var b0 = rnd.NextDouble();
            var b1 = rnd.NextDouble();
            x[i] = [b0, b1];
            y[i] = 0.7 * b0 + 0.3 * b1 + (rnd.NextDouble() - 0.5) * 0.01;
        }

        var lambda = StackedReturnPredictor.SelectLambdaByCv(x, y, k: 2, fallbackLambda: 1.0);
        Assert.True(lambda <= 0.3, $"su base informative la CV deve regolarizzare poco (λ={lambda})");
    }
}
