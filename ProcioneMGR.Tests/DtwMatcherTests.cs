using ProcioneMGR.Data;
using ProcioneMGR.Services.Discovery.Dtw;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D4] Verifica del matching per forma. Segue i quattro livelli di `docs/STANDARD-VERIFICA.md`.
///
/// I due test che decidono se il pezzo vale qualcosa:
///  - <see cref="LowerBound_IsNeverGreaterThanTheRealDistance"/> — se LB_Keogh NON fosse un vero
///    limite inferiore, il pruning scarterebbe in silenzio proprio le corrispondenze migliori, e
///    tutto il resto sarebbe costruito sulla sabbia.
///  - <see cref="PlantedPattern_IsFoundAtThePlantedPositions"/> — il gate non negoziabile della
///    roadmap: prima di fidarsi di un risultato su dati reali, la macchina deve ritrovare un
///    pattern che sappiamo esserci.
/// </summary>
public class DtwMatcherTests
{
    private static readonly DtwMatcher Dtw = new();

    private static OhlcvData Bar(int i, decimal close) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    private static List<OhlcvData> Series(IEnumerable<decimal> closes)
        => closes.Select((c, i) => Bar(i, c)).ToList();

    /// <summary>Forma a "V": scende e risale. Distintiva e facile da ritrovare.</summary>
    private static decimal[] VShape(decimal scale = 1m) =>
        [10m * scale, 8m * scale, 6m * scale, 4m * scale, 6m * scale, 8m * scale, 10m * scale];

    // --- Normalizzazione --------------------------------------------------------------------------

    [Fact]
    public void ZNormalize_ProducesZeroMeanUnitDeviation()
    {
        var z = Dtw.ZNormalize([1, 2, 3, 4, 5]);

        Assert.Equal(0.0, z.Average(), 10);
        var sd = Math.Sqrt(z.Sum(v => v * v) / z.Count);
        Assert.Equal(1.0, sd, 10);
    }

    [Fact]
    public void ZNormalize_OnAConstantSeries_ReturnsZerosInsteadOfNaN()
    {
        // Deviazione zero: dividere darebbe NaN, che si propagherebbe fino a "distanza 0", cioe'
        // una corrispondenza perfetta con qualunque cosa.
        var z = Dtw.ZNormalize([7, 7, 7, 7]);

        Assert.All(z, v => Assert.Equal(0.0, v));
        Assert.DoesNotContain(z, double.IsNaN);
    }

    [Fact]
    public void ZNormalize_MakesTheSameShapeIdenticalAtAnyPriceLevel()
    {
        // E' il motivo per cui la normalizzazione e' obbligatoria: la stessa forma a 60.000 e a
        // 30.000 deve risultare identica.
        var a = Dtw.ZNormalize(VShape(1m).Select(v => (double)v).ToList());
        var b = Dtw.ZNormalize(VShape(1000m).Select(v => (double)v).ToList());

        Assert.Equal(0.0, Dtw.Distance(a, b, band: 2), 9);
    }

    // --- Distanza ---------------------------------------------------------------------------------

    [Fact]
    public void IdenticalSequences_HaveZeroDistance()
    {
        var s = Dtw.ZNormalize([1, 3, 2, 5, 4]);
        Assert.Equal(0.0, Dtw.Distance(s, s, band: 2), 10);
    }

    [Fact]
    public void DtwToleratesTimeStretching_WhereEuclideanWouldNot()
    {
        // IL PUNTO DI DTW: la stessa forma svolta piu' lentamente. Punto a punto sarebbe lontana;
        // con l'allineamento non lineare no.
        var fast = Dtw.ZNormalize([0, 1, 2, 3, 2, 1, 0]);
        var slow = Dtw.ZNormalize([0, 1, 1, 2, 3, 3, 2, 1, 1, 0]);

        var dtw = Dtw.Distance(fast, slow, band: 5);

        var len = Math.Min(fast.Count, slow.Count);
        var euclidean = Math.Sqrt(Enumerable.Range(0, len).Sum(i => Math.Pow(fast[i] - slow[i], 2)));

        Assert.True(dtw < euclidean,
            $"DTW ({dtw:F4}) deve battere l'euclidea ({euclidean:F4}) su una forma dilatata nel tempo");
    }

