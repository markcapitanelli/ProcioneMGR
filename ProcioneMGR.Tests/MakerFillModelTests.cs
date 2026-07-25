using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3] Test del modello di fill per gli ingressi MAKER nel backtest.
///
/// Il motivo per cui questo modello esiste: la frontiera dei costi
/// (docs/REPORT-RICERCA-2026-07.md) mostrava che a commissioni maker un candidato in perdita
/// diventava profittevole. Quel numero però assumeva che ogni ordine limite venisse riempito al
/// suo prezzo, che è l'assunzione ottimistica per eccellenza: un limite passivo si riempie solo
/// quando il mercato ci viene addosso. Senza modellare il mancato riempimento, "passare a maker"
/// sembra uno sconto sulle commissioni e basta.
///
/// I test qui sotto fissano le proprietà del modello; la misura di quanto costi davvero la
/// selezione avversa sulle strategie reali va fatta sui dati, non qui.
/// </summary>
public class MakerFillModelTests
{
    /// <summary>
    /// Il motore con le sole dipendenze che questo percorso usa: passando l'istanza di strategia
    /// all'overload a tre argomenti si salta la risoluzione per nome, quindi DB, factory e fattori
    /// alpha non vengono mai toccati.
    /// </summary>
    private static BacktestEngine Engine() =>
        new(null!, null!, new TechnicalIndicatorsService(), null!, NullLogger<BacktestEngine>.Instance);

