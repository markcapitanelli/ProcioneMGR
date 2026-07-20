// PlatformExpand: harness per espandere dati e stressare la piattaforma end-to-end.
// Fasi: stats | ingest | analyze | altdata
//   stats   -> inventario READ-ONLY (sicuro anche con l'app in esecuzione, WAL)
//   ingest  -> nuove coppie, timeframe 5m, storia piu' profonda (app FERMA)
//   analyze -> esercita regime/pairs/volatilita'/fattori/creative discovery, cerca errori (app FERMA)
//   altdata -> sync news + sentiment (app FERMA)
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
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Risk;

// Npgsql "legacy timestamp behavior": stessa impostazione dell'app (vedi ProcioneMGR/Program.cs).
// Le colonne sono 'timestamp without time zone' e il codice usa DateTime con Kind=Utc: senza questo
// switch Npgsql RIFIUTA la scrittura e ogni fase che tocca l'OHLCV muore con
// "Cannot write DateTime with Kind=UTC to PostgreSQL type 'timestamp without time zone'".
// Va impostato PRIMA di costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var pgConn = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresConnection")
    ?? "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure";

// --- Universo di espansione ---
// 18 coppie storiche (gia' presenti) + 12 nuove liquide su Binance.
string[] existingSymbols =
[
    "BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT", "ADA/USDT",
    "LINK/USDT", "AVAX/USDT", "LTC/USDT", "NEAR/USDT", "TRX/USDT", "UNI/USDT", "ATOM/USDT",
    "FIL/USDT", "SHIB/USDT", "PEPE/USDT", "DOT/USDT",
];
string[] newSymbols =
[
    "OP/USDT", "ARB/USDT", "APT/USDT", "SUI/USDT", "INJ/USDT", "TIA/USDT",
    "SEI/USDT", "AAVE/USDT", "GRT/USDT", "ALGO/USDT", "ICP/USDT", "HBAR/USDT",
];
string[] allSymbols = [.. existingSymbols, .. newSymbols];

var classicFrom = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc);
var deepDailyFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);   // 1d storia profonda
var deep4hFrom = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);      // 4h storia media
var intradayFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);    // 5m/15m: piu' corto (volume dati)

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
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

var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var phase = args.Length > 0 ? args[0] : "stats";

// Periodo di selezione + holdout (mai visto in selezione) per la discovery espansa.
var selectionTo = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
var holdoutFrom = selectionTo;
var holdoutTo = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
var resultsPath = Path.Combine(AppContext.BaseDirectory, "expand-discovery.json");

switch (phase)
{
    case "stats": await StatsAsync(); break;
    case "ingest": await IngestAsync(); break;
    case "ingest1m": await Ingest1mAsync(); break;
    case "costprofile": await CostProfileAsync(); break;
    case "discover": await DiscoverAsync(); break;
    default: Console.WriteLine($"Fase sconosciuta '{phase}'. Usa: stats | ingest | ingest1m | costprofile | discover"); break;
}

