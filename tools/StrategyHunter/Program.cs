// StrategyHunter v2: caccia sistematica alle strategie con i servizi REALI della piattaforma.
// Fasi: ingest | ingest2 | discover | validate | probe | save   (app FERMA durante l'esecuzione).
// v2: 18 coppie, timeframe 15m (walk-forward corto), slippage nei backtest di validazione,
//     LeverageAdvisor sui sopravvissuti (capitale piccolo + leva).
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Risk;

// Npgsql "legacy timestamp behavior": stessa impostazione dell'app (vedi ProcioneMGR/Program.cs).
// Le colonne sono 'timestamp without time zone' e il codice usa DateTime con Kind=Utc: senza questo
// switch Npgsql rifiuta la scrittura ("Cannot write DateTime with Kind=UTC to PostgreSQL type
// 'timestamp without time zone'"). Va impostato PRIMA di costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Guida raggiungibile a zero configurazione: senza argomenti si stampa l'usage e si esce,
// PRIMA di pretendere la connection string (che serve solo alle fasi vere).
if (args.Length == 0)
{
    Console.WriteLine("Uso: StrategyHunter <fase>  —  fasi: ingest | ingest2 | discover | validate | probe | save [indici]");
    Console.WriteLine("Env richiesta: ConnectionStrings__PostgresConnection");
    return;
}

// Connection string: solo da env, nessun fallback hardcoded con password (evita che una
// credenziale finisca nel sorgente / venga usata per errore in un container).
var pgConn = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresConnection")
    ?? throw new InvalidOperationException(
        "Variabile d'ambiente ConnectionStrings__PostgresConnection non impostata (obbligatoria).");
var harnessDir = AppContext.BaseDirectory;
var resultsPath = Path.Combine(harnessDir, "discovery-results.json");
var validationPath = Path.Combine(harnessDir, "validation-results.json");

string[] symbolsV1 = ["BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT",
                      "DOGE/USDT", "ADA/USDT", "LINK/USDT", "AVAX/USDT", "LTC/USDT"];
string[] symbolsV2 = ["NEAR/USDT", "TRX/USDT", "UNI/USDT", "ATOM/USDT", "FIL/USDT",
                      "SHIB/USDT", "PEPE/USDT", "DOT/USDT"];
string[] symbols = [.. symbolsV1, .. symbolsV2];
string[] timeframes = ["1h", "4h", "1d"];

var ingestFrom = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc);
var ingest15From = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

// Periodo di SELEZIONE (tutte le scelte) e HOLDOUT finale mai visto da nessuna decisione.
var selectionFrom = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc);
var selection15From = ingest15From;
var selectionTo = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
var holdoutFrom = selectionTo;
var holdoutTo = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);

// Attriti realistici nei backtest di validazione (perp futures su coppie liquide).
const decimal ValidationSlippagePercent = 0.05m;

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(pgConn));
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
services.AddScoped<IOhlcvIngestionService, OhlcvIngestionService>();
services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
services.AddSingleton<IStrategyFactory, StrategyFactory>();
services.AddSingleton<ProcioneMGR.Services.Alpha.IAlphaFactorFactory, ProcioneMGR.Services.Alpha.AlphaFactorFactory>();
services.AddScoped<IBacktestEngine, BacktestEngine>();
services.AddScoped<IOptimizationEngine, OptimizationEngine>();
services.AddScoped<IStrategyDiscovery, StrategyDiscoveryEngine>();
await using var provider = services.BuildServiceProvider();

var phase = args.Length > 0 ? args[0] : "all";
switch (phase)
{
    case "ingest": await IngestAsync(symbols, timeframes, ingestFrom); break;
    case "ingest2":
        await IngestAsync(symbolsV2, timeframes, ingestFrom);       // 8 coppie nuove, TF classici
        await IngestAsync(symbols, ["15m"], ingest15From);          // 15m per tutte e 18
        break;
    case "discover": await DiscoverAsync(); break;
    case "validate": await ValidateAsync(); break;
    case "probe": await ProbeAsync(); break;
    case "save": await SaveAsync(args.Length > 1 ? args[1..] : []); break;
    default: Console.WriteLine("Fase sconosciuta. Usa: ingest | ingest2 | discover | validate | probe | save"); break;
}

