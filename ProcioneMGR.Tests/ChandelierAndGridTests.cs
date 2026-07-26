using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 5a — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Trailing "chandelier": distanza k×ATR dal
/// miglior prezzo invece di una percentuale fissa. È l'unica raccomandazione del PDF sugli stop che
/// la piattaforma non avesse già in forma migliore, ed entra come <b>candidato da falsificare</b>,
/// non come miglioramento assunto.
///
/// Le due proprietà che questi test difendono sono quelle che, sbagliate, renderebbero il confronto
/// con i bracket esistenti privo di senso: la <b>causalità</b> (l'ATR della barra corrente non può
/// entrare in uno stop che è "in macchina" prima che quella barra apra) e il fatto che il trailing
/// ATR <b>sostituisca</b> quello percentuale invece di sommarvisi.
/// </summary>
public sealed class ChandelierTrailingTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Candle(int i, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = Start.AddHours(i),
        Open = open, High = high, Low = low, Close = close, Volume = 100m,
    };

    /// <summary>Sale in modo regolare, poi ritraccia bruscamente: fa scattare qualsiasi trailing.</summary>
    private static List<OhlcvData> RiseThenDrop(int riseBars, decimal step, decimal dropTo)
    {
        var candles = new List<OhlcvData>();
        var price = 100m;
        for (var i = 0; i < riseBars; i++)
        {
            var next = price + step;
            candles.Add(Candle(i, price, next, price, next));
            price = next;
        }
        candles.Add(Candle(riseBars, price, price, dropTo, dropTo));
        for (var i = 1; i <= 3; i++) candles.Add(Candle(riseBars + i, dropTo, dropTo, dropTo, dropTo));
        return candles;
    }

    /// <summary>Strategia scriptata: long alla barra indicata, poi mai più segnali.</summary>
    private sealed class LongAt(int bar) : IStrategy
    {
        public string Name => "LongAt";
        public string DisplayName => "LongAt";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) =>
            index == bar ? Signal.Long : Signal.Hold;
    }

    private sealed class SingleStrategyFactory(IStrategy strategy) : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [strategy];
        public IStrategy Create(string strategyName) => strategy;
    }

    private static async Task<BacktestResult> RunAsync(List<OhlcvData> candles, Action<BacktestConfiguration> configure)
    {
        var config = new BacktestConfiguration
        {
            Symbol = "BTC/USDT", Timeframe = "1h", StrategyName = "LongAt",
            InitialCapital = 10_000m, PositionSizePercent = 100m,
            FeePercent = 0m, SlippagePercent = 0m,
        };
        configure(config);

        // dbFactory/alphaFactory null: questo percorso non li tocca (stesso pattern di
        // FundingHistoryTests), la strategia è passata già costruita.
        var engine = new BacktestEngine(
            null!, new SingleStrategyFactory(new LongAt(2)), new TechnicalIndicatorsService(),
            null!, NullLogger<BacktestEngine>.Instance);
        return await engine.RunBacktestAsync(config, candles, new LongAt(2), CancellationToken.None);
    }

    [Fact]
    public async Task AtrTrailing_ClosesThePosition_LikeThePercentOne()
    {
        // Prova di vita: con un ritracciamento largo il chandelier deve chiudere, altrimenti i
        // confronti successivi misurerebbero solo "stop mai scattato".
        var candles = RiseThenDrop(riseBars: 30, step: 1m, dropTo: 100m);

        var withAtr = await RunAsync(candles, c => { c.TrailingAtrMultiple = 2m; c.TrailingAtrPeriod = 14; });

        Assert.NotEmpty(withAtr.Trades);
        Assert.All(withAtr.Trades, t => Assert.True(t.ExitPrice > 0m));
    }

    [Fact]
    public async Task AtrTrailing_ReplacesPercentTrailing_InsteadOfStacking()
    {
        // Se i due trailing si sommassero, vincerebbe sempre il più stretto e il parametro ATR
        // sarebbe inerte quando la percentuale è aggressiva: il confronto fra i due approcci
        // diventerebbe impossibile da leggere. Qui la percentuale è strettissima (0,5%) e l'ATR
        // largo: se il risultato con ATR coincidesse con quello a sola percentuale, si starebbero
        // sommando.
        var candles = RiseThenDrop(riseBars: 30, step: 1m, dropTo: 100m);

        var percentOnly = await RunAsync(candles, c => c.TrailingStopPercent = 0.5m);
        var atrOnly = await RunAsync(candles, c => { c.TrailingStopPercent = 0.5m; c.TrailingAtrMultiple = 5m; c.TrailingAtrPeriod = 14; });

        Assert.NotEmpty(percentOnly.Trades);
        Assert.NotEmpty(atrOnly.Trades);
        Assert.NotEqual(percentOnly.Trades[0].ExitPrice, atrOnly.Trades[0].ExitPrice);
    }

    [Fact]
    public async Task AtrTrailing_IsInert_WhenNotConfigured()
    {
        // Anti-regressione sul default: chi non chiede il chandelier non deve vedere cambiare nulla.
        var candles = RiseThenDrop(riseBars: 30, step: 1m, dropTo: 100m);

        var baseline = await RunAsync(candles, _ => { });
        var explicitZero = await RunAsync(candles, c => c.TrailingAtrMultiple = 0m);

        Assert.Equal(baseline.Trades.Count, explicitZero.Trades.Count);
        Assert.Equal(baseline.TotalReturnPercent, explicitZero.TotalReturnPercent);
    }

    [Fact]
    public async Task AtrTrailing_WiderMultiple_ExitsNoEarlier()
    {
        // Monotonia: uno stop più largo non può chiudere PRIMA di uno più stretto. Se accadesse,
        // il livello starebbe usando l'ATR della barra sbagliata (sguardo in avanti) — il modo
        // silenzioso in cui questo tipo di stop finisce per barare.
        var candles = RiseThenDrop(riseBars: 40, step: 1m, dropTo: 100m);

        var tight = await RunAsync(candles, c => { c.TrailingAtrMultiple = 1m; c.TrailingAtrPeriod = 14; });
        var wide = await RunAsync(candles, c => { c.TrailingAtrMultiple = 4m; c.TrailingAtrPeriod = 14; });

        Assert.NotEmpty(tight.Trades);
        Assert.NotEmpty(wide.Trades);
        Assert.True(wide.Trades[0].ExitTime >= tight.Trades[0].ExitTime,
            "uno stop più largo non può uscire prima di uno più stretto");
    }
}

