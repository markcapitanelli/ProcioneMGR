using ProcioneMGR.Data;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D-04, Fase 1 PRD-RISANAMENTO] Il gemello nullo conserva l'ASIMMETRIA degli stoppini. Prima li
/// spartiva 50/50 sopra e sotto il corpo: geometria idealizzata che sposta la probabilita' di
/// tocco di stop e target intra-barra — proprio cio' che i backtest usano per decidere le uscite,
/// e proprio sull'orizzonte intraday di riferimento. Ora la quota sopra/sotto e' campionata dalla
/// stessa barra sorgente (accoppiata come volume e ampiezza) e SPECCHIATA quando il segno del
/// rendimento e' stato invertito.
/// </summary>
public sealed class NullTwinWickAsymmetryTests
{
    /// <summary>Serie sintetica con stoppini FORTEMENTE sbilanciati verso l'alto: ogni barra ha
    /// tutto lo stoppino sopra il corpo (high molto oltre, low incollato al corpo).</summary>
    private static List<OhlcvData> UpperWickSeries(int n)
    {
        var list = new List<OhlcvData>();
        var close = 100m;
        var rng = new Random(7);
        for (var i = 0; i < n; i++)
        {
            var open = close;
            close = open * (1m + (decimal)(rng.NextDouble() - 0.5) * 0.02m);
            var bodyHi = Math.Max(open, close);
            var bodyLo = Math.Min(open, close);
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = new DateTime(2026, 1, 1).AddHours(i),
                Open = open,
                High = bodyHi * 1.02m, // stoppino superiore ~2%
                Low = bodyLo,          // stoppino inferiore NULLO
                Close = close,
                Volume = 100m + i,
            });
        }
        return list;
    }

    private static (double UpShare, double Total) WickShares(IReadOnlyList<OhlcvData> bars)
    {
        double up = 0, total = 0;
        foreach (var c in bars.Skip(1))
        {
            var body = Math.Abs((double)(c.Close - c.Open));
            var range = (double)(c.High - c.Low);
            var wick = Math.Max(0d, range - body);
            if (wick <= 0) continue;
            up += (double)(c.High - Math.Max(c.Open, c.Close));
            total += wick;
        }
        return (total > 0 ? up / total : 0.5, total);
    }

    [Fact]
    public void Twin_PreservesWickAsymmetry_InsteadOfHalving()
    {
        var real = UpperWickSeries(500);
        var twin = NullTwinGenerator.Generate(real, seed: 123);

        var (twinUpShare, twinTotal) = WickShares(twin);
        Assert.True(twinTotal > 0, "il gemello deve avere stoppini (la serie sorgente li ha)");

        // La sorgente ha quota superiore ~1,0. Col segno i.i.d. (p=0,5) meta' delle barre e'
        // specchiata: l'atteso della quota superiore del GEMELLO e' ~0,5 in MEDIA ma con barre
        // individuali a 0 o a 1 — MAI a 0,5 come faceva il 50/50 fisso. Si verifica quindi la
        // proprieta' distintiva: le barre del gemello sono SBILANCIATE (quota per-barra lontana
        // da 0,5), non spartite a meta'.
        var perBar = new List<double>();
        foreach (var c in twin.Skip(1))
        {
            var body = Math.Abs((double)(c.Close - c.Open));
            var wick = Math.Max(0d, (double)(c.High - c.Low) - body);
            if (wick <= 1e-9) continue;
            var upper = (double)(c.High - Math.Max(c.Open, c.Close));
            perBar.Add(upper / wick);
        }
        Assert.NotEmpty(perBar);
        // Col vecchio 50/50 ogni barra stava a 0,5 esatto; ora la stragrande maggioranza deve
        // stare agli estremi (la sorgente ha stoppini tutti da un lato).
        var extreme = perBar.Count(s => s < 0.1 || s > 0.9);
        Assert.True(extreme > perBar.Count * 0.9,
            $"attese barre sbilanciate come la sorgente, trovate {extreme}/{perBar.Count} agli estremi");
    }

    [Fact]
    public void Twin_IsStillDeterministic_PerSeed()
    {
        var real = UpperWickSeries(200);
        var a = NullTwinGenerator.Generate(real, seed: 42);
        var b = NullTwinGenerator.Generate(real, seed: 42);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Close, b[i].Close);
            Assert.Equal(a[i].High, b[i].High);
            Assert.Equal(a[i].Low, b[i].Low);
        }
    }

    [Fact]
    public void Twin_HighLowEnvelope_StaysCoherent()
    {
        var real = UpperWickSeries(300);
        var twin = NullTwinGenerator.Generate(real, seed: 9);
        foreach (var c in twin)
        {
            Assert.True(c.High >= Math.Max(c.Open, c.Close), "High deve contenere il corpo");
            Assert.True(c.Low <= Math.Min(c.Open, c.Close), "Low deve contenere il corpo");
            Assert.True(c.Low > 0m, "pavimento anti-degenerazione");
        }
    }
}
