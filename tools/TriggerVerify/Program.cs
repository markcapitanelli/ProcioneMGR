// [P0-5] Verifica LIVE ISOLATA dell'ordine TRIGGER reduce-only "resting"
// (PlaceFuturesTriggerOrderAsync) contro Bitget Demo / Binance Testnet, usando le credenziali
// testnet già salvate in ExchangeSettings.
//
// SICUREZZA: si opera SOLO su credenziali IsTestnet. Il tool PIAZZA ordini reali sull'ambiente
// DEMO/TESTNET (mai fondi veri), ma SOLO se il conto ha margine disponibile: con saldo 0 salta
// del tutto la parte di piazzamento e riporta il blocco. Il trigger viene messo LONTANO dal
// prezzo (per un long, ben sotto il mark) così da restare "resting" e non attivarsi; viene poi
// cancellato e la posizione richiusa. Nessun ordine resta appeso a fine run.
//
// Passa "--place" per abilitare davvero il ciclo apri→trigger→cancella→chiudi; senza flag il tool
// esegue solo i controlli read-only (dry-run), utile come check di sicurezza.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;

var place = args.Contains("--place");
var appDir = @"C:\Users\proci\Desktop\ProgettoP\ProcioneMGR";

var configuration = new ConfigurationBuilder()
    .SetBasePath(appDir)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(
    configuration.GetConnectionString("PostgresConnection")
    ?? throw new InvalidOperationException("PostgresConnection non configurata in appsettings.json.")));
services.AddHttpClient<BinanceClient>(c =>
{
    c.BaseAddress = new Uri("https://api.binance.com");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
});
services.AddHttpClient<BitgetClient>(c =>
{
    c.BaseAddress = new Uri("https://api.bitget.com");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
});
services.AddSingleton<IExchangeClientFactory, ExchangeClientFactory>();
await using var provider = services.BuildServiceProvider();

var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var exchangeFactory = provider.GetRequiredService<IExchangeClientFactory>();

await using var db = await dbFactory.CreateDbContextAsync();
var creds = await db.ExchangeCredentials.Where(c => c.IsTestnet).ToListAsync();

Console.WriteLine($"TriggerVerify — modalità: {(place ? "PLACE (piazza ordini demo)" : "dry-run (solo controlli)")}.");
Console.WriteLine($"Trovate {creds.Count} credenziali testnet/demo nel DB.");

const string symbol = "BTC/USDT";