// ------------------------------------------------------------------ STATS (read-only)
async Task StatsAsync()
{
    await using var db = await dbFactory.CreateDbContextAsync();
    Console.WriteLine("=== INVENTARIO OHLCV (per Symbol/Timeframe) ===");

    // Aggregazione lato DB: conteggio + range temporale per ogni serie.
    var rows = await db.OhlcvData
        .GroupBy(c => new { c.Symbol, c.Timeframe })
        .Select(g => new
        {
            g.Key.Symbol,
            g.Key.Timeframe,
            Count = g.Count(),
            Min = g.Min(c => c.TimestampUtc),
            Max = g.Max(c => c.TimestampUtc),
        })
        .ToListAsync();

    var tfOrder = new Dictionary<string, int> { ["1m"] = 0, ["5m"] = 1, ["15m"] = 2, ["30m"] = 3, ["1h"] = 4, ["4h"] = 5, ["1d"] = 6 };
    long grand = 0;
    foreach (var g in rows
        .OrderBy(r => r.Symbol)
        .ThenBy(r => tfOrder.TryGetValue(r.Timeframe, out var o) ? o : 99))
    {
        grand += g.Count;
        Console.WriteLine($"  {g.Symbol,-11} {g.Timeframe,-4} {g.Count,8:N0}  {g.Min:yyyy-MM-dd} -> {g.Max:yyyy-MM-dd}");
    }
    Console.WriteLine($"  --- {rows.Count} serie, {grand:N0} candele totali ---");

    // Riepilogo per timeframe.
    Console.WriteLine("\n=== Per timeframe ===");
    foreach (var tf in rows.GroupBy(r => r.Timeframe).OrderBy(g => tfOrder.TryGetValue(g.Key, out var o) ? o : 99))
    {
        Console.WriteLine($"  {tf.Key,-4} {tf.Count(),3} serie, {tf.Sum(r => (long)r.Count),12:N0} candele");
    }

    // Copertura: quali coppie di 'allSymbols' hanno quali TF.
    Console.WriteLine("\n=== Copertura coppie x timeframe (S=presente) ===");
    string[] tfs = ["5m", "15m", "1h", "4h", "1d"];
    Console.WriteLine($"  {"Symbol",-11} {string.Join(" ", tfs.Select(t => t.PadLeft(4)))}");
    var present = rows.Select(r => (r.Symbol, r.Timeframe)).ToHashSet();
    foreach (var s in allSymbols)
    {
        var marks = tfs.Select(t => (present.Contains((s, t)) ? "S" : "·").PadLeft(4));
        var isNew = newSymbols.Contains(s) ? " (NUOVA)" : "";
        Console.WriteLine($"  {s,-11} {string.Join(" ", marks)}{isNew}");
    }

    // Altri artefatti.
    Console.WriteLine("\n=== Altri dati ===");
    Console.WriteLine($"  TrackedSeries:            {await db.TrackedSeries.CountAsync()} ({await db.TrackedSeries.CountAsync(t => t.Enabled)} abilitate)");
    Console.WriteLine($"  SavedStrategies:          {await db.SavedStrategies.CountAsync()}");
    Console.WriteLine($"  TradeRecords:             {await db.TradeRecords.CountAsync()}");
    Console.WriteLine($"  EnsembleStates:           {await db.EnsembleStates.CountAsync()}");
    Console.WriteLine($"  PipelineConfigurations:   {await db.Set<ProcioneMGR.Services.Pipeline.PipelineConfiguration>().CountAsync()}");
    Console.WriteLine($"  PipelineRuns:             {await db.Set<ProcioneMGR.Services.Pipeline.PipelineRun>().CountAsync()}");
}

// ------------------------------------------------------------------ INGEST (app ferma)
async Task IngestAsync()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long total = 0;

    // Piano di ingestione: (coppie, timeframe, da).
    var plan = new List<(string[] Symbols, string Tf, DateTime From, string Label)>
    {
        // Nuove coppie: TF classici + intraday.
        (newSymbols, "1d", deepDailyFrom, "nuove 1d"),
        (newSymbols, "4h", deep4hFrom, "nuove 4h"),
        (newSymbols, "1h", classicFrom, "nuove 1h"),
        (newSymbols, "15m", intradayFrom, "nuove 15m"),
        // Storia piu' profonda per le esistenti.
        (existingSymbols, "1d", deepDailyFrom, "esistenti 1d deep"),
        (existingSymbols, "4h", deep4hFrom, "esistenti 4h deep"),
        // Timeframe 5m (intraday) per TUTTE le coppie.
        (allSymbols, "5m", intradayFrom, "tutte 5m"),
    };

    foreach (var (syms, tf, from, label) in plan)
    {
        Console.WriteLine($"\n=== INGEST {label}: {syms.Length} coppie x {tf}, da {from:yyyy-MM-dd} ===");
        foreach (var symbol in syms)
        {
            using var scope = provider.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IOhlcvIngestionService>();
            try
            {
                var result = await ingestion.IngestHistoricalDataAsync(
                    "Binance", symbol, tf, from, DateTime.UtcNow, null, CancellationToken.None);
                total += result.CandlesProcessed;
                Console.WriteLine($"  {symbol,-11} {tf,-4} -> {result.CandlesProcessed,8:N0} candele");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {symbol,-11} {tf,-4} -> ERRORE: {ex.Message}");
            }
        }
    }

    // Registra tutte le serie nella watchlist (idempotente).
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        var wanted = plan.SelectMany(p => p.Symbols.Select(s => (s, p.Tf))).Distinct().ToList();
        var added = 0;
        foreach (var (symbol, tf) in wanted)
        {
            var exists = await db.TrackedSeries.AnyAsync(t =>
                t.Exchange == ExchangeName.Binance && t.Symbol == symbol && t.Timeframe == tf);
            if (!exists)
            {
                db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = tf, Enabled = true });
                added++;
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"\n  Watchlist: {added} nuove serie tracciate.");
    }

    Console.WriteLine($"\n=== INGEST completata: {total:N0} candele in {sw.Elapsed.TotalMinutes:F1} min ===");
}

