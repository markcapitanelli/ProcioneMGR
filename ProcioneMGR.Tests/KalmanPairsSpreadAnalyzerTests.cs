using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="KalmanPairsSpreadAnalyzer"/> (C2): stesse invarianti della rolling OLS
/// (anti-look-ahead, warm-up, recupero del β vero su coppia sintetica) più la proprietà per cui il
/// filtro esiste — su un β che DERIVA nel tempo, l'errore di inseguimento del Kalman deve essere
/// minore di quello della rolling OLS (che β lo vede solo attraverso una finestra in ritardo).
/// </summary>
public class KalmanPairsSpreadAnalyzerTests
{
    private readonly KalmanPairsSpreadAnalyzer _kalman = new();

    private static List<decimal> RandomWalk(int n, double stepScale, int seed)
    {
        var rnd = new Random(seed);
        var logLevel = Math.Log(100.0);
        var series = new List<decimal>(n) { 100m };
        for (var i = 1; i < n; i++)
        {
            logLevel += (rnd.NextDouble() - 0.5) * 2 * stepScale * 0.01;
            series.Add((decimal)Math.Exp(logLevel));
        }
        return series;
    }

    private static List<decimal> CointegratedWith(List<decimal> x, double beta, int seed, double noise = 0.003)
    {
        var rnd = new Random(seed);
        return x.Select(xi =>
            (decimal)Math.Exp(beta * Math.Log((double)xi) + (rnd.NextDouble() - 0.5) * 2 * noise)).ToList();
    }

    /// <summary>Y legata a X con β che varia nel tempo: β(i) fornito dal chiamante.</summary>
    private static List<decimal> DriftingBetaWith(List<decimal> x, Func<int, double> beta, int seed, double noise = 0.003)
    {
        var rnd = new Random(seed);
        return x.Select((xi, i) =>
            (decimal)Math.Exp(beta(i) * Math.Log((double)xi) + (rnd.NextDouble() - 0.5) * 2 * noise)).ToList();
    }

    /// <summary>
    /// Random walk geometrico attorno a log-livello 1 (prezzo ≈ 2,7). La scelta del livello NON è
    /// cosmetica, è ciò che rende β identificabile barra per barra: a log x ≈ 4,6 (prezzo 100) un β
    /// che cambia produce un trend β̇·log x che si aliasa con le derive locali di x (misurato: MAE
    /// ~0,5 per ENTRAMBI gli estimatori — stime senza senso); a log x ≈ 0 (prezzo 1) β sparisce
    /// dall'osservazione ogni volta che x attraversa 1 e il filtro attribuisce il cambiamento ad α.
    /// A log x ≈ 1 nessuno dei due artefatti è attivo.
    /// </summary>
    private static List<decimal> RandomWalkAroundE(int n, double stepScale, int seed)
    {
        var rnd = new Random(seed);
        var logLevel = 1.0;
        var series = new List<decimal>(n) { (decimal)Math.Exp(1.0) };
        for (var i = 1; i < n; i++)
        {
            logLevel += (rnd.NextDouble() - 0.5) * 2 * stepScale * 0.01;
            series.Add((decimal)Math.Exp(logLevel));
        }
        return series;
    }

    [Fact]
    public void Analyze_IsAntiLookAhead_TruncationDoesNotChangePastValues()
    {
        var x = RandomWalk(500, 1.0, seed: 1);
        var y = CointegratedWith(x, beta: 1.5, seed: 2);

        var full = _kalman.Analyze(y, x, warmupWindow: 60, delta: 1e-4, zScoreLookback: 15);

        foreach (var cut in new[] { 200, 300, 499 })
        {
            var truncated = _kalman.Analyze(y.Take(cut + 1).ToList(), x.Take(cut + 1).ToList(), 60, 1e-4, 15);

            Assert.Equal(full.HedgeRatio[cut].HasValue, truncated.HedgeRatio[cut].HasValue);
            if (full.HedgeRatio[cut].HasValue)
            {
                Assert.Equal(full.HedgeRatio[cut]!.Value, truncated.HedgeRatio[cut]!.Value, 9);
            }
            Assert.Equal(full.Spread[cut].HasValue, truncated.Spread[cut].HasValue);
            if (full.Spread[cut].HasValue)
            {
                Assert.Equal(full.Spread[cut]!.Value, truncated.Spread[cut]!.Value, 6);
            }
        }
    }

    [Fact]
    public void Analyze_HedgeRatio_ConvergesToTrueBeta_OnCointegratedPair()
    {
        var x = RandomWalk(1000, 1.0, seed: 3);
        const double trueBeta = 1.4;
        var y = CointegratedWith(x, trueBeta, seed: 4);

        var result = _kalman.Analyze(y, x, warmupWindow: 100, delta: 1e-4, zScoreLookback: 20);

        var lastHedge = result.HedgeRatio[^1];
        Assert.NotNull(lastHedge);
        Assert.True(Math.Abs(lastHedge!.Value - trueBeta) < 0.2, $"elasticità={lastHedge}, attesa ~{trueBeta}");
    }

