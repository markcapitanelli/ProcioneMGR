using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Tests;

/// <summary>Test delle metriche estese del tearsheet (Sortino, Calmar, Omega, VaR/CVaR, drawdown duration, exposure, hit-rate).</summary>
public class TearsheetStatisticsTests
{
    private static List<EquityPoint> MakeCurve(IReadOnlyList<decimal> capitals, DateTime? start = null, int stepHours = 1)
    {
        var t0 = start ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<EquityPoint>(capitals.Count);
        for (var i = 0; i < capitals.Count; i++)
        {
            list.Add(new EquityPoint { Timestamp = t0.AddHours(i * stepHours), Capital = capitals[i] });
        }
        return list;
    }

    // --- Sortino -----------------------------------------------------------------------------

    [Fact]
    public void Sortino_NoDownside_IsZero_NoDivideByZero()
    {
        // Rendimenti tutti positivi -> nessuna downside deviation -> 0 (evita div/0), non infinito.
        var eq = MakeCurve([100m, 110m, 121m, 133.1m]);
        Assert.Equal(0m, Statistics.SortinoRatio(eq, 8760));
    }

    [Fact]
    public void Sortino_PositiveTrend_WithSomeLosses_IsPositive()
    {
        var eq = new List<EquityPoint>();
        decimal cap = 100m;
        var deltas = new[] { 0.02m, -0.01m, 0.03m, 0.01m, -0.005m, 0.02m, 0.015m, -0.008m, 0.025m, 0.01m };
        eq.Add(new EquityPoint { Capital = cap });
        foreach (var d in deltas)
        {
            cap *= 1 + d;
            eq.Add(new EquityPoint { Capital = cap });
        }
        Assert.True(Statistics.SortinoRatio(eq, 8760) > 0m);
    }

    [Fact]
    public void Sortino_TooFewPoints_IsZero()
    {
        var eq = MakeCurve([100m, 110m]);
        Assert.Equal(0m, Statistics.SortinoRatio(eq, 8760));
    }

    // --- Annualized return / Calmar -----------------------------------------------------------

    [Fact]
    public void AnnualizedReturn_KnownDoubling_MatchesHandComputed()
    {
        // 4 punti (3 periodi) con periodsPerYear=12 (dati mensili): esponente 12/3=4.
        // Capitale raddoppia in 3 periodi -> annualizzato = 2^4 - 1 = 15.
        var eq = MakeCurve([100m, 126m, 158.76m, 200m]);
        var expected = (decimal)Math.Pow(2.0, 4.0) - 1m;
        var actual = Statistics.AnnualizedReturn(eq, 12);
        Assert.True(Math.Abs(actual - expected) < 1e-6m, $"actual={actual} expected={expected}");
    }

    [Fact]
    public void AnnualizedReturn_ExtremeExtrapolation_ReturnsZero_NoOverflow()
    {
        // Curva cortissima rispetto a periodsPerYear -> estrapolazione fuori scala: 0, non overflow.
        var eq = MakeCurve([100m, 200m, 400m, 800m]);
        Assert.Equal(0m, Statistics.AnnualizedReturn(eq, 8760));
    }

    [Fact]
    public void CalmarRatio_NoDrawdown_IsZero_NoDivideByZero()
    {
        var eq = MakeCurve([100m, 110m, 121m, 133.1m]); // monotona crescente -> drawdown 0
        Assert.Equal(0m, Statistics.CalmarRatio(eq, 8760));
    }

    [Fact]
    public void CalmarRatio_WithDrawdown_IsPositive_ForNetGain()
    {
        // 30 punti giornalieri: trend netto in salita con un ritracciamento (drawdown) a metà.
        var caps = new List<decimal> { 100m };
        for (var i = 0; i < 14; i++) caps.Add(caps[^1] * 1.02m);   // sale
        for (var i = 0; i < 5; i++) caps.Add(caps[^1] * 0.97m);    // drawdown
        for (var i = 0; i < 10; i++) caps.Add(caps[^1] * 1.02m);   // recupera e supera
        var eq = MakeCurve(caps, stepHours: 24);

        var calmar = Statistics.CalmarRatio(eq, periodsPerYear: 365);
        Assert.True(calmar > 0m, $"Calmar={calmar}");
    }

    // --- Omega ---------------------------------------------------------------------------------

    [Fact]
    public void Omega_NoLosses_IsZero_NoDivideByZero()
    {
        var eq = MakeCurve([100m, 110m, 121m]);
        Assert.Equal(0m, Statistics.OmegaRatio(eq));
    }

