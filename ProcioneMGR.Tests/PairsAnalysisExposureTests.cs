using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I10] <b>Una sola verità sullo z-score.</b>
///
/// Il difetto, entrato col C2 (adozione del Kalman, 2026-07-26) e trovato il 2026-08-18: la pagina
/// <c>/pairs-trading</c> passava al motore l'estimatore selezionato ma disegnava il grafico dello
/// z-score con un <c>RollingPairsSpreadAnalyzer</c> <b>fisso</b>. Scegliendo Kalman si vedeva la
/// curva dell'OLS — <b>il grafico descriveva un backtest diverso da quello eseguito</b>, e la doc di
/// pagina dichiarava esattamente che questo non poteva succedere («nessuna doppia verità»).
///
/// <para>Il rimedio non è ricalcolare meglio: è <b>non ricalcolare</b>. Il motore espone l'analisi
/// che ha deciso, così non esiste un secondo calcolo che possa divergere.</para>
/// </summary>
public class PairsAnalysisExposureTests
{
    /// <summary>
    /// Due serie cointegrate con rumore: abbastanza struttura perché i due estimatori producano
    /// hedge ratio diversi, che è la condizione in cui il difetto si vede.
    /// </summary>
    private static (List<OhlcvData> Y, List<OhlcvData> X) Series(int n = 400)
    {
        var rnd = new Random(20260819);
        var y = new List<OhlcvData>(n);
        var x = new List<OhlcvData>(n);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var level = 100.0;
        for (var i = 0; i < n; i++)
        {
            level += (rnd.NextDouble() - 0.5) * 2;                 // random walk comune
            var beta = 1.5 + i * 0.001;                             // beta che DERIVA: OLS e Kalman divergono
            var noise = (rnd.NextDouble() - 0.5) * 1.5;
            var ts = t0.AddHours(i);
            x.Add(new OhlcvData { Symbol = "X/USDT", Timeframe = "1h", TimestampUtc = ts, Open = (decimal)level, High = (decimal)level, Low = (decimal)level, Close = (decimal)level, Volume = 1 });
            var yv = level * beta + noise;
            y.Add(new OhlcvData { Symbol = "Y/USDT", Timeframe = "1h", TimestampUtc = ts, Open = (decimal)yv, High = (decimal)yv, Low = (decimal)yv, Close = (decimal)yv, Volume = 1 });
        }
        return (y, x);
    }

    private static PairsBacktestConfiguration Config(PairsHedgeRatioEstimator estimator) => new()
    {
        SymbolY = "Y/USDT",
        SymbolX = "X/USDT",
        InitialCapital = 10_000m,
        PositionSizePercent = 10m,
        FeePercent = 0.1m,
        LookbackWindow = 60,
        RecalibrationInterval = 5,
        ZScoreLookback = 30,
        EntryZScore = 2.0m,
        ExitZScore = 0.5m,
        HedgeRatioEstimator = estimator,
    };

    /// <summary>
    /// <b>Il test che sul codice precedente FALLISCE.</b> Il risultato deve portare l'analisi
    /// dell'estimatore CHIESTO: prima non la portava affatto e la pagina ne fabbricava una propria.
    /// </summary>
    [Fact]
    public void IlRisultatoPortaLAnalisiDellEstimatoreChiesto()
    {
        var (y, x) = Series();
        var engine = new PairsBacktestEngine();

        var kalman = engine.RunBacktest(y, x, Config(PairsHedgeRatioEstimator.Kalman));
        var ols = engine.RunBacktest(y, x, Config(PairsHedgeRatioEstimator.RollingOls));

        Assert.NotNull(kalman.Analysis);
        Assert.NotNull(ols.Analysis);
        Assert.Equal(PairsHedgeRatioEstimator.Kalman, kalman.EstimatorUsed);
        Assert.Equal(PairsHedgeRatioEstimator.RollingOls, ols.EstimatorUsed);
    }

    /// <summary>
    /// <b>La prova che il difetto era visibile</b>: le due curve z-score sono DIVERSE. Se fossero
    /// uguali, disegnare quella sbagliata non avrebbe conseguenze e questo item non servirebbe —
    /// e il test non potrebbe fallire, cioè non sarebbe una verifica.
    /// </summary>
    [Fact]
    public void IDueEstimatoriProduconoCurveDiverse()
    {
        var (y, x) = Series();
        var engine = new PairsBacktestEngine();

        var kalman = engine.RunBacktest(y, x, Config(PairsHedgeRatioEstimator.Kalman)).Analysis!;
        var ols = engine.RunBacktest(y, x, Config(PairsHedgeRatioEstimator.RollingOls)).Analysis!;

        var diverse = kalman.ZScore.Zip(ols.ZScore)
            .Count(p => p.First.HasValue && p.Second.HasValue && Math.Abs(p.First.Value - p.Second.Value) > 1e-6);

        Assert.True(diverse > 0,
            "I due estimatori producono la stessa curva: il caso di prova non esercita il difetto.");
    }

    /// <summary>
    /// L'analisi esposta è ESATTAMENTE quella che l'estimatore produce chiamato direttamente: il
    /// risultato non porta una terza cosa. È il riferimento indipendente del livello 1.
    /// </summary>
    [Fact]
    public void LAnalisiEspostaCoincideConQuellaDellAnalizzatoreChiamatoDirettamente()
    {
        var (y, x) = Series();
        var cfg = Config(PairsHedgeRatioEstimator.Kalman);
        var (alignedY, alignedX) = PairsCandleAligner.Align(y, x);

        var dalMotore = new PairsBacktestEngine().RunBacktest(y, x, cfg).Analysis!;
        var diretta = new KalmanPairsSpreadAnalyzer().Analyze(
            alignedY.Select(c => c.Close).ToList(), alignedX.Select(c => c.Close).ToList(),
            cfg.LookbackWindow, cfg.KalmanDelta, cfg.ZScoreLookback);

        Assert.Equal(diretta.ZScore.Count, dalMotore.ZScore.Count);
        for (var i = 0; i < diretta.ZScore.Count; i++)
        {
            Assert.Equal(diretta.ZScore[i], dalMotore.ZScore[i]);
        }
    }

    /// <summary>
    /// <b>Il controllo sul rumore per i costi</b>: lo slippage MORDE. Se non mordesse, esporlo in
    /// pagina sarebbe una manopola che non muove nulla — e su una coppia il costo si paga su DUE
    /// gambe per trade, cioè è il posto dove uno sconto silenzioso fa più danno.
    /// </summary>
    [Fact]
    public void LoSlippageRiduceIlRisultato_ENonEIgnorato()
    {
        var (y, x) = Series();
        var engine = new PairsBacktestEngine();

        var senza = engine.RunBacktest(y, x, Config(PairsHedgeRatioEstimator.RollingOls));
        var cfgConCosti = Config(PairsHedgeRatioEstimator.RollingOls);
        cfgConCosti.SlippagePercent = 0.05m;
        var con = engine.RunBacktest(y, x, cfgConCosti);

        // Il caso di prova deve produrre trade, altrimenti il confronto non dimostra niente.
        Assert.True(senza.TotalTrades > 0, "Nessun trade: il caso non esercita i costi.");
        Assert.True(con.FinalCapital < senza.FinalCapital,
            $"Lo slippage non ha ridotto il capitale finale ({con.FinalCapital} vs {senza.FinalCapital}).");
    }
}