    [Fact]
    public void DistanceIsSymmetric()
    {
        var a = Dtw.ZNormalize([1, 4, 2, 8, 3]);
        var b = Dtw.ZNormalize([2, 3, 5, 6, 1]);

        Assert.Equal(Dtw.Distance(a, b, 2), Dtw.Distance(b, a, 2), 10);
    }

    [Fact]
    public void EmptySequences_GiveInfiniteDistanceInsteadOfThrowing()
    {
        Assert.True(double.IsPositiveInfinity(Dtw.Distance([], [1, 2], 1)));
        Assert.True(double.IsPositiveInfinity(Dtw.Distance([1, 2], [], 1)));
    }

    [Fact]
    public void ANarrowBandStillAllowsDifferentLengths()
    {
        // Con banda piu' stretta della differenza di lunghezza non esisterebbe alcun cammino: la
        // banda viene allargata al minimo necessario invece di restituire infinito.
        var a = Dtw.ZNormalize([1, 2, 3, 4, 5, 6, 7, 8]);
        var b = Dtw.ZNormalize([1, 3, 5, 7]);

        var d = Dtw.Distance(a, b, band: 1);

        Assert.False(double.IsInfinity(d));
        Assert.True(d >= 0);
    }

    // --- Il limite inferiore: la proprietà da cui dipende tutto il pruning -------------------------

    [Fact]
    public void LowerBound_IsNeverGreaterThanTheRealDistance()
    {
        // Se LB_Keogh superasse anche una sola volta la distanza vera, il pruning scarterebbe in
        // SILENZIO le corrispondenze migliori: il motore troverebbe meno pattern senza dire perche'.
        var rnd = new Random(17);
        var violations = 0;

        for (var trial = 0; trial < 3000; trial++)
        {
            var len = rnd.Next(4, 25);
            var band = rnd.Next(1, Math.Max(2, len / 2));
            var a = Dtw.ZNormalize(Enumerable.Range(0, len).Select(_ => rnd.NextDouble() * 20 - 10).ToList());
            var b = Dtw.ZNormalize(Enumerable.Range(0, len).Select(_ => rnd.NextDouble() * 20 - 10).ToList());

            var lb = Dtw.LowerBound(a, b, band);
            var real = Dtw.Distance(a, b, band);

            if (lb > real + 1e-9) violations++;
        }

        Assert.Equal(0, violations);
    }

    [Fact]
    public void LowerBound_IsZeroForIdenticalSequences()
    {
        var s = Dtw.ZNormalize([3, 1, 4, 1, 5, 9]);
        Assert.Equal(0.0, Dtw.LowerBound(s, s, band: 2), 10);
    }

    // --- IL GATE: il pattern piantato ---------------------------------------------------------------

    [Fact]
    public void PlantedPattern_IsFoundAtThePlantedPositions()
    {
        // Gate non negoziabile della roadmap: si pianta una forma a V in posizioni NOTE dentro una
        // serie di rumore, e la macchina deve ritrovarle. Senza questo, un risultato su dati reali
        // non direbbe se ha funzionato il metodo o il caso.
        var rnd = new Random(5);
        var closes = new List<decimal>();
        var plantedAt = new List<int> { 120, 400, 760 };
        var shape = VShape();

        for (var i = 0; i < 1000; i++) closes.Add(100m + (decimal)(rnd.NextDouble() * 2 - 1));
        foreach (var pos in plantedAt)
        {
            for (var k = 0; k < shape.Length; k++) closes[pos + k] = shape[k];
        }

        var matches = Dtw.FindMatches(Series(closes), shape,
            new DtwConfig { MaxDistance = 0.8, BandPercent = 20 });

        // Ogni posizione piantata dev'essere fra le occorrenze trovate.
        foreach (var pos in plantedAt)
        {
            Assert.Contains(matches, m => m.StartIndex == pos);
        }
        // E la ricerca non deve annegare in falsi positivi sul rumore.
        Assert.True(matches.Count <= plantedAt.Count + 3,
            $"trovate {matches.Count} occorrenze per {plantedAt.Count} piantate: troppi falsi positivi");
    }

