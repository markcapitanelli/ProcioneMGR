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

// Npgsql "legacy timestamp behavior": stessa impostazione dell'app (vedi ProcioneMGR/Program.cs).
// Le colonne sono 'timestamp without time zone' e il codice usa DateTime con Kind=Utc: senza questo
// switch Npgsql rifiuta la scrittura ("Cannot write DateTime with Kind=UTC to PostgreSQL type
// 'timestamp without time zone'"). Va impostato PRIMA di costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

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
}

Console.WriteLine();
Console.WriteLine("Verifica completata (nessun ordine è stato piazzato).");
