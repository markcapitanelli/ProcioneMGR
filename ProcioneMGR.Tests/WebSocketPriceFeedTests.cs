using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R1] Test del ciclo di vita della connessione real-time, con un transport finto al posto della
/// rete: connessione, sottoscrizione, riconnessione dopo una caduta, tolleranza ai frame inutili,
/// filtro sulle quotazioni implausibili e rilevamento di staleness.
///
/// Il comportamento più importante è che una CADUTA È NORMALE: la rete cade, e un feed che non
/// riprende da solo lascia gli stop ciechi senza che nessuno se ne accorga.
/// </summary>
public class WebSocketPriceFeedTests
{
    /// <summary>Transport pilotato dal test: consegna i messaggi di un copione e simula chiusure.</summary>
    private sealed class FakeTransport(Queue<string?> script, FakeTransportFactory owner) : IWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            owner.Connections.Add(uri);
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken ct)
        {
            owner.Sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            if (script.Count == 0)
            {
                // Copione esaurito: si resta in attesa finché il test non cancella, così il feed
                // non gira a vuoto riconnettendo all'infinito.
                await Task.Delay(Timeout.Infinite, ct);
            }
            var next = script.Dequeue();
            return next; // null = canale chiuso
        }

        public ValueTask DisposeAsync()
        {
            owner.NoteDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransportFactory(params Queue<string?>[] scripts) : IWebSocketTransportFactory
    {
        private int _created;
        private int _disposed;

        public ConcurrentBag<Uri> Connections { get; } = [];
        public ConcurrentBag<string> Sent { get; } = [];
        public int Created => _created;

        /// <summary>Transport chiusi: è l'osservabile con cui i test di riciclo vedono la DISCONNESSIONE.</summary>
        public int Disposed => _disposed;

        internal void NoteDisposed() => Interlocked.Increment(ref _disposed);

        public IWebSocketTransport Create()
        {
            var index = Interlocked.Increment(ref _created) - 1;
            var script = index < scripts.Length ? scripts[index] : new Queue<string?>();
            return new FakeTransport(script, this);
        }
    }

    private static Queue<string?> Script(params string?[] messages) => new(messages);

    private static WebSocketPriceFeed BuildFeed(
        IExchangeStreamMapper mapper, FakeTransportFactory factory, RealtimeFeedOptions? options = null) =>
        new(mapper, factory,
            (options ?? new RealtimeFeedOptions
            {
                Enabled = true,
                ReconnectInitialDelayMs = 1,
                ReconnectMaxDelayMs = 5,
            }).AsMonitor(),
            NullLogger.Instance);

    private static StreamSubscription BtcSpot(ExchangeName exchange = ExchangeName.Binance) =>
        new(exchange, "BTC/USDT", "5m", MarketType.Spot);

    private const string BookTicker = """
        {"stream":"btcusdt@bookTicker","data":{"s":"BTCUSDT","b":"100.0","B":"1","a":"100.2","A":"1"}}
        """;

    /// <summary>Attende che una condizione diventi vera, o fallisce: niente sleep a tempo fisso.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timeout in attesa di: {what}");
    }

    [Fact]
    public async Task Feed_EmitsTicks_FromReceivedFrames()
    {
        var factory = new FakeTransportFactory(Script(BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot()]);

        var ticks = new ConcurrentBag<PriceTick>();
        feed.TickReceived += ticks.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => !ticks.IsEmpty, "un tick emesso");
        await cts.CancelAsync();
        await run;

        var tick = Assert.Single(ticks);
        Assert.Equal("BTC/USDT", tick.Symbol);
        Assert.Equal(100.1m, tick.Mid);
    }

    [Fact]
    public async Task SeriesHealth_TracksPerSymbol_ASilentSymbolIsVisible()
    {
        // [G2] Il cuore del fix: BTC consegna, ETH tace. La salute del FEED è verde (un messaggio
        // c'è), ma la watchdog deve poter vedere che ETH non ha MAI consegnato — è esattamente il
        // silenzio che la versione per-feed mascherava.
        var factory = new FakeTransportFactory(Script(BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot(), new StreamSubscription(ExchangeName.Binance, "ETH/USDT", "5m", MarketType.Spot)]);

        var ticks = new ConcurrentBag<PriceTick>();
        feed.TickReceived += ticks.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);
        await WaitForAsync(() => !ticks.IsEmpty, "il tick BTC");
        await cts.CancelAsync();
        await run;

        var snapshot = feed.SeriesHealthSnapshot;
        Assert.Equal(2, snapshot.Count);
        var btc = Assert.Single(snapshot, s => s.Symbol == "BTC/USDT");
        var eth = Assert.Single(snapshot, s => s.Symbol == "ETH/USDT");
        Assert.NotNull(btc.LastEventUtc);       // ha consegnato
        Assert.Null(eth.LastEventUtc);          // mai un evento: il suo silenzio è VISIBILE
        Assert.True(eth.SubscribedSinceUtc <= DateTime.UtcNow, "la grazia della serie parte dalla sottoscrizione");
    }

    [Fact]
    public void SeriesHealth_ForgetsUnsubscribedSymbols()
    {
        // [G2] Una serie tolta dal set non va sorvegliata: lasciarla nel tracciamento produrrebbe
        // allarmi su simboli che nessuna corsia opera più.
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory(Script()));
        feed.UpdateSubscriptions([BtcSpot(), new StreamSubscription(ExchangeName.Binance, "ETH/USDT", "5m", MarketType.Spot)]);
        Assert.Equal(2, feed.SeriesHealthSnapshot.Count);

        feed.UpdateSubscriptions([BtcSpot()]);
        var remaining = Assert.Single(feed.SeriesHealthSnapshot);
        Assert.Equal("BTC/USDT", remaining.Symbol);
    }

    [Fact]
    public async Task Feed_Reconnects_AfterChannelDrop()
    {
        // Primo canale: un tick, poi cade (null). Secondo canale: un altro tick.
        var factory = new FakeTransportFactory(
            Script(BookTicker, null),
            Script(BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot()]);

        var ticks = 0;
        feed.TickReceived += _ => Interlocked.Increment(ref ticks);

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => Volatile.Read(ref ticks) >= 2, "due tick, uno per connessione");
        await cts.CancelAsync();
        await run;

        Assert.True(factory.Created >= 2, "il feed deve aver creato un nuovo transport dopo la caduta");
        Assert.True(feed.Health.Reconnects >= 1);
    }

    [Fact]
    public async Task Feed_SendsSubscribeFrames_WhenExchangeRequiresThem()
    {
        // Bitget negozia le sottoscrizioni via frame; Binance le codifica nell'URL.
        var factory = new FakeTransportFactory(Script());
        var feed = BuildFeed(new BitgetStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot(ExchangeName.Bitget)]);

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => factory.Sent.Any(s => s.Contains("subscribe", StringComparison.Ordinal)),
            "il frame di sottoscrizione inviato");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Feed_DropsImplausibleTicks()
    {
        // Book incrociato (ask < bid): quotazione rotta, non ci si decide un'uscita.
        const string crossed = """
            {"stream":"btcusdt@bookTicker","data":{"s":"BTCUSDT","b":"105.0","B":"1","a":"100.0","A":"1"}}
            """;
        var factory = new FakeTransportFactory(Script(crossed, BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot()]);

        var ticks = new ConcurrentBag<PriceTick>();
        feed.TickReceived += ticks.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => !ticks.IsEmpty, "il tick valido emesso");
        await cts.CancelAsync();
        await run;

        // Solo quello sano: il book incrociato è stato scartato.
        Assert.Equal(100.1m, Assert.Single(ticks).Mid);
    }

    [Fact]
    public async Task Feed_SurvivesThrowingHandler()
    {
        // Il feed è infrastruttura: la sua sopravvivenza non può dipendere dalla correttezza dei
        // consumatori. Un gestore che lancia non deve abbattere la connessione.
        var factory = new FakeTransportFactory(Script(BookTicker, BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot()]);

        var seen = 0;
        feed.TickReceived += _ =>
        {
            Interlocked.Increment(ref seen);
            throw new InvalidOperationException("consumatore difettoso");
        };

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => Volatile.Read(ref seen) >= 2, "entrambi i tick consegnati nonostante l'eccezione");
        await cts.CancelAsync();
        await run;

        // Nessuna riconnessione: l'eccezione del gestore non ha rotto il canale.
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Feed_RecyclesLiveConnection_WhenSubscriptionsChange_Binance()
    {
        // [C1] Binance codifica gli stream nell'URL: aggiungere una corsia a connessione viva DEVE
        // riciclare il socket, altrimenti i tick del nuovo simbolo non arrivano mai — mentre il
        // log rassicura e la watchdog resta verde sui messaggi del simbolo vecchio.
        var factory = new FakeTransportFactory(Script(BookTicker), Script());
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot()]);

        var ticks = new ConcurrentBag<PriceTick>();
        feed.TickReceived += ticks.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        // Prima connessione VIVA: ha consegnato un tick e ora pende sulla ReceiveAsync.
        await WaitForAsync(() => !ticks.IsEmpty, "un tick dalla prima connessione");
        Assert.Single(factory.Connections);

        feed.UpdateSubscriptions([BtcSpot(), new StreamSubscription(ExchangeName.Binance, "ETH/USDT", "5m", MarketType.Spot)]);

        await WaitForAsync(() => factory.Connections.Count == 2, "la riconnessione col set aggiornato");
        await cts.CancelAsync();
        await run;

        // Il vecchio transport è stato chiuso e il nuovo endpoint porta ANCHE il simbolo aggiunto.
        Assert.True(factory.Disposed >= 1, "il transport della prima connessione deve risultare chiuso");
        Assert.Contains(factory.Connections, u => u.AbsoluteUri.Contains("ethusdt", StringComparison.Ordinal));

        // Il riciclo non è un guasto: non deve sporcare il contatore delle riconnessioni da errore.
        Assert.Equal(0, feed.Health.Reconnects);
    }

    [Fact]
    public async Task Feed_RecyclesLiveConnection_WhenSubscriptionsChange_Bitget()
    {
        // [C1] Bitget negozia le sottoscrizioni via frame, ma SOLO al connect: anche qui il cambio
        // richiede il riciclo, e la nuova connessione deve presentare i frame col simbolo nuovo.
        var factory = new FakeTransportFactory(Script(), Script());
        var feed = BuildFeed(new BitgetStreamMapper(), factory);
        feed.UpdateSubscriptions([BtcSpot(ExchangeName.Bitget)]);

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);

        await WaitForAsync(() => factory.Sent.Any(s => s.Contains("BTCUSDT", StringComparison.Ordinal)),
            "il frame di sottoscrizione della prima connessione");

        feed.UpdateSubscriptions([
            BtcSpot(ExchangeName.Bitget),
            new StreamSubscription(ExchangeName.Bitget, "ETH/USDT", "5m", MarketType.Spot),
        ]);

        await WaitForAsync(() => factory.Sent.Any(s => s.Contains("ETHUSDT", StringComparison.Ordinal)),
            "il frame di sottoscrizione col simbolo nuovo dopo il riciclo");
        await cts.CancelAsync();
        await run;

        Assert.True(factory.Created >= 2, "il cambio di sottoscrizioni deve aver riciclato il transport");
        Assert.True(factory.Disposed >= 1, "il transport della prima connessione deve risultare chiuso");
        Assert.Equal(0, feed.Health.Reconnects);
    }

    [Fact]
    public void UpdateSubscriptions_ReportsChangeOnlyWhenActuallyDifferent()
    {
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory());

        Assert.True(feed.UpdateSubscriptions([BtcSpot()]));
        Assert.False(feed.UpdateSubscriptions([BtcSpot()]));                   // identiche
        Assert.False(feed.UpdateSubscriptions([BtcSpot(), BtcSpot()]));        // duplicato: irrilevante
        Assert.True(feed.UpdateSubscriptions([]));                             // svuotate: è un cambio
    }

    [Fact]
    public void UpdateSubscriptions_ToleratesTwoLanesOnTheSameSymbol()
    {
        // Riproduzione dell'incidente del 2026-08-09 (pod procionemgr-trading): due corsie su
        // DOT/USDT facevano lanciare "ArgumentException: An item with the same key has already
        // been added. Key: DOTUSDT" dentro UpdateSubscriptions — l'indice del parsing ha per
        // chiave il solo simbolo, e due sottoscrizioni distinte (timeframe diversi) collidono.
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory());

        Assert.True(feed.UpdateSubscriptions([
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "15m", MarketType.Spot),
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "1h", MarketType.Spot),
        ]));

        // La watchdog vede UNA serie (la salute è per-simbolo, non per-timeframe)…
        Assert.Equal("DOT/USDT", Assert.Single(feed.SeriesHealthSnapshot).Symbol);

        // …e ripresentare lo stesso set NON è un cambio: il refresh converge invece di riciclare
        // (o, prima del fix, di fallire) a ogni giro.
        Assert.False(feed.UpdateSubscriptions([
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "15m", MarketType.Spot),
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "1h", MarketType.Spot),
        ]));
    }

    [Fact]
    public async Task Feed_ServesBothTimeframes_WhenTwoLanesShareTheSymbol_Binance()
    {
        // Il seguito dell'incidente: non basta non lanciare, entrambe le corsie devono essere
        // SERVITE. L'endpoint deve portare un bookTicker solo e TUTTI i kline; la candela va
        // etichettata col timeframe dichiarato dallo stream, non con quello della sottoscrizione
        // rappresentante rimasta nell'indice.
        const string dotTicker = """
            {"stream":"dotusdt@bookTicker","data":{"s":"DOTUSDT","b":"4.00","B":"1","a":"4.02","A":"1"}}
            """;
        const string dotKline1h = """
            {"stream":"dotusdt@kline_1h","data":{"s":"DOTUSDT","k":{"x":true,"t":1754697600000,"i":"1h","o":"4.00","h":"4.10","l":"3.90","c":"4.05","v":"1000"}}}
            """;
        var factory = new FakeTransportFactory(Script(dotTicker, dotKline1h));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);
        feed.UpdateSubscriptions([
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "15m", MarketType.Spot),
            new StreamSubscription(ExchangeName.Binance, "DOT/USDT", "1h", MarketType.Spot),
        ]);

        var ticks = new ConcurrentBag<PriceTick>();
        var bars = new ConcurrentBag<BarClosed>();
        feed.TickReceived += ticks.Add;
        feed.BarClosed += bars.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);
        await WaitForAsync(() => !ticks.IsEmpty && !bars.IsEmpty, "tick e candela sul simbolo condiviso");
        await cts.CancelAsync();
        await run;

        var uri = Assert.Single(factory.Connections).AbsoluteUri;
        Assert.Contains("dotusdt@bookTicker", uri, StringComparison.Ordinal);
        Assert.Contains("dotusdt@kline_15m", uri, StringComparison.Ordinal);
        Assert.Contains("dotusdt@kline_1h", uri, StringComparison.Ordinal);

        Assert.Equal("DOT/USDT", Assert.Single(ticks).Symbol);
        var bar = Assert.Single(bars);
        Assert.Equal("1h", bar.Timeframe); // il timeframe lo dice lo stream ("i"), non l'indice
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1754697600000).UtcDateTime, bar.OpenTimeUtc);
    }

    [Fact]
    public async Task Feed_ToleratesSpotAndFuturesLanes_OnTheSameSymbol_Bitget()
    {
        // Variante Bitget della stessa collisione: Spot e Futures condividono la connessione
        // pubblica, quindi due corsie sulla stessa coppia in mercati diversi sono un caso normale
        // della flotta — e la chiave dell'indice ("DOTUSDT") è identica per entrambe.
        const string dotTicker = """
            {"action":"snapshot","arg":{"instType":"SPOT","channel":"ticker","instId":"DOTUSDT"},"data":[{"instId":"DOTUSDT","bidPr":"4.00","askPr":"4.02","ts":"1754697600000"}]}
            """;
        var factory = new FakeTransportFactory(Script(dotTicker));
        var feed = BuildFeed(new BitgetStreamMapper(), factory);
        feed.UpdateSubscriptions([
            new StreamSubscription(ExchangeName.Bitget, "DOT/USDT", "15m", MarketType.Spot),
            new StreamSubscription(ExchangeName.Bitget, "DOT/USDT", "15m", MarketType.Futures),
        ]);

        var ticks = new ConcurrentBag<PriceTick>();
        feed.TickReceived += ticks.Add;

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);
        await WaitForAsync(() => !ticks.IsEmpty, "il tick sul simbolo condiviso");
        await cts.CancelAsync();
        await run;

        // Il frame di sottoscrizione porta ENTRAMBI i mercati: nessuna corsia perde il suo stream.
        var frame = Assert.Single(factory.Sent, s => s.Contains("subscribe", StringComparison.Ordinal));
        Assert.Contains("\"SPOT\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"USDT-FUTURES\"", frame, StringComparison.Ordinal);
        Assert.Equal("DOT/USDT", Assert.Single(ticks).Symbol);
    }

    [Fact]
    public void UpdateSubscriptions_IgnoresOtherExchanges()
    {
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory());

        Assert.False(feed.UpdateSubscriptions([BtcSpot(ExchangeName.Bitget)]));
    }

    [Fact]
    public void Health_IsStale_WhenSilentBeyondThreshold()
    {
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory());
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var never = feed.Health;
        Assert.True(never.IsStale(TimeSpan.FromSeconds(60), now)); // mai ricevuto nulla = stale

        var fresh = never with { LastMessageUtc = now.AddSeconds(-10) };
        Assert.False(fresh.IsStale(TimeSpan.FromSeconds(60), now));

        var old = never with { LastMessageUtc = now.AddSeconds(-90) };
        Assert.True(old.IsStale(TimeSpan.FromSeconds(60), now));
    }

    [Fact]
    public async Task Feed_WithNoSubscriptions_NeverConnects()
    {
        // Nessuna corsia attiva: tenere aperta una connessione sarebbe spreco (e rumore verso
        // l'exchange) senza alcun beneficio.
        var factory = new FakeTransportFactory(Script(BookTicker));
        var feed = BuildFeed(new BinanceStreamMapper(), factory);

        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await run;

        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public void BackoffDelay_GrowsAndStaysWithinCap()
    {
        var options = new RealtimeFeedOptions { ReconnectInitialDelayMs = 100, ReconnectMaxDelayMs = 5_000 };
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory(), options);

        // Jitter: si verificano i CONFINI, non un valore esatto.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var delay = feed.BackoffDelay(attempt);
            Assert.InRange(delay.TotalMilliseconds, options.ReconnectInitialDelayMs, options.ReconnectMaxDelayMs);
        }

        // Un tentativo alto è mediamente più lungo di uno basso, nonostante il jitter.
        var early = Enumerable.Range(0, 40).Average(_ => feed.BackoffDelay(1).TotalMilliseconds);
        var late = Enumerable.Range(0, 40).Average(_ => feed.BackoffDelay(10).TotalMilliseconds);
        Assert.True(late > early, $"il backoff deve crescere: {early} -> {late}");
    }
}