    [Fact]
    public void PlantedPattern_IsFoundEvenWhenStretchedInTime()
    {
        // Il pattern piantato e' la stessa forma svolta piu' lentamente: e' il caso che giustifica
        // DTW invece di un confronto punto a punto.
        var rnd = new Random(23);
        var closes = new List<decimal>();
        for (var i = 0; i < 600; i++) closes.Add(100m + (decimal)(rnd.NextDouble() * 2 - 1));

        decimal[] stretched = [10m, 9m, 8m, 7m, 6m, 5m, 4m, 5m, 6m, 7m, 8m, 9m, 10m];
        const int pos = 300;
        for (var k = 0; k < stretched.Length; k++) closes[pos + k] = stretched[k];

        var matches = Dtw.FindMatches(Series(closes), VShape(),
            new DtwConfig { MaxDistance = 1.2, BandPercent = 40 });

        Assert.Contains(matches, m => Math.Abs(m.StartIndex - pos) <= 3);
    }

    // --- Il controllo sul rumore --------------------------------------------------------------------

    [Fact]
    public void PureNoise_DoesNotProduceAFloodOfMatches()
    {
        // Su una serie senza alcun pattern piantato, una soglia sensata non deve restituire
        // centinaia di "occorrenze": sarebbero allineamenti spurii, il difetto noto di DTW.
        var rnd = new Random(31);
        var closes = Enumerable.Range(0, 2000)
            .Select(_ => 100m + (decimal)(rnd.NextDouble() * 2 - 1)).ToList();

        var matches = Dtw.FindMatches(Series(closes), VShape(),
            new DtwConfig { MaxDistance = 0.8, BandPercent = 20 });

        Assert.True(matches.Count <= 5,
            $"su puro rumore trovate {matches.Count} occorrenze: la soglia sta raccogliendo allineamenti spurii");
    }

    // --- Non sovrapposizione --------------------------------------------------------------------------

    [Fact]
    public void OverlappingMatches_AreCollapsedIntoOne()
    {
        // Un pattern che combacia alla barra i combacia quasi sempre anche a i+1: senza la
        // separazione minima si otterrebbero decine di "occorrenze" che sono una sola.
        var closes = new List<decimal>();
        for (var i = 0; i < 200; i++) closes.Add(100m);
        var shape = VShape();
        for (var k = 0; k < shape.Length; k++) closes[100 + k] = shape[k];

        var matches = Dtw.FindMatches(Series(closes), shape,
            new DtwConfig { MaxDistance = 2.0, BandPercent = 20 });

        var near = matches.Where(m => Math.Abs(m.StartIndex - 100) < shape.Length).ToList();
        Assert.Single(near);
    }

    [Fact]
    public void MatchesAreReturnedInChronologicalOrder()
    {
        var rnd = new Random(8);
        var closes = Enumerable.Range(0, 900).Select(_ => 100m + (decimal)(rnd.NextDouble() * 2 - 1)).ToList();
        var shape = VShape();
        foreach (var pos in new[] { 700, 100, 400 })
        {
            for (var k = 0; k < shape.Length; k++) closes[pos + k] = shape[k];
        }

        var matches = Dtw.FindMatches(Series(closes), shape, new DtwConfig { MaxDistance = 0.8, BandPercent = 20 });

        for (var i = 1; i < matches.Count; i++)
        {
            Assert.True(matches[i].StartIndex > matches[i - 1].StartIndex);
        }
    }

    // --- La serie di eventi (come il pattern entra in Discovery) -------------------------------------

    [Fact]
    public void EventSeries_MarksTheClosingBar_NotTheOpeningOne()
    {
        // Segnare l'evento all'inizio del pattern sarebbe look-ahead: a quel punto il pattern non
        // e' ancora avvenuto.
        var matches = new List<DtwMatch>
        {
            new(10, 16, DateTime.UnixEpoch, DateTime.UnixEpoch, 0.5),
        };

        var events = Dtw.ToEventSeries(50, matches);

        Assert.False(events[10]);
        Assert.True(events[16]);
        Assert.Equal(1, events.Count(e => e));
    }

    [Fact]
    public void EventSeries_IgnoresMatchesOutsideTheSeries()
    {
        var matches = new List<DtwMatch> { new(90, 96, DateTime.UnixEpoch, DateTime.UnixEpoch, 0.1) };
        var events = Dtw.ToEventSeries(50, matches);
        Assert.DoesNotContain(true, events);
    }