    [Fact]
    public void Omega_MoreGainsThanLosses_IsGreaterThanOne()
    {
        // Rendimenti: +0.10, -0.02, +0.08, -0.01 -> gains=0.18, losses=0.03 -> Omega=6
        var eq = MakeCurve([100m, 110m, 107.8m, 116.424m, 115.26m]);
        var omega = Statistics.OmegaRatio(eq);
        Assert.True(omega > 1m);
    }

    // --- Tail ratio / VaR / CVaR ----------------------------------------------------------------

    [Fact]
    public void TailRatio_SymmetricReturns_IsCloseToOne()
    {
        var caps = new List<decimal> { 100m };
        var deltas = new decimal[] { 0.05m, -0.05m, 0.04m, -0.04m, 0.03m, -0.03m, 0.02m, -0.02m, 0.01m, -0.01m, 0m };
        foreach (var d in deltas) caps.Add(caps[^1] * (1 + d));
        var eq = MakeCurve(caps);
        var ratio = Statistics.TailRatio(eq);
        Assert.True(Math.Abs(ratio - 1m) < 0.5m, $"TailRatio={ratio}");
    }

    [Fact]
    public void HistoricalVaR_IsPositive_ForLosingTail()
    {
        var caps = new List<decimal> { 100m };
        // 20 rendimenti, la coda sinistra ha perdite chiare.
        var rnd = new Random(1);
        for (var i = 0; i < 40; i++)
        {
            var shock = (decimal)(rnd.NextDouble() - 0.55) * 0.05m; // leggermente sbilanciato al ribasso
            caps.Add(Math.Max(1m, caps[^1] * (1 + shock)));
        }
        var eq = MakeCurve(caps);
        var vaR = Statistics.HistoricalVaR(eq, 0.95m);
        Assert.True(vaR > 0m, $"VaR={vaR}");
    }

    [Fact]
    public void HistoricalCVaR_IsAtLeastAsSevereAsVaR()
    {
        var caps = new List<decimal> { 100m };
        var rnd = new Random(2);
        for (var i = 0; i < 60; i++)
        {
            var shock = (decimal)(rnd.NextDouble() - 0.5) * 0.06m;
            caps.Add(Math.Max(1m, caps[^1] * (1 + shock)));
        }
        var eq = MakeCurve(caps);
        var vaR = Statistics.HistoricalVaR(eq, 0.95m);
        var cVaR = Statistics.HistoricalCVaR(eq, 0.95m);
        // CVaR = media della coda oltre il VaR -> perdita media >= perdita-soglia (VaR).
        Assert.True(cVaR >= vaR - 1e-9m, $"VaR={vaR} CVaR={cVaR}");
    }

    // --- Max drawdown duration -------------------------------------------------------------------

    [Fact]
    public void MaxDrawdownDuration_RecoveredDrawdown_CountsUntilNewHigh()
    {
        // Picco a indice 1 (110), valle, nuovo massimo a indice 4 (111) -> durata = 4-1 = 3
        var eq = MakeCurve([100m, 110m, 90m, 100m, 111m]);
        Assert.Equal(3, Statistics.MaxDrawdownDurationPeriods(eq));
    }

    [Fact]
    public void MaxDrawdownDuration_NeverRecovered_CountsUntilEnd()
    {
        // Picco a indice 1 (120), mai più recuperato -> durata = ultimo indice(3) - peakIndex(1) = 2.
        var eq = MakeCurve([100m, 120m, 90m, 95m]);
        Assert.Equal(2, Statistics.MaxDrawdownDurationPeriods(eq));
    }

    [Fact]
    public void MaxDrawdownDuration_MonotonicUp_IsZero()
    {
        var eq = MakeCurve([100m, 110m, 121m]);
        Assert.Equal(0, Statistics.MaxDrawdownDurationPeriods(eq));
    }

    // --- Exposure / hit-rate ---------------------------------------------------------------------

