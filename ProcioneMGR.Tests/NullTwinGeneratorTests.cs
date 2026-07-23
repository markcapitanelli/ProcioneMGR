using ProcioneMGR.Data;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I2 roadmap frontiere-profitto] Il gemello sintetico NULLO: block bootstrap dei rendimenti +
/// segno i.i.d. per barra. Questi test fissano il contratto del nullo: (a) stessa "anagrafica"
/// della serie reale (lunghezza, timestamp, prima candela); (b) i moduli dei rendimenti vengono
/// dalla popolazione reale (il clustering di |r| è ereditato, non inventato); (c) la struttura
/// DIREZIONALE muore — un drift fortissimo nel reale non sopravvive nel gemello; (d) determinismo
/// a parità di seme; (e) candele sempre valide (High/Low coerenti, mai prezzi ≤ 0).
/// </summary>
public class NullTwinGeneratorTests
{
    private static List<OhlcvData> Series(IReadOnlyList<decimal> closes, decimal volume = 100m)
    {
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<OhlcvData>(closes.Count);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            list.Add(new OhlcvData
            {
                Symbol = "TWIN/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = i == 0 ? c : closes[i - 1], High = c * 1.002m, Low = c * 0.998m, Close = c,
                Volume = volume + i % 7,
            });
        }
        return list;
    }

    private static List<OhlcvData> NoisySeries(int n, int seed)
    {
        var rng = new Random(seed);
        var closes = new List<decimal>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            price *= 1m + (decimal)((rng.NextDouble() - 0.5) * 0.02);
            closes.Add(price);
        }
        return Series(closes);
    }

    [Fact]
    public void Twin_KeepsLengthTimestampsFirstCandleAndIdentity()
    {
        var real = NoisySeries(500, seed: 1);
        var twin = NullTwinGenerator.Generate(real, seed: 42);

        Assert.Equal(real.Count, twin.Count);
        Assert.Equal(real[0].Close, twin[0].Close);
        Assert.Equal("TWIN/USDT", twin[100].Symbol);
        Assert.Equal("1h", twin[100].Timeframe);
        for (var i = 0; i < real.Count; i++)
        {
            Assert.Equal(real[i].TimestampUtc, twin[i].TimestampUtc);
        }
    }

    [Fact]
    public void Twin_IsDeterministicPerSeed_AndDiffersAcrossSeeds()
    {
        var real = NoisySeries(400, seed: 2);

        var a = NullTwinGenerator.Generate(real, seed: 7);
        var b = NullTwinGenerator.Generate(real, seed: 7);
        var c = NullTwinGenerator.Generate(real, seed: 8);

        Assert.Equal(a.Select(x => x.Close), b.Select(x => x.Close));
        Assert.NotEqual(a.Select(x => x.Close), c.Select(x => x.Close));
    }

    [Fact]
    public void Twin_AbsoluteReturns_ComeFromTheRealPopulation()
    {
        var real = NoisySeries(600, seed: 3);
        var twin = NullTwinGenerator.Generate(real, seed: 11);

        var realAbs = new HashSet<double>();
        for (var i = 1; i < real.Count; i++)
        {
            realAbs.Add(Math.Round(Math.Abs((double)(real[i].Close / real[i - 1].Close - 1m)), 9));
        }
        for (var i = 1; i < twin.Count; i++)
        {
            var abs = Math.Round(Math.Abs((double)(twin[i].Close / twin[i - 1].Close - 1m)), 9);
            // Tolleranza: il round-trip decimal→double→decimal della ricostruzione può spostare
            // l'ultima cifra; si verifica l'appartenenza con arrotondamento a 9 decimali.
            Assert.Contains(realAbs, v => Math.Abs(v - abs) < 1e-8);
        }
    }

    [Fact]
    public void Twin_KillsDirectionalStructure_StrongDriftDoesNotSurvive()
    {
        // Reale: +1% a barra, SEMPRE (la struttura direzionale più forte possibile).
        var closes = new List<decimal>(800);
        var price = 100m;
        for (var i = 0; i < 800; i++) { price *= 1.01m; closes.Add(price); }
        var real = Series(closes);

        var twin = NullTwinGenerator.Generate(real, seed: 5);

        var returns = new List<double>();
        var ups = 0;
        for (var i = 1; i < twin.Count; i++)
        {
            var r = (double)(twin[i].Close / twin[i - 1].Close - 1m);
            returns.Add(r);
            if (r > 0) ups++;
        }
        // Segni i.i.d.: circa metà su (binomiale, banda larga per non essere flaky)...
        Assert.InRange(ups, returns.Count * 35 / 100, returns.Count * 65 / 100);
        // ...e la media crolla dall'1% verso zero (|r| resta ~1%, il segno lo uccide).
        Assert.True(Math.Abs(returns.Average()) < 0.004,
            $"drift residuo {returns.Average():P3}: la struttura direzionale doveva morire");
    }

    [Fact]
    public void Twin_CandlesAreAlwaysValid_AndVolumesComeFromTheRealSeries()
    {
        var real = NoisySeries(500, seed: 4);
        var twin = NullTwinGenerator.Generate(real, seed: 13);

        var realVolumes = real.Select(c => c.Volume).ToHashSet();
        for (var i = 1; i < twin.Count; i++)
        {
            var c = twin[i];
            Assert.True(c.High >= Math.Max(c.Open, c.Close), $"High incoerente a {i}");
            Assert.True(c.Low <= Math.Min(c.Open, c.Close), $"Low incoerente a {i}");
            Assert.True(c.Low > 0m, $"prezzo non positivo a {i}");
            Assert.Contains(c.Volume, realVolumes);
            Assert.Equal(twin[i - 1].Close, c.Open);   // catena open = close precedente
        }
    }

    [Fact]
    public void Generate_RejectsDegenerateInput()
    {
        Assert.Throws<ArgumentException>(() => NullTwinGenerator.Generate(NoisySeries(2, 1), seed: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullTwinGenerator.Generate(NoisySeries(10, 1), seed: 1, meanBlockLength: 0));
    }
}
