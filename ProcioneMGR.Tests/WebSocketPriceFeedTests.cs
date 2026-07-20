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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransportFactory(params Queue<string?>[] scripts) : IWebSocketTransportFactory
    {
        private int _created;

        public ConcurrentBag<Uri> Connections { get; } = [];
        public ConcurrentBag<string> Sent { get; } = [];
        public int Created => _created;

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
    public void UpdateSubscriptions_ReportsChangeOnlyWhenActuallyDifferent()
    {
        var feed = BuildFeed(new BinanceStreamMapper(), new FakeTransportFactory());

        Assert.True(feed.UpdateSubscriptions([BtcSpot()]));
        Assert.False(feed.UpdateSubscriptions([BtcSpot()]));                   // identiche
        Assert.False(feed.UpdateSubscriptions([BtcSpot(), BtcSpot()]));        // duplicato: irrilevante
        Assert.True(feed.UpdateSubscriptions([]));                             // svuotate: è un cambio
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
