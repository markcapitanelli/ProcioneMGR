using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RiskFactorPca"/>: casi limite noti (correlazione perfetta -> una sola
/// componente spiega tutta la varianza; simboli indipendenti -> varianza spiegata ripartita),
/// proprietà strutturali (autovettori normalizzati, lunghezza degli score) e validazione input.
/// </summary>
public class RiskFactorPcaTests
{
    private readonly IRiskFactorPca _pca = new RiskFactorPca();

    private static List<decimal> RandomSeries(int n, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (decimal)(rnd.NextDouble() * 2 - 1)).ToList();
    }

    [Fact]
    public void PerfectlyCorrelatedSymbols_FirstComponentExplainsAllVariance()
    {
        var a = RandomSeries(300, seed: 1);
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = a,
            ["B"] = a, // identico -> correlazione = 1
        };

        var result = _pca.Compute(returns, componentCount: 2);

        Assert.Equal(2, result.Components.Count);
        Assert.True(result.Components[0].ExplainedVarianceRatio > 0.999,
            $"Prima componente attesa ~1.0, ottenuto {result.Components[0].ExplainedVarianceRatio:F4}");
        Assert.True(result.Components[1].ExplainedVarianceRatio < 0.001,
            $"Seconda componente attesa ~0.0, ottenuto {result.Components[1].ExplainedVarianceRatio:F4}");
    }

    [Fact]
    public void IndependentSymbols_VarianceIsSpreadAcrossComponents()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(2000, seed: 10),
            ["B"] = RandomSeries(2000, seed: 20),
            ["C"] = RandomSeries(2000, seed: 30),
        };

        var result = _pca.Compute(returns, componentCount: 3);

        // Con 3 simboli scorrelati la varianza si ripartisce ~1/3 a testa: nessuna componente
        // dovrebbe dominare nettamente come nel caso perfettamente correlato.
        Assert.True(result.Components[0].ExplainedVarianceRatio < 0.6,
            $"Prima componente troppo dominante per simboli indipendenti: {result.Components[0].ExplainedVarianceRatio:F3}");
        Assert.True(Math.Abs(result.TotalExplainedVarianceRatio - 1.0) < 1e-6);
    }

    [Fact]
    public void Loadings_AreUnitNormalized_PerComponent()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(500, seed: 1),
            ["B"] = RandomSeries(500, seed: 2),
            ["C"] = RandomSeries(500, seed: 3),
        };

        var result = _pca.Compute(returns, componentCount: 3);

        foreach (var c in result.Components)
        {
            var sumOfSquares = c.Loadings.Values.Sum(v => v * v);
            Assert.True(Math.Abs(sumOfSquares - 1.0) < 1e-6, $"Componente {c.Index}: somma quadrati loading = {sumOfSquares:F4}");
        }
    }

    [Fact]
    public void Scores_HaveSameLengthAsInputSeries()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(150, seed: 5),
            ["B"] = RandomSeries(150, seed: 6),
        };

        var result = _pca.Compute(returns, componentCount: 1);
        Assert.Equal(150, result.Components[0].Scores.Count);
    }

    [Fact]
    public void ExplainedVarianceRatios_AreDescending()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(500, seed: 1),
            ["B"] = RandomSeries(500, seed: 2),
            ["C"] = RandomSeries(500, seed: 3),
            ["D"] = RandomSeries(500, seed: 4),
        };

        var result = _pca.Compute(returns, componentCount: 4);
        for (var i = 1; i < result.Components.Count; i++)
        {
            Assert.True(result.Components[i - 1].ExplainedVarianceRatio >= result.Components[i].ExplainedVarianceRatio);
        }
    }

    [Fact]
    public void TooFewSymbols_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>> { ["A"] = RandomSeries(50, 1) };
        Assert.Throws<ArgumentException>(() => _pca.Compute(returns, 1));
    }

    [Fact]
    public void MismatchedSeriesLengths_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(100, 1),
            ["B"] = RandomSeries(90, 2),
        };
        Assert.Throws<ArgumentException>(() => _pca.Compute(returns, 1));
    }

    [Fact]
    public void TooFewObservations_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(2, 1),
            ["B"] = RandomSeries(2, 2),
        };
        Assert.Throws<ArgumentException>(() => _pca.Compute(returns, 1));
    }

    [Fact]
    public void ComponentCountOutOfRange_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = RandomSeries(100, 1),
            ["B"] = RandomSeries(100, 2),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => _pca.Compute(returns, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _pca.Compute(returns, 3));
    }
}
