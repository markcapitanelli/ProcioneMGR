using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 1 — proprietà anti-leakage della cross-validation temporale, verificate come
/// INVARIANTI su griglie di parametri (non su singoli esempi): nessun indice di train dentro
/// le bande purge/embargo, nessuna finestra label del train che tocca il test quando
/// purge ≥ orizzonte della label, determinismo, conteggio combinatorio del CPCV.
/// </summary>
public class AuditCvLeakageTests
{
    private readonly PurgedTimeSeriesCv _cv = new();
    private readonly CombinatorialPurgedCv _cpcv = new();

    // --- PurgedTimeSeriesCv --------------------------------------------------------------------

    [Theory]
    [InlineData(100, 5, 0, 0)]
    [InlineData(100, 5, 10, 5)]
    [InlineData(97, 4, 13, 9)]   // resto di divisione + bande larghe
    [InlineData(60, 3, 20, 20)]  // bande enormi rispetto al fold
    [InlineData(500, 10, 24, 12)]
    [InlineData(11, 5, 3, 2)]    // fold minuscoli
    public void PurgedCv_NoTrainIndexEverFallsInPurgeOrEmbargoBand(int samples, int folds, int purge, int embargo)
    {
        foreach (var split in _cv.Split(samples, folds, purge, embargo))
        {
            var testStart = split.TestIndices.Min();
            var testEnd = split.TestIndices.Max() + 1; // esclusivo
            foreach (var t in split.TrainIndices)
            {
                Assert.False(t >= testStart - purge && t < testEnd + embargo,
                    $"fold {split.Fold}: train {t} dentro la banda esclusa [{testStart - purge}, {testEnd + embargo})");
            }
            Assert.Empty(split.TrainIndices.Intersect(split.TestIndices));
        }
    }

    [Theory]
    [InlineData(200, 5, 5)]
    [InlineData(200, 4, 12)]
    [InlineData(150, 3, 1)]
    public void PurgedCv_TrainLabelWindow_NeverOverlapsTestSet_WhenPurgeCoversHorizon(int samples, int folds, int horizon)
    {
        // La label del campione t è il forward-return calcolato su (t, t+horizon]. Con
        // purge = horizon, NESSUN campione di train che precede il test può avere una label
        // che "vede" dentro il periodo di test: è la definizione operativa di no-leakage.
        foreach (var split in _cv.Split(samples, folds, purgeWindow: horizon, embargoPeriods: 0))
        {
            var testStart = split.TestIndices.Min();
            foreach (var t in split.TrainIndices.Where(t => t < testStart))
            {
                Assert.True(t + horizon < testStart,
                    $"fold {split.Fold}: la label di train {t} (finestra fino a {t + horizon}) tocca il test che inizia a {testStart}");
            }
        }
    }

    [Fact]
    public void PurgedCv_IsDeterministic()
    {
        var a = _cv.Split(313, 7, 11, 5);
        var b = _cv.Split(313, 7, 11, 5);
        Assert.Equal(a.Count, b.Count);
        for (var k = 0; k < a.Count; k++)
        {
            Assert.Equal(a[k].TrainIndices, b[k].TrainIndices);
            Assert.Equal(a[k].TestIndices, b[k].TestIndices);
        }
    }

    [Fact]
    public void PurgedCv_ExtremeBands_TrainCanBecomeEmpty_ButNeverLeaks()
    {
        // Bande più larghe dell'intero campione: il train può legittimamente svuotarsi,
        // ma non deve MAI contenere indici della banda esclusa (fail-safe, non fail-open).
        var splits = _cv.Split(50, 2, purgeWindow: 100, embargoPeriods: 100);
        foreach (var split in splits)
        {
            Assert.Empty(split.TrainIndices);
        }
    }

    // --- CombinatorialPurgedCv -------------------------------------------------------------------

    private static int Binomial(int n, int k)
    {
        var r = 1L;
        for (var i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return (int)r;
    }

    [Theory]
    [InlineData(120, 6, 2, 5, 3)]
    [InlineData(100, 5, 2, 0, 0)]
    [InlineData(90, 4, 3, 7, 2)]
    [InlineData(101, 5, 1, 10, 10)] // resto + gruppo singolo
    public void Cpcv_TrainNeverIntersectsAnyPurgeEmbargoBand(int samples, int groups, int testGroups, int purge, int embargo)
    {
        var splits = _cpcv.Split(samples, groups, testGroups, purge, embargo);
        Assert.Equal(Binomial(groups, testGroups), splits.Count);

        var groupSize = samples / groups;
        foreach (var s in splits)
        {
            Assert.Empty(s.TrainIndices.Intersect(s.TestIndices));
            foreach (var g in s.TestGroups)
            {
                var start = g * groupSize;
                var end = g == groups - 1 ? samples : start + groupSize;
                var from = Math.Max(0, start - purge);
                var to = Math.Min(samples, end + embargo);
                Assert.DoesNotContain(s.TrainIndices, t => t >= from && t < to);
            }
        }
    }

    [Fact]
    public void Cpcv_TestIndices_AreExactlyTheUnionOfChosenGroups()
    {
        const int samples = 97;
        const int groups = 5;
        var groupSize = samples / groups;
        foreach (var s in _cpcv.Split(samples, groups, 2, 3, 2))
        {
            var expected = new List<int>();
            foreach (var g in s.TestGroups)
            {
                var start = g * groupSize;
                var end = g == groups - 1 ? samples : start + groupSize;
                expected.AddRange(Enumerable.Range(start, end - start));
            }
            expected.Sort();
            Assert.Equal(expected, s.TestIndices);
        }
    }

    [Fact]
    public void Cpcv_IsDeterministic_AndCombinationsAreLexicographic()
    {
        var a = _cpcv.Split(150, 6, 2, 4, 2);
        var b = _cpcv.Split(150, 6, 2, 4, 2);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].TestGroups, b[i].TestGroups);
            Assert.Equal(a[i].TrainIndices, b[i].TrainIndices);
        }
        // Ordine lessicografico: la prima combinazione è {0,1}, l'ultima {4,5}.
        Assert.Equal(new[] { 0, 1 }, a[0].TestGroups);
        Assert.Equal(new[] { 4, 5 }, a[^1].TestGroups);
    }

    [Fact]
    public void Cpcv_AdjacentTestGroups_OverlappingBands_AreUnionedWithoutDuplicates()
    {
        // Gruppi di test ADIACENTI con purge/embargo che si sovrappongono: la banda esclusa
        // dev'essere l'unione, e gli indici di train devono restare unici e ordinati.
        foreach (var s in _cpcv.Split(80, 4, 2, 15, 15))
        {
            Assert.Equal(s.TrainIndices.Distinct().Count(), s.TrainIndices.Count);
            var sorted = s.TrainIndices.OrderBy(x => x).ToList();
            Assert.Equal(sorted, s.TrainIndices);
        }
    }
}