// ------------------------------------------------------------------ INGEST 1m (app ferma)
// [R2] Timeframe 1m, DELIBERATAMENTE limitato.
//
// Perché non tutte le coppie: a 1m un anno vale ~525.000 candele per coppia. Sulle 30 coppie della
// watchlist sarebbero ~15,8 milioni di righe contro i ~7,7 milioni dell'INTERO database attuale —
// più che raddoppiarlo per rispondere a una domanda che si può rispondere su sei coppie.
//
// Perché proprio queste sei: sono le più liquide, cioè quelle dove lo slippage reale è più basso e
// quindi dove 1m ha la MIGLIORE probabilità di funzionare. Se l'edge netto non sopravvive qui, non
// sopravvive da nessuna parte, e la risposta di R2 è chiusa senza scaricare altri 20 milioni di righe.
async Task Ingest1mAsync()
{
    string[] liquidSymbols = ["BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT"];
    var from1m = DateTime.UtcNow.Date.AddMonths(-12);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    long total = 0;

    Console.WriteLine($"=== INGEST 1m: {liquidSymbols.Length} coppie da {from1m:yyyy-MM-dd} ===");
    Console.WriteLine($"    Attesi ~{525_600L * liquidSymbols.Length:N0} candele. Richiede parecchi minuti.\n");

    foreach (var symbol in liquidSymbols)
    {
        using var scope = provider.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IOhlcvIngestionService>();
        var symSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await ingestion.IngestHistoricalDataAsync(
                "Binance", symbol, "1m", from1m, DateTime.UtcNow, null, CancellationToken.None);
            total += result.CandlesProcessed;
            Console.WriteLine($"  {symbol,-11} -> {result.CandlesProcessed,9:N0} candele in {symSw.Elapsed.TotalMinutes:F1} min");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {symbol,-11} -> ERRORE: {ex.Message}");
        }
    }

    // Watchlist: idempotente come nella fase ingest.
    //
    // Enabled=FALSE di proposito. Sei serie 1m in più nel ciclo di sincronizzazione periodico
    // significano sei richieste REST ogni 5 minuti per dati che, finché la misura di R2 non ha
    // detto se 1m è operabile, nessuno consuma. Si abilitano quando (e se) servono davvero.
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        var added = 0;
        foreach (var symbol in liquidSymbols)
        {
            var exists = await db.TrackedSeries.AnyAsync(t =>
                t.Exchange == ExchangeName.Binance && t.Symbol == symbol && t.Timeframe == "1m");
            if (!exists)
            {
                db.TrackedSeries.Add(new TrackedSeries
                {
                    Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = "1m", Enabled = false,
                });
                added++;
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"\n  Watchlist: {added} serie 1m registrate (DISABILITATE: si accendono solo se 1m si dimostra operabile).");
    }

    Console.WriteLine($"\n=== INGEST 1m completata: {total:N0} candele in {sw.Elapsed.TotalMinutes:F1} min ===");
}

