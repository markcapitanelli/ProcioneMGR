using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — l'HRP ora usa un linkage configurabile (default Average/UPGMA) invece del solo single-linkage
/// dell'articolo originale, che soffre di "chaining". Verifica il default, la validità dei pesi e che
/// la scelta del linkage sia effettivamente cablata (su una struttura a catena Single ≠ Average).
/// </summary>
public class HrpLinkageTests
{
    /// <summary>
    /// Due cluster stretti {A,B} (fattore f1) e {C,D} (fattore f2) più un asset-ponte E correlato con
    /// entrambi: il single-linkage tende a incatenare i cluster via E, l'average li tiene separati.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<decimal>> BridgedClusters(int n, int seed)
    {
        var rnd = new Random(seed);
        var a = new List<decimal>(n);
        var b = new List<decimal>(n);
        var c = new List<decimal>(n);
        var d = new List<decimal>(n);
        var e = new List<decimal>(n);
        for (var i = 0; i < n; i++)
        {
            var f1 = rnd.NextDouble() - 0.5;
            var f2 = rnd.NextDouble() - 0.5;
            double Noise() => (rnd.NextDouble() - 0.5) * 0.15;
            a.Add((decimal)(2.0 * f1 + Noise()));
            b.Add((decimal)(2.0 * f1 + Noise()));
            c.Add((decimal)(2.0 * f2 + Noise()));
            d.Add((decimal)(2.0 * f2 + Noise()));
            e.Add((decimal)(f1 + f2 + Noise())); // ponte
        }
        return new() { ["A"] = a, ["B"] = b, ["C"] = c, ["D"] = d, ["E"] = e };
    }

    [Fact]
    public void Hrp_DefaultLinkage_IsAverage()
    {
        Assert.Equal(LinkageMethod.Average, new PortfolioOptimizationConfig().HrpLinkage);
    }

    [Fact]
    public void Hrp_AverageLinkage_ProducesValidWeights()
    {
        var hrp = new HierarchicalRiskParityOptimizer(new HierarchicalClustering());
        var result = hrp.Optimize(BridgedClusters(800, 1)); // default = Average

        Assert.Equal(5, result.Weights.Count);
        Assert.All(result.Weights.Values, w => Assert.True(w > 0m, $"peso non positivo: {w}"));
        Assert.True(Math.Abs(result.Weights.Values.Sum() - 1m) < 0.001m);
    }

    [Fact]
    public void SingleAndAverageLinkage_DifferAsAlgorithms_OnPathologicalChain()
    {
        // Average non è cosmetico: su una catena patologica (chaining) il single-linkage e l'average
        // producono dendrogrammi con ORDINE delle foglie diverso. Distanze costruite a mano: due coppie
        // strette {0,1} e {3,4} con un anello 2 equidistante, e 1-2 più vicino di quanto l'average tolleri.
        // Il ponte 2 è VICINISSIMO a 0 (min) ma lontano da 1: il single lo attacca a {0,1} (min 0.5),
        // l'average lo attacca a {3,4} (media 1.0 < media 1.25) ⇒ ordine delle foglie diverso.
        var d = new double[5, 5];
        double[,] pos =
        {
            { 0.0, 0.2, 0.5, 3.0, 3.0 }, // 0
            { 0.2, 0.0, 2.0, 3.0, 3.0 }, // 1  (0,1 stretti)
            { 0.5, 2.0, 0.0, 1.0, 1.0 }, // 2  (ponte)
            { 3.0, 3.0, 1.0, 0.0, 0.2 }, // 3  (3,4 stretti)
            { 3.0, 3.0, 1.0, 0.2, 0.0 }, // 4
        };
        for (var i = 0; i < 5; i++) for (var j = 0; j < 5; j++) d[i, j] = pos[i, j];

        var labels = new[] { "0", "1", "2", "3", "4" };
        var clustering = new HierarchicalClustering();
        var single = clustering.BuildDendrogram(d, labels, LinkageMethod.Single).LeafIndices.ToList();
        var average = clustering.BuildDendrogram(d, labels, LinkageMethod.Average).LeafIndices.ToList();

        Assert.NotEqual(single, average); // i due linkage sono algoritmi genuinamente diversi
    }
}
