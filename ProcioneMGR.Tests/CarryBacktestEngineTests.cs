using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Carry;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E3 roadmap profitto-intraday] Motore di backtest del carry delta-neutro. Questi test fissano:
/// (a) a funding costante alto la strategia entra e incassa il funding meno i costi delle due gambe;
/// (b) l'A/B a funding zero: netto = −costi (nessun income, si pagano solo i fill); (c) l'isteresi
/// enter&gt;exit è obbligatoria; (d) determinismo.
/// </summary>
public class CarryBacktestEngineTests
{
    private static List<FundingRatePoint> Funding(int n, decimal ratePctPer8h, DateTime? start = null)
    {
        var t0 = start ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n).Select(i => new FundingRatePoint(t0.AddHours(i * 8), ratePctPer8h)).ToList();
    }

    [Fact]
    public void ConstantHighFunding_EntersAndCollectsFundingMinusTwoLegCosts()
    {
        // Funding costante 0,03%/8h = annualizzato 0,03·3·365 = 32,85% >> soglia 5% → entra subito
        // (dopo il warm-up della finestra) e resta dentro. Netto ≈ funding incassato − costi 1 episodio.
        var funding = Funding(120, 0.03m);
        var cfg = new CarryConfiguration
        {
            InitialCapital = 10_000m, PositionSizePercent = 100m,
            EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 0m, TrailingFundingEvents = 9,
            SpotFeePercent = 0.1m, PerpFeePercent = 0.05m, SlippagePercent = 0.03m,
        };

        var r = new CarryBacktestEngine().Run(funding, cfg);

        Assert.Equal(1, r.Episodes);                       // un solo ingresso, mai esce (funding sempre alto)
        Assert.True(r.GrossFundingPercent > 3m, $"gross atteso >3% ( ~111 eventi × 0,03%), trovato {r.GrossFundingPercent}");
        // Netto = gross − costo di un episodio (0,42%): positivo e vicino al gross.
        Assert.True(r.TotalReturnPercent > 2.5m, $"netto atteso >2,5%, trovato {r.TotalReturnPercent}");
        Assert.True(r.TotalReturnPercent < r.GrossFundingPercent, "il netto deve essere < gross (si pagano i costi)");
        Assert.True(r.TimeInPositionFraction > 0.8m, "con funding sempre alto si sta quasi sempre in posizione");
    }

    [Fact]
    public void ZeroFunding_NeverEnters_NetIsZero()
    {
        // A/B: funding nullo → annualizzato 0 < soglia 5% → non entra mai → netto esattamente 0.
        var funding = Funding(120, 0m);
        var cfg = new CarryConfiguration { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 0m };

        var r = new CarryBacktestEngine().Run(funding, cfg);

        Assert.Equal(0, r.Episodes);
        Assert.Equal(0m, r.TotalReturnPercent);
        Assert.Equal(0m, r.GrossFundingPercent);
    }

    [Fact]
    public void NegativeFunding_ShortWouldPay_StaysOut()
    {
        // Funding NEGATIVO: lo short PAGHEREBBE. L'annualizzato è negativo, sotto la soglia di entrata:
        // non si entra mai. (È la protezione ovvia: non si incassa un flusso che è a nostro sfavore.)
        var funding = Funding(120, -0.02m);
        var cfg = new CarryConfiguration { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 0m };

        var r = new CarryBacktestEngine().Run(funding, cfg);
        Assert.Equal(0, r.Episodes);
    }

    [Fact]
    public void FundingDropsMidway_ExitsAndBanksTheEpisode()
    {
        // Prima metà funding alto (entra), seconda metà zero (esce). Un episodio chiuso, netto positivo.
        var high = Funding(60, 0.03m);
        var low = Funding(60, 0m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(60 * 8));
        var funding = high.Concat(low).ToList();
        var cfg = new CarryConfiguration
        {
            EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 2m, TrailingFundingEvents = 9,
            PositionSizePercent = 100m,
        };

        var r = new CarryBacktestEngine().Run(funding, cfg);

        Assert.Equal(1, r.Episodes);
        var ep = Assert.Single(r.EpisodeList);
        Assert.True(ep.NetPercent > 0m, $"episodio atteso positivo, trovato {ep.NetPercent}");
        Assert.True(r.TimeInPositionFraction < 0.7m, "esce a metà: tempo in posizione < 70%");
    }

    [Fact]
    public void ExitThresholdNotBelowEnter_IsRejected()
    {
        var funding = Funding(50, 0.02m);
        Assert.Throws<ArgumentException>(() =>
            new CarryBacktestEngine().Run(funding, new CarryConfiguration { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 5m }));
    }

    [Fact]
    public void SameInput_SameResult_Deterministic()
    {
        var funding = Funding(200, 0.025m);
        var cfg = new CarryConfiguration { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 1m };
        var a = new CarryBacktestEngine().Run(funding, cfg);
        var b = new CarryBacktestEngine().Run(funding, cfg);
        Assert.Equal(a.TotalReturnPercent, b.TotalReturnPercent);
        Assert.Equal(a.Episodes, b.Episodes);
    }
}