/// <summary>
/// [Fase 5b] La strategia a gradini fissi. Il test più importante non è sul profitto — è che la
/// strategia faccia <b>quello che il suo nome dichiara</b>: cicli finiti che raccolgono un gradino,
/// non un grid multi-ordine (inesprimibile in un motore a posizione singola).
/// </summary>
public sealed class GridMeanReversionTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = Start.AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    private static async Task<GridMeanReversionStrategy> BuildAsync(
        List<OhlcvData> candles, decimal step = 1m, int rungs = 1, int anchorPeriod = 10, int direction = 0)
    {
        var strategy = new GridMeanReversionStrategy();
        await strategy.InitializeAsync(
            [.. candles.Select(c => c.Close)], candles,
            new Dictionary<string, decimal>
            {
                ["AnchorPeriod"] = anchorPeriod,
                ["StepPercent"] = step,
                ["EntryRungs"] = rungs,
                ["Direction"] = direction,
            },
            new TechnicalIndicatorsService(), CancellationToken.None);
        return strategy;
    }

    [Fact]
    public async Task EntersBelowAnchor_AndHarvestsOneRung()
    {
        // 20 barre piatte a 100 (ancoraggio = 100), poi un tuffo a 97 (3 gradini sotto) e il
        // ritorno a 101,5. Ingresso al tuffo, uscita al primo gradino di profitto.
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 20; i++) candles.Add(Candle(i, 100m));
        candles.Add(Candle(20, 97m));
        candles.Add(Candle(21, 98.5m));

        var strategy = await BuildAsync(candles, step: 1m, rungs: 1);

        var signals = new List<Signal>();
        for (var i = 0; i < candles.Count; i++)
        {
            signals.Add(strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc));
        }

        Assert.Equal(Signal.Long, signals[20]);    // 97 <= 100 × (1 − 1%)
        Assert.Equal(Signal.Close, signals[21]);   // 98,5 >= 97 × (1 + 1%) = 97,97
    }

    [Fact]
    public async Task DoesNotEnter_WithinTheRung()
    {
        // Un movimento che non copre il gradino non apre nulla: è il gradino a definire il ciclo.
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 20; i++) candles.Add(Candle(i, 100m));
        candles.Add(Candle(20, 99.7m));   // −0,3%, dentro un gradino dell'1%

        var strategy = await BuildAsync(candles, step: 1m, rungs: 1);

        for (var i = 0; i < candles.Count; i++)
        {
            Assert.Equal(Signal.Hold, strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc));
        }
    }

    [Fact]
    public async Task MoreRungs_RequireADeeperMove()
    {
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 20; i++) candles.Add(Candle(i, 100m));
        candles.Add(Candle(20, 98.5m));   // −1,5%: basta per 1 gradino, non per 3

        var oneRung = await BuildAsync(candles, step: 1m, rungs: 1);
        var threeRungs = await BuildAsync(candles, step: 1m, rungs: 3);

        Signal Last(GridMeanReversionStrategy s)
        {
            var signal = Signal.Hold;
            for (var i = 0; i < candles.Count; i++) signal = s.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc);
            return signal;
        }

        Assert.Equal(Signal.Long, Last(oneRung));
        Assert.Equal(Signal.Hold, Last(threeRungs));
    }

    [Fact]
    public async Task AnchorIsCausal_NeverIncludesTheCurrentBar()
    {
        // L'ancoraggio deve venire dalla barra PRECEDENTE. Una SMA che include la close corrente
        // saprebbe già dove il prezzo è andato, e la distanza da essa sarebbe in parte una
        // retroazione: la strategia sembrerebbe brava a comprare i tuffi che ha appena visto.
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 10; i++) candles.Add(Candle(i, 100m));

        var strategy = await BuildAsync(candles, anchorPeriod: 10);

        // Alla barra 9 la SMA a 10 è appena calcolabile, ma quella della barra 8 no: nessun segnale
        // può essere emesso prima che esista un ancoraggio precedente.
        Assert.Equal(Signal.Hold, strategy.EvaluateSignal(9, 90m, candles[9].TimestampUtc));
    }

    [Fact]
    public async Task ShortSide_IsSymmetric()
    {
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 20; i++) candles.Add(Candle(i, 100m));
        candles.Add(Candle(20, 103m));
        candles.Add(Candle(21, 101.5m));

        var strategy = await BuildAsync(candles, step: 1m, rungs: 1, direction: 1);

        var signals = new List<Signal>();
        for (var i = 0; i < candles.Count; i++)
        {
            signals.Add(strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc));
        }

        Assert.Equal(Signal.Short, signals[20]);
        Assert.Equal(Signal.Close, signals[21]);   // 101,5 <= 103 × (1 − 1%) = 101,97
    }

    [Fact]
    public void IsRegisteredInTheCatalog()
    {
        var factory = new StrategyFactory();
        Assert.Contains(factory.Prototypes, p => p.Name == "GridMeanReversion");
        Assert.IsType<GridMeanReversionStrategy>(factory.Create("GridMeanReversion"));
    }

    [Fact]
    public async Task RejectsInvalidParameters()
    {
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 20; i++) candles.Add(Candle(i, 100m));

        await Assert.ThrowsAsync<ArgumentException>(() => BuildAsync(candles, step: 0m));
        await Assert.ThrowsAsync<ArgumentException>(() => BuildAsync(candles, rungs: 0));
    }
}