    [Fact]
    public void ExposurePercent_HalfTimeInMarket_IsAboutFifty()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var eq = MakeCurve(Enumerable.Repeat(100m, 11).ToList(), start); // 10h totali (indice 0..10)
        var trades = new List<BacktestTrade>
        {
            new() { EntryTime = start, ExitTime = start.AddHours(5) }, // 5h su 10h totali -> 50%
        };
        var exposure = Statistics.ExposurePercent(trades, eq);
        Assert.True(Math.Abs(exposure - 50m) < 0.01m, $"Exposure={exposure}");
    }

    [Fact]
    public void ExposurePercent_OpenTradeAtEnd_ClampsToCurveEnd()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var eq = MakeCurve(Enumerable.Repeat(100m, 11).ToList(), start);
        var trades = new List<BacktestTrade>
        {
            new() { EntryTime = start.AddHours(8), ExitTime = null }, // ancora aperto -> conta fino a fine curva
        };
        var exposure = Statistics.ExposurePercent(trades, eq);
        Assert.True(Math.Abs(exposure - 20m) < 0.01m, $"Exposure={exposure}"); // 2h su 10h
    }

    [Fact]
    public void HitRate_MixedTrades_MatchesRatio()
    {
        var trades = new List<BacktestTrade>
        {
            new() { Pnl = 10m }, new() { Pnl = -5m }, new() { Pnl = 3m }, new() { Pnl = 0m },
        };
        Assert.Equal(50m, Statistics.HitRate(trades));
    }

    [Fact]
    public void HitRate_NoTrades_IsZero()
    {
        Assert.Equal(0m, Statistics.HitRate(new List<BacktestTrade>()));
    }

    // --- Tearsheet composito -----------------------------------------------------------------

    [Fact]
    public void ComputeTearsheet_ReturnsAllFieldsConsistentWithIndividualMethods()
    {
        var eq = MakeCurve([100m, 110m, 99m, 108m, 95m, 115m]);
        var trades = new List<BacktestTrade>
        {
            new() { EntryTime = eq[0].Timestamp, ExitTime = eq[2].Timestamp, Pnl = -1m },
            new() { EntryTime = eq[2].Timestamp, ExitTime = eq[5].Timestamp, Pnl = 16m },
        };

        var sheet = Statistics.ComputeTearsheet(eq, trades, periodsPerYear: 8760);

        Assert.Equal(Statistics.SharpeRatio(eq, 8760), sheet.Sharpe);
        Assert.Equal(Statistics.SortinoRatio(eq, 8760), sheet.Sortino);
        Assert.Equal(Statistics.CalmarRatio(eq, 8760), sheet.Calmar);
        Assert.Equal(Statistics.OmegaRatio(eq), sheet.Omega);
        Assert.Equal(Statistics.MaxDrawdownPercent(eq), sheet.MaxDrawdownPercent);
        Assert.Equal(Statistics.MaxDrawdownDurationPeriods(eq), sheet.MaxDrawdownDurationPeriods);
        Assert.Equal(Statistics.ExposurePercent(trades, eq), sheet.ExposurePercent);
        Assert.Equal(Statistics.HitRate(trades), sheet.HitRatePercent);
    }

    // --- Deflated Sharpe (single track, per il gate Champion del ModelRegistry) ---------------

    [Fact]
    public void DeflatedSharpeSingleTrack_TooShortOrNullCurve_IsNull()
    {
        Assert.Null(Statistics.DeflatedSharpeSingleTrack(MakeCurve([100m, 110m]), 8760));
        Assert.Null(Statistics.DeflatedSharpeSingleTrack(null, 8760));
    }

    [Fact]
    public void DeflatedSharpeSingleTrack_StrongSteadyTrack_IsSignificant()
    {
        // Drift positivo costante con varianza minima su un track lungo ⇒ PSR (= DSR a 1 trial) ≈ 1.
        var caps = new List<decimal> { 100m };
        for (var i = 0; i < 400; i++)
        {
            var d = 0.003m + (i % 2 == 0 ? 0.0005m : -0.0005m);
            caps.Add(caps[^1] * (1 + d));
        }
        var dsr = Statistics.DeflatedSharpeSingleTrack(MakeCurve(caps), periodsPerYear: 365);
        Assert.NotNull(dsr);
        Assert.True(dsr > 0.95, $"atteso significativo (>0.95), ottenuto {dsr}");
    }

    [Fact]
    public void DeflatedSharpeSingleTrack_ZeroDriftNoise_IsNotSignificant()
    {
        // Rendimenti alternati ±2% (media ~0) ⇒ Sharpe per-periodo ~0 ⇒ PSR ≈ 0.5 < soglia 0.95.
        var caps = new List<decimal> { 100m };
        for (var i = 0; i < 400; i++)
        {
            var d = i % 2 == 0 ? 0.02m : -0.02m;
            caps.Add(caps[^1] * (1 + d));
        }
        var dsr = Statistics.DeflatedSharpeSingleTrack(MakeCurve(caps), periodsPerYear: 365);
        Assert.NotNull(dsr);
        Assert.True(dsr < 0.95, $"atteso non significativo (<0.95), ottenuto {dsr}");
    }
}