    /// <summary>Strategia guidata da uno script: segnale scelto per indice di candela.</summary>
    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => script(index);
    }

    private static Task<BacktestResult> RunAsync(
        Func<int, Signal> script, BacktestConfiguration config, List<OhlcvData> candles) =>
        Engine().RunBacktestAsync(config, candles, new ScriptedStrategy(script), CancellationToken.None);

    /// <summary>Candela con escursione esplicita, per controllare se un limite viene toccato.</summary>
    private static OhlcvData Candle(int i, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 1000m,
    };

    private static BacktestConfiguration MakerConfig(bool fallback = false) => new()
    {
        ExchangeName = "Binance",
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        InitialCapital = 10_000m,
        PositionSizePercent = 100m,
        FeePercent = 0.1m,          // taker
        MakerFeePercent = 0.02m,    // maker
        EntryExecution = EntryExecutionStyle.Maker,
        MakerOffsetPercent = 1.0m,  // limite l'1% sotto la close del segnale
        MakerMaxWaitBars = 2,
        MakerFallbackToTaker = fallback,
    };

    [Fact]
    public async Task PriceNeverComesBack_LimitIsNotFilled_AndTheSignalIsLost()
    {
        // Segnale long alla candela 2 con close 100 -> limite a 99. Il prezzo da lì sale e non
        // torna più: nessun fill. È il caso che rende ottimistica l'ipotesi "maker = sconto".
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),      // segnale: limite a 99
            Candle(3, 101, 102, 100.5m, 102),
            Candle(4, 102, 103, 101.5m, 103),
            Candle(5, 103, 104, 102.5m, 104),
            Candle(6, 104, 105, 103.5m, 105),
        };

        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, MakerConfig(), candles);

        Assert.Equal(1, result.MakerEntriesAttempted);
        Assert.Equal(0, result.MakerEntriesFilled);
        Assert.Equal(1, result.MakerEntriesMissed);
        Assert.Equal(0, result.TotalTrades);
        Assert.Equal(0m, result.MakerFillRate);
    }

    [Fact]
    public async Task PriceDipsToTheLimit_FillsAtTheLimitPrice_WithMakerFee()
    {
        // Stesso segnale, ma la candela successiva scende a toccare 99: fill ESATTAMENTE a 99,
        // senza slippage, e con commissione maker invece che taker.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),        // segnale: limite a 99
            Candle(3, 100, 100, 98.5m, 99.5m),    // il minimo scende sotto 99 -> riempito
            Candle(4, 100, 100, 100, 100),
            Candle(5, 100, 100, 100, 100),
        };

        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, MakerConfig(), candles);

        Assert.Equal(1, result.MakerEntriesAttempted);
        Assert.Equal(1, result.MakerEntriesFilled);
        Assert.Equal(0, result.MakerEntriesMissed);
        Assert.Equal(100m, result.MakerFillRate);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(99m, trade.EntryPrice);   // il prezzo del limite, non la close del segnale
    }

    [Fact]
    public async Task QueuePenetration_WickThatOnlyKissesTheLimit_DoesNotFill()
    {
        // [F-queue] Segnale long alla candela 2, close 100 -> limite a 99. La candela 3 SFIORA 99
        // (Low = 99 esatto) e rimbalza. Con penetrazione richiesta 0,1%, un touch esatto NON basta:
        // servirebbe Low <= 99·(1−0,001) = 98,901. È il proxy della posizione in coda — il prezzo
        // bacia il tuo livello ma passa senza riempirti.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),        // segnale: limite a 99
            Candle(3, 100, 100, 99m, 99.8m),      // Low = 99 esatto: SOLO sfiorato
            Candle(4, 100, 100, 100, 100),
        };

        var cfg = MakerConfig();
        cfg.MakerQueuePenetrationPercent = 0.1m;   // richiede penetrazione 0,1% oltre il limite
        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, cfg, candles);

        Assert.Equal(1, result.MakerEntriesAttempted);
        Assert.Equal(0, result.MakerEntriesFilled);   // sfiorato ≠ riempito
        Assert.Equal(0, result.TotalTrades);
    }

    [Fact]
    public async Task QueuePenetration_DecisiveMoveThroughTheLimit_Fills()
    {
        // Stessa penetrazione richiesta, ma la candela 3 ATTRAVERSA deciso (Low = 98,5 < 98,901):
        // il prezzo è passato oltre il tuo livello con margine -> riempito, ancora al prezzo del limite.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),        // segnale: limite a 99
            Candle(3, 100, 100, 98.5m, 99.2m),    // Low = 98,5: attraversato oltre la soglia di coda
            Candle(4, 100, 100, 100, 100),
        };

        var cfg = MakerConfig();
        cfg.MakerQueuePenetrationPercent = 0.1m;
        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, cfg, candles);

        Assert.Equal(1, result.MakerEntriesFilled);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(99m, trade.EntryPrice);   // fill al prezzo del LIMITE (sei maker), non al Low
    }

    [Fact]
    public async Task QueuePenetration_Zero_IsBitIdenticalToTouchFill()
    {
        // Con penetrazione 0 il comportamento è quello storico (touch = fill): il touch esatto riempie.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),
            Candle(3, 100, 100, 99m, 99.8m),      // Low = 99 esatto
            Candle(4, 100, 100, 100, 100),
        };

        var cfg = MakerConfig();   // MakerQueuePenetrationPercent = 0 di default
        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, cfg, candles);

        Assert.Equal(1, result.MakerEntriesFilled);   // touch=fill storico
    }

    [Fact]
    public async Task LimitExpiresUnfilled_WithFallback_EntersAtMarketInstead()
    {
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),      // segnale: limite a 99, scade dopo 2 candele
            Candle(3, 101, 102, 100.5m, 102),
            Candle(4, 102, 103, 101.5m, 103),   // qui scade -> fallback a mercato
            Candle(5, 103, 104, 102.5m, 104),
            Candle(6, 104, 105, 103.5m, 105),
        };

        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, MakerConfig(fallback: true), candles);

        Assert.Equal(1, result.MakerEntriesAttempted);
        Assert.Equal(0, result.MakerEntriesFilled);
        Assert.Equal(1, result.MakerEntriesFallbackTaker);
        Assert.Equal(0, result.MakerEntriesMissed);

        // Il fallback entra alla close della candela di scadenza, cioè a un prezzo PEGGIORE di
        // quello che si sperava: è esattamente il costo della selezione avversa, reso esplicito.
        var trade = Assert.Single(result.Trades);
        Assert.Equal(103m, trade.EntryPrice);
    }

    [Fact]
    public async Task PersistentSignal_PlacesOneLimitPerOpportunity_NotOnePerCandle()
    {
        // Un segnale che resta acceso non deve ripiazzare il limite a ogni candela: altrimenti il
        // tasso di riempimento misurerebbe quante volte si riprova, non la probabilità di fill.
        var candles = Enumerable.Range(0, 12)
            .Select(i => Candle(i, 100, 100.2m, 99.8m, 100m)).ToList();

        var result = await RunAsync(i => i >= 2 ? Signal.Long : Signal.Hold, MakerConfig(), candles);

        Assert.Equal(1, result.MakerEntriesAttempted);
    }

    [Fact]
    public async Task TakerIsTheDefault_AndLeavesBehaviourUnchanged()
    {
        // Rete di sicurezza sul default: senza toccare la configurazione, il motore deve entrare
        // a mercato alla close del segnale come ha sempre fatto, e nessun contatore maker si muove.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100, 100, 100, 100),
            Candle(1, 100, 100, 100, 100),
            Candle(2, 100, 100, 100, 100),
            Candle(3, 101, 102, 100.5m, 102),
            Candle(4, 102, 103, 101.5m, 103),
        };

        var config = MakerConfig();
        config.EntryExecution = EntryExecutionStyle.Taker;

        var result = await RunAsync(i => i == 2 ? Signal.Long : Signal.Hold, config, candles);

        Assert.Equal(0, result.MakerEntriesAttempted);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(100m, trade.EntryPrice);   // close della candela del segnale
    }
}
