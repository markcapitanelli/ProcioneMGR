using ProcioneMGR.Data;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="HarRvForecaster"/> e <see cref="RealizedVariance"/> (C3): causalità
/// (invariante di troncamento), warm-up, capacità di battere il naive su un processo di varianza
/// persistente, e la contabilità della RV giornaliera dai 5m (giorni bucati scartati).
/// </summary>
public class HarRvForecasterTests
{
    /// <summary>
    /// RV sintetica: varianza LATENTE AR(1) sulla log-scala (persistente, sempre positiva) più
    /// ERRORE DI MISURA moltiplicativo. Il rumore di misura non è un dettaglio: la RV reale è una
    /// stima campionaria della varianza integrata, e senza quell'errore il naive "domani come oggi"
    /// sarebbe quasi ottimo (misurato: batteva l'HAR) — è proprio mediando più scale che l'HAR
    /// filtra il rumore di misura, ed è per questo che esiste.
    /// </summary>
    private static List<double> PersistentRv(int n, int seed, double phi = 0.97, double measurementNoise = 0.25)
    {
        var rnd = new Random(seed);
        var logIv = Math.Log(1e-4);
        const double mean = -9.2; // ~ log(1e-4)
        var series = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            logIv = mean + phi * (logIv - mean) + 0.3 * (rnd.NextDouble() - 0.5) * 2;
            var obsNoise = measurementNoise * (rnd.NextDouble() - 0.5) * 2;
            series.Add(Math.Exp(logIv + obsNoise));
        }
        return series;
    }

    [Fact]
    public void ForecastSeries_IsAntiLookAhead_TruncationDoesNotChangePastValues()
    {
        var rv = PersistentRv(400, seed: 1);
        var full = HarRvForecaster.ForecastSeries(rv, horizon: 1);

        foreach (var cut in new[] { 150, 250, 399 })
        {
            var truncated = HarRvForecaster.ForecastSeries(rv.Take(cut + 1).ToList(), horizon: 1);
            Assert.Equal(full[cut].HasValue, truncated[cut].HasValue);
            if (full[cut].HasValue)
            {
                Assert.Equal(full[cut]!.Value, truncated[cut]!.Value, 12);
            }
        }
    }

    [Fact]
    public void ForecastSeries_WarmupIsNull_ThenEmits()
    {
        var rv = PersistentRv(200, seed: 2);
        var forecasts = HarRvForecaster.ForecastSeries(rv, horizon: 1);

        // Prima emissione: regressori mensili pieni (22) + righe minime di fit (60) + orizzonte.
        var firstExpected = HarRvForecaster.MonthWindow - 1 + HarRvForecaster.MinFitRows + 1;
        for (var i = 0; i < firstExpected; i++) Assert.Null(forecasts[i]);
        Assert.NotNull(forecasts[firstExpected]);
        Assert.True(forecasts[firstExpected]!.Value > 0, "la previsione di varianza deve essere positiva");
    }

    [Fact]
    public void ForecastSeries_BeatsNaive_OnPersistentVarianceProcess()
    {
        // Su un processo persistente ma mean-reverting il HAR (che media piu' scale) deve battere
        // il naive "domani come oggi" in QLIKE — è la ragione della sua esistenza.
        var rv = PersistentRv(600, seed: 3);
        var har = HarRvForecaster.ForecastSeries(rv, horizon: 1);

        double qHar = 0, qNaive = 0;
        var rows = 0;
        for (var i = 0; i < rv.Count - 1; i++)
        {
            if (har[i] is not { } hf) continue;
            var actual = Math.Sqrt(rv[i + 1]);
            qHar += ProcioneMGR.Services.ML.VolForecastEvaluator.Qlike(Math.Sqrt(hf), actual);
            qNaive += ProcioneMGR.Services.ML.VolForecastEvaluator.Qlike(Math.Sqrt(rv[i]), actual);
            rows++;
        }
        Assert.True(rows > 100, $"servono abbastanza righe valutabili, trovate {rows}");
        Assert.True(qHar / rows < qNaive / rows,
            $"HAR dovrebbe battere il naive su un processo persistente: QLIKE HAR {qHar / rows:F4} vs naive {qNaive / rows:F4}");
    }

    [Fact]
    public void ForecastSeries_InvalidHorizon_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HarRvForecaster.ForecastSeries([1e-4, 2e-4], horizon: 0));
    }

    [Fact]
    public void ForecastSeries_LogVariant_IsCausal_Positive_AndRobustToSpikes()
    {
        // La variante log (quella ADOTTATA dal gate C3) deve restare causale e sempre positiva; e
        // su una serie con un salto estremo di RV la sua previsione post-salto deve restare piu'
        // vicina al regime normale di quella sui livelli (il salto in log pesa poco, in livello
        // domina i coefficienti). E' il meccanismo per cui sui simboli con salti il log-HAR
        // vinceva 24/24 dove il livello perdeva.
        var rv = PersistentRv(400, seed: 11);
        rv[300] = rv[300] * 400; // salto estremo isolato (flash crash)

        var log = HarRvForecaster.ForecastSeries(rv, horizon: 1, onLogRv: true);
        var lvl = HarRvForecaster.ForecastSeries(rv, horizon: 1);

        foreach (var cut in new[] { 200, 399 })
        {
            var truncated = HarRvForecaster.ForecastSeries(rv.Take(cut + 1).ToList(), horizon: 1, onLogRv: true);
            Assert.Equal(log[cut].HasValue, truncated[cut].HasValue);
            if (log[cut].HasValue) Assert.Equal(log[cut]!.Value, truncated[cut]!.Value, 12);
        }
        Assert.All(log.Where(v => v.HasValue), v => Assert.True(v!.Value > 0));

        // 30 giorni dopo il salto (fuori dalla finestra settimanale, dentro la mensile): la
        // previsione log deve essere meno gonfiata di quella sui livelli.
        var typical = rv.Skip(250).Take(40).Average();
        Assert.True(log[330]!.Value < lvl[330]!.Value,
            $"post-salto il log-HAR ({log[330]:E2}) dovrebbe restare sotto il HAR sui livelli ({lvl[330]:E2}); RV tipica {typical:E2}");
    }

    [Fact]
    public void DailyFromIntraday_ComputesSumOfSquaredLogReturns_AndSkipsSparseDays()
    {
        // Giorno 1: 288 barre 5m piene. Giorno 2: solo 100 barre (buco) -> scartato.
        // Giorno 3: 288 barre piene -> incluso (il rendimento di mezzanotte appartiene al giorno 3).
        var candles = new List<OhlcvData>();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        decimal price = 100m;
        var rnd = new Random(7);

        void AddBars(int count)
        {
            for (var i = 0; i < count; i++)
            {
                price *= 1m + (decimal)((rnd.NextDouble() - 0.5) * 0.002);
                candles.Add(new OhlcvData
                {
                    Symbol = "TEST/USDT", Timeframe = "5m", TimestampUtc = t,
                    Open = price, High = price, Low = price, Close = price, Volume = 1m,
                });
                t = t.AddMinutes(5);
            }
        }

        AddBars(288);                     // giorno 1 completo
        AddBars(100); t = t.Date.AddDays(1); // giorno 2 monco (poi salto a mezzanotte del giorno 3)
        AddBars(288);                     // giorno 3 completo

        var daily = RealizedVariance.DailyFromIntraday(candles);

        Assert.Equal(2, daily.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), daily[0].Day);
        Assert.Equal(new DateOnly(2026, 1, 3), daily[1].Day);
        Assert.All(daily, d => Assert.True(d.Rv > 0));

        // Verifica contabile sul giorno 1: somma dei quadrati dei log-rendimenti delle barre del
        // giorno (il primo rendimento parte dalla seconda barra: non c'e' una barra precedente).
        double expected = 0;
        for (var i = 1; i < 288; i++)
        {
            var r = Math.Log((double)(candles[i].Close / candles[i - 1].Close));
            expected += r * r;
        }
        Assert.Equal(expected, daily[0].Rv, 12);
    }
}