foreach (var c in creds)
{
    Console.WriteLine();
    Console.WriteLine($"=== {c.ExchangeName} ('{c.Label}', Id={c.Id}) ===");
    var tc = new TradingCredentials(c.ApiKey, c.ApiSecret, c.Passphrase, c.IsTestnet);
    var futures = exchangeFactory.CreateFutures(c.ExchangeName);
    var spot = exchangeFactory.Create(c.ExchangeName);

    // Prezzo di riferimento (ultima candela 1m pubblica) per dimensionare la size e il trigger.
    decimal price;
    try
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        var candles = await spot.FetchOhlcvAsync(symbol, "1m", since, 10);
        price = candles.Count > 0 ? candles[^1].Close : 0m;
        Console.WriteLine($"[OK] Prezzo di riferimento {symbol}: {price}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] Prezzo di riferimento: {ex.GetType().Name}: {ex.Message}"); continue; }
    if (price <= 0m) { Console.WriteLine("[SKIP] Prezzo non disponibile."); continue; }

    var filters = await futures.GetFuturesSymbolFiltersAsync(symbol, c.IsTestnet);
    var balance = await futures.GetFuturesBalanceAsync(tc);
    Console.WriteLine($"[INFO] Saldo demo: available={balance.AvailableMargin} equity={balance.TotalEquity}; minNotional={filters.MinNotional} step={filters.StepSize} minQty={filters.MinQty}");

    // Size minima che rispetta minNotional e minQty.
    var qty = filters.MinNotional > 0m ? Math.Ceiling(filters.MinNotional / price / Math.Max(filters.StepSize, 0.0001m)) * Math.Max(filters.StepSize, 0.0001m) : filters.MinQty;
    if (qty < filters.MinQty) qty = filters.MinQty;
    var marginNeeded = qty * price / 2m; // leva 2x

    if (!place)
    {
        Console.WriteLine($"[DRY-RUN] Salterei il piazzamento. Size che userei: {qty} (~{qty * price:F2} USDT nozionale, ~{marginNeeded:F2} margine a 2x). Rilancia con --place per eseguire.");
        continue;
    }

    if (balance.AvailableMargin < marginNeeded)
    {
        Console.WriteLine($"[BLOCCATO] Margine disponibile ({balance.AvailableMargin}) < necessario (~{marginNeeded:F2}). "
            + "Rivendica i fondi virtuali sul Demo Trading (Bitget) o abilita i permessi Futures sulla key (Binance testnet), poi rilancia. NESSUN ordine piazzato.");
        continue;
    }

    // --- Ciclo reale su DEMO: apri → trigger resting → conferma → cancella → chiudi ---
    try
    {
        await futures.SetLeverageAsync(symbol, 2, tc);

        var openId = Guid.NewGuid().ToString("N");
        var open = await futures.PlaceFuturesOrderAsync(new PlaceOrderRequest
        {
            Symbol = symbol, Side = "BUY", Type = "MARKET", Quantity = qty, ClientOrderId = openId, Credentials = tc,
        }, reduceOnly: false);
        Console.WriteLine(open.Success ? $"[OK] Posizione LONG aperta qty={qty} (fill={open.FilledPrice})." : $"[FAIL] Apertura: {open.Error}");
        if (!open.Success) continue;

        await Task.Delay(1500);

        // Stop reduce-only MOLTO sotto il mark (30%): resta resting, non si attiva.
        var stopTrigger = filters.RoundPrice(price * 0.70m);
        var trigId = Guid.NewGuid().ToString("N");
        var trig = await futures.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
        {
            Symbol = symbol, Side = "SELL", Quantity = qty, TriggerPrice = stopTrigger, ClientOrderId = trigId, Credentials = tc,
        }, isStopLoss: true);
        Console.WriteLine(trig.Success
            ? $"[OK] ✅ TRIGGER reduce-only piazzato a {stopTrigger} (id={trig.ExchangeOrderId}). QUESTO è l'endpoint P0-5."
            : $"[FAIL] ❌ Trigger: {trig.Error}");

        await Task.Delay(1500);
        var openOrders = await futures.GetOpenFuturesOrdersAsync(symbol, tc);
        Console.WriteLine($"[INFO] Ordini aperti sull'exchange dopo il trigger: {openOrders.Count}.");

        // Cleanup: cancella il trigger (se piazzato) e chiudi la posizione.
        if (trig.Success)
        {
            var cancel = await futures.CancelFuturesOrderAsync(symbol, trigId, tc);
            Console.WriteLine(cancel.Success ? "[OK] Trigger cancellato." : $"[WARN] Cancellazione trigger: {cancel.Error}");
        }
        var closeId = Guid.NewGuid().ToString("N");
        var close = await futures.PlaceFuturesOrderAsync(new PlaceOrderRequest
        {
            Symbol = symbol, Side = "SELL", Type = "MARKET", Quantity = qty, ClientOrderId = closeId, Credentials = tc,
        }, reduceOnly: true);
        Console.WriteLine(close.Success ? "[OK] Posizione richiusa (reduce-only)." : $"[WARN] Chiusura: {close.Error} — CONTROLLA MANUALMENTE che non resti aperta.");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] Ciclo ordini: {ex.GetType().Name}: {ex.Message}"); }
}

Console.WriteLine();
Console.WriteLine(place ? "TriggerVerify completato." : "TriggerVerify (dry-run) completato — nessun ordine piazzato.");