// ------------------------------------------------------------------ COST PROFILE (app ferma)
// [R2] Risponde alla domanda "1m è operabile?" nel modo più diretto possibile: prende le STESSE
// strategie, sulle STESSE coppie, nella STESSA finestra di calendario, e le fa girare su timeframe
// diversi con i costi onesti. Poi confronta quanto dell'edge LORDO se lo mangia l'attrito.
//
// Non è un sweep di ottimizzazione: usa i parametri di default. È voluto. La domanda qui non è
// "quali parametri vanno bene a 1m" ma "a 1m resta qualcosa da ottimizzare, dopo i costi?". Se il
// cost drag divora il lordo a parametri ragionevoli, ottimizzare significa solo cercare più a fondo
// nel rumore — e conviene saperlo prima di bruciare ore di sweep.
async Task CostProfileAsync()
{
    string[] symbols = ["BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT"];
    string[] timeframes = ["1m", "5m", "15m", "1h"];

    // Finestra comune a tutti i timeframe: gli ultimi 6 mesi, che l'ingestione 1m copre di sicuro.
    var to = DateTime.UtcNow.Date;
    var from = to.AddMonths(-6);

    var costs = new PipelineCosts(
        PipelineCosts.DefaultSlippagePercent,
        PipelineCosts.DefaultFeePercent,
        PipelineCosts.DefaultFundingRatePercentPer8h);

    Console.WriteLine($"=== PROFILO DI COSTO per timeframe — {from:yyyy-MM-dd} → {to:yyyy-MM-dd} ===");
    Console.WriteLine($"    {symbols.Length} coppie, costi: fee {costs.FeePercent}%/lato, slippage {costs.SlippagePercent}%/fill\n");

    // ---- Parte 1: il TETTO STRUTTURALE, indipendente da qualunque strategia ----
    //
    // Questa è la misura più forte del profilo, perché non si può obiettare che "dipende dai
    // parametri": confronta l'ampiezza TIPICA di una candela con il costo di un giro completo.
    // Se il movimento mediano di una barra a 1m è 0,03% e un round-turn costa 0,30%, allora QUALUNQUE
    // strategia a 1m deve catturare un movimento pari a ~10 barre tipiche solo per andare in pari.
    // Non dice che è impossibile: dice quanto deve essere brava, e quante barre deve tenere.
    var roundTurnPercent = 2m * (costs.FeePercent + costs.SlippagePercent);
    Console.WriteLine($"=== TETTO STRUTTURALE (round-turn = {roundTurnPercent:F2}%) ===");
    Console.WriteLine($"  {"TF",-5} {"barre",10} {"|mossa| mediana",16} {"barre per pareggiare",22}");

    foreach (var tf in timeframes)
    {
        var moves = new List<decimal>();
        var bars = 0L;
        foreach (var symbol in symbols)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var closes = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == tf
                         && c.TimestampUtc >= from && c.TimestampUtc <= to)
                .OrderBy(c => c.TimestampUtc)
                .Select(c => c.Close)
                .ToListAsync();
            bars += closes.Count;
            for (var i = 1; i < closes.Count; i++)
            {
                if (closes[i - 1] > 0m) moves.Add(Math.Abs(closes[i] - closes[i - 1]) / closes[i - 1] * 100m);
            }
        }

        if (moves.Count == 0) { Console.WriteLine($"  {tf,-5} — nessun dato"); continue; }
        moves.Sort();
        var medianMove = moves[moves.Count / 2];
        var barsToBreakEven = medianMove > 0m ? roundTurnPercent / medianMove : 0m;
        Console.WriteLine($"  {tf,-5} {bars,10:N0} {medianMove,15:F4}% {barsToBreakEven,21:F1}");
    }

    Console.WriteLine();

    // ---- Parte 2: cosa succede davvero alle strategie della piattaforma ----
    using var scope = provider.CreateScope();
    var backtest = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var strategyFactory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();

    // Strategie a parametri di default, escluse quelle che richiedono un modello ML addestrato.
    var strategyNames = strategyFactory.Prototypes
        .Where(p => !p.Name.Contains("Ml", StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Name)
        .ToList();

    var rows = new List<(string Tf, string Symbol, string Strategy, BacktestResult R)>();

    foreach (var tf in timeframes)
    {
        foreach (var symbol in symbols)
        {
            List<OhlcvData> candles;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                candles = await db.OhlcvData.AsNoTracking()
                    .Where(c => c.Symbol == symbol && c.Timeframe == tf
                             && c.TimestampUtc >= from && c.TimestampUtc <= to)
                    .OrderBy(c => c.TimestampUtc)
                    .ToListAsync();
            }
            if (candles.Count < 500)
            {
                Console.WriteLine($"  {tf,-4} {symbol,-11} SALTATA: solo {candles.Count} candele nella finestra.");
                continue;
            }

            foreach (var name in strategyNames)
            {
                var cfg = costs.ApplyTo(new BacktestConfiguration
                {
                    ExchangeName = "Binance",
                    Symbol = symbol,
                    Timeframe = tf,
                    From = from,
                    To = to,
                    InitialCapital = 10_000m,
                    // Dimensione REALISTICA, non 100%. Col capitale intero su ogni trade e migliaia
                    // di giri, l'attrito compone fino ad azzerare il conto e tutte le mediane
                    // saturano a -100%: un numero vero ma inutile, perché non distingue più i
                    // timeframe fra loro. Al 10% (l'ordine di grandezza di SafetyConfiguration)
                    // le differenze restano leggibili.
                    PositionSizePercent = 10m,
                    StrategyName = name,
                });
                try
                {
                    var strategy = strategyFactory.Create(name);
                    var r = await backtest.RunBacktestAsync(cfg, candles, strategy, CancellationToken.None);
                    if (r.TotalTrades > 0) rows.Add((tf, symbol, name, r));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {tf,-4} {symbol,-11} {name,-22} ERRORE: {ex.Message}");
                }
            }
            Console.WriteLine($"  {tf,-4} {symbol,-11} {candles.Count,9:N0} candele, {strategyNames.Count} strategie valutate");
        }
    }

    Console.WriteLine($"\n=== SINTESI (mediane su {rows.Count} backtest con almeno un trade, size 10%) ===");
    Console.WriteLine($"  {"TF",-5} {"n",5} {"trade",8} {"lordo%",9} {"costi%",9} {"netto%",9} {"costi/lordo",12}");

    static decimal Median(IEnumerable<decimal> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0m : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2m;
    }

    foreach (var tf in timeframes)
    {
        var g = rows.Where(r => r.Tf == tf).ToList();
        if (g.Count == 0) { Console.WriteLine($"  {tf,-5} — nessun dato"); continue; }

        var trades = Median(g.Select(r => (decimal)r.R.TotalTrades));
        var gross = Median(g.Select(r => r.R.GrossReturnPercent));
        var drag = Median(g.Select(r => r.R.CostDragPercent));
        var net = Median(g.Select(r => r.R.TotalReturnPercent));

        // Rapporto costi/lordo: quanto dell'edge lordo se ne va in attrito. Calcolato solo sui casi
        // con lordo POSITIVO — su un lordo negativo il rapporto non significa niente (la strategia
        // perdeva già prima dei costi, e il problema non sono i costi).
        var positive = g.Where(r => r.R.GrossReturnPercent > 0m).ToList();
        var ratio = positive.Count == 0
            ? "n/d"
            : $"{Median(positive.Select(r => r.R.CostDragPercent / r.R.GrossReturnPercent)) * 100m:F0}%";

        Console.WriteLine($"  {tf,-5} {g.Count,5} {trades,8:F0} {gross,9:F2} {drag,9:F2} {net,9:F2} {ratio,12}");
    }

    // Quante combinazioni restano profittevoli AL NETTO: è il conto che decide.
    Console.WriteLine($"\n=== Sopravvissuti al netto dei costi ===");
    foreach (var tf in timeframes)
    {
        var g = rows.Where(r => r.Tf == tf).ToList();
        if (g.Count == 0) continue;
        var grossOk = g.Count(r => r.R.GrossReturnPercent > 0m);
        var netOk = g.Count(r => r.R.TotalReturnPercent > 0m);
        Console.WriteLine($"  {tf,-5} lordo>0: {grossOk,4}/{g.Count,-4} ({(decimal)grossOk / g.Count:P0})   " +
                          $"netto>0: {netOk,4}/{g.Count,-4} ({(decimal)netOk / g.Count:P0})");
    }

    var outPath = Path.Combine(AppContext.BaseDirectory, "cost-profile.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(
        rows.Select(r => new
        {
            r.Tf, r.Symbol, r.Strategy,
            r.R.TotalTrades, r.R.TotalReturnPercent, r.R.GrossReturnPercent,
            r.R.CostDragPercent, r.R.TotalFeesPaid, r.R.TotalSlippagePaid, r.R.MaxDrawdownPercent,
        }), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nDettaglio completo: {outPath}");
}

