// Verifica LIVE dell'integrazione SPOT Bitget (e Binance per confronto), usando le credenziali
// reali già salvate in ExchangeSettings (decifrate con la stessa AesGcmEncryptionService/master
// key dell'app). Nata per l'item dell'audit 2026-07 sul MARKET-BUY spot Bitget: la doc v2
// documenta "size" come CONTROVALORE QUOTE (USDT) per i market-buy, ma il client manda la
// quantità BASE — finché non è verificato dal vivo, il guard Trading:Bitget:SpotMarketBuyVerified
// tiene i market-buy spot Bitget BLOCCATI nell'app.
//
// Di default esegue SOLO passi in lettura (filtri, saldo, lookup di un ordine inesistente).
// Con il flag ESPLICITO --place-min-order piazza su Bitget un MARKET-BUY di size=5 (≈5 USDT se
// la semantica è quote), ne legge l'esito via orderInfo (baseVolume/priceAvg dicono QUALE
// semantica ha applicato l'exchange) e rivende subito il ricevuto (sell-back, semantica base
// certa). ⚠️ Su credenziali NON demo usa ~5 USDT di denaro reale: lanciarlo consapevolmente.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;

var placeMinOrder = args.Contains("--place-min-order", StringComparer.OrdinalIgnoreCase);

var appDir = @"C:\Users\proci\Desktop\ProgettoP\ProcioneMGR";

var configuration = new ConfigurationBuilder()
    .SetBasePath(appDir)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    // Questo tool È il verificatore: bypassa il guard dell'app SOLO in questo processo.
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Trading:Bitget:SpotMarketBuyVerified"] = "true" })
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
var creds = await db.ExchangeCredentials.ToListAsync();

Console.WriteLine($"Trovate {creds.Count} credenziali nel DB. Modalità: {(placeMinOrder ? "PLACE-MIN-ORDER (⚠️ ordini reali!)" : "sola lettura")}");

const string symbol = "BTC/USDT";

foreach (var c in creds)
{
    Console.WriteLine();
    Console.WriteLine($"=== {c.ExchangeName} SPOT ('{c.Label}', Id={c.Id}, testnet={c.IsTestnet}) ===");
    var tc = new TradingCredentials(c.ApiKey, c.ApiSecret, c.Passphrase, c.IsTestnet);
    var client = exchangeFactory.Create(c.ExchangeName);

    // --- Passi in sola lettura -----------------------------------------------------------------

    try
    {
        var filters = await client.GetSymbolFiltersAsync(symbol, c.IsTestnet);
        Console.WriteLine($"[OK] GetSymbolFiltersAsync({symbol}): step={filters.StepSize} minQty={filters.MinQty} minNotional={filters.MinNotional}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetSymbolFiltersAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var balance = await client.GetBalanceAsync(tc);
        var top = balance.Free.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}={kv.Value}");
        Console.WriteLine($"[OK] GetBalanceAsync: {string.Join(", ", top)}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetBalanceAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        // Lookup di un clientOid che NON esiste: deve tornare NOT-FOUND certo (Found=false,
        // NetworkUncertain=false) — valida la mappatura degli errori usata dalla riconciliazione C2.
        var status = await client.GetOrderStatusAsync(symbol, $"spotverify-{Guid.NewGuid():N}", tc);
        Console.WriteLine(status is { Found: false, NetworkUncertain: false }
            ? "[OK] GetOrderStatusAsync(inesistente): NOT-FOUND certo (mappatura errori corretta)."
            : $"[WARN] GetOrderStatusAsync(inesistente): Found={status.Found} NetworkUncertain={status.NetworkUncertain} Error={status.Error}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetOrderStatusAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var open = await client.GetOpenOrdersAsync(symbol, tc);
        Console.WriteLine($"[OK] GetOpenOrdersAsync: {open.Count} ordini aperti.");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetOpenOrdersAsync: {ex.GetType().Name}: {ex.Message}"); }

    // --- Verifica semantica MARKET-BUY (solo Bitget, solo con flag esplicito) -------------------

    if (!placeMinOrder || c.ExchangeName != ExchangeName.Bitget)
    {
        continue;
    }

    Console.WriteLine();
    Console.WriteLine("⚠️  PLACE-MIN-ORDER su Bitget SPOT: market-buy size=5, poi orderInfo, poi sell-back.");
    var buyClientId = $"spotverify-buy-{Guid.NewGuid():N}"[..32];
    try
    {
        var buy = await client.PlaceOrderAsync(new PlaceOrderRequest
        {
            Symbol = symbol, Side = "BUY", Type = "MARKET",
            Quantity = 5m,   // se la semantica è QUOTE: ~5 USDT; se fosse BASE: 5 BTC → rifiutato per saldo
            ClientOrderId = buyClientId, Credentials = tc,
        });
        Console.WriteLine(buy.Success
            ? $"[OK] MARKET-BUY size=5 accettato: orderId={buy.ExchangeOrderId} fillPrice={buy.FilledPrice} fillQty={buy.FilledQuantity}"
            : $"[INFO] MARKET-BUY size=5 RIFIUTATO dall'exchange: {buy.Error} — se l'errore è 'insufficient balance' su un conto con >5 USDT, la size è interpretata come BASE.");

        if (buy.Success)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var info = await client.GetOrderStatusAsync(symbol, buyClientId, tc);
            Console.WriteLine($"[INFO] orderInfo: status={info.Status} priceAvg={info.FilledPrice} baseVolume={info.FilledQuantity}");
            if (info.FilledPrice is decimal price && info.FilledQuantity is decimal baseQty && price > 0m)
            {
                var quoteSpent = price * baseQty;
                Console.WriteLine(Math.Abs(quoteSpent - 5m) < 1m
                    ? $"[VERDETTO] speso ≈ {quoteSpent:F2} USDT per size=5 → 'size' = CONTROVALORE QUOTE (come da doc v2). Imposta Trading:Bitget:SpotMarketBuyVerified=true e ADATTA PlaceOrderAsync a convertire base→quote per i market-buy."
                    : $"[VERDETTO] speso ≈ {quoteSpent:F2} USDT per size=5 → 'size' NON è il controvalore quote: verificare manualmente prima di sbloccare.");

                // Sell-back: la semantica del market-SELL è base qty senza ambiguità.
                var sell = await client.PlaceOrderAsync(new PlaceOrderRequest
                {
                    Symbol = symbol, Side = "SELL", Type = "MARKET", Quantity = baseQty,
                    ClientOrderId = $"spotverify-sell-{Guid.NewGuid():N}"[..32], Credentials = tc,
                });
                Console.WriteLine(sell.Success
                    ? $"[OK] sell-back di {baseQty} eseguito (fillPrice={sell.FilledPrice})."
                    : $"[WARN] sell-back FALLITO: {sell.Error} — chiudere a mano la posizione spot!");
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] place-min-order: {ex.GetType().Name}: {ex.Message}"); }
}

Console.WriteLine();
Console.WriteLine(placeMinOrder
    ? "Verifica completata (sono stati piazzati ordini reali di taglia minima su Bitget)."
    : "Verifica completata in sola lettura (nessun ordine piazzato). Usa --place-min-order per la verifica semantica del market-buy.");