// ------------------------------------------------------------------ INGEST
async Task IngestAsync(string[] whichSymbols, string[] whichTfs, DateTime from)
{
    Console.WriteLine($"=== INGEST: {whichSymbols.Length} coppie x [{string.Join(",", whichTfs)}], dal {from:yyyy-MM-dd} ===");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long total = 0;

    foreach (var symbol in whichSymbols)
    {
        foreach (var tf in whichTfs)
        {
            using var scope = provider.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IOhlcvIngestionService>();
            try
            {
                var result = await ingestion.IngestHistoricalDataAsync(
                    "Binance", symbol, tf, from, DateTime.UtcNow, null, CancellationToken.None);
                total += result.CandlesProcessed;
                Console.WriteLine($"  {symbol,-10} {tf,-3} -> {result.CandlesProcessed,7} candele");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {symbol,-10} {tf,-3} -> ERRORE: {ex.Message}");
            }
        }
    }

    var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        foreach (var symbol in whichSymbols)
        {
            foreach (var tf in whichTfs)
            {
                var exists = await db.TrackedSeries.AnyAsync(t =>
                    t.Exchange == ExchangeName.Binance && t.Symbol == symbol && t.Timeframe == tf);
                if (!exists)
                {
                    db.TrackedSeries.Add(new TrackedSeries
                    {
                        Exchange = ExchangeName.Binance,
                        Symbol = symbol,
                        Timeframe = tf,
                        Enabled = true,
                    });
                }
            }
        }
        var added = await db.SaveChangesAsync();
        Console.WriteLine($"  Watchlist: {added} nuove serie tracciate.");
    }

    Console.WriteLine($"=== INGEST completata: {total} candele in {sw.Elapsed.TotalMinutes:F1} min ===");
}