// ------------------------------------------------------------------ DISCOVER (app ferma)
// Discovery su scala espansa: 30 coppie, intraday (5m/15m) + swing (1h/4h), holdout riservato.
// Stressa optimizer + walk-forward su un universo piu' grande di prima e stampa una classifica.
async Task DiscoverAsync()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var scope = provider.CreateScope();
    var discovery = scope.ServiceProvider.GetRequiredService<IStrategyDiscovery>();
    var all = new List<DiscoveryCandidate>();

    // Swing: 1h/4h dal 2024-01, walk-forward 8/2/2 (30 coppie: universo espanso).
    all.AddRange(await RunDiscovery(discovery, ["1h", "4h"],
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 8, 2, 2, "swing 1h/4h"));

    // Intraday: 15m dal 2025-01, walk-forward compresso 4/1/1 (5m completo = ore, disponibile in UI).
    all.AddRange(await RunDiscovery(discovery, ["15m"],
        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), 4, 1, 1, "intraday 15m"));

    var ranked = all.OrderByDescending(c => c.OutOfSampleSharpe).ToList();
    Console.WriteLine($"\n=== Totale candidati: {ranked.Count} in {sw.Elapsed.TotalMinutes:F1} min ===");
    Console.WriteLine($"  {"#",-3} {"Strategia",-22} {"Symbol",-10} {"TF",-4} {"OOS",6} {"IS",6} {"Trd",5} {"Win",4}");
    foreach (var (c, i) in ranked.Take(30).Select((c, i) => (c, i)))
        Console.WriteLine($"  {i + 1,-3} {c.StrategyName,-22} {c.Symbol,-10} {c.Timeframe,-4} {c.OutOfSampleSharpe,6:F2} {c.InSampleSharpe,6:F2} {c.TotalTrades,5} {c.Windows,4}");

    File.WriteAllText(resultsPath, JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true }));

    // Holdout rapido dei top-15 (Sharpe out-of-sample sul periodo mai visto).
    Console.WriteLine($"\n=== HOLDOUT top-15 ({holdoutFrom:yyyy-MM-dd} -> {holdoutTo:yyyy-MM-dd}) ===");
    var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    Console.WriteLine($"  {"Strategia",-22} {"Symbol",-10} {"TF",-4} {"HoldSh",7} {"HoldRet",8} {"Trd",5}");
    foreach (var c in ranked.Where(c => c.OutOfSampleSharpe > 0.3m && c.TotalTrades >= 10).Take(15))
    {
        try
        {
            var hold = await engine.RunBacktestAsync(new BacktestConfiguration
            {
                ExchangeName = "Binance", Symbol = c.Symbol, Timeframe = c.Timeframe,
                From = holdoutFrom, To = holdoutTo, InitialCapital = 10_000m,
                PositionSizePercent = 20m, FeePercent = 0.1m, SlippagePercent = 0.05m,
                StrategyName = c.StrategyName, StrategyParameters = new Dictionary<string, decimal>(c.Parameters),
            }, CancellationToken.None);
            var sh = Statistics.SharpeRatio(hold.EquityCurve, Statistics.PeriodsPerYear(c.Timeframe));
            Console.WriteLine($"  {c.StrategyName,-22} {c.Symbol,-10} {c.Timeframe,-4} {sh,7:F2} {hold.TotalReturnPercent,7:F1}% {hold.TotalTrades,5}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {c.StrategyName,-22} {c.Symbol,-10} {c.Timeframe,-4} -> ERRORE: {ex.Message}");
        }
    }
    Console.WriteLine($"=== DISCOVER completata -> {resultsPath} ===");
}