    // --- Stress e input degeneri ---------------------------------------------------------------------

    [Fact]
    public void DegenerateInputs_DoNotThrow()
    {
        var series = Series(Enumerable.Repeat(100m, 50));

        Assert.Empty(Dtw.FindMatches(series, [], new DtwConfig()));                    // pattern vuoto
        Assert.Empty(Dtw.FindMatches(series, [1m, 2m], new DtwConfig()));              // pattern troppo corto
        Assert.Empty(Dtw.FindMatches([], VShape(), new DtwConfig()));                  // serie vuota
        Assert.Empty(Dtw.FindMatches(Series([1m, 2m]), VShape(), new DtwConfig()));    // serie piu' corta del pattern
        Assert.Empty(Dtw.ToEventSeries(0, []));                                        // serie di lunghezza zero
    }

    [Fact]
    public void AFlatSeries_DoesNotMatchAShapedPattern()
    {
        // Una serie costante z-normalizza a tutti zeri: senza la guardia sulla deviazione nulla
        // darebbe distanza 0 con qualunque cosa, cioe' "corrisponde sempre".
        var matches = Dtw.FindMatches(Series(Enumerable.Repeat(100m, 300)), VShape(),
            new DtwConfig { MaxDistance = 0.5, BandPercent = 20 });

        Assert.Empty(matches);
    }

    [Fact]
    public void RandomFuzzing_AlwaysProducesFiniteNonNegativeDistances()
    {
        // Test random: nessun input plausibile deve produrre NaN, negativi o eccezioni.
        var rnd = new Random(99);
        for (var trial = 0; trial < 300; trial++)
        {
            var seriesLen = rnd.Next(10, 300);
            var patternLen = rnd.Next(3, 30);
            var closes = Enumerable.Range(0, seriesLen)
                .Select(_ => (decimal)(rnd.NextDouble() * 1000)).ToList();
            var pattern = Enumerable.Range(0, patternLen)
                .Select(_ => (decimal)(rnd.NextDouble() * 1000)).ToList();

            var matches = Dtw.FindMatches(Series(closes), pattern, new DtwConfig
            {
                MaxDistance = rnd.NextDouble() * 5,
                BandPercent = rnd.Next(1, 100),
            });

            Assert.All(matches, m =>
            {
                Assert.False(double.IsNaN(m.Distance));
                Assert.True(m.Distance >= 0);
                Assert.True(m.EndIndex > m.StartIndex);
                Assert.True(m.EndIndex < closes.Count);
            });
        }
    }

    [Fact]
    public void LargeSeries_CompletesInReasonableTime()
    {
        // Stress: 50.000 barre. Senza il pruning LB_Keogh questo test sarebbe proibitivo, ed e'
        // proprio la ragione per cui il pruning esiste.
        var rnd = new Random(3);
        var closes = Enumerable.Range(0, 50_000)
            .Select(_ => 100m + (decimal)(rnd.NextDouble() * 4 - 2)).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var matches = Dtw.FindMatches(Series(closes), VShape(),
            new DtwConfig { MaxDistance = 0.6, BandPercent = 20 });
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 30,
            $"scansione di 50.000 barre in {sw.Elapsed.TotalSeconds:F1}s: troppo lenta per essere usabile");
        Assert.All(matches, m => Assert.True(m.Distance <= 0.6));
    }

    [Fact]
    public void SearchIsDeterministic()
    {
        var rnd = new Random(44);
        var closes = Enumerable.Range(0, 1500).Select(_ => 100m + (decimal)(rnd.NextDouble() * 3 - 1.5)).ToList();
        var shape = VShape();
        for (var k = 0; k < shape.Length; k++) closes[500 + k] = shape[k];
        var series = Series(closes);
        var config = new DtwConfig { MaxDistance = 0.9, BandPercent = 20 };

        var a = Dtw.FindMatches(series, shape, config);
        var b = Dtw.FindMatches(series, shape, config);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].StartIndex, b[i].StartIndex);
            Assert.Equal(a[i].Distance, b[i].Distance, 12);
        }
    }
}
