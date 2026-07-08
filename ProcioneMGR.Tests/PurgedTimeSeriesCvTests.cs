using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="PurgedTimeSeriesCv"/>: copertura completa del test set attraverso i fold,
/// nessuna sovrapposizione train/test, e correttezza delle bande di purge/embargo.
/// </summary>
public class PurgedTimeSeriesCvTests
{
    private readonly IPurgedTimeSeriesCv _cv = new PurgedTimeSeriesCv();

    [Fact]
    public void Split_ProducesRequestedNumberOfFolds()
    {
        var splits = _cv.Split(sampleCount: 100, folds: 5, purgeWindow: 0, embargoPeriods: 0);
        Assert.Equal(5, splits.Count);
    }

    [Fact]
    public void Split_TestSetsCoverAllSamplesExactlyOnce_NoPurgeEmbargo()
    {
        var splits = _cv.Split(sampleCount: 100, folds: 5, purgeWindow: 0, embargoPeriods: 0);

        var allTest = splits.SelectMany(s => s.TestIndices).OrderBy(i => i).ToList();
        Assert.Equal(Enumerable.Range(0, 100), allTest);
    }

    [Fact]
    public void Split_TestSetsAreContiguousAndNonOverlapping()
    {
        var splits = _cv.Split(sampleCount: 97, folds: 4, purgeWindow: 0, embargoPeriods: 0);

        foreach (var s in splits)
        {
            for (var k = 1; k < s.TestIndices.Count; k++)
            {
                Assert.Equal(s.TestIndices[k - 1] + 1, s.TestIndices[k]); // contiguo
            }
        }
        // Nessuna sovrapposizione fra i test set di fold diversi.
        var seen = new HashSet<int>();
        foreach (var s in splits)
        {
            foreach (var idx in s.TestIndices)
            {
                Assert.True(seen.Add(idx), $"Indice {idx} presente in più di un test set");
            }
        }
    }

    [Fact]
    public void Split_NoPurgeEmbargo_TrainIsExactlyComplementOfTest()
    {
        var splits = _cv.Split(sampleCount: 50, folds: 5, purgeWindow: 0, embargoPeriods: 0);
        foreach (var s in splits)
        {
            var expectedTrain = Enumerable.Range(0, 50).Except(s.TestIndices).OrderBy(i => i).ToList();
            Assert.Equal(expectedTrain, s.TrainIndices.OrderBy(i => i).ToList());
        }
    }

    [Fact]
    public void Split_WithPurgeAndEmbargo_TrainExcludesBandsAroundTest()
    {
        var splits = _cv.Split(sampleCount: 100, folds: 5, purgeWindow: 3, embargoPeriods: 2);
        // Fold 2: test = [40..60). Purge esclude [37..40), embargo esclude [60..62).
        var fold2 = splits.Single(s => s.Fold == 2);
        Assert.DoesNotContain(37, fold2.TrainIndices);
        Assert.DoesNotContain(39, fold2.TrainIndices);
        Assert.DoesNotContain(60, fold2.TrainIndices);
        Assert.DoesNotContain(61, fold2.TrainIndices);
        Assert.Contains(36, fold2.TrainIndices);  // fuori dalla banda di purge
        Assert.Contains(62, fold2.TrainIndices);  // fuori dalla banda di embargo
        Assert.DoesNotContain(45, fold2.TrainIndices); // dentro il test set stesso
    }

    [Fact]
    public void Split_TrainAndTest_NeverOverlap_ForAnyPurgeEmbargo()
    {
        var splits = _cv.Split(sampleCount: 120, folds: 6, purgeWindow: 4, embargoPeriods: 3);
        foreach (var s in splits)
        {
            var trainSet = s.TrainIndices.ToHashSet();
            foreach (var t in s.TestIndices)
            {
                Assert.False(trainSet.Contains(t), $"Fold {s.Fold}: indice {t} presente sia in train che in test");
            }
        }
    }

    [Fact]
    public void Split_LastFold_AbsorbsRemainder()
    {
        // 103 campioni / 5 fold = 20 resto 3 -> l'ultimo fold deve avere 20+3=23 elementi di test.
        var splits = _cv.Split(sampleCount: 103, folds: 5, purgeWindow: 0, embargoPeriods: 0);
        Assert.Equal(20, splits[0].TestIndices.Count);
        Assert.Equal(23, splits[^1].TestIndices.Count);
    }

    [Fact]
    public void Split_TooFewSamplesForFolds_Throws()
    {
        Assert.Throws<ArgumentException>(() => _cv.Split(sampleCount: 3, folds: 5, purgeWindow: 0, embargoPeriods: 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Split_InvalidFolds_Throws(int folds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _cv.Split(100, folds, 0, 0));
    }
}
