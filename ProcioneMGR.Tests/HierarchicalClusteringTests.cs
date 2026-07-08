using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="HierarchicalClustering"/>: struttura del dendrogramma su una matrice di
/// distanza nota (2 coppie ben separate), differenza fra i criteri di linkage, e
/// <see cref="CorrelationDistance"/>.
/// </summary>
public class HierarchicalClusteringTests
{
    private readonly IHierarchicalClustering _clustering = new HierarchicalClustering();

    [Fact]
    public void FourPoints_TwoClearPairs_MergesWithinPairsFirst()
    {
        // 0-1 vicini (dist 1), 2-3 vicini (dist 1), tutto il resto lontano (dist 10).
        var d = new double[,]
        {
            { 0, 1, 10, 10 },
            { 1, 0, 10, 10 },
            { 10, 10, 0, 1 },
            { 10, 10, 1, 0 },
        };
        var labels = new[] { "A", "B", "C", "D" };

        var root = _clustering.BuildDendrogram(d, labels);

        Assert.Equal(4, root.LeafIndices.Count);
        Assert.False(root.IsLeaf);
        // La fusione finale (root) unisce le due coppie -> distanza alta (vicina a 10, con
        // average linkage esattamente 10 dato che le due coppie sono equidistanti).
        Assert.True(root.Distance > 5, $"Distanza root attesa alta, ottenuto {root.Distance}");

        // I due sotto-cluster diretti del root devono essere esattamente {A,B} e {C,D} (in un ordine qualsiasi).
        Assert.NotNull(root.Left);
        Assert.NotNull(root.Right);
        var leftSet = root.Left!.LeafIndices.OrderBy(i => i).ToArray();
        var rightSet = root.Right!.LeafIndices.OrderBy(i => i).ToArray();
        var sets = new[] { leftSet, rightSet }.OrderBy(s => s[0]).ToArray();
        Assert.Equal(new[] { 0, 1 }, sets[0]);
        Assert.Equal(new[] { 2, 3 }, sets[1]);

        // Le fusioni interne (0,1) e (2,3) devono essere avvenute a distanza 1 (i cluster foglia diretti).
        Assert.Equal(1.0, root.Left!.Distance);
        Assert.Equal(1.0, root.Right!.Distance);
    }

    [Theory]
    [InlineData(LinkageMethod.Single, 2.0)]
    [InlineData(LinkageMethod.Complete, 3.0)]
    [InlineData(LinkageMethod.Average, 2.5)]
    public void ThreePoints_LinkageMethod_ChangesSecondMergeDistance(LinkageMethod method, double expectedDistance)
    {
        // A-B=1 (si fondono per primi, sempre), poi {A,B} con C: dist(A,C)=3, dist(B,C)=2.
        var d = new double[,]
        {
            { 0, 1, 3 },
            { 1, 0, 2 },
            { 3, 2, 0 },
        };
        var root = _clustering.BuildDendrogram(d, ["A", "B", "C"], method);

        // Un figlio del root e' la foglia C, l'altro e' la fusione {A,B} avvenuta a distanza 1
        // (la coppia piu' vicina in assoluto, si fonde per prima indipendentemente dal linkage).
        var leaf = root.Left!.IsLeaf ? root.Left! : root.Right!;
        var pair = root.Left!.IsLeaf ? root.Right! : root.Left!;
        Assert.Equal("C", leaf.Label);
        Assert.Equal(1.0, pair.Distance, 6);
        Assert.Equal(new[] { 0, 1 }, pair.LeafIndices.OrderBy(i => i).ToArray());

        Assert.Equal(expectedDistance, root.Distance, 6);
    }

    [Fact]
    public void LeafNodes_HaveZeroDistanceAndSingleIndex()
    {
        var d = new double[,] { { 0, 5 }, { 5, 0 } };
        var root = _clustering.BuildDendrogram(d, ["X", "Y"]);

        Assert.True(root.Left!.IsLeaf);
        Assert.True(root.Right!.IsLeaf);
        Assert.Equal(0.0, root.Left!.Distance);
        Assert.Single(root.Left!.LeafIndices);
    }

    [Fact]
    public void MismatchedMatrixSize_Throws()
    {
        var d = new double[,] { { 0, 1 }, { 1, 0 } };
        Assert.Throws<ArgumentException>(() => _clustering.BuildDendrogram(d, ["A", "B", "C"]));
    }

    [Fact]
    public void TooFewLabels_Throws()
    {
        var d = new double[,] { { 0 } };
        Assert.Throws<ArgumentException>(() => _clustering.BuildDendrogram(d, ["A"]));
    }

    // --- CorrelationDistance -------------------------------------------------------------------

    [Fact]
    public void CorrelationDistance_PerfectCorrelation_IsZero()
    {
        var corr = new double[,] { { 1, 1 }, { 1, 1 } };
        var d = CorrelationDistance.FromCorrelationMatrix(corr);
        Assert.Equal(0.0, d[0, 1], 9);
    }

    [Fact]
    public void CorrelationDistance_PerfectAnticorrelation_IsOne()
    {
        var corr = new double[,] { { 1, -1 }, { -1, 1 } };
        var d = CorrelationDistance.FromCorrelationMatrix(corr);
        Assert.Equal(1.0, d[0, 1], 9);
    }

    [Fact]
    public void CorrelationDistance_ZeroCorrelation_IsSqrtHalf()
    {
        var corr = new double[,] { { 1, 0 }, { 0, 1 } };
        var d = CorrelationDistance.FromCorrelationMatrix(corr);
        Assert.Equal(Math.Sqrt(0.5), d[0, 1], 9);
    }

    [Fact]
    public void CorrelationDistance_NonSquareMatrix_Throws()
    {
        var corr = new double[,] { { 1, 0, 0 }, { 0, 1, 0 } };
        Assert.Throws<ArgumentException>(() => CorrelationDistance.FromCorrelationMatrix(corr));
    }
}
