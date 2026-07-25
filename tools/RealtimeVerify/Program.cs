// [R1] Verifica LIVE del feed di prezzo WebSocket contro gli stream PUBBLICI reali di Binance e
// Bitget. Nessuna credenziale, nessun ordine, nessun database: apre le connessioni, stampa i tick
// e le candele chiuse che arrivano, e riporta la salute del feed.
//
// Serve a rispondere alle domande che i test unitari non possono chiudere, perché richiedono la
// rete e il dialetto REALE degli exchange:
//   - i payload veri corrispondono a quelli che i mapper si aspettano?
//   - Bitget accetta il frame di sottoscrizione e risponde al ping applicativo?
//   - i prezzi sono plausibili e coerenti fra i due exchange?
//   - la riconnessione riparte davvero se si stacca la rete a metà corsa?
//
// Uso:
//   dotnet run --project tools/RealtimeVerify                       (30s su BTC/USDT, entrambi)
//   dotnet run --project tools/RealtimeVerify -- --seconds 120      (finestra più lunga)
//   dotnet run --project tools/RealtimeVerify -- --symbol ETH/USDT
//   dotnet run --project tools/RealtimeVerify -- --exchange Binance
//
// Suggerimento per provare la riconnessione: lanciarlo con --seconds 120 e disattivare la rete
// per una decina di secondi a metà. Devono comparire un warning di caduta, un'attesa di backoff e
// la ripresa dei tick, senza che il processo muoia.
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Trading;

var seconds = ArgValue("--seconds") is string s && int.TryParse(s, out var parsed) ? parsed : 30;
var symbol = ArgValue("--symbol") ?? "BTC/USDT";
var onlyExchange = ArgValue("--exchange");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("RealtimeVerify");

var options = new FixedOptions<RealtimeFeedOptions>(new RealtimeFeedOptions
{
    Enabled = true,
    ReconnectInitialDelayMs = 1_000,
    ReconnectMaxDelayMs = 15_000,
    MaxSpreadPercent = 2m,
});

var mappers = new List<IExchangeStreamMapper> { new BinanceStreamMapper(), new BitgetStreamMapper() };
if (onlyExchange is not null)
{
    mappers = mappers
        .Where(m => m.Exchange.ToString().Equals(onlyExchange, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (mappers.Count == 0)
    {
        Console.Error.WriteLine($"Exchange '{onlyExchange}' sconosciuto. Valori validi: Binance, Bitget.");
        return 1;
    }
}

Console.WriteLine($"Ascolto {symbol} per {seconds}s su: {string.Join(", ", mappers.Select(m => m.Exchange))}");
Console.WriteLine("(solo market data pubblico: nessuna credenziale, nessun ordine)\n");

var transportFactory = new ClientWebSocketTransportFactory();
var feeds = new List<WebSocketPriceFeed>();
var tickCounts = new Dictionary<ExchangeName, int>();
var barCounts = new Dictionary<ExchangeName, int>();
var lastPrice = new Dictionary<ExchangeName, decimal>();
var gate = new Lock();

foreach (var mapper in mappers)
{
    var feed = new WebSocketPriceFeed(mapper, transportFactory, options, logger);
    var exchange = mapper.Exchange;
    tickCounts[exchange] = 0;
    barCounts[exchange] = 0;

    feed.TickReceived += tick =>
    {
        int count;
        lock (gate)
        {
            count = ++tickCounts[exchange];
            lastPrice[exchange] = tick.Mid;
        }
        // I primi tick per esteso, poi uno ogni 50: la console resta leggibile su mercati vivaci.
        if (count <= 3 || count % 50 == 0)
        {
            Console.WriteLine(
                $"  [{exchange,-7}] tick #{count,-5} {tick.Symbol}  bid {tick.Bid}  ask {tick.Ask}  " +
                $"mid {tick.Mid}  spread {tick.SpreadPercent:F4}%");
        }
    };

    feed.BarClosed += bar =>
    {
        int count;
        lock (gate) { count = ++barCounts[exchange]; }
        Console.WriteLine(
            $"  [{exchange,-7}] CANDELA CHIUSA #{count} {bar.Symbol} {bar.Timeframe} {bar.OpenTimeUtc:u}  " +
            $"O {bar.Open} H {bar.High} L {bar.Low} C {bar.Close} V {bar.Volume}");
    };

    // Timeframe 1m: su una finestra di pochi minuti è l'unico che fa vedere almeno una chiusura.
    feed.UpdateSubscriptions([new StreamSubscription(exchange, symbol, "1m", MarketType.Spot)]);
    feeds.Add(feed);
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var runs = feeds.Select(f => f.RunAsync(cts.Token)).ToList();
try { await Task.WhenAll(runs); }
catch (OperationCanceledException) { /* fine finestra */ }

Console.WriteLine("\n--- Esito ---");
var ok = true;
foreach (var feed in feeds)
{
    var health = feed.Health;
    int ticks, bars;
    lock (gate)
    {
        ticks = tickCounts[feed.Exchange];
        bars = barCounts[feed.Exchange];
    }

    var price = lastPrice.TryGetValue(feed.Exchange, out var p) ? p.ToString() : "—";
    Console.WriteLine(
        $"{feed.Exchange,-8} tick {ticks,-6} candele {bars,-3} riconnessioni {health.Reconnects,-3} " +
        $"messaggi {health.MessagesReceived,-6} ultimo prezzo {price}");

    if (ticks == 0)
    {
        ok = false;
        Console.WriteLine($"         ⚠️  NESSUN TICK da {feed.Exchange}: {health.LastError ?? "nessun errore riportato"}");
    }
}

// Se entrambi gli exchange hanno risposto, un confronto dei prezzi è il modo più veloce per
// accorgersi che un mapper sta leggendo il campo sbagliato: due venue sullo stesso asset non
// possono divergere di percentuali intere.
if (lastPrice.Count == 2)
{
    var prices = lastPrice.Values.ToList();
    var spread = Math.Abs(prices[0] - prices[1]) / Math.Max(prices[0], prices[1]) * 100m;
    Console.WriteLine($"\nScarto fra i due exchange: {spread:F3}%");
    if (spread > 1m)
    {
        ok = false;
        Console.WriteLine("⚠️  Scarto sospetto fra le due venue: probabile errore di parsing in un mapper.");
    }
}

Console.WriteLine(ok ? "\n✅ Feed verificato." : "\n❌ Verifica NON superata: vedi sopra.");
return ok ? 0 : 1;

string? ArgValue(string name)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Monitor di opzioni a valore fisso: qui non c'è configurazione da ricaricare.</summary>
file sealed class FixedOptions<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string> listener) => Noop.Instance;

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
