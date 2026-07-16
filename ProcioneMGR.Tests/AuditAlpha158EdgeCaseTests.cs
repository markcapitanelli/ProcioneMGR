using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Alpha.Alpha158;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 1 — casi limite del catalogo Alpha158 NON coperti dai test funzionali esistenti:
/// input degeneri (prezzo costante, volume zero, doji perfetti, candele-glitch a prezzo 0,
/// input vuoto/singolo) e valori estremi. Contratto atteso per TUTTO il catalogo: mai
/// un'eccezione, serie sempre allineate all'input, null dove il valore non è calcolabile.
/// In decimal non esistono NaN/Inf: ogni valore presente è finito per costruzione, quindi
/// "gestione NaN/Inf corretta" = nessun OverflowException e null nei casi degeneri.
/// </summary>
public class AuditAlpha158EdgeCaseTests
{
    private static readonly IReadOnlyDictionary<string, decimal> NoParams = new Dictionary<string, decimal>();

    private static OhlcvData Candle(int i, decimal o, decimal h, decimal l, decimal c, decimal v) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = o, High = h, Low = l, Close = c, Volume = v,
    };

    private static List<OhlcvData> Build(int n, Func<int, OhlcvData> make) =>
        Enumerable.Range(0, n).Select(make).ToList();

    /// <summary>Candele "vive" deterministiche (stessa convenzione di Alpha158FactorTests).</summary>
    private static List<OhlcvData> LiveCandles(int n, decimal scale = 1m, int seed = 42)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var close = 100m * scale;
        for (var i = 0; i < n; i++)
        {
            var open = close;
            var shock = (decimal)(rnd.NextDouble() - 0.48) * 2m * scale;
            close = Math.Max(scale, open + shock);
            var high = Math.Max(open, close) + (decimal)rnd.NextDouble() * scale;
            var low = Math.Min(open, close) - (decimal)rnd.NextDouble() * scale;
            if (low <= 0m) low = scale / 2m;
            list.Add(Candle(i, open, high, low, close, (50m + (decimal)(rnd.NextDouble() * 100.0)) * scale));
        }
        return list;
    }

    private static void AssertCatalogSafeOn(List<OhlcvData> candles)
    {
        foreach (var factor in Alpha158Catalog.BuildCatalog())
        {
            IReadOnlyList<decimal?> v;
            try
            {
                v = factor.Compute(candles, NoParams);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{factor.Name} ha sollevato {ex.GetType().Name}: {ex.Message}");
                return;
            }
            Assert.Equal(candles.Count, v.Count);
        }
    }

    // --- Input degeneri: mai eccezioni, serie allineate --------------------------------------

    [Fact]
    public void EntireCatalog_ConstantPriceAndVolume_NoThrow_SeriesAligned()
        => AssertCatalogSafeOn(Build(150, i => Candle(i, 100m, 100m, 100m, 100m, 50m)));

    [Fact]
    public void EntireCatalog_ZeroVolumeEverywhere_NoThrow()
        => AssertCatalogSafeOn(Build(150, i => Candle(i, 100m + i, 101m + i, 99m + i, 100.5m + i, 0m)));

    [Fact]
    public void EntireCatalog_ZeroPriceGlitches_NoThrow()
    {
        // Una candela su 10 è un glitch a prezzo/volume 0 (dato corrotto del feed).
        var candles = Build(120, i => i % 10 == 5
            ? Candle(i, 0m, 0m, 0m, 0m, 0m)
            : Candle(i, 100m + i, 101m + i, 99m + i, 100.5m + i, 50m));
        AssertCatalogSafeOn(candles);
    }

    [Fact]
    public void EntireCatalog_PerfectDojis_HighEqualsLow_NoThrow()
        => AssertCatalogSafeOn(Build(100, i => Candle(i, 50m, 50m, 50m, 50m, 10m + i % 3)));

    [Fact]
    public void EntireCatalog_EmptyAndSingleCandle_NoThrowAndAligned()
    {
        var empty = new List<OhlcvData>();
        var one = new List<OhlcvData> { Candle(0, 100m, 101m, 99m, 100.5m, 10m) };
        foreach (var factor in Alpha158Catalog.BuildCatalog())
        {
            Assert.Empty(factor.Compute(empty, NoParams));
            Assert.Single(factor.Compute(one, NoParams));
        }
    }

    [Fact]
    public void EntireCatalog_FewerCandlesThanHorizon_AllNull_NoThrow()
    {
        var candles = LiveCandles(3); // meno del minimo orizzonte rolling (5)
        foreach (var factor in Alpha158Catalog.BuildCatalog().Cast<Alpha158Factor>().Where(f => f.Horizon >= 5))
        {
            var v = factor.Compute(candles, NoParams);
            Assert.Equal(3, v.Count);
            Assert.All(v, x => Assert.Null(x));
        }
    }

    // --- Valori estremi: nessun overflow decimal ----------------------------------------------

    [Fact]
    public void EntireCatalog_HugePrices_1e12_NoOverflow()
        => AssertCatalogSafeOn(LiveCandles(120, scale: 1_000_000_000_000m));

    [Fact]
    public void EntireCatalog_TinyPrices_1e_8_NoOverflow()
        => AssertCatalogSafeOn(LiveCandles(120, scale: 0.00000001m));

    // --- Warm-up: nessun valore prima del completamento della finestra ------------------------

    [Fact]
    public void HorizonFactors_NeverProduceValuesBeforeWindowCompletes()
    {
        var candles = LiveCandles(100);
        foreach (var factor in Alpha158Catalog.BuildCatalog().Cast<Alpha158Factor>().Where(f => f.Horizon >= 2))
        {
            var v = factor.Compute(candles, NoParams);
            // Una finestra di d candele si completa non prima dell'indice d-1: tutto ciò che
            // precede DEVE essere null (un valore lì indicherebbe una finestra mal contata).
            for (var i = 0; i < factor.Horizon - 1; i++)
            {
                Assert.True(v[i] is null, $"{factor.Name}: valore inatteso {v[i]} all'indice {i} (warm-up)");
            }
        }
    }

    // --- Range degli operatori normalizzati su input degeneri ---------------------------------

    [Fact]
    public void BoundedOperators_StayInRange_EvenOnDegenerateInput()
    {
        // Serie con lunghi tratti piatti intervallati da salti: stress per i rapporti direzionali.
        var candles = Build(200, i =>
        {
            var c = 100m + (i / 25 % 2 == 0 ? 0m : 10m);
            return Candle(i, c, c + 0.1m, c - 0.1m, c, i % 7 == 0 ? 0m : 20m);
        });
        foreach (var name in new[]
                 {
                     "A158_RSV_20", "A158_RANK_20", "A158_CNTP_20", "A158_CNTN_20", "A158_RSQR_20",
                     "A158_SUMP_20", "A158_SUMN_20", "A158_VSUMP_20", "A158_VSUMN_20",
                     "A158_IMAX_20", "A158_IMIN_20",
                 })
        {
            Assert.True(Alpha158Catalog.TryCreate(name, out var factor), name);
            foreach (var x in factor.Compute(candles, NoParams).Where(x => x.HasValue))
            {
                Assert.InRange(x!.Value, 0m, 1m);
            }
        }
        foreach (var name in new[] { "A158_CNTD_20", "A158_SUMD_20", "A158_VSUMD_20", "A158_IMXD_20" })
        {
            Assert.True(Alpha158Catalog.TryCreate(name, out var factor), name);
            foreach (var x in factor.Compute(candles, NoParams).Where(x => x.HasValue))
            {
                Assert.InRange(x!.Value, -1m, 1m);
            }
        }
        // CORR/CORD sono correlazioni: [-1, 1] anche su input con volumi nulli.
        foreach (var name in new[] { "A158_CORR_20", "A158_CORD_20" })
        {
            Assert.True(Alpha158Catalog.TryCreate(name, out var factor), name);
            foreach (var x in factor.Compute(candles, NoParams).Where(x => x.HasValue))
            {
                Assert.InRange(x!.Value, -1.0000001m, 1.0000001m);
            }
        }
    }
}
