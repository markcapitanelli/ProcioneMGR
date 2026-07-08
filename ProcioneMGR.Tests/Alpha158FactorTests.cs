using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Alpha.Alpha158;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del catalogo Alpha158 (rif. <c>docs/ROADMAP-QLIB.md §1.1</c>). Il cuore è l'invariante
/// ANTI-LOOK-AHEAD verificato in un solo test parametrico su TUTTO il catalogo (~150 feature),
/// non un test scritto a mano per feature. Più: coerenza del round-trip per nome (persistenza),
/// dimensione del catalogo, e correttezza numerica di alcuni operatori rappresentativi.
/// </summary>
public class Alpha158FactorTests
{
    private readonly IAlphaFactorFactory _factory = new AlphaFactorFactory();
    private static readonly IReadOnlyDictionary<string, decimal> NoParams = new Dictionary<string, decimal>();

    // --- Helpers -----------------------------------------------------------------------------

    /// <summary>Candele sintetiche deterministiche con OHLC/volume "vivi" (range non degenere).</summary>
    private static List<OhlcvData> MakeCandles(int n, int seed = 42)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var close = 100m;
        for (var i = 0; i < n; i++)
        {
            var open = close;
            var shock = (decimal)(rnd.NextDouble() - 0.48) * 2m; // lieve drift positivo
            close = Math.Max(1m, open + shock);
            var high = Math.Max(open, close) + (decimal)rnd.NextDouble();
            var low = Math.Min(open, close) - (decimal)rnd.NextDouble();
            if (low <= 0m) low = 0.5m;
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = 50m + (decimal)(rnd.NextDouble() * 100.0),
            });
        }
        return list;
    }

    private static List<OhlcvData> MakeLinearCandles(int n)
    {
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var c = 100m + i;             // prezzo strettamente crescente lineare
            var prev = i > 0 ? 100m + (i - 1) : c;
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = prev,
                High = c + 0.5m,
                Low = prev - 0.5m,
                Close = c,
                Volume = 100m + i,
            });
        }
        return list;
    }

    // --- Anti-look-ahead su TUTTO il catalogo ------------------------------------------------

    [Fact]
    public void EntireCatalog_IsAntiLookAhead()
    {
        var candles = MakeCandles(300);
        var catalog = Alpha158Catalog.BuildCatalog();
        Assert.NotEmpty(catalog);

        var failures = new List<string>();
        foreach (var factor in catalog)
        {
            var full = factor.Compute(candles, NoParams);
            Assert.Equal(candles.Count, full.Count); // serie allineata all'input

            foreach (var cut in new[] { 80, 140, 210, 299 })
            {
                var truncated = factor.Compute(candles.Take(cut + 1).ToList(), NoParams);
                if (full[cut].HasValue != truncated[cut].HasValue ||
                    (full[cut].HasValue && full[cut]!.Value != truncated[cut]!.Value))
                {
                    failures.Add($"{factor.Name}@{cut}: full={full[cut]} trunc={truncated[cut]}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Violazione anti-look-ahead in: " + string.Join(" | ", failures.Take(20)));
    }

    // --- Round-trip per nome (persistenza SavedMlModel) --------------------------------------

    [Fact]
    public void EveryCatalogFactor_RoundTripsThroughFactoryByName()
    {
        var candles = MakeCandles(200);
        foreach (var factor in Alpha158Catalog.BuildCatalog())
        {
            var recreated = _factory.Create(factor.Name);
            Assert.Equal(factor.Name, recreated.Name);

            // Stesso nome ⇒ stessa serie (il nome basta a ricostruire la feature).
            var a = factor.Compute(candles, NoParams);
            var b = recreated.Compute(candles, NoParams);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void Catalog_HasExpectedSize_AndUniqueNames()
    {
        var catalog = Alpha158Catalog.BuildCatalog();
        // 9 KBAR (orizzonte-indipendenti) + (operatori rolling) × 5 orizzonti di default.
        var expected = 9 + (Alpha158Catalog.OperatorCount - 9) * Alpha158Catalog.DefaultHorizons.Length;
        Assert.Equal(expected, catalog.Count);
        Assert.True(catalog.Count is >= 110 and <= 160, $"Catalogo fuori range atteso: {catalog.Count}");

        var names = catalog.Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count()); // nomi univoci
        Assert.All(names, n => Assert.StartsWith("A158_", n));
    }

    [Fact]
    public void CustomHorizon_RoundTrips_EvenOutsideDefaults()
    {
        var catalog = Alpha158Catalog.BuildCatalog(new[] { 7 }); // orizzonte non standard
        var roc7 = catalog.First(f => f.Name == "A158_ROC_7");
        var recreated = _factory.Create("A158_ROC_7");
        Assert.Equal(roc7.Name, recreated.Name);
        Assert.Equal(7, ((Alpha158Factor)recreated).Horizon);
    }

    [Fact]
    public void TryCreate_RejectsUnknownOrMalformedNames()
    {
        Assert.False(Alpha158Catalog.TryCreate("Momentum", out _));      // fattore storico, non Alpha158
        Assert.False(Alpha158Catalog.TryCreate("A158_NOPE_5", out _));   // operatore inesistente
        Assert.False(Alpha158Catalog.TryCreate("A158_ROC", out _));      // rolling senza orizzonte
        Assert.False(Alpha158Catalog.TryCreate("A158_KMID_5", out _));   // KBAR con orizzonte spurio
        Assert.False(Alpha158Catalog.TryCreate("A158_ROC_x", out _));    // orizzonte non numerico
        Assert.True(Alpha158Catalog.TryCreate("A158_ROC_20", out var ok));
        Assert.Equal("A158_ROC_20", ok.Name);
    }

    [Fact]
    public void Factory_Create_ThrowsForTrulyUnknownFactor()
        => Assert.Throws<NotSupportedException>(() => _factory.Create("A158_DoesNotExist_5"));

    // --- Correttezza numerica di operatori rappresentativi ----------------------------------

    [Fact]
    public void Kbar_KmidAndKlen_MatchDefinitionAndWarmupIsZeroLength()
    {
        var candles = MakeCandles(20);
        var kmid = _factory.Create("A158_KMID").Compute(candles, NoParams);
        var klen = _factory.Create("A158_KLEN").Compute(candles, NoParams);

        // KBAR sono per-candela: nessun warm-up (valore già a i=0).
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            Assert.Equal((c.Close - c.Open) / c.Open, kmid[i]!.Value, 10);
            Assert.Equal((c.High - c.Low) / c.Open, klen[i]!.Value, 10);
            Assert.True(klen[i]!.Value > 0m); // range sempre positivo per costruzione
        }
    }

    [Fact]
    public void Rsv_And_Rank_And_Cntp_StayInUnitInterval()
    {
        var candles = MakeCandles(250);
        foreach (var name in new[] { "A158_RSV_20", "A158_RANK_20", "A158_CNTP_20", "A158_CNTN_20" })
        {
            var v = _factory.Create(name).Compute(candles, NoParams);
            foreach (var x in v.Where(x => x.HasValue))
            {
                Assert.InRange(x!.Value, 0m, 1m);
            }
        }
    }

    [Fact]
    public void Rsqr_OnPerfectlyLinearSeries_IsOne()
    {
        var candles = MakeLinearCandles(80);
        var v = _factory.Create("A158_RSQR_30").Compute(candles, NoParams);
        // Dove definito, R² di una retta perfetta = 1.
        var last = v[^1];
        Assert.NotNull(last);
        Assert.Equal(1m, last!.Value, 6);
    }

    [Fact]
    public void Roc_MatchesRefRatioDefinition()
    {
        var candles = MakeCandles(60);
        var d = 10;
        var v = _factory.Create($"A158_ROC_{d}").Compute(candles, NoParams);
        for (var i = 0; i < d; i++) Assert.Null(v[i]); // warm-up: nessun prezzo di d periodi fa
        for (var i = d; i < candles.Count; i++)
        {
            var expected = candles[i - d].Close / candles[i].Close;
            Assert.Equal(expected, v[i]!.Value, 10);
        }
    }

    [Fact]
    public void Cntp_OnMonotonicUpSeries_IsOne()
    {
        var candles = MakeLinearCandles(60); // sempre in salita
        var v = _factory.Create("A158_CNTP_20").Compute(candles, NoParams);
        Assert.Equal(1m, v[^1]!.Value, 10); // tutte le barre in salita
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var candles = MakeCandles(150);
        foreach (var name in new[] { "A158_STD_20", "A158_CORR_20", "A158_WVMA_10", "A158_BETA_30" })
        {
            var f = _factory.Create(name);
            var a = f.Compute(candles, NoParams);
            var b = f.Compute(candles, NoParams);
            for (var i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
        }
    }
}