async Task<List<DiscoveryCandidate>> RunDiscovery(
    IStrategyDiscovery discovery, List<string> tfs, DateTime from, int isM, int oosM, int stepM, string label)
{
    Console.WriteLine($"\n  -- Run {label}: {allSymbols.Length} coppie, da {from:yyyy-MM-dd}, WF {isM}/{oosM}/{stepM}");
    var config = new StrategyDiscoveryConfiguration
    {
        ExchangeName = "Binance",
        Symbols = [.. allSymbols],
        Timeframes = tfs,
        Strategies = [],
        From = from,
        To = selectionTo,
        TopN = 80,
        WalkForward = new WalkForwardConfiguration { InSampleMonths = isM, OutOfSampleMonths = oosM, StepMonths = stepM },
    };
    var progress = new Progress<DiscoveryProgress>(p =>
    {
        if (p.Completed % 100 == 0) Console.WriteLine($"     ... {p.Completed}/{p.Total} job, best OOS {p.BestSharpeSoFar:F2}");
    });
    var result = await discovery.DiscoverAsync(config, progress, CancellationToken.None);
    Console.WriteLine($"     Run {label}: {result.CombinationsTested} combos, {result.Candidates.Count} candidati");
    return result.Candidates;
}

sealed class PassthroughEncryption : ProcioneMGR.Services.Security.IEncryptionService
{
    public string Encrypt(string plaintext) => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}
