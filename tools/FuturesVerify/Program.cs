// Verifica LIVE (sola lettura, nessun ordine) dell'integrazione Futures contro Binance
// Testnet e Bitget Demo Trading, usando le credenziali reali già salvate in ExchangeSettings
// (decifrate con la stessa AesGcmEncryptionService/master key dell'app). Conferma che firma
// HMAC, URL e parsing JSON funzionino contro le API vere, senza rischiare nulla (nessun
// PlaceFuturesOrderAsync viene chiamato qui).
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;

var appDir = @"C:\Users\proci\Desktop\ProgettoP\ProcioneMGR";
var dbPath = Path.Combine(appDir, "Data", "app.db");

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
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"DataSource={dbPath}"));
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

Console.WriteLine($"Trovate {creds.Count} credenziali testnet/demo nel DB.");

foreach (var c in creds)
{
    Console.WriteLine();
    Console.WriteLine($"=== {c.ExchangeName} ('{c.Label}', Id={c.Id}) ===");
    var tc = new TradingCredentials(c.ApiKey, c.ApiSecret, c.Passphrase, c.IsTestnet);
    var client = exchangeFactory.CreateFutures(c.ExchangeName);
    const string symbol = "BTC/USDT";

    try
    {
        var filters = await client.GetFuturesSymbolFiltersAsync(symbol, c.IsTestnet);
        Console.WriteLine($"[OK] GetFuturesSymbolFiltersAsync({symbol}): step={filters.StepSize} minQty={filters.MinQty} tick={filters.TickSize} minNotional={filters.MinNotional}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetFuturesSymbolFiltersAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var funding = await client.GetFundingRateAsync(symbol, c.IsTestnet);
        Console.WriteLine($"[OK] GetFundingRateAsync({symbol}): {funding}%");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetFundingRateAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var balance = await client.GetFuturesBalanceAsync(tc);
        Console.WriteLine($"[OK] GetFuturesBalanceAsync: available={balance.AvailableMargin} equity={balance.TotalEquity}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetFuturesBalanceAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var pos = await client.GetPositionAsync(symbol, tc);
        Console.WriteLine(pos is null
            ? "[OK] GetPositionAsync: nessuna posizione aperta (atteso)."
            : $"[OK] GetPositionAsync: {pos.Side} qty={pos.Quantity} entry={pos.EntryPrice} liq={pos.LiquidationPrice}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetPositionAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var lev = await client.SetLeverageAsync(symbol, 2, tc);
        Console.WriteLine(lev.Success
            ? $"[OK] SetLeverageAsync(2x): confermata leva={lev.Leverage}"
            : $"[FAIL] SetLeverageAsync: {lev.Error}");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] SetLeverageAsync: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var open = await client.GetOpenFuturesOrdersAsync(symbol, tc);
        Console.WriteLine($"[OK] GetOpenFuturesOrdersAsync: {open.Count} ordini aperti.");
    }
    catch (Exception ex) { Console.WriteLine($"[FAIL] GetOpenFuturesOrdersAsync: {ex.GetType().Name}: {ex.Message}"); }

    // Bitget: la demo futures espone solo alcuni major. Verifichiamo il simbolo "buono" (ETH,
    // atteso OK) e uno "cattivo" (SUI, atteso errore tradotto) per confermare dal vivo sia la
    // disponibilità dei contratti demo sia la traduzione dell'errore 40034 (nessun ordine).
    if (c.ExchangeName == ExchangeName.Bitget)
    {
        try
        {
            var ethLev = await client.SetLeverageAsync("ETH/USDT", 2, tc);
            Console.WriteLine(ethLev.Success
                ? $"[OK] SetLeverageAsync(ETH/USDT 2x): confermata leva={ethLev.Leverage}"
                : $"[FAIL] SetLeverageAsync(ETH/USDT): {ethLev.Error}");
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] SetLeverageAsync(ETH/USDT): {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            var suiLev = await client.SetLeverageAsync("SUI/USDT", 2, tc);
            Console.WriteLine(suiLev.Success
                ? "[?] SetLeverageAsync(SUI/USDT): inatteso successo (SUI ora disponibile sulla demo?)"
                : $"[OK] SetLeverageAsync(SUI/USDT) rifiutato con messaggio chiaro: {suiLev.Error}");
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] SetLeverageAsync(SUI/USDT): {ex.GetType().Name}: {ex.Message}"); }
    }
}

Console.WriteLine();
Console.WriteLine("Verifica completata (nessun ordine è stato piazzato).");