    [Fact]
    public void Analyze_AdaptsToBetaRegimeChange_FasterThanRollingOls()
    {
        // Cambio di regime: β salta da 1,0 a 1,5 alla barra 600. La rolling OLS deve ASPETTARE che
        // la finestra di 120 barre si riempia del nuovo regime (più la stantiezza del ricalibro ogni
        // 30); il Kalman riattribuisce l'innovazione barra per barra. È la proprietà per cui la
        // letteratura preferisce il filtro — e un salto è il caso pulito, perché prima e dopo il β
        // vero è costante (una deriva continua a livello prezzo ~100 non è nemmeno identificabile,
        // vedi RandomWalkAroundE). MAE misurato sulle 300 barre dopo il salto.
        var x = RandomWalkAroundE(1200, 4.0, seed: 5);
        var y = DriftingBetaWith(x, i => i < 600 ? 1.0 : 1.5, seed: 6);

        // δ commisurato al problema: q = δ·R deve permettere al β filtrato di muoversi in fretta
        // dopo il salto (qui R ≈ 3e-6, minuscolo di proposito perché β sia osservabile). Sui dati
        // reali R è ordini di grandezza più grande e si usa il δ di letteratura (1e-4).
        var kal = _kalman.Analyze(y, x, warmupWindow: 120, delta: 0.01, zScoreLookback: 20);
        var ols = new RollingPairsSpreadAnalyzer().Analyze(y, x, lookbackWindow: 120, recalibrationInterval: 30, zScoreLookback: 20);

        double maeKal = 0, maeOls = 0;
        var count = 0;
        for (var i = 600; i < 900; i++)
        {
            maeKal += Math.Abs(kal.HedgeRatio[i]!.Value - 1.5);
            maeOls += Math.Abs(ols.HedgeRatio[i]!.Value - 1.5);
            count++;
        }
        maeKal /= count;
        maeOls /= count;

        Assert.True(maeKal < maeOls,
            $"il Kalman dovrebbe riadattarsi a un salto di β piu' in fretta della rolling OLS: MAE Kalman {maeKal:F4} vs OLS {maeOls:F4}");
    }

    [Fact]
    public void Analyze_WarmupBeforeWindow_IsNull_AndFirstEmissionMatchesOls()
    {
        var x = RandomWalk(200, 1.0, seed: 7);
        var y = CointegratedWith(x, 1.2, seed: 8);

        var kal = _kalman.Analyze(y, x, warmupWindow: 50, delta: 1e-4, zScoreLookback: 10);
        var ols = new RollingPairsSpreadAnalyzer().Analyze(y, x, lookbackWindow: 50, recalibrationInterval: 10, zScoreLookback: 10);

        for (var i = 0; i < 50; i++) Assert.Null(kal.HedgeRatio[i]);
        Assert.NotNull(kal.HedgeRatio[50]);

        // Alla prima barra utile i due estimatori partono dallo STESSO fit OLS sul warm-up: stesso
        // β (il predetto del Kalman è l'inizializzazione) e stesso spread. Da lì in poi divergono.
        Assert.Equal(ols.HedgeRatio[50]!.Value, kal.HedgeRatio[50]!.Value, 6);
        Assert.Equal(ols.Spread[50]!.Value, kal.Spread[50]!.Value, 6);
    }

    [Fact]
    public void Analyze_InvalidDelta_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(100, 1.0, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => _kalman.Analyze(y, x, 50, delta: 0, zScoreLookback: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => _kalman.Analyze(y, x, 50, delta: 1.0, zScoreLookback: 10));
    }

    [Fact]
    public void Engine_DefaultIsKalman_AndRollingOlsRemainsSelectable()
    {
        // Contratto post-gate C2 (2026-07-26): il default di classe è Kalman; la rolling OLS resta
        // selezionabile e il suo ramo è quello storico. La config di default deve produrre lo
        // STESSO risultato della scelta esplicita di Kalman.
        var x = RandomWalk(400, 1.0, seed: 9);
        var y = CointegratedWith(x, 1.3, seed: 10);
        var candlesY = ToCandles(y);
        var candlesX = ToCandles(x);

        var engine = new PairsBacktestEngine();
        var byDefault = engine.RunBacktest(candlesY, candlesX, new PairsBacktestConfiguration());
        var explicitKalman = engine.RunBacktest(candlesY, candlesX, new PairsBacktestConfiguration
        {
            HedgeRatioEstimator = PairsHedgeRatioEstimator.Kalman,
        });
        var explicitOls = engine.RunBacktest(candlesY, candlesX, new PairsBacktestConfiguration
        {
            HedgeRatioEstimator = PairsHedgeRatioEstimator.RollingOls,
        });

        Assert.Equal(PairsHedgeRatioEstimator.Kalman, new PairsBacktestConfiguration().HedgeRatioEstimator);
        Assert.Equal(explicitKalman.FinalCapital, byDefault.FinalCapital);
        Assert.Equal(explicitKalman.TotalTrades, byDefault.TotalTrades);
        // Entrambi i rami producono una equity curve completa sulla stessa serie.
        Assert.Equal(byDefault.EquityCurve.Count, explicitOls.EquityCurve.Count);
    }

    private static List<ProcioneMGR.Data.OhlcvData> ToCandles(List<decimal> closes)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return closes.Select((c, i) => new ProcioneMGR.Data.OhlcvData
        {
            Symbol = "TEST/USDT",
            Timeframe = "1d",
            TimestampUtc = t0.AddDays(i),
            Open = c, High = c, Low = c, Close = c, Volume = 1m,
        }).ToList();
    }
}