// ------------------------------------------------------------------ DISCOVER
async Task DiscoverAsync()
{
    Console.WriteLine($"=== DISCOVERY v2 (holdout riservato: {holdoutFrom:yyyy-MM-dd} -> {holdoutTo:yyyy-MM-dd}) ===");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var all = new List<DiscoveryCandidate>();

    using var scope = provider.CreateScope();
    var discovery = scope.ServiceProvider.GetRequiredService<IStrategyDiscovery>();

    // Run A: timeframe classici, walk-forward 12/3/3.
    all.AddRange(await RunDiscovery(discovery, [.. timeframes], selectionFrom, 12, 3, 3, "A (1h/4h/1d)"));

    // Run B: 15m — storico piu' corto, walk-forward compresso 6/2/2.
    all.AddRange(await RunDiscovery(discovery, ["15m"], selection15From, 6, 2, 2, "B (15m)"));

    var ranked = all.OrderByDescending(c => c.OutOfSampleSharpe).ToList();
    Console.WriteLine($"\n  Totale candidati: {ranked.Count} in {sw.Elapsed.TotalMinutes:F1} min");
    Console.WriteLine($"  {"#",-3} {"Strategia",-24} {"Symbol",-10} {"TF",-4} {"OOS Sh",7} {"IS Sh",7} {"Trades",6} {"Win",4}");
    for (var i = 0; i < Math.Min(40, ranked.Count); i++)
    {
        var c = ranked[i];
        Console.WriteLine($"  {i + 1,-3} {c.StrategyName,-24} {c.Symbol,-10} {c.Timeframe,-4} {c.OutOfSampleSharpe,7:F2} {c.InSampleSharpe,7:F2} {c.TotalTrades,6} {c.Windows,4}");
    }

    File.WriteAllText(resultsPath, JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"=== DISCOVERY completata -> {resultsPath} ===");
}

async Task<List<DiscoveryCandidate>> RunDiscovery(
    IStrategyDiscovery discovery, List<string> tfs, DateTime from, int isMonths, int oosMonths, int stepMonths, string label)
{
    Console.WriteLine($"  -- Run {label}: {symbols.Length} coppie, da {from:yyyy-MM-dd}, WF {isMonths}/{oosMonths}/{stepMonths}");
    var config = new StrategyDiscoveryConfiguration
    {
        ExchangeName = "Binance",
        Symbols = [.. symbols],
        Timeframes = tfs,
        Strategies = [],
        From = from,
        To = selectionTo,
        TopN = 60,
        WalkForward = new WalkForwardConfiguration
        {
            InSampleMonths = isMonths,
            OutOfSampleMonths = oosMonths,
            StepMonths = stepMonths,
        },
    };
    var progress = new Progress<DiscoveryProgress>(p =>
    {
        if (p.Completed % 60 == 0)
        {
            Console.WriteLine($"     ... {p.Completed}/{p.Total} job, best OOS {p.BestSharpeSoFar:F2}");
        }
    });
    var result = await discovery.DiscoverAsync(config, progress, CancellationToken.None);
    Console.WriteLine($"     Run {label}: {result.CombinationsTested} combos, {result.Candidates.Count} candidati");
    return result.Candidates;
}

// ------------------------------------------------------------------ VALIDATE
async Task ValidateAsync()
{
    var candidates = JsonSerializer.Deserialize<List<DiscoveryCandidate>>(File.ReadAllText(resultsPath))!;

    var shortlist = candidates
        .Where(c => c.OutOfSampleSharpe > 0.3m && c.TotalTrades >= 12 && c.Windows >= 3)
        .OrderByDescending(c => c.OutOfSampleSharpe)
        .Take(15)
        .ToList();

    Console.WriteLine($"=== VALIDATE v2: {shortlist.Count} candidati (slippage {ValidationSlippagePercent}% su ogni fill) ===");

    var variants = new (string Name, decimal Sl, decimal Tsl)[]
    {
        ("base", 0m, 0m),
        ("SL3", 3m, 0m),
        ("TSL5", 0m, 5m),
        ("SL3+TSL5", 3m, 5m),
    };

    var reports = new List<ValidationReport>();
    using var scope = provider.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var kelly = new KellyCalculator();
    var mc = new MonteCarloAnalyzer();
    var levAdvisor = new LeverageAdvisor();

    foreach (var c in shortlist)
    {
        var from = c.Timeframe == "15m" ? selection15From : selectionFrom;
        Console.WriteLine($"\n--- {c.StrategyName} {c.Symbol} {c.Timeframe} [{string.Join(", ", c.Parameters.Select(p => $"{p.Key}={p.Value}"))}] ---");

        BacktestResult? bestSel = null;
        (string Name, decimal Sl, decimal Tsl) bestVariant = variants[0];
        decimal bestSelSharpe = decimal.MinValue;
        foreach (var v in variants)
        {
            var selRes = await engine.RunBacktestAsync(MakeConfig(c, from, selectionTo, v.Sl, v.Tsl), CancellationToken.None);
            var sh = Statistics.SharpeRatio(selRes.EquityCurve, Statistics.PeriodsPerYear(c.Timeframe));
            Console.WriteLine($"  selezione [{v.Name,-9}] Sharpe {sh,6:F2}  Ret {selRes.TotalReturnPercent,7:F1}%  DD {selRes.MaxDrawdownPercent,5:F1}%  Trades {selRes.TotalTrades}");
            if (sh > bestSelSharpe)
            {
                bestSelSharpe = sh;
                bestSel = selRes;
                bestVariant = v;
            }
        }

        var selReport = TradeStatistics.ComputeTradeReport(bestSel!.Trades, bestSel.EquityCurve);
        var mcRes = mc.Run(bestSel.Trades.Select(t => t.Pnl).ToList(),
            new MonteCarloConfig { NumberOfShuffles = 500, Seed = 42 });
        var kellyRes = kelly.FromTradeHistory(bestSel.Trades);
        var levAdvice = levAdvisor.Advise(bestSel.Trades, marginFraction: 0.2m);

        var holdRes = await engine.RunBacktestAsync(MakeConfig(c, holdoutFrom, holdoutTo, bestVariant.Sl, bestVariant.Tsl), CancellationToken.None);
        var holdSharpe = Statistics.SharpeRatio(holdRes.EquityCurve, Statistics.PeriodsPerYear(c.Timeframe));
        var holdReport = TradeStatistics.ComputeTradeReport(holdRes.Trades, holdRes.EquityCurve);

        Console.WriteLine($"  variante scelta: {bestVariant.Name}  |  PF sel {selReport.ProfitFactor:F2}, MC RF95 {mcRes.RiskFactor95:F2}x, Kelly {kellyRes.KellyFraction:P1}, LEVA consigliata {levAdvice.RecommendedLeverage:N0}x");
        Console.WriteLine($"  HOLDOUT: Sharpe {holdSharpe:F2}  Ret {holdRes.TotalReturnPercent:F1}%  PF {holdReport.ProfitFactor:F2}  DD {holdRes.MaxDrawdownPercent:F1}%  Trades {holdRes.TotalTrades}");

        reports.Add(new ValidationReport(
            c.StrategyName, c.Symbol, c.Timeframe, c.Parameters,
            bestVariant.Name, bestVariant.Sl, bestVariant.Tsl,
            bestSelSharpe, selReport.ProfitFactor, selReport.AverageTrade, selReport.KestnerRatio,
            bestSel.TotalReturnPercent, bestSel.MaxDrawdownPercent, bestSel.TotalTrades,
            mcRes.RiskFactor95, mcRes.MaxDrawdown95, kellyRes.KellyFraction, kellyRes.HalfKelly,
            levAdvice.RecommendedLeverage,
            holdSharpe, holdRes.TotalReturnPercent, holdReport.ProfitFactor, holdRes.MaxDrawdownPercent, holdRes.TotalTrades,
            c.OutOfSampleSharpe));
    }

    File.WriteAllText(validationPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine("\n=== CLASSIFICA FINALE (ordinata per Sharpe holdout) ===");
    Console.WriteLine($"{"Strategia",-24} {"Symbol",-10} {"TF",-4} {"Var",-9} {"HoldSh",7} {"HoldRet",8} {"HoldPF",7} {"HoldTrd",7} {"Leva",5} | {"SelSh",6} {"WF-OOS",6}");
    foreach (var r in reports.OrderByDescending(r => r.HoldoutSharpe))
    {
        Console.WriteLine($"{r.Strategy,-24} {r.Symbol,-10} {r.Timeframe,-4} {r.Variant,-9} {r.HoldoutSharpe,7:F2} {r.HoldoutReturn,7:F1}% {r.HoldoutProfitFactor,7:F2} {r.HoldoutTrades,7} {r.RecommendedLeverage,4:N0}x | {r.SelectionSharpe,6:F2} {r.WalkForwardOosSharpe,6:F2}");
    }
    Console.WriteLine($"=== VALIDATE completata -> {validationPath} ===");
}

// ------------------------------------------------------------------ PROBE
async Task ProbeAsync()
{
    using var scope = provider.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();

    var probes = new (string Strategy, string Tf, Dictionary<string, decimal> Params, decimal Sl, decimal Tsl, string Label)[]
    {
        ("PriceSmaCross", "4h", new() { ["Period"] = 100m, ["AllowShort"] = 1m }, 3m, 0m, "PriceSmaCross(100,short) SL3"),
    };

    foreach (var probe in probes)
    {
        Console.WriteLine($"\n=== PROBE cross-asset: {probe.Label} su {probe.Tf} ===");
        Console.WriteLine($"  {"Symbol",-10} | {"SelSh",6} {"SelRet",8} {"SelPF",6} | {"HoldSh",7} {"HoldRet",8} {"HoldPF",7}");
        foreach (var symbol in symbols)
        {
            var candidate = new DiscoveryCandidate
            {
                StrategyName = probe.Strategy,
                Symbol = symbol,
                Timeframe = probe.Tf,
                Parameters = probe.Params,
            };
            var sel = await engine.RunBacktestAsync(MakeConfig(candidate, selectionFrom, selectionTo, probe.Sl, probe.Tsl), CancellationToken.None);
            var selSh = Statistics.SharpeRatio(sel.EquityCurve, Statistics.PeriodsPerYear(probe.Tf));
            var selReport = TradeStatistics.ComputeTradeReport(sel.Trades, sel.EquityCurve);
            var hold = await engine.RunBacktestAsync(MakeConfig(candidate, holdoutFrom, holdoutTo, probe.Sl, probe.Tsl), CancellationToken.None);
            var holdSh = Statistics.SharpeRatio(hold.EquityCurve, Statistics.PeriodsPerYear(probe.Tf));
            var holdReport = TradeStatistics.ComputeTradeReport(hold.Trades, hold.EquityCurve);
            Console.WriteLine($"  {symbol,-10} | {selSh,6:F2} {sel.TotalReturnPercent,7:F1}% {selReport.ProfitFactor,6:F2} | {holdSh,7:F2} {hold.TotalReturnPercent,7:F1}% {holdReport.ProfitFactor,7:F2}");
        }
    }
}

// ------------------------------------------------------------------ SAVE
async Task SaveAsync(string[] indices)
{
    var reports = JsonSerializer.Deserialize<List<ValidationReport>>(File.ReadAllText(validationPath))!
        .OrderByDescending(r => r.HoldoutSharpe).ToList();

    var chosen = indices.Length > 0
        ? indices.Select(int.Parse).Select(i => reports[i]).ToList()
        : reports.Where(r => r.HoldoutSharpe > 0m && r.HoldoutProfitFactor >= 1m && r.HoldoutTrades >= 5).ToList();

    var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    var adminId = await db.Users.Where(u => u.Email == "procionemgr@gmail.com").Select(u => u.Id).SingleAsync();

    foreach (var r in chosen)
    {
        var parameters = new Dictionary<string, decimal>(r.Parameters);
        var name = $"{r.Strategy} {r.Symbol.Replace("/USDT", "")} {r.Timeframe} [{r.Variant}]";
        if (name.Length > 64) name = name[..64];

        var exists = await db.SavedStrategies.AnyAsync(s => s.Name == name && s.UserId == adminId);
        if (exists)
        {
            Console.WriteLine($"  gia' presente: {name}");
            continue;
        }
        db.SavedStrategies.Add(new SavedStrategy
        {
            UserId = adminId,
            Name = name,
            StrategyName = r.Strategy,
            ParametersJson = JsonSerializer.Serialize(parameters),
            IsOptimized = true,
            OptimizationDate = DateTime.UtcNow,
            OptimizationSharpe = r.HoldoutSharpe,
        });
        Console.WriteLine($"  salvata: {name}  (holdout Sharpe {r.HoldoutSharpe:F2}, PF {r.HoldoutProfitFactor:F2}, leva consigliata {r.RecommendedLeverage:N0}x)");
    }
    await db.SaveChangesAsync();
    Console.WriteLine("=== SAVE completata ===");
}

BacktestConfiguration MakeConfig(DiscoveryCandidate c, DateTime from, DateTime to, decimal sl, decimal tsl) => new()
{
    ExchangeName = "Binance",
    Symbol = c.Symbol,
    Timeframe = c.Timeframe,
    From = from,
    To = to,
    InitialCapital = 10_000m,
    PositionSizePercent = 20m,
    FeePercent = 0.1m,
    StrategyName = c.StrategyName,
    StrategyParameters = new Dictionary<string, decimal>(c.Parameters),
    StopLossPercent = sl,
    TrailingStopPercent = tsl,
    SlippagePercent = ValidationSlippagePercent,
};

sealed record ValidationReport(
    string Strategy, string Symbol, string Timeframe, Dictionary<string, decimal> Parameters,
    string Variant, decimal StopLoss, decimal TrailingStop,
    decimal SelectionSharpe, decimal SelectionProfitFactor, decimal SelectionAvgTrade, decimal SelectionKestner,
    decimal SelectionReturn, decimal SelectionMaxDd, int SelectionTrades,
    decimal MonteCarloRiskFactor95, decimal MonteCarloDd95, decimal KellyFraction, decimal HalfKelly,
    decimal RecommendedLeverage,
    decimal HoldoutSharpe, decimal HoldoutReturn, decimal HoldoutProfitFactor, decimal HoldoutMaxDd, int HoldoutTrades,
    decimal WalkForwardOosSharpe);

sealed class PassthroughEncryption : ProcioneMGR.Services.Security.IEncryptionService
{
    public string Encrypt(string plaintext) => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}
