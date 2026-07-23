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
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.PairsTrading;
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
    case "expand2": await Expand2Async(); break;
    case "hunt": await HuntAsync(); break;
    case "deploy": await DeployAsync(); break;
    case "holdout": await HoldoutAsync(); break;
    case "pairs": await PairsAsync(); break;
    case "control": await ControlAsync(); break;
    case "costfrontier": await CostFrontierAsync(); break;
    case "makerfill": await MakerFillAsync(); break;
    case "xsection": await CrossSectionAsync(); break;
    case "voloverlay": await VolOverlayAsync(); break;
    case "volsingle": await VolSingleAsync(); break;
    case "volrobust": await VolRobustAsync(); break;
    case "coverage": await CoverageAsync(); break;
    case "lanes": await LanesAsync(args.Length > 1 && args[1].Equals("clean", StringComparison.OrdinalIgnoreCase)); break;
    case "discover": await DiscoverAsync(); break;
    default: Console.WriteLine($"Fase sconosciuta '{phase}'. Usa: stats | ingest | ingest1m | costprofile | expand2 | hunt | discover"); break;
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

// ------------------------------------------------------------------ LANES (ispeziona e, su richiesta, svuota)
// `lanes`        -> sola LETTURA: cosa c'e' su ogni corsia.
// `lanes clean`  -> svuota le corsie SPERIMENTALI, cioe' solo quelle su cui questa sessione ha
//                   schierato qualcosa. La 0 (storico dell'utente) e la 2 (Testnet) non si toccano:
//                   il permesso a "svuotare le corsie" riguarda il disordine lasciato dagli
//                   esperimenti, non i dati di chi la piattaforma la usa davvero.
async Task LanesAsync(bool clean)
{
    // Corsie che questa sessione ha usato per gli esperimenti. Tenerlo esplicito, e non "tutte",
    // e' cio' che rende l'operazione reversibile nella pratica: si sa esattamente cosa sparisce.
    int[] experimental = [1];

    var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    Console.WriteLine(clean ? "=== CORSIE — svuotamento delle sole sperimentali ===\n" : "=== CORSIE — stato attuale ===\n");

    await using var db = await dbFactory.CreateDbContextAsync();
    for (var lane = 0; lane < 3; lane++)
    {
        var st = await db.TradingEngineStates.AsNoTracking().FirstOrDefaultAsync(s => s.LaneId == lane);
        var row = await db.EnsembleStates.AsNoTracking().Where(e => e.LaneId == lane).OrderBy(e => e.Id).FirstOrDefaultAsync();
        var cfg = row is null || string.IsNullOrWhiteSpace(row.ConfigurationJson)
            ? new EnsembleConfiguration()
            : JsonSerializer.Deserialize<EnsembleConfiguration>(row.ConfigurationJson, json) ?? new();

        var trades = await db.TradeRecords.CountAsync(t => t.LaneId == lane);
        var orders = await db.Orders.CountAsync(o => o.LaneId == lane);
        var positions = await db.OpenPositions.CountAsync(p => p.LaneId == lane);
        var tag = experimental.Contains(lane) ? "SPERIMENTALE" : "dell'utente";

        Console.WriteLine($"  Corsia {lane} ({tag})");
        Console.WriteLine($"    config:   {(string.IsNullOrEmpty(cfg.Symbol) ? "(vuota)" : cfg.Symbol)} {cfg.Timeframe}, " +
                          $"{cfg.Strategies.Count} strategie, profilo '{cfg.RiskProfileName ?? "-"}'");
        Console.WriteLine($"    motore:   {(st is null ? "mai avviato" : $"{st.Mode}, running={st.IsRunning}, emergency={st.IsEmergencyStopped}")}");
        Console.WriteLine($"    dati:     {trades} trade, {orders} ordini, {positions} posizioni aperte");

        if (!clean || !experimental.Contains(lane)) { Console.WriteLine(); continue; }

        // Si ferma il motore PRIMA di togliergli i dati sotto i piedi, e si azzera anche la sua
        // CONTABILITA'.
        //
        // Non farlo e' stato un errore vero, visto solo guardando la pagina: cancellando trade e
        // ordini ma lasciando AvailableCapital/RealizedPnl/PeakEquity, la corsia mostrava
        // "0 operazioni" accanto a "+710,02 di PnL" e un drawdown del 6,37%. Cioe' proprio quel
        // tipo di stato contabile incoerente che LaneInvariantWatchdog esiste per intercettare e
        // che, trovato dal vivo, manderebbe una corsia in quarantena.
        if (st is not null)
        {
            var live = await db.TradingEngineStates.FirstAsync(s => s.LaneId == lane);
            live.IsRunning = false;
            live.IsEmergencyStopped = false;
            live.EmergencyStopReason = null;
            live.AvailableCapital = live.TotalCapital;
            live.RealizedPnl = 0m;
            live.PeakEquity = live.TotalCapital;
            live.MaxDrawdownPercent = 0m;
            live.DailyPnl = 0m;
            live.DailyAnchorUtc = DateTime.UtcNow;
            live.StartedAtUtc = null;
            live.LastOrderUtc = null;
        }
        var delPos = await db.OpenPositions.Where(p => p.LaneId == lane).ExecuteDeleteAsync();
        var delOrd = await db.Orders.Where(o => o.LaneId == lane).ExecuteDeleteAsync();
        var delTrd = await db.TradeRecords.Where(t => t.LaneId == lane).ExecuteDeleteAsync();

        var empty = JsonSerializer.Serialize(new EnsembleConfiguration(), json);
        var ens = await db.EnsembleStates.Where(e => e.LaneId == lane).OrderBy(e => e.Id).FirstOrDefaultAsync();
        if (ens is not null) { ens.ConfigurationJson = empty; ens.StatusJson = "{}"; ens.LastUpdatedUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync();

        Console.WriteLine($"    -> SVUOTATA: {delPos} posizioni, {delOrd} ordini, {delTrd} trade rimossi; config azzerata; motore fermo.\n");
    }

    if (!clean)
    {
        Console.WriteLine("  (sola lettura — usa `lanes clean` per svuotare le sole corsie sperimentali)");
    }
}

// ------------------------------------------------------------------ COST FRONTIER (quanto deve costare eseguire)
// La ricerca di segnali migliori e' esaurita (docs/REPORT-RICERCA-2026-07.md). Ma il report indica
// una leva che non e' stata ancora misurata, e che agisce sull'altro lato dell'equazione: il COSTO
// di esecuzione. Il caso PriceSmaCross DOGE 4h e' emblematico — fuori campione faceva lordo +7,74%
// e netto -7,57%: il segnale funzionava, l'hanno ucciso le commissioni.
//
// Domanda precisa a cui questa fase risponde: a quale livello di costo ciascun candidato passerebbe
// da perdente a vincente? Il numero che ne esce non e' un'opinione su quale exchange usare, e' il
// requisito di esecuzione che la strategia impone — e dice se sia raggiungibile o fantascienza.
//
// I livelli non sono inventati: corrispondono a modi reali di eseguire.
async Task CostFrontierAsync()
{
    var huntPath = Path.Combine(AppContext.BaseDirectory, "hunt-results.json");
    if (!File.Exists(huntPath))
    {
        Console.WriteLine("Nessun risultato di caccia da analizzare. Lancia prima 'hunt'.");
        return;
    }

    var candidates = JsonSerializer.Deserialize<List<DiscoveryCandidate>>(File.ReadAllText(huntPath))!
        .OrderByDescending(c => c.OutOfSampleSharpe).Take(6).ToList();

    // (etichetta, fee per lato %, slippage per fill %) — scenari di esecuzione realmente disponibili.
    var scenarios = new (string Label, decimal Fee, decimal Slip)[]
    {
        ("taker Binance (base)",       0.100m, 0.050m),
        ("taker Bitget",               0.060m, 0.050m),
        // TETTO DELLO SLICING. Gli algoritmi TWAP/VWAP/Iceberg/Adaptive riducono l'impatto di
        // mercato, cioe' lo SLIPPAGE — non la commissione, che dipende dall'essere maker o taker.
        // E oggi l'intero percorso live piazza solo ordini MARKET (SignalOrderBuilder,
        // PositionOpener, PositionCloser), quindi e' sempre taker. Questi due scenari sono il
        // limite teorico dello slicing: slippage azzerato, commissione taker invariata.
        ("taker + slicing perfetto",   0.100m, 0.000m),
        ("taker Bitget + slicing",     0.060m, 0.000m),
        ("maker Binance +BNB",         0.0225m, 0.020m),
        ("maker Bitget",               0.020m, 0.020m),
        ("costo zero (limite)",        0.000m, 0.000m),
    };

    Console.WriteLine($"=== FRONTIERA DEI COSTI — holdout {holdoutFrom:yyyy-MM-dd} -> oggi ===");
    Console.WriteLine("    Quanto dovrebbe costare eseguire perche' questi candidati smettano di perdere?\n");
    Console.WriteLine("    NB: il maker non e' gratis in senso pratico — un ordine limite puo' non essere");
    Console.WriteLine("    eseguito, e una strategia che INSEGUE il prezzo non puo' fare il maker per");
    Console.WriteLine("    definizione. Questi numeri dicono cosa servirebbe, non che sia gratuito ottenerlo.\n");

    using var scope = provider.CreateScope();
    var backtest = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();

    foreach (var c in candidates)
    {
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == c.Symbol && o.Timeframe == c.Timeframe && o.TimestampUtc >= holdoutFrom)
                .OrderBy(o => o.TimestampUtc).ToListAsync();
        }
        if (candles.Count < 200) continue;

        Console.WriteLine($"  {c.StrategyName} {c.Symbol} {c.Timeframe}");
        Console.WriteLine($"    {"scenario",-22} {"round-turn",11} {"netto",9} {"costi",8}");

        decimal? breakEvenRt = null;
        foreach (var s in scenarios)
        {
            var cfg = new BacktestConfiguration
            {
                ExchangeName = "Binance", Symbol = c.Symbol, Timeframe = c.Timeframe,
                From = holdoutFrom, To = DateTime.UtcNow,
                InitialCapital = 10_000m, PositionSizePercent = 100m,
                FeePercent = s.Fee, SlippagePercent = s.Slip,
                StrategyName = c.StrategyName, StrategyParameters = new(c.Parameters),
            };
            var r = await backtest.RunBacktestAsync(cfg, candles, factory.Create(c.StrategyName), CancellationToken.None);
            var rt = 2m * (s.Fee + s.Slip);
            if (r.TotalReturnPercent > 0m && breakEvenRt is null) breakEvenRt = rt;

            var mark = r.TotalReturnPercent > 0m ? "+" : " ";
            Console.WriteLine($"  {mark} {s.Label,-22} {rt,10:F3}% {r.TotalReturnPercent,8:F2}% {r.CostDragPercent,7:F2}%");
        }

        Console.WriteLine(breakEvenRt is decimal be
            ? $"    -> diventa profittevole a round-turn <= {be:F3}%\n"
            : "    -> resta in perdita anche a COSTO ZERO: non e' un problema di esecuzione, il segnale non funziona\n");
    }
}

// ------------------------------------------------------------------ COVERAGE (T0.0 roadmap macchina-ricerca)
// L'audit che viene PRIMA di qualunque "spremiamo i dati": cosa c'e' davvero in casa, con quali
// buchi, e quali episodi storici nominabili sono coperti. Read-only, sicuro con l'app accesa.
// L'output alimenta la sezione "Inventario" di docs/ROADMAP-MACCHINA-RICERCA.md.
async Task CoverageAsync()
{
    await using var db = await dbFactory.CreateDbContextAsync();

    static int TfMinutes(string tf) => tf switch
    {
        "1m" => 1, "5m" => 5, "15m" => 15, "30m" => 30, "1h" => 60, "4h" => 240, "1d" => 1440, _ => 0,
    };

    Console.WriteLine("=== T0.0 AUDIT DI COPERTURA DATI ===\n");

    // --- OHLCV -------------------------------------------------------------------------------
    var ohlcv = await db.OhlcvData.AsNoTracking()
        .GroupBy(o => new { o.Symbol, o.Timeframe })
        .Select(g => new
        {
            g.Key.Symbol,
            g.Key.Timeframe,
            N = g.Count(),
            From = g.Min(o => o.TimestampUtc),
            To = g.Max(o => o.TimestampUtc),
            ZeroVol = g.Count(o => o.Volume == 0m),
        })
        .ToListAsync();

    Console.WriteLine($"--- OHLCV: {ohlcv.Count} serie, {ohlcv.Sum(x => (long)x.N):N0} candele totali ---");
    Console.WriteLine("    Copertura = candele presenti / attese sull'intervallo [from, to]. Sotto il 99% c'e' un buco.\n");

    var perTf = ohlcv.GroupBy(x => x.Timeframe).OrderBy(g => TfMinutes(g.Key));
    foreach (var g in perTf)
    {
        var syms = g.Count();
        var candles = g.Sum(x => (long)x.N);
        var minFrom = g.Min(x => x.From);
        var maxTo = g.Max(x => x.To);
        Console.WriteLine($"  {g.Key,-4} {syms,4} simboli {candles,12:N0} candele   {minFrom:yyyy-MM-dd} -> {maxTo:yyyy-MM-dd}");
    }

    Console.WriteLine("\n    Serie con copertura < 99% o volume-zero anomalo:");
    var flagged = 0;
    foreach (var x in ohlcv.OrderBy(x => x.Symbol).ThenBy(x => TfMinutes(x.Timeframe)))
    {
        var mins = TfMinutes(x.Timeframe);
        if (mins == 0) continue;
        var expected = (x.To - x.From).TotalMinutes / mins + 1;
        var cov = expected > 0 ? x.N / expected : 0;
        var zeroPct = x.N > 0 ? (double)x.ZeroVol / x.N : 0;
        if (cov < 0.99 || zeroPct > 0.01)
        {
            Console.WriteLine($"      {x.Symbol,-11} {x.Timeframe,-4} copertura {cov,7:P1}  vol=0 {zeroPct,6:P1}  ({x.N:N0} candele, {x.From:yyyy-MM-dd} -> {x.To:yyyy-MM-dd})");
            flagged++;
        }
    }
    if (flagged == 0) Console.WriteLine("      nessuna: tutte le serie sono dense.");

    // --- Metriche sentiment ------------------------------------------------------------------
    var sm = await db.SentimentMetricPoints.AsNoTracking()
        .GroupBy(s => s.Metric)
        .Select(g => new
        {
            Metric = g.Key,
            N = g.Count(),
            Symbols = g.Select(s => s.Symbol).Distinct().Count(),
            From = g.Min(s => s.TimestampUtc),
            To = g.Max(s => s.TimestampUtc),
        })
        .ToListAsync();

    Console.WriteLine($"\n--- SentimentMetricPoints: {sm.Sum(x => (long)x.N):N0} punti ---");
    foreach (var x in sm.OrderBy(x => x.Metric))
    {
        var days = (x.To - x.From).TotalDays;
        Console.WriteLine($"  {x.Metric,-24} {x.N,8:N0} punti {x.Symbols,3} simboli   {x.From:yyyy-MM-dd} -> {x.To:yyyy-MM-dd}  (~{days:F0}gg)");
    }

    // --- Alt-data (eventi/notizie) -----------------------------------------------------------
    var alt = await db.AltDataPoints.AsNoTracking()
        .GroupBy(a => a.Category)
        .Select(g => new { Category = g.Key, N = g.Count(), From = g.Min(a => a.TimestampUtc), To = g.Max(a => a.TimestampUtc) })
        .ToListAsync();

    Console.WriteLine($"\n--- AltDataPoints (eventi/notizie): {alt.Sum(x => (long)x.N):N0} ---");
    foreach (var x in alt.OrderBy(x => x.Category))
        Console.WriteLine($"  {x.Category,-14} {x.N,7:N0}   {x.From:yyyy-MM-dd} -> {x.To:yyyy-MM-dd}");

    // --- Episodi storici nominabili ----------------------------------------------------------
    // Serve a T2 (event-study): quali episodi noti cadono DENTRO la storia OHLCV disponibile?
    // Elenco fermo a inizio 2025 (eventi ampiamente documentati); estendibile a mano.
    (string Label, DateTime When)[] episodes =
    [
        ("Crash COVID (Black Thursday)", new DateTime(2020, 3, 12)),
        ("Crash maggio 2021 (Cina/leva)", new DateTime(2021, 5, 19)),
        ("ATH di ciclo 2021", new DateTime(2021, 11, 10)),
        ("Collasso LUNA/UST", new DateTime(2022, 5, 9)),
        ("Collasso FTX", new DateTime(2022, 11, 8)),
        ("Crisi SVB / depeg USDC", new DateTime(2023, 3, 10)),
        ("Approvazione ETF spot BTC", new DateTime(2024, 1, 10)),
        ("Halving Bitcoin 2024", new DateTime(2024, 4, 20)),
        ("Crash yen carry-trade", new DateTime(2024, 8, 5)),
        ("Elezioni USA 2024", new DateTime(2024, 11, 5)),
    ];

    Console.WriteLine("\n--- Episodi nominabili coperti dall'OHLCV (riferimento BTC/USDT) ---");
    var btc = ohlcv.Where(x => x.Symbol == "BTC/USDT").ToList();
    foreach (var (label, when) in episodes)
    {
        var tfs = btc.Where(x => x.From <= when && when <= x.To).Select(x => x.Timeframe)
                     .OrderBy(TfMinutes).ToList();
        Console.WriteLine($"  {label,-34} {when:yyyy-MM-dd}  {(tfs.Count > 0 ? string.Join(",", tfs) : "NON coperto")}");
    }

    Console.WriteLine("\n    NB: la profondita' varia per timeframe (1d dal 2020, 4h dal 2022, 1h dal 2023,");
    Console.WriteLine("    5m/15m dal 2025, 1m da luglio 2025): gli event-study intraday sono possibili solo");
    Console.WriteLine("    per gli episodi recenti; quelli storici solo su 1d/4h.");
}

// ------------------------------------------------------------------ VOLROBUST (era rumore?)
// Due panieri hanno dato due risposte opposte sul dosaggio: +0,31 di Sharpe su 24 monete, -0,22 su
// 12. Con due campioni non si decide niente. Qui se ne generano CENTINAIA per randomizzazione:
// panieri casuali estratti dallo stesso universo, ognuno con il suo controllo a esposizione
// costante. Se l'effetto e' reale la distribuzione delle differenze e' spostata sopra lo zero; se
// era rumore e' centrata sullo zero, e i due risultati di partenza sono semplicemente le due code.
//
// E' la domanda "quel 0,43 era rumore?" posta in modo che i dati possano rispondere no.
async Task VolRobustAsync()
{
    const string tf = "1d";
    var from = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const decimal CostPerSide = 0.0015m;
    const double TargetVol = 0.30;
    const int Lookback = 30, Rebal = 30;
    const int Trials = 400;

    Console.WriteLine("=== IL DOSAGGIO E' UN EFFETTO O ERA RUMORE? (randomizzazione sui panieri) ===");
    Console.WriteLine($"    {Trials} panieri casuali, {tf}, da {from:yyyy-MM-dd}, target {TargetVol:P0}");
    Console.WriteLine("    Per ogni paniere: Sharpe dosato meno Sharpe dell'esposizione COSTANTE equivalente.\n");

    // Tutti i simboli con storia 1d sufficiente.
    List<string> all;
    await using (var db0 = await dbFactory.CreateDbContextAsync())
    {
        all = await db0.OhlcvData.AsNoTracking()
            .Where(o => o.Timeframe == tf && o.TimestampUtc >= from)
            .GroupBy(o => o.Symbol)
            .Where(g => g.Count() >= 1200)
            .Select(g => g.Key)
            .ToListAsync();
    }
    all.Sort();
    Console.WriteLine($"    Simboli disponibili con >= 1200 candele giornaliere: {all.Count}");
    if (all.Count < 15) { Console.WriteLine("    Universo troppo piccolo per randomizzare."); return; }

    var closes = new Dictionary<string, Dictionary<DateTime, decimal>>();
    await using (var db1 = await dbFactory.CreateDbContextAsync())
    {
        foreach (var s in all)
        {
            var rows = await db1.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == s && o.Timeframe == tf && o.TimestampUtc >= from)
                .OrderBy(o => o.TimestampUtc)
                .Select(o => new { o.TimestampUtc, o.Close }).ToListAsync();
            closes[s] = rows.ToDictionary(r => r.TimestampUtc.Date, r => r.Close);
        }
    }

    // Calendario comune a TUTTI: cosi' ogni paniere casuale gira sulle stesse date e i risultati
    // sono confrontabili fra loro.
    var cal = closes[all[0]].Keys.ToHashSet();
    foreach (var s in all) cal.IntersectWith(closes[s].Keys);
    var days = cal.OrderBy(d => d).ToList();
    Console.WriteLine($"    Giorni comuni a tutti: {days.Count} ({days[0]:yyyy-MM-dd} -> {days[^1]:yyyy-MM-dd})\n");
    if (days.Count < 400) { Console.WriteLine("    Storia comune insufficiente."); return; }

    double SharpeOf(List<double> rets)
    {
        if (rets.Count < 3) return 0;
        var m = rets.Average();
        var sd = Math.Sqrt(rets.Sum(v => (v - m) * (v - m)) / (rets.Count - 1));
        return sd > 1e-12 ? m / sd * Math.Sqrt(365.0) : 0;
    }

    (double Diff, double VtSh, double CtSh) Evaluate(List<string> basketSymbols)
    {
        var daily = new List<double>(days.Count);
        for (var i = 1; i < days.Count; i++)
        {
            var r = 0m;
            foreach (var s in basketSymbols) r += (closes[s][days[i]] / closes[s][days[i - 1]] - 1m) / basketSymbols.Count;
            daily.Add((double)r);
        }

        var path = new double[daily.Count];
        double cur = 0;
        for (var i = 0; i < daily.Count; i++)
        {
            if (i >= Lookback && (i - Lookback) % Rebal == 0)
            {
                var win = daily.Skip(i - Lookback).Take(Lookback).ToList();
                var m = win.Average();
                var sd = Math.Sqrt(win.Sum(v => (v - m) * (v - m)) / (win.Count - 1)) * Math.Sqrt(365.0);
                cur = sd > 1e-9 ? Math.Clamp(TargetVol / sd, 0.25, 1.0) : 0.0;
            }
            path[i] = cur;
        }

        var vtRets = new List<double>(daily.Count);
        double expSum = 0;
        for (var i = 0; i < daily.Count; i++) { vtRets.Add(path[i] * daily[i]); expSum += path[i]; }
        var avgExp = expSum / daily.Count;
        var ctRets = daily.Select(d => avgExp * d).ToList();

        var vtSh = SharpeOf(vtRets);
        var ctSh = SharpeOf(ctRets);
        return (vtSh - ctSh, vtSh, ctSh);
    }

    var rnd = new Random(20260720);   // seme fisso: l'esperimento e' riproducibile
    var diffs = new List<double>(Trials);
    foreach (var size in new[] { 8, 12, 20 })
    {
        var sub = new List<double>();
        for (var t = 0; t < Trials / 3; t++)
        {
            var pick = all.OrderBy(_ => rnd.Next()).Take(size).ToList();
            var e = Evaluate(pick);
            sub.Add(e.Diff); diffs.Add(e.Diff);
        }
        var pos = sub.Count(d => d > 0);
        Console.WriteLine($"    panieri da {size,2} simboli: differenza media {sub.Average(),7:F3}   positivi {pos,3}/{sub.Count}   " +
                          $"min {sub.Min(),6:F2}  max {sub.Max(),5:F2}");
    }

    var mean = diffs.Average();
    var positive = diffs.Count(d => d > 0);

    Console.WriteLine($"\n    --- Sensibilita' alla COMPOSIZIONE, su {diffs.Count} panieri ---");
    Console.WriteLine($"    differenza media di Sharpe (dosato - costante): {mean:F4}");
    Console.WriteLine($"    panieri con effetto POSITIVO                  : {positive}/{diffs.Count} ({(double)positive / diffs.Count:P0})");
    Console.WriteLine();
    Console.WriteLine("    ATTENZIONE a come si legge questo numero: NON e' una prova di robustezza, e");
    Console.WriteLine("    NON va accompagnato da una statistica t. Le cripto si muovono quasi tutte");
    Console.WriteLine("    insieme, quindi centinaia di panieri estratti dallo STESSO periodo non sono");
    Console.WriteLine("    centinaia di esperimenti indipendenti: sono un esperimento solo, ripetuto.");
    Console.WriteLine("    Dice soltanto che, dentro questa finestra, il risultato non dipende da QUALI");
    Console.WriteLine("    monete si scelgono. La domanda vera e' se dipenda dal PERIODO — qui sotto.");

    // --- La prova che conta: finestre TEMPORALI separate --------------------------------------
    // I risultati opposti ottenuti finora venivano da finestre diverse, non da universi diversi.
    // Qui l'universo resta fisso e si cambia solo il periodo, in blocchi non sovrapposti.
    Console.WriteLine("\n    --- Sensibilita' al PERIODO (universo fisso, finestre non sovrapposte) ---");
    Console.WriteLine($"    {"finestra",-26} {"giorni",7} {"dosato",8} {"costante",9} {"differenza",11}");

    (double VtSh, double CtSh) EvaluateRange(List<string> syms2, int i0, int i1)
    {
        var daily = new List<double>();
        for (var i = i0 + 1; i <= i1; i++)
        {
            var r = 0m;
            foreach (var s in syms2) r += (closes[s][days[i]] / closes[s][days[i - 1]] - 1m) / syms2.Count;
            daily.Add((double)r);
        }
        if (daily.Count < Lookback + Rebal * 2) return (0, 0);

        var path = new double[daily.Count];
        double cur = 0;
        for (var i = 0; i < daily.Count; i++)
        {
            if (i >= Lookback && (i - Lookback) % Rebal == 0)
            {
                var win = daily.Skip(i - Lookback).Take(Lookback).ToList();
                var m = win.Average();
                var sd = Math.Sqrt(win.Sum(v => (v - m) * (v - m)) / (win.Count - 1)) * Math.Sqrt(365.0);
                cur = sd > 1e-9 ? Math.Clamp(TargetVol / sd, 0.25, 1.0) : 0.0;
            }
            path[i] = cur;
        }
        var vt = new List<double>(); double expSum = 0;
        for (var i = 0; i < daily.Count; i++) { vt.Add(path[i] * daily[i]); expSum += path[i]; }
        var avg = expSum / daily.Count;
        return (SharpeOf(vt), SharpeOf(daily.Select(d => avg * d).ToList()));
    }

    var windowDays = 180;
    var perPeriod = new List<double>();
    for (var start = 0; start + windowDays < days.Count; start += windowDays)
    {
        var end = Math.Min(start + windowDays, days.Count - 1);
        var e = EvaluateRange(all, start, end);
        if (e.VtSh == 0 && e.CtSh == 0) continue;
        var d = e.VtSh - e.CtSh;
        perPeriod.Add(d);
        Console.WriteLine($"    {days[start]:yyyy-MM-dd} -> {days[end]:yyyy-MM-dd} {end - start,7} {e.VtSh,8:F2} {e.CtSh,9:F2} {d,11:F2}");
    }

    if (perPeriod.Count >= 2)
    {
        var pm = perPeriod.Average();
        var psd = Math.Sqrt(perPeriod.Sum(d => (d - pm) * (d - pm)) / (perPeriod.Count - 1));
        var pPos = perPeriod.Count(d => d > 0);
        Console.WriteLine($"\n    finestre temporali: {perPeriod.Count} | positive {pPos}/{perPeriod.Count} | media {pm:F3} | dev.std {psd:F3}");
        Console.WriteLine(pPos == perPeriod.Count
            ? "    -> l'effetto c'e' in OGNI periodo: e' la firma di un fenomeno statistico, non di un caso."
            : $"    -> l'effetto cambia segno da un periodo all'altro ({perPeriod.Count - pPos} negative su {perPeriod.Count}):\n"
            + "       dipende dal regime di mercato, non e' una proprieta' stabile su cui contare.");
        Console.WriteLine($"\n    NB: {perPeriod.Count} finestre da {windowDays} giorni su un solo ciclo cripto restano poche.");
        Console.WriteLine("    Servirebbe un mercato diverso (azionario, o un ciclo precedente) per decidere.");
    }
}

// ------------------------------------------------------------------ VOLSINGLE (il dosaggio su un simbolo solo)
// Sul PANIERE equipesato il dosaggio funziona, ma una parte del merito potrebbe essere la
// diversificazione: mediando 24 monete la volatilita' del paniere e' piu' stabile e piu' prevedibile
// di quella di una singola. Su un simbolo solo quella stampella non c'e', quindi l'effetto deve
// venire tutto dal dosaggio. E' il test che distingue "funziona" da "funziona perche' e' un paniere".
//
// Per ogni simbolo si confrontano TRE cose, non due: comprare e tenere, dosare, e — controllo
// decisivo — tenere un'esposizione COSTANTE pari a quella media del dosaggio.
async Task VolSingleAsync()
{
    string[] universe =
    [
        "BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT", "ADA/USDT",
        "LINK/USDT", "AVAX/USDT", "LTC/USDT", "DOT/USDT", "ATOM/USDT",
    ];
    const string tf = "1d";
    var from = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const decimal CostPerSide = 0.0015m;
    const double TargetVol = 0.30;
    const int Lookback = 30, Rebal = 30;

    Console.WriteLine("=== DOSAGGIO SU SINGOLO SIMBOLO (senza la stampella della diversificazione) ===");
    Console.WriteLine($"    {tf}, da {from:yyyy-MM-dd}, target {TargetVol:P0}, stima su {Lookback} barre, ribilancio {Rebal}g\n");
    Console.WriteLine($"    {"simbolo",-11} {"b&h rend",9} {"b&h Sh",7} | {"dosata",9} {"Sh",6} {"espos.",7} | {"costante",9} {"Sh",6}  esito");

    int migliora = 0, tot = 0;
    await using var db = await dbFactory.CreateDbContextAsync();

    foreach (var sym in universe)
    {
        var closes = await db.OhlcvData.AsNoTracking()
            .Where(o => o.Symbol == sym && o.Timeframe == tf && o.TimestampUtc >= from)
            .OrderBy(o => o.TimestampUtc).Select(o => o.Close).ToListAsync();
        if (closes.Count < 400) continue;

        var daily = new List<double>();
        for (var i = 1; i < closes.Count; i++)
            daily.Add(closes[i - 1] > 0m ? (double)(closes[i] / closes[i - 1] - 1m) : 0d);

        (double Ret, double Sh, double AvgExp) Simulate(Func<int, double> expo)
        {
            double eq = 1, expSum = 0, prev = 0;
            var rets = new List<double>();
            for (var i = 0; i < daily.Count; i++)
            {
                var e = expo(i);
                if (e != prev) { eq *= 1 - Math.Abs(e - prev) * (double)CostPerSide; prev = e; }
                var r = e * daily[i];
                eq *= 1 + r; rets.Add(r); expSum += e;
            }
            var m = rets.Average();
            var sd = Math.Sqrt(rets.Sum(v => (v - m) * (v - m)) / (rets.Count - 1));
            return ((eq - 1) * 100, sd > 1e-12 ? m / sd * Math.Sqrt(365.0) : 0, expSum / rets.Count);
        }

        // Esposizione dosata, causale: usa solo le barre passate.
        var path = new double[daily.Count];
        double cur = 0;
        for (var i = 0; i < daily.Count; i++)
        {
            if (i >= Lookback && (i - Lookback) % Rebal == 0)
            {
                var win = daily.Skip(i - Lookback).Take(Lookback).ToList();
                var m2 = win.Average();
                var sd2 = Math.Sqrt(win.Sum(v => (v - m2) * (v - m2)) / (win.Count - 1)) * Math.Sqrt(365.0);
                cur = sd2 > 1e-9 ? Math.Clamp(TargetVol / sd2, 0.25, 1.0) : 0.0;
            }
            path[i] = cur;
        }

        var bh = Simulate(_ => 1.0);
        var vt = Simulate(i => path[i]);
        var ct = Simulate(_ => vt.AvgExp);        // controllo a esposizione media pari

        tot++;
        var ok = vt.Sh > ct.Sh + 0.05;
        if (ok) migliora++;
        var verdict = ok ? "dosare aggiunge" : "nessun guadagno oltre l'esposizione ridotta";
        Console.WriteLine($"    {sym,-11} {bh.Ret,8:F1}% {bh.Sh,7:F2} | {vt.Ret,8:F1}% {vt.Sh,6:F2} {vt.AvgExp,6:P0} | {ct.Ret,8:F1}% {ct.Sh,6:F2}  {verdict}");
    }

    Console.WriteLine($"\n    Simboli in cui dosare batte l'esposizione costante equivalente: {migliora}/{tot}");
    Console.WriteLine("    Il confronto che conta e' con la COSTANTE, non con il buy&hold: battere il buy&hold");
    Console.WriteLine("    tenendo meno mercato in un periodo negativo e' automatico e non prova nulla.");

    // --- CONFRONTO PULITO: gli STESSI simboli, in paniere -------------------------------------
    // Se nessuno dei 12 mostra l'effetto ma il paniere sui 24 lo mostra fortissimo, la differenza
    // puo' venire dal PANIERE oppure dall'aver cambiato insieme di simboli e periodo. Qui si tiene
    // tutto fisso e si cambia una cosa sola: aggregare o no.
    Console.WriteLine("\n--- Gli STESSI simboli, aggregati in paniere equipesato (unica variabile: aggregare) ---");

    var perSymbol = new Dictionary<string, List<(DateTime T, decimal C)>>();
    foreach (var sym in universe)
    {
        var rows = await db.OhlcvData.AsNoTracking()
            .Where(o => o.Symbol == sym && o.Timeframe == tf && o.TimestampUtc >= from)
            .OrderBy(o => o.TimestampUtc)
            .Select(o => new { o.TimestampUtc, o.Close }).ToListAsync();
        if (rows.Count >= 400) perSymbol[sym] = rows.Select(r => (r.TimestampUtc.Date, r.Close)).ToList();
    }
    var syms = perSymbol.Keys.OrderBy(s => s).ToList();
    var maps = syms.ToDictionary(s => s, s => perSymbol[s].ToDictionary(v => v.T, v => v.C));
    var common = maps[syms[0]].Keys.ToHashSet();
    foreach (var s in syms) common.IntersectWith(maps[s].Keys);
    var days = common.OrderBy(d => d).ToList();

    var basket = new List<double>();
    for (var i = 1; i < days.Count; i++)
    {
        var r = 0m;
        foreach (var s in syms) r += (maps[s][days[i]] / maps[s][days[i - 1]] - 1m) / syms.Count;
        basket.Add((double)r);
    }

    (double Ret, double Sh, double AvgExp) Sim(Func<int, double> expo)
    {
        double eq = 1, expSum = 0, prev = 0;
        var rets = new List<double>();
        for (var i = 0; i < basket.Count; i++)
        {
            var e = expo(i);
            if (e != prev) { eq *= 1 - Math.Abs(e - prev) * (double)CostPerSide; prev = e; }
            var r = e * basket[i];
            eq *= 1 + r; rets.Add(r); expSum += e;
        }
        var m = rets.Average();
        var sd = Math.Sqrt(rets.Sum(v => (v - m) * (v - m)) / (rets.Count - 1));
        return ((eq - 1) * 100, sd > 1e-12 ? m / sd * Math.Sqrt(365.0) : 0, expSum / rets.Count);
    }

    var bpath = new double[basket.Count];
    double bcur = 0;
    for (var i = 0; i < basket.Count; i++)
    {
        if (i >= Lookback && (i - Lookback) % Rebal == 0)
        {
            var win = basket.Skip(i - Lookback).Take(Lookback).ToList();
            var m3 = win.Average();
            var sd3 = Math.Sqrt(win.Sum(v => (v - m3) * (v - m3)) / (win.Count - 1)) * Math.Sqrt(365.0);
            bcur = sd3 > 1e-9 ? Math.Clamp(TargetVol / sd3, 0.25, 1.0) : 0.0;
        }
        bpath[i] = bcur;
    }

    var bBh = Sim(_ => 1.0);
    var bVt = Sim(i => bpath[i]);
    var bCt = Sim(_ => bVt.AvgExp);
    Console.WriteLine($"    {"paniere " + syms.Count + " simboli",-24} rend {bBh.Ret,9:F1}%  Sharpe {bBh.Sh,5:F2}");
    Console.WriteLine($"    {"  dosato",-24} rend {bVt.Ret,9:F1}%  Sharpe {bVt.Sh,5:F2}  (esposizione media {bVt.AvgExp:P0})");
    Console.WriteLine($"    {"  costante equivalente",-24} rend {bCt.Ret,9:F1}%  Sharpe {bCt.Sh,5:F2}");
    Console.WriteLine(bVt.Sh > bCt.Sh + 0.05
        ? "    -> sugli stessi simboli, AGGREGARE fa comparire l'effetto: e' un fenomeno di paniere"
        : "    -> sugli stessi simboli l'effetto NON compare nemmeno in paniere: il risultato sui 24\n"
        + "       dipendeva dall'insieme di simboli o dal periodo, non dall'aggregazione");
}

// ------------------------------------------------------------------ VOLOVERLAY (il dosaggio salva le strategie?)
// Il dosaggio sulla volatilita' funziona sul PANIERE equipesato (docs/REPORT-DOSAGGIO-VOLATILITA.md).
// Domanda diversa e legittima: applicato SOPRA le strategie del catalogo — che l'holdout ha bocciato
// tutte — le recupera? La risposta onesta va misurata, non intuita: il dosaggio riduce l'esposizione,
// quindi riduce anche le PERDITE, ma non trasforma un segnale sbagliato in uno giusto.
async Task VolOverlayAsync()
{
    var huntPath = Path.Combine(AppContext.BaseDirectory, "hunt-results.json");
    if (!File.Exists(huntPath))
    {
        Console.WriteLine("Nessun risultato di caccia. Lancia prima 'hunt'.");
        return;
    }

    var candidates = JsonSerializer.Deserialize<List<DiscoveryCandidate>>(File.ReadAllText(huntPath))!
        .OrderByDescending(c => c.OutOfSampleSharpe).Take(6).ToList();

    Console.WriteLine($"=== IL DOSAGGIO RECUPERA LE STRATEGIE DEL CATALOGO? — holdout {holdoutFrom:yyyy-MM-dd} -> oggi ===");
    Console.WriteLine("    Stessa strategia, stessi costi: cambia solo se la size e' dosata sulla volatilita'.\n");
    Console.WriteLine($"    {"strategia",-18} {"coppia",-11} {"senza",9} {"dosata",9} {"b&h",9}  esito");

    using var scope = provider.CreateScope();
    var backtest = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();
    int rescued = 0, tested = 0;

    foreach (var c in candidates)
    {
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == c.Symbol && o.Timeframe == c.Timeframe && o.TimestampUtc >= holdoutFrom)
                .OrderBy(o => o.TimestampUtc).ToListAsync();
        }
        if (candles.Count < 200) continue;

        BacktestConfiguration Cfg(bool dosata) => new()
        {
            ExchangeName = "Binance", Symbol = c.Symbol, Timeframe = c.Timeframe,
            From = holdoutFrom, To = DateTime.UtcNow,
            InitialCapital = 10_000m, PositionSizePercent = 100m,
            FeePercent = PipelineCosts.DefaultFeePercent, SlippagePercent = PipelineCosts.DefaultSlippagePercent,
            StrategyName = c.StrategyName, StrategyParameters = new(c.Parameters),
            VolatilityTargeting = new VolatilityTargetingOptions
            {
                Enabled = dosata, TargetAnnualVolatilityPercent = 30m, LookbackBars = 30,
                MinExposureMultiplier = 0.25m, MaxExposureMultiplier = 1.0m,
            },
        };

        var plain = await backtest.RunBacktestAsync(Cfg(false), candles, factory.Create(c.StrategyName), CancellationToken.None);
        var scaled = await backtest.RunBacktestAsync(Cfg(true), candles, factory.Create(c.StrategyName), CancellationToken.None);
        var bh = candles[^1].Close / candles[0].Close - 1m;

        tested++;
        var ok = scaled.TotalReturnPercent > 0m && scaled.TotalReturnPercent > bh * 100m;
        if (ok) rescued++;
        var verdict = ok ? "RECUPERATA" : (scaled.TotalReturnPercent > plain.TotalReturnPercent ? "perde meno, ma perde" : "nessun miglioramento");
        Console.WriteLine($"    {c.StrategyName,-18} {c.Symbol,-11} {plain.TotalReturnPercent,8:F1}% {scaled.TotalReturnPercent,8:F1}% {bh * 100m,8:F1}%  {verdict}");
    }

    Console.WriteLine($"\n    Recuperate: {rescued}/{tested}");
    Console.WriteLine(rescued == 0
        ? "    Il dosaggio NON crea un edge dove non c'e': riduce l'esposizione, quindi riduce le\n"
        + "    perdite, ma un segnale sbagliato dosato resta un segnale sbagliato. E' gestione del\n"
        + "    rischio, non una fonte di rendimento — esattamente come dichiarato nel report."
        : "    Attenzione: un recupero va verificato fuori campione prima di crederci.");
}

// ------------------------------------------------------------------ XSECTION (momentum trasversale)
// Le cinque ricerche precedenti hanno provato tutte la stessa COSA: prevedere il singolo simbolo nel
// tempo (timing). Tutte negative, e il filo conduttore misurato in R2 e' che a ucciderle e' il
// TURNOVER, non la qualita' del segnale.
//
// Questo angolo e' strutturalmente diverso: non prevede se BTC sale, ma ordina l'universo per forza
// relativa e tiene i primi K. E' l'anomalia piu' documentata in letteratura (cross-sectional
// momentum), e soprattutto ha turnover BASSO per costruzione — si ribilancia ogni R giorni, non ad
// ogni segnale. Attacca cioe' esattamente la variabile che ha affossato tutto il resto.
//
// Disciplina anti-overfitting: i parametri (lookback, K, ribilanciamento) si scelgono SOLO sulla
// finestra di selezione; l'holdout viene calcolato una volta sola alla fine, sul parametro scelto.
async Task CrossSectionAsync()
{
    string[] universe =
    [
        "BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT", "ADA/USDT",
        "LINK/USDT", "AVAX/USDT", "LTC/USDT", "DOT/USDT", "ATOM/USDT", "BCH/USDT", "ETC/USDT",
        "XLM/USDT", "UNI/USDT", "AAVE/USDT", "ALGO/USDT", "ICP/USDT", "FIL/USDT",
        "NEAR/USDT", "TRX/USDT", "SHIB/USDT", "CRV/USDT",
    ];
    const string tf = "1d";
    var from = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Costo per LATO su ogni unita' di nozionale scambiata: fee taker + slippage.
    const decimal CostPerSide = 0.0015m;   // 0,10% fee + 0,05% slippage

    Console.WriteLine("=== MOMENTUM TRASVERSALE (cross-sectional) ===");
    Console.WriteLine($"    Universo {universe.Length} simboli, {tf}, da {from:yyyy-MM-dd}");
    Console.WriteLine($"    Selezione fino al {selectionTo:yyyy-MM-dd}, holdout dopo\n");
    Console.WriteLine("    Non prevede la direzione di un simbolo: ordina l'universo per forza relativa");
    Console.WriteLine("    e tiene i primi K. Turnover basso per costruzione — che e' il punto.\n");

    // --- Carico e allineo le serie su un calendario comune -----------------------------------
    var series = new Dictionary<string, Dictionary<DateTime, decimal>>();
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        foreach (var s in universe)
        {
            var rows = await db.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == s && o.Timeframe == tf && o.TimestampUtc >= from)
                .OrderBy(o => o.TimestampUtc)
                .Select(o => new { o.TimestampUtc, o.Close })
                .ToListAsync();
            if (rows.Count > 0) series[s] = rows.ToDictionary(r => r.TimestampUtc.Date, r => r.Close);
        }
    }

    // Calendario = date presenti in TUTTI i simboli (evita che l'ingresso tardivo di una moneta
    // cambi la composizione dell'universo a meta' strada, che sarebbe survivorship mascherato).
    var symbols = series.Keys.OrderBy(s => s).ToList();
    var calendar = series[symbols[0]].Keys.ToHashSet();
    foreach (var s in symbols) calendar.IntersectWith(series[s].Keys);
    var dates = calendar.OrderBy(d => d).ToList();

    Console.WriteLine($"    {symbols.Count} simboli con storia comune, {dates.Count} giorni allineati");
    Console.WriteLine($"    {dates[0]:yyyy-MM-dd} -> {dates[^1]:yyyy-MM-dd}\n");
    if (dates.Count < 400) { Console.WriteLine("    Storia comune insufficiente."); return; }

    // --- Il motore: una singola configurazione, valutata su un intervallo di date --------------
    // Ritorna (rendimento totale %, Sharpe annualizzato, maxDD %, turnover medio per ribilanciamento,
    //          costo totale in % del capitale, numero di ribilanciamenti)
    (decimal Ret, double Sharpe, decimal MaxDd, decimal Turn, decimal Cost, int Rebals) Run(
        int lookback, int topK, int rebalDays, bool marketNeutral, DateTime rFrom, DateTime rTo)
    {
        var idx = dates.Select((d, i) => (d, i)).Where(x => x.d >= rFrom && x.d <= rTo).ToList();
        if (idx.Count < lookback + rebalDays * 3) return (0m, 0d, 0m, 0m, 0m, 0);

        var equity = 1m;
        var peak = 1m;
        var maxDd = 0m;
        var daily = new List<double>();
        Dictionary<string, decimal> weights = new();   // pesi correnti (somma |w| <= 1)
        decimal totalCost = 0m, totalTurn = 0m;
        var rebals = 0;

        for (var k = 0; k < idx.Count; k++)
        {
            var (date, gi) = idx[k];
            if (gi < lookback) continue;

            // Ribilanciamento
            if (k % rebalDays == 0)
            {
                var scores = new List<(string S, decimal R)>();
                foreach (var s in symbols)
                {
                    var pNow = series[s][dates[gi]];
                    var pPast = series[s][dates[gi - lookback]];
                    if (pPast > 0m) scores.Add((s, pNow / pPast - 1m));
                }
                if (scores.Count >= topK * 2)
                {
                    var ranked = scores.OrderByDescending(x => x.R).ToList();
                    var target = new Dictionary<string, decimal>();
                    var wLong = 1m / topK * (marketNeutral ? 0.5m : 1m);
                    foreach (var t in ranked.Take(topK)) target[t.S] = wLong;
                    if (marketNeutral)
                        foreach (var b in ranked.TakeLast(topK)) target[b.S] = -wLong;

                    // Turnover = somma delle variazioni assolute di peso; il costo si paga su quello.
                    var turn = 0m;
                    foreach (var s in symbols)
                    {
                        var oldW = weights.TryGetValue(s, out var ow) ? ow : 0m;
                        var newW = target.TryGetValue(s, out var nw) ? nw : 0m;
                        turn += Math.Abs(newW - oldW);
                    }
                    var cost = turn * CostPerSide;
                    equity *= 1m - cost;
                    totalCost += cost;
                    totalTurn += turn;
                    rebals++;
                    weights = target;
                }
            }

            // Rendimento del giorno successivo sui pesi correnti
            if (k + 1 < idx.Count && weights.Count > 0)
            {
                var gNext = idx[k + 1].i;
                var r = 0m;
                foreach (var (s, w) in weights)
                {
                    var p0 = series[s][dates[gi]];
                    var p1 = series[s][dates[gNext]];
                    if (p0 > 0m) r += w * (p1 / p0 - 1m);
                }
                equity *= 1m + r;
                daily.Add((double)r);
                if (equity > peak) peak = equity;
                var dd = peak > 0m ? (peak - equity) / peak * 100m : 0m;
                if (dd > maxDd) maxDd = dd;
            }
        }

        var sharpe = 0d;
        if (daily.Count > 2)
        {
            var mean = daily.Average();
            var sd = Math.Sqrt(daily.Sum(v => (v - mean) * (v - mean)) / (daily.Count - 1));
            if (sd > 1e-12) sharpe = mean / sd * Math.Sqrt(365.0);
        }
        return ((equity - 1m) * 100m, sharpe, maxDd, rebals > 0 ? totalTurn / rebals : 0m, totalCost * 100m, rebals);
    }

    // --- FASE 1: scelta dei parametri, SOLO in selezione ---------------------------------------
    Console.WriteLine("--- Selezione (i parametri si scelgono qui, l'holdout non e' ancora toccato) ---");
    Console.WriteLine($"    {"lookback",8} {"topK",5} {"rebal",6} {"tipo",-8} {"rend",9} {"Sharpe",7} {"maxDD",7} {"turn",6} {"costi",7}");

    var grid = new List<(int L, int K, int R, bool MN, decimal Ret, double Sh, decimal Dd, decimal Tu, decimal Co)>();
    foreach (var L in new[] { 20, 30, 60, 90, 120 })
        foreach (var K in new[] { 3, 5, 8 })
            foreach (var R in new[] { 7, 14, 30 })
                foreach (var MN in new[] { false, true })
                {
                    var r = Run(L, K, R, MN, dates[0], selectionTo);
                    if (r.Rebals < 8) continue;
                    grid.Add((L, K, R, MN, r.Ret, r.Sharpe, r.MaxDd, r.Turn, r.Cost));
                }

    foreach (var g in grid.OrderByDescending(g => g.Sh).Take(12))
        Console.WriteLine($"    {g.L,8} {g.K,5} {g.R,6} {(g.MN ? "neutrale" : "long"),-8} {g.Ret,8:F1}% {g.Sh,7:F2} {g.Dd,6:F1}% {g.Tu,6:F2} {g.Co,6:F1}%");

    if (grid.Count == 0) { Console.WriteLine("    Nessuna configurazione valutabile."); return; }

    // --- FASE 2: UNA sola configurazione va all'holdout ----------------------------------------
    var best = grid.OrderByDescending(g => g.Sh).First();
    Console.WriteLine($"\n--- Holdout {selectionTo:yyyy-MM-dd} -> {dates[^1]:yyyy-MM-dd} (mai visto in selezione) ---");
    Console.WriteLine($"    Configurazione scelta: lookback {best.L}g, top {best.K}, ribilancio {best.R}g, {(best.MN ? "market neutral" : "solo long")}");

    var hold = Run(best.L, best.K, best.R, best.MN, selectionTo, dates[^1]);
    Console.WriteLine($"    selezione : rend {best.Ret,7:F1}%  Sharpe {best.Sh,5:F2}  maxDD {best.Dd,5:F1}%");
    Console.WriteLine($"    HOLDOUT   : rend {hold.Ret,7:F1}%  Sharpe {hold.Sharpe,5:F2}  maxDD {hold.MaxDd,5:F1}%  ({hold.Rebals} ribilanciamenti)");
    Console.WriteLine($"    costi totali in holdout: {hold.Cost:F2}% del capitale, turnover medio {hold.Turn:F2}x per ribilanciamento");

    // --- Riferimento: comprare e tenere l'universo equipesato ----------------------------------
    var bhStart = dates.FindIndex(d => d >= selectionTo);
    if (bhStart > 0)
    {
        var bh = 0m;
        foreach (var s in symbols) bh += (series[s][dates[^1]] / series[s][dates[bhStart]] - 1m) / symbols.Count;
        Console.WriteLine($"\n    Riferimento buy&hold equipesato sullo stesso holdout: {bh * 100m:F1}%");
        Console.WriteLine(hold.Ret > bh * 100m
            ? "    -> la strategia BATTE il riferimento passivo"
            : "    -> la strategia NON batte il semplice comprare e tenere: nessun motivo di usarla");
    }

    // ----------------------------------------------------------------- DOSAGGIO DELLA VOLATILITA'
    // Angolo diverso da tutti i precedenti: non prevede NIENTE. Tiene il paniere equipesato e regola
    // quanto capitale esporre per puntare a una volatilita' costante. Vedi docs/REPORT-DOSAGGIO-VOLATILITA.md
    Console.WriteLine("\n=== DOSAGGIO DELLA VOLATILITA' sul paniere equipesato ===");

    var bhDaily = new List<double>();
    for (var i = 1; i < dates.Count; i++)
    {
        var r = 0m;
        foreach (var s in symbols) r += (series[s][dates[i]] / series[s][dates[i - 1]] - 1m) / symbols.Count;
        bhDaily.Add((double)r);
    }

    (double Ret, double Sharpe, double MaxDd, double Cost, double AvgExp) VolTarget(double targetVol, int lookback, int rebal)
    {
        double eq = 1, peak = 1, maxDd = 0, cost = 0, exposure = 0, expSum = 0;
        var rets = new List<double>();
        for (var i = lookback; i < bhDaily.Count; i++)
        {
            if ((i - lookback) % rebal == 0)
            {
                var win = bhDaily.Skip(i - lookback).Take(lookback).ToList();
                var m = win.Average();
                var sd = Math.Sqrt(win.Sum(v => (v - m) * (v - m)) / (win.Count - 1)) * Math.Sqrt(365.0);
                var t = sd > 1e-9 ? Math.Clamp(targetVol / sd, 0.0, 1.0) : 0.0;
                var c = Math.Abs(t - exposure) * (double)CostPerSide;
                eq *= 1 - c; cost += c; exposure = t;
            }
            expSum += exposure;
            var r = exposure * bhDaily[i];
            eq *= 1 + r; rets.Add(r);
            if (eq > peak) peak = eq;
            var dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd;
        }
        var mean = rets.Average();
        var s2 = Math.Sqrt(rets.Sum(v => (v - mean) * (v - mean)) / (rets.Count - 1));
        return ((eq - 1) * 100, s2 > 1e-12 ? mean / s2 * Math.Sqrt(365.0) : 0, maxDd, cost * 100, expSum / rets.Count);
    }

    (double Ret, double Sharpe, double MaxDd) Constant(double exposure)
    {
        double eq = 1, peak = 1, maxDd = 0;
        var rets = new List<double>();
        foreach (var d in bhDaily)
        {
            var r = exposure * d;
            eq *= 1 + r; rets.Add(r);
            if (eq > peak) peak = eq;
            var dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd;
        }
        var m = rets.Average();
        var sd = Math.Sqrt(rets.Sum(v => (v - m) * (v - m)) / (rets.Count - 1));
        return ((eq - 1) * 100, sd > 1e-12 ? m / sd * Math.Sqrt(365.0) : 0, maxDd);
    }

    {
        double eq = 1, peak = 1, maxDd = 0;
        foreach (var r in bhDaily) { eq *= 1 + r; if (eq > peak) peak = eq; var dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd; }
        var m = bhDaily.Average();
        var sd = Math.Sqrt(bhDaily.Sum(v => (v - m) * (v - m)) / (bhDaily.Count - 1));
        Console.WriteLine($"    {"riferimento b&h equipesato",-32} rend {(eq - 1) * 100,8:F1}%  Sharpe {(sd > 1e-12 ? m / sd * Math.Sqrt(365.0) : 0),5:F2}  maxDD {maxDd,5:F1}%");
    }

    foreach (var tv in new[] { 0.30, 0.50, 0.70 })
    {
        var v = VolTarget(tv, 30, 30);
        Console.WriteLine($"    target {tv:P0} lookback 30g rebal 30g       rend {v.Ret,8:F1}%  Sharpe {v.Sharpe,5:F2}  maxDD {v.MaxDd,5:F1}%  costi {v.Cost,4:F1}%  espos.media {v.AvgExp:P0}");
    }

    // IL CONTROLLO: a parita' di esposizione MEDIA, il dosaggio aggiunge qualcosa o e' solo
    // "tenere meno mercato mentre scende"? Senza questo confronto il risultato non e' credibile.
    var vt = VolTarget(0.30, 30, 30);
    var ct = Constant(vt.AvgExp);
    Console.WriteLine("\n--- CONTROLLO: esposizione COSTANTE alla stessa media ---");
    Console.WriteLine($"    dosata  (media {vt.AvgExp:P0})   rend {vt.Ret,8:F1}%  Sharpe {vt.Sharpe,5:F2}  maxDD {vt.MaxDd,5:F1}%");
    Console.WriteLine($"    COSTANTE alla stessa media {vt.AvgExp:P0}   rend {ct.Ret,8:F1}%  Sharpe {ct.Sharpe,5:F2}  maxDD {ct.MaxDd,5:F1}%");
    Console.WriteLine(vt.Sharpe > ct.Sharpe + 0.05
        ? $"    -> lo Sharpe migliora a parita' di mercato tenuto ({ct.Sharpe:F2} -> {vt.Sharpe:F2}): conta QUANDO si e' esposti"
        : "    -> nessun guadagno oltre il tenere meno mercato: il dosaggio non aggiunge nulla");
}

// ------------------------------------------------------------------ MAKERFILL (il maker, ma davvero)
// La frontiera dei costi diceva che a commissioni maker alcuni candidati passano in positivo. Quel
// numero pero' assume che ogni ordine limite venga riempito al suo prezzo, che e' l'assunzione
// ottimistica per eccellenza: un limite passivo si riempie SOLO quando il mercato viene a
// prenderlo. Per un long appoggiato sotto il prezzo vuol dire riempirsi quando il prezzo scende —
// cioe' sproporzionatamente quando il segnale stava sbagliando. E' la selezione avversa.
//
// Questa fase rimisura gli stessi candidati con il modello di fill vero (EntryExecutionStyle.Maker)
// e mette le due colonne accanto: quanto prometteva il maker ideale, quanto ne resta.
async Task MakerFillAsync()
{
    var huntPath = Path.Combine(AppContext.BaseDirectory, "hunt-results.json");
    if (!File.Exists(huntPath))
    {
        Console.WriteLine("Nessun risultato di caccia da analizzare. Lancia prima 'hunt'.");
        return;
    }

    var candidates = JsonSerializer.Deserialize<List<DiscoveryCandidate>>(File.ReadAllText(huntPath))!
        .OrderByDescending(c => c.OutOfSampleSharpe).Take(6).ToList();

    // Commissioni Bitget, lo scenario piu' favorevole al maker fra quelli della frontiera.
    const decimal TakerFee = 0.060m, MakerFee = 0.020m, Slip = 0.050m;

    Console.WriteLine($"=== MAKER CON MODELLO DI FILL — holdout {holdoutFrom:yyyy-MM-dd} -> oggi ===");
    Console.WriteLine("    Il maker ideale assume fill garantito al prezzo limite. Qui il limite puo' NON");
    Console.WriteLine("    essere riempito: si misura quanto dell'edge promesso sopravvive.\n");
    Console.WriteLine("    NB: le USCITE restano taker in ogni scenario. Uno stop protettivo e' un ordine a");
    Console.WriteLine("    mercato per natura: non lo si puo' appoggiare al book e sperare che prenda.\n");

    using var scope = provider.CreateScope();
    var backtest = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();

    foreach (var c in candidates)
    {
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == c.Symbol && o.Timeframe == c.Timeframe && o.TimestampUtc >= holdoutFrom)
                .OrderBy(o => o.TimestampUtc).ToListAsync();
        }
        if (candles.Count < 200) continue;

        BacktestConfiguration Cfg() => new()
        {
            ExchangeName = "Binance", Symbol = c.Symbol, Timeframe = c.Timeframe,
            From = holdoutFrom, To = DateTime.UtcNow,
            InitialCapital = 10_000m, PositionSizePercent = 100m,
            StrategyName = c.StrategyName, StrategyParameters = new(c.Parameters),
        };

        // (a) taker puro: la linea di base.
        var takerCfg = Cfg();
        takerCfg.FeePercent = TakerFee;
        takerCfg.SlippagePercent = Slip;
        var taker = await backtest.RunBacktestAsync(takerCfg, candles, factory.Create(c.StrategyName), CancellationToken.None);

        // (b) maker IDEALE: come lo contava la frontiera — commissione maker su tutto, fill certo.
        var idealCfg = Cfg();
        idealCfg.FeePercent = MakerFee;
        idealCfg.SlippagePercent = 0.020m;
        var ideal = await backtest.RunBacktestAsync(idealCfg, candles, factory.Create(c.StrategyName), CancellationToken.None);

        Console.WriteLine($"  {c.StrategyName} {c.Symbol} {c.Timeframe}");
        Console.WriteLine($"    {"scenario",-34} {"netto",9} {"trade",7} {"fill",8}");
        Console.WriteLine($"    {"taker Bitget (base)",-34} {taker.TotalReturnPercent,8:F2}% {taker.TotalTrades,6}        -");
        Console.WriteLine($"    {"maker IDEALE (fill garantito)",-34} {ideal.TotalReturnPercent,8:F2}% {ideal.TotalTrades,6}        -");

        // (c) maker REALE, al variare di quanto passivo si mette il limite. Piu' e' passivo, meglio
        //     si entra quando si entra — e piu' spesso non si entra affatto.
        foreach (var offset in new[] { 0.05m, 0.10m, 0.25m })
        {
            foreach (var fallback in new[] { false, true })
            {
                var cfg = Cfg();
                cfg.FeePercent = TakerFee;          // le uscite (e l'eventuale fallback) restano taker
                cfg.MakerFeePercent = MakerFee;
                cfg.SlippagePercent = Slip;
                cfg.EntryExecution = EntryExecutionStyle.Maker;
                cfg.MakerOffsetPercent = offset;
                cfg.MakerMaxWaitBars = 3;
                cfg.MakerFallbackToTaker = fallback;

                var r = await backtest.RunBacktestAsync(cfg, candles, factory.Create(c.StrategyName), CancellationToken.None);
                var label = $"maker reale offset {offset:F2}% {(fallback ? "+fallback" : "no fallback")}";
                Console.WriteLine($"    {label,-34} {r.TotalReturnPercent,8:F2}% {r.TotalTrades,6} {r.MakerFillRate,7:F0}%"
                                + $"   (tentati {r.MakerEntriesAttempted}, presi {r.MakerEntriesFilled}, persi {r.MakerEntriesMissed})");
            }
        }
        Console.WriteLine();
    }
}

// ------------------------------------------------------------------ CONTROL (l'esperimento che mancava)
// Cinque angoli di ricerca, cinque esiti negativi. Ma "non abbiamo trovato niente" ammette DUE
// spiegazioni molto diverse, e finora non le avevo distinte:
//   (a) su questi dati non c'e' un edge sfruttabile al netto dei costi;
//   (b) la macchina che cerca e valida e' rotta, e non troverebbe un edge nemmeno se ci fosse.
//
// Questo e' l'esperimento di controllo. Si costruisce una serie sintetica con dentro un edge
// PIANTATO e conosciuto — un processo a ritorno verso la media, con ampiezza molto superiore ai
// costi — e si chiede alla stessa pipeline di trovarlo. Se lo trova, i cinque esiti negativi
// parlano del mercato; se non lo trova, parlavano dei nostri strumenti e vanno buttati.
//
// La serie e' generata con seme fisso: l'esito e' riproducibile.
async Task ControlAsync()
{
    const string symbol = "SYNTH/TEST";
    const string tf = "4h";
    var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    Console.WriteLine("=== CONTROLLO: la pipeline sa trovare un edge che ci abbiamo messo noi? ===\n");

    // Ornstein-Uhlenbeck attorno a una media lentamente crescente: il prezzo oscilla di circa
    // +/-4,6% attorno al proprio livello con emivita di poche barre. Un ritorno verso la media di
    // questa ampiezza e' enormemente sopra il round-turn dello 0,30%: se la pipeline funziona,
    // le strategie mean-reversion DEVONO trovarlo.
    var rng = new Random(20260720);
    var candles = new List<OhlcvData>();
    var x = 0.0;
    var mean = 100.0;
    var t = from;
    for (var i = 0; i < 4400; i++)
    {
        x = x * 0.90 + (rng.NextDouble() - 0.5) * 2 * 0.02;   // rumore uniforme, phi 0.90
        mean *= 1.00002;                                       // deriva lenta, non domina
        var close = mean * Math.Exp(x);
        var open = i == 0 ? close : (double)candles[^1].Close;
        var high = Math.Max(open, close) * 1.001;
        var low = Math.Min(open, close) * 0.999;
        candles.Add(new OhlcvData
        {
            Symbol = symbol, Timeframe = tf, TimestampUtc = t,
            Open = (decimal)open, High = (decimal)high, Low = (decimal)low, Close = (decimal)close,
            Volume = 1000m,
        });
        t = t.AddHours(4);
    }
    Console.WriteLine($"  Serie sintetica: {candles.Count} candele {tf} da {from:yyyy-MM-dd}, " +
                      $"oscillazione tipica +/-{Math.Sqrt(0.02 * 0.02 / 3 / (1 - 0.81)) * 100:F1}% attorno alla media");

    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        // Idempotente: si riparte sempre da zero, cosi' due esecuzioni non si sommano.
        await db.OhlcvData.Where(c => c.Symbol == symbol).ExecuteDeleteAsync();
        db.OhlcvData.AddRange(candles);
        await db.SaveChangesAsync();
    }

    try
    {
        using var scope = provider.CreateScope();
        var discovery = scope.ServiceProvider.GetRequiredService<IStrategyDiscovery>();
        var result = await discovery.DiscoverAsync(new StrategyDiscoveryConfiguration
        {
            ExchangeName = "Binance",
            Symbols = [symbol],
            Timeframes = [tf],
            Strategies = [],
            From = from,
            To = from.AddHours(4 * 4400),
            TopN = 10,
            WalkForward = new WalkForwardConfiguration { InSampleMonths = 8, OutOfSampleMonths = 2, StepMonths = 2 },
            // Costi ONESTI, gli stessi delle cacce reali: il controllo deve passare dalla stessa porta.
        }, null, CancellationToken.None);

        var ranked = result.Candidates.OrderByDescending(c => c.OutOfSampleSharpe).Take(6).ToList();
        Console.WriteLine($"\n  {result.CombinationsTested:N0} combinazioni provate, {result.Candidates.Count} candidati\n");
        Console.WriteLine($"  {"Strategia",-24} {"OOS",6} {"IS",6} {"Trade",6} {"DSR",6}");
        foreach (var c in ranked)
        {
            var dsr = c.Validation is null ? "—" : c.Validation.DeflatedSharpe.ToString("F2");
            Console.WriteLine($"  {c.StrategyName,-24} {c.OutOfSampleSharpe,6:F2} {c.InSampleSharpe,6:F2} {c.TotalTrades,6} {dsr,6}");
        }

        var best = ranked.FirstOrDefault();
        var significant = ranked.Count(c => c.Validation?.IsSignificant == true);
        Console.WriteLine();
        if (best is not null && best.OutOfSampleSharpe > 1.0m && significant > 0)
        {
            Console.WriteLine("  ESITO: la pipeline TROVA l'edge piantato, e il Deflated Sharpe lo dichiara");
            Console.WriteLine($"  significativo ({significant} candidati sopra 0,95). Gli strumenti funzionano:");
            Console.WriteLine("  i cinque esiti negativi sui dati reali parlano del MERCATO, non di un difetto.");
        }
        else if (best is not null && best.OutOfSampleSharpe > 1.0m)
        {
            Console.WriteLine($"  ESITO PARZIALE: l'edge viene trovato (Sharpe OOS {best.OutOfSampleSharpe:F2}) ma NESSUN");
            Console.WriteLine("  candidato supera il Deflated Sharpe. Il gate e' piu' severo del necessario: rischia di");
            Console.WriteLine("  scartare edge reali, e i risultati negativi precedenti vanno riletti con prudenza.");
        }
        else
        {
            Console.WriteLine("  ESITO NEGATIVO: la pipeline NON trova un edge che sappiamo esserci per costruzione.");
            Console.WriteLine("  Prima di qualunque altra ricerca va capito perche': tutti i risultati precedenti");
            Console.WriteLine("  sarebbero privi di valore informativo.");
        }
    }
    finally
    {
        // La serie sintetica non deve restare nel database di produzione, nemmeno se qualcosa lancia.
        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.OhlcvData.Where(c => c.Symbol == symbol).ExecuteDeleteAsync();
        Console.WriteLine($"\n  Pulizia: {removed:N0} candele sintetiche rimosse dal database.");
    }
}

// ------------------------------------------------------------------ PAIRS (angolo market-neutral)
// Angolo di ricerca NUOVO, dopo tre cacce a strategia singola tutte finite a zero.
//
// La motivazione non e' "proviamo qualcos'altro" ma una lettura dei risultati: nell'holdout ogni
// candidato perdeva, e nello stesso periodo DOGE faceva -25% e BCH -54%. Perdere in un mercato che
// crolla non distingue una strategia rotta da una strategia lunga: il pairs trading e' invece
// market-neutral per costruzione (lungo una gamba, corto l'altra), quindi risponde a una domanda
// diversa da quella che ha gia' avuto tre volte risposta negativa.
//
// Disciplina identica al resto: la cointegrazione si stima SOLO sulla finestra di selezione (fino a
// selectionTo) e il backtest si valuta SOLO sull'holdout, che quella selezione non ha mai visto.
// Cercare le coppie cointegrate sull'intero periodo e poi "validarle" al suo interno sarebbe
// esattamente l'errore che il Deflated Sharpe esiste per smascherare.
async Task PairsAsync()
{
    string[] universe =
    [
        "BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT", "DOGE/USDT", "ADA/USDT",
        "LINK/USDT", "AVAX/USDT", "LTC/USDT", "DOT/USDT", "ATOM/USDT", "BCH/USDT", "ETC/USDT",
        "XLM/USDT", "UNI/USDT", "AAVE/USDT", "ALGO/USDT", "ICP/USDT", "FIL/USDT",
    ];
    const string tf = "4h";
    var selFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    Console.WriteLine($"=== PAIRS su {universe.Length} simboli, {tf} ===");
    Console.WriteLine($"    Selezione (cointegrazione): {selFrom:yyyy-MM-dd} -> {selectionTo:yyyy-MM-dd}");
    Console.WriteLine($"    Holdout   (backtest):       {holdoutFrom:yyyy-MM-dd} -> oggi\n");

    // Serie della sola finestra di SELEZIONE, per stimare la cointegrazione.
    var sel = new Dictionary<string, List<OhlcvData>>();
    var hold = new Dictionary<string, List<OhlcvData>>();
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        foreach (var s in universe)
        {
            sel[s] = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == s && c.Timeframe == tf && c.TimestampUtc >= selFrom && c.TimestampUtc < selectionTo)
                .OrderBy(c => c.TimestampUtc).ToListAsync();
            hold[s] = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == s && c.Timeframe == tf && c.TimestampUtc >= holdoutFrom)
                .OrderBy(c => c.TimestampUtc).ToListAsync();
        }
    }

    var coint = new ProcioneMGR.Services.TimeSeries.EngleGrangerCointegrationTest();
    var found = new List<(string Y, string X, double Adf, double Crit, double Hedge)>();

    foreach (var y in universe)
    {
        foreach (var x in universe)
        {
            if (string.CompareOrdinal(y, x) >= 0) continue;              // ogni coppia una volta sola
            if (sel[y].Count < 500 || sel[x].Count < 500) continue;

            var (ay, ax) = PairsCandleAligner.Align(sel[y], sel[x]);
            if (ay.Count < 500) continue;

            var r = coint.Test([.. ay.Select(c => c.Close)], [.. ax.Select(c => c.Close)]);
            // IsTradeable, non IsCointegrated: scarta anche le coppie il cui spread e' stazionario
            // ma la cui elasticita' e' fuori banda (il caso AAVE/XLM).
            if (r.IsTradeable) found.Add((y, x, r.AdfStatistic, r.CriticalValue, r.HedgeRatio));
        }
    }

    var total = universe.Length * (universe.Length - 1) / 2;
    Console.WriteLine($"  Coppie operabili in selezione: {found.Count}/{total} ({(decimal)found.Count / total:P0})");
    if (found.Count > total * 0.25)
    {
        Console.WriteLine("  ATTENZIONE: una frazione cosi' alta di coppie 'cointegrate' e' sospetta. Su asset");
        Console.WriteLine("  cripto, che si muovono quasi tutti insieme, l'ADF tende a rifiutare la radice");
        Console.WriteLine("  unitaria per semplice co-movimento di mercato: e' il rilievo 'cointegrazione troppo");
        Console.WriteLine("  liberale' dell'audit 2026-07, e qui si vede in numeri.");
    }
    Console.WriteLine();

    // Le piu' fortemente cointegrate per margine sul valore critico (piu' negativo = piu' severo).
    var top = found.OrderBy(f => f.Adf - f.Crit).Take(8).ToList();
    var engine = new PairsBacktestEngine();
    var survivors = 0;

    foreach (var p in top)
    {
        if (hold[p.Y].Count < 100 || hold[p.X].Count < 100)
        {
            Console.WriteLine($"  {p.Y,-11}/{p.X,-11} SALTATA: holdout insufficiente.");
            continue;
        }

        var cfg = new PairsBacktestConfiguration
        {
            SymbolY = p.Y, SymbolX = p.X,
            InitialCapital = 10_000m, PositionSizePercent = 10m,
            FeePercent = PipelineCosts.DefaultFeePercent,
            SlippagePercent = PipelineCosts.DefaultSlippagePercent,
        };
        var r = engine.RunBacktest(hold[p.Y], hold[p.X], cfg);

        var ok = r.TotalReturnPercent > 0m && r.TotalTrades >= 5;
        if (ok) survivors++;
        Console.WriteLine($"  {(ok ? "OK " : "-- ")}{p.Y,-11}/{p.X,-11}  ADF {p.Adf,6:F2} (crit {p.Crit,6:F2})  hedge {p.Hedge,6:F2}");
        Console.WriteLine($"      HOLDOUT: netto {r.TotalReturnPercent,7:F2}%   trade {r.TotalTrades,3}   maxDD {r.MaxDrawdownPercent,5:F1}%");
    }

    Console.WriteLine($"\n=== SOPRAVVISSUTI: {survivors}/{top.Count} (netto > 0 e almeno 5 operazioni) ===");
}

// ------------------------------------------------------------------ HOLDOUT (test davvero fuori campione)
// Le due cacce condividono lo stesso periodo di selezione (fino a selectionTo = 2026-03-01), quindi
// il fatto che trovino lo stesso candidato dimostra robustezza allo split walk-forward, NON conferma
// out-of-sample. Qui si usa la finestra di holdout — dal 2026-03-01 in avanti — che nessuna delle
// due cacce ha mai visto: e' l'unico dato realmente vergine gia' disponibile.
//
// Il confronto e' contro un BUY-AND-HOLD sulla stessa finestra: uno Sharpe positivo non dice nulla
// se nello stesso periodo bastava comprare e stare fermi.
async Task HoldoutAsync()
{
    // I candidati arrivano dall'ULTIMA caccia, non da una lista scritta a mano: cosi' il ciclo
    // caccia -> holdout si chiude da solo e nessuno puo' dimenticarsi di aggiornare un array.
    var huntPath = Path.Combine(AppContext.BaseDirectory, "hunt-results.json");
    if (!File.Exists(huntPath))
    {
        Console.WriteLine($"Nessun risultato di caccia da validare ({huntPath} assente). Lancia prima la fase 'hunt'.");
        return;
    }

    var candidates = JsonSerializer.Deserialize<List<DiscoveryCandidate>>(File.ReadAllText(huntPath))!
        .OrderByDescending(c => c.OutOfSampleSharpe)
        .Take(12)
        .Select(c => (Strategy: c.StrategyName, Symbol: c.Symbol, Tf: c.Timeframe,
                      P: new Dictionary<string, decimal>(c.Parameters),
                      SelectionSharpe: c.OutOfSampleSharpe,
                      Dsr: c.Validation?.DeflatedSharpe ?? double.NaN))
        .ToArray();

    var from = holdoutFrom;
    var to = DateTime.UtcNow;
    Console.WriteLine($"=== HOLDOUT {from:yyyy-MM-dd} -> {to:yyyy-MM-dd} (mai usato in selezione) ===");
    Console.WriteLine($"    Costi: fee {PipelineCosts.DefaultFeePercent}%/lato + slippage {PipelineCosts.DefaultSlippagePercent}%/fill\n");

    using var scope = provider.CreateScope();
    var backtest = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();
    var survivors = 0;

    foreach (var c in candidates)
    {
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(o => o.Symbol == c.Symbol && o.Timeframe == c.Tf && o.TimestampUtc >= from && o.TimestampUtc <= to)
                .OrderBy(o => o.TimestampUtc)
                .ToListAsync();
        }
        if (candles.Count < 200)
        {
            Console.WriteLine($"  {c.Strategy,-18} {c.Symbol,-11} SALTATO: solo {candles.Count} candele.");
            continue;
        }

        var cfg = new PipelineCosts(
            PipelineCosts.DefaultSlippagePercent, PipelineCosts.DefaultFeePercent, PipelineCosts.DefaultFundingRatePercentPer8h)
            .ApplyTo(new BacktestConfiguration
            {
                ExchangeName = "Binance", Symbol = c.Symbol, Timeframe = c.Tf, From = from, To = to,
                InitialCapital = 10_000m, PositionSizePercent = 100m,
                StrategyName = c.Strategy, StrategyParameters = c.P,
            });

        var r = await backtest.RunBacktestAsync(cfg, candles, factory.Create(c.Strategy), CancellationToken.None);
        var ppy = ProcioneMGR.Services.Optimization.Statistics.PeriodsPerYear(c.Tf);
        var sharpe = ProcioneMGR.Services.Optimization.Statistics.SharpeRatio(r.EquityCurve, ppy);

        // Riferimento: comprare alla prima candela e non fare piu' nulla.
        var hold = (candles[^1].Close - candles[0].Close) / candles[0].Close * 100m;

        var beatsHold = r.TotalReturnPercent > hold;
        var survives = sharpe > 0.5m && beatsHold;
        if (survives) survivors++;

        Console.WriteLine($"  {(survives ? "OK " : "-- ")}{c.Strategy,-18} {c.Symbol,-11} {c.Tf}  su {candles.Count} candele");
        Console.WriteLine($"      selezione: Sharpe {c.SelectionSharpe,5:F2}  DSR {c.Dsr,5:F2}   ->   HOLDOUT: Sharpe {sharpe,6:F2}");
        Console.WriteLine($"      netto {r.TotalReturnPercent,7:F2}%   lordo {r.GrossReturnPercent,7:F2}%   costi {r.CostDragPercent,6:F2}%   " +
                          $"trade {r.TotalTrades,4}   maxDD {r.MaxDrawdownPercent,5:F1}%");
        Console.WriteLine($"      buy & hold {hold,7:F2}%  ->  {(beatsHold ? "BATTE" : "NON batte")} il comprare e stare fermi\n");
    }

    Console.WriteLine($"=== SOPRAVVISSUTI ALL'HOLDOUT: {survivors}/{candidates.Length} ===");
    Console.WriteLine("    (criterio: Sharpe fuori campione > 0,5 E meglio del buy-and-hold)");
    if (survivors == 0)
    {
        Console.WriteLine("    Nessuno. E' un esito informativo, non un fallimento: significa che cio' che la");
        Console.WriteLine("    selezione premiava non sopravvive a dati mai visti — esattamente cio' che il");
        Console.WriteLine("    Deflated Sharpe aveva gia' segnalato rifiutandoli.");
    }
}

// ------------------------------------------------------------------ DEPLOY (candidato -> corsia)
// Schiera UN candidato sulla corsia 1 in configurazione, senza avviare nulla: l'avvio resta
// un'azione esplicita dalla UI, come per PipelineApplier.
//
// PERCHÉ LA CORSIA 1 E NON LA 0 O LA 2: la 0 ha 159 operazioni di storico ed è quella che la
// pagina /bot governa; la 2 è in modalità Testnet. La 1 porta un esperimento fermo e senza trade
// (LTC/USDT, Sharpe -2,15), quindi è quella che si può sovrascrivere senza distruggere nulla.
//
// COSA QUESTO SCHIERAMENTO NON È: una validazione. Il candidato NON ha superato il Deflated Sharpe
// (0,69-0,81 contro una soglia di 0,95). Va in Paper come OSSERVAZIONE, per generare dati
// realmente nuovi in avanti nel tempo — l'unica cosa che può sciogliere l'ambiguità, visto che le
// due cacce che l'hanno trovato condividono lo stesso periodo storico e quindi non si confermano
// a vicenda quanto sembrerebbe. Il passaggio a Testnet resta comunque dietro settimane di
// evidenza in Paper (PromotionEvaluator), e quello a Live è sempre manuale.
async Task DeployAsync()
{
    const int laneId = 1;
    const string symbol = "DOGE/USDT";
    const string timeframe = "1h";

    // Lettura/scrittura diretta di EnsembleState: questo tool non compone le corsie keyed
    // (AddTradingLanes vive nell'app), e tirarsi dietro l'intero cono di dipendenze di
    // EnsembleManager per due campi non varrebbe la pena. Le opzioni JSON DEVONO combaciare con
    // quelle del manager (camelCase), altrimenti l'app rileggerebbe una configurazione vuota.
    var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    EnsembleConfiguration cfg;
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        var row = await db.EnsembleStates.Where(e => e.LaneId == laneId).OrderBy(e => e.Id).FirstOrDefaultAsync();
        cfg = row is null || string.IsNullOrWhiteSpace(row.ConfigurationJson)
            ? new EnsembleConfiguration()
            : JsonSerializer.Deserialize<EnsembleConfiguration>(row.ConfigurationJson, json) ?? new EnsembleConfiguration();
    }

    Console.WriteLine($"=== DEPLOY su corsia {laneId} ===");
    Console.WriteLine($"    Prima:  {cfg.Symbol} {cfg.Timeframe}, {cfg.Strategies.Count} strategie, profilo '{cfg.RiskProfileName ?? "(nessuno)"}'");

    // NESSUN bracket automatico e NESSUN profilo di rischio, e sono due scelte deliberate corrette
    // dopo un primo tentativo sbagliato (2026-07-20).
    //
    // Il primo deploy applicava, come fa PipelineApplier, uno stop/target dai percentili di
    // escursione (SL 0,68% / TP 1,98%) e il profilo Prudente. Osservato dal vivo: 430 ordini
    // RIFIUTATI su 500. Due cause che si sommavano, entrambe dovute allo scarto fra ciò che era
    // stato validato e ciò che era stato schierato:
    //
    //  1. lo stop allo 0,68% e' strettissimo per DOGE su candele orarie: la posizione veniva
    //     chiusa quasi subito, ma il segnale Supertrend restava valido e il motore ritentava
    //     l'ingresso alla candela successiva, all'infinito;
    //  2. il tetto di turnover del profilo Prudente (86.400s fra ingressi) rifiutava quei
    //     rientri, uno dopo l'altro.
    //
    // Il risultato era una strategia mutilata all'86%: qualunque cosa avesse fatto in Paper non
    // avrebbe detto NULLA sul candidato che la caccia aveva selezionato. La caccia ha validato
    // Supertrend con le SUE uscite e senza cap di frequenza: per osservarlo bisogna schierarlo
    // cosi', altrimenti si osserva un'altra cosa.
    //
    // Le protezioni non spariscono: restano le soglie globali (drawdown, perdita giornaliera,
    // esposizione) e l'emergency stop. Ed e' Paper, quindi denaro finto.
    cfg.ExchangeName = "Binance";
    cfg.Symbol = symbol;
    cfg.Timeframe = timeframe;
    cfg.IsFutures = false;
    cfg.Leverage = 1;
    cfg.TotalCapital = 10_000m;
    cfg.RiskProfileName = null;
    cfg.Strategies =
    [
        new EnsembleStrategy
        {
            StrategyName = "Supertrend",
            DisplayName = "Supertrend DOGE 1h (osservazione, non validato)",
            IsActive = true,
            CurrentAllocation = 100m,
            Parameters = new() { ["AtrPeriod"] = 14m, ["Multiplier"] = 3m, ["AllowShort"] = 1m },
            ExpectedSharpe = 1.58m,   // media delle due cacce: alimenta il monitor di decadimento
        },
    ];

    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        var row = await db.EnsembleStates.Where(e => e.LaneId == laneId).OrderBy(e => e.Id).FirstOrDefaultAsync();
        var payload = JsonSerializer.Serialize(cfg, json);
        if (row is null)
        {
            db.EnsembleStates.Add(new EnsembleState { LaneId = laneId, ConfigurationJson = payload, StatusJson = "{}", LastUpdatedUtc = DateTime.UtcNow });
        }
        else
        {
            row.ConfigurationJson = payload;
            row.LastUpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    Console.WriteLine($"    Dopo:   {cfg.Symbol} {cfg.Timeframe}, 1 strategia (Supertrend 14/3/short), profilo '{cfg.RiskProfileName}'");
    Console.WriteLine("    Uscite: quelle della strategia (nessun bracket aggiunto), nessun cap di turnover per corsia");
    Console.WriteLine("            -> in Paper gira COME e' stata validata dalla caccia. Restano soglie globali ed emergency stop.");
    Console.WriteLine("    NESSUN trading avviato: l'avvio in Paper e' un'azione esplicita dalla UI.");
}

// ------------------------------------------------------------------ HUNT (nuove + vecchie coppie)
// Caccia su TUTTO l'universo tracciato, letto dalla watchlist invece che da una lista fissa: le
// coppie appena aggiunte da expand2 entrano da sole, senza che nessuno si ricordi di aggiornare
// un array.
//
// Timeframe: 1d, 4h, 1h. Il 15m è escluso dalla caccia principale non per pigrizia ma perché R2
// ha misurato lì un cost drag del 9% contro il 3,4% di 1h, con un tetto strutturale doppio da
// superare — su un universo di 45 coppie conviene spendere le ore di calcolo dove l'edge netto ha
// più probabilità di sopravvivere. I dati 15m restano ingeriti e disponibili.
//
// Le candidature passano un doppio filtro: i gate anti-rumore del Report Caccia (Sharpe OOS e
// numero minimo di trade) e il Deflated Sharpe, che chiede se il migliore di N tentativi sia
// significativo DOPO aver corretto per il test multiplo. Con i costi ora onesti anche in
// selezione (R2), uno Sharpe che sopravvive qui è più credibile di quelli trovati prima.
async Task HuntAsync()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    List<string> symbols;
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        symbols = await db.TrackedSeries.AsNoTracking()
            .Where(t => t.Enabled)
            .Select(t => t.Symbol)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();
    }

    Console.WriteLine($"=== CACCIA su {symbols.Count} coppie (nuove + vecchie) ===");
    Console.WriteLine($"    Costi in SELEZIONE: fee {PipelineCosts.DefaultFeePercent}%/lato + slippage {PipelineCosts.DefaultSlippagePercent}%/fill\n");

    using var scope = provider.CreateScope();
    var discovery = scope.ServiceProvider.GetRequiredService<IStrategyDiscovery>();
    var all = new List<DiscoveryCandidate>();

    // Ogni ondata ha la propria finestra e il proprio walk-forward: più lento è il timeframe, più
    // lunga dev'essere la storia perché una finestra out-of-sample contenga abbastanza barre.
    // Variante "slow": solo 1d e 4h, con storia piu' profonda e finestre piu' lunghe. R2 ha
    // misurato li' il cost drag piu' basso (3,4% a 1h contro 24% a 5m e 77% a 1m), e i timeframe
    // lenti danno anche piu' anni di storia per lo stesso numero di barre — cioe' piu' regimi di
    // mercato diversi attraversati, che e' cio' che rende un edge credibile.
    //
    // Il primo giro (1d/4h/1h) e' finito con 6 candidati e ZERO significativi al Deflated Sharpe,
    // e i due migliori sono poi crollati nell'holdout (Sharpe -2,37 e -3,21, peggio del
    // buy-and-hold). Qui si cambia deliberatamente il PERIODO oltre che i timeframe: non serve
    // rifare la stessa domanda sugli stessi dati.
    var slowOnly = args.Length > 1 && args[1].Equals("slow", StringComparison.OrdinalIgnoreCase);
    var waves = slowOnly
        ? new (string Tf, DateTime From, int Is, int Oos, int Step)[]
          {
              ("1d", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 18, 6, 6),
              ("4h", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), 12, 4, 4),
          }
        : new (string Tf, DateTime From, int Is, int Oos, int Step)[]
          {
              ("1d", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), 12, 3, 3),
              ("4h", new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc), 8, 2, 2),
              ("1h", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6, 2, 2),
          };

    foreach (var w in waves)
    {
        var waveSw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"\n  -- Ondata {w.Tf}: da {w.From:yyyy-MM-dd}, walk-forward {w.Is}/{w.Oos}/{w.Step} mesi");
        var config = new StrategyDiscoveryConfiguration
        {
            ExchangeName = "Binance",
            Symbols = symbols,
            Timeframes = [w.Tf],
            Strategies = [],                       // vuoto = tutte le strategie disponibili
            From = w.From,
            To = selectionTo,
            TopN = 60,
            WalkForward = new WalkForwardConfiguration { InSampleMonths = w.Is, OutOfSampleMonths = w.Oos, StepMonths = w.Step },
            // CommissionPercent e SlippagePercent restano ai default onesti di R2.
        };

        var progress = new Progress<DiscoveryProgress>(p =>
        {
            if (p.Completed % 50 == 0) Console.WriteLine($"     ... {p.Completed}/{p.Total} job, miglior OOS finora {p.BestSharpeSoFar:F2}");
        });

        try
        {
            var result = await discovery.DiscoverAsync(config, progress, CancellationToken.None);
            all.AddRange(result.Candidates);
            Console.WriteLine($"     Ondata {w.Tf}: {result.CombinationsTested:N0} combinazioni, {result.Candidates.Count} candidati in {waveSw.Elapsed.TotalMinutes:F1} min");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     Ondata {w.Tf} FALLITA: {ex.Message}");
        }
    }

    // Gate anti-rumore: uno Sharpe alto con pochi trade è rumore, non edge.
    const decimal minOosSharpe = 0.5m;
    const int minTrades = 20;
    var kept = all
        .Where(c => c.OutOfSampleSharpe >= minOosSharpe && c.TotalTrades >= minTrades)
        .OrderByDescending(c => c.OutOfSampleSharpe)
        .ToList();

    Console.WriteLine($"\n=== ESITO: {all.Count} candidati grezzi -> {kept.Count} oltre i gate " +
                      $"(Sharpe OOS >= {minOosSharpe}, trade >= {minTrades}) in {sw.Elapsed.TotalMinutes:F1} min ===\n");

    if (kept.Count == 0)
    {
        Console.WriteLine("  Nessun candidato ha superato i gate. Con i costi onesti in selezione è un esito");
        Console.WriteLine("  possibile e informativo: significa che su questo universo e queste finestre non");
        Console.WriteLine("  c'è un edge che sopravviva alle commissioni.");
        return;
    }

    Console.WriteLine($"  {"#",-3} {"Strategia",-24} {"Coppia",-13} {"TF",-4} {"OOS",6} {"IS",6} {"Trade",6} {"DSR",6}");
    foreach (var (c, i) in kept.Take(40).Select((c, i) => (c, i)))
    {
        var dsr = c.Validation is null ? "—" : c.Validation.DeflatedSharpe.ToString("F2");
        Console.WriteLine($"  {i + 1,-3} {c.StrategyName,-24} {c.Symbol,-13} {c.Timeframe,-4} " +
                          $"{c.OutOfSampleSharpe,6:F2} {c.InSampleSharpe,6:F2} {c.TotalTrades,6} {dsr,6}");
    }

    // Quanti sopravvivono anche al Deflated Sharpe: è il conteggio che dice se abbiamo trovato
    // qualcosa o se abbiamo solo pescato il massimo di una distribuzione di rumore.
    var significant = kept.Where(c => c.Validation?.IsSignificant == true).ToList();
    Console.WriteLine($"\n  Significativi al Deflated Sharpe (DSR > 0.95): {significant.Count}/{kept.Count}");

    var outPath = Path.Combine(AppContext.BaseDirectory, "hunt-results.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\n  Dettaglio: {outPath}");
}

// ------------------------------------------------------------------ EXPAND2 (nuove coppie)
// Seconda espansione dell'universo: 15 coppie liquide non ancora tracciate, sui timeframe che R2
// ha indicato come economicamente sostenibili.
//
// NIENTE 5m e NIENTE 1m di proposito. R2 ha misurato un cost drag del 24% a 5m e del 77% a 1m
// contro il 3,4% a 1h sulla stessa finestra: allargare l'universo proprio dove i costi divorano
// l'edge significherebbe moltiplicare i dati e le ore di ricerca per esplorare la parte dello
// spazio che sappiamo già essere la peggiore. Si scende a 15m come estremo veloce, non oltre.
async Task Expand2Async()
{
    // Coppie liquide su Binance non presenti nell'universo attuale di 30.
    string[] wave2 =
    [
        "BCH/USDT", "ETC/USDT", "XLM/USDT", "VET/USDT", "RUNE/USDT",
        "LDO/USDT", "CRV/USDT", "MKR/USDT", "IMX/USDT", "STX/USDT",
        "FET/USDT", "RENDER/USDT", "ONDO/USDT", "JUP/USDT", "WIF/USDT",
    ];

    var plan = new List<(string Tf, DateTime From)>
    {
        ("1d", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("4h", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("1h", new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("15m", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
    };

    var sw = System.Diagnostics.Stopwatch.StartNew();
    long total = 0;
    var alive = new List<string>();

    foreach (var (tf, from) in plan)
    {
        Console.WriteLine($"\n=== EXPAND2 {tf} da {from:yyyy-MM-dd} ===");
        foreach (var symbol in wave2)
        {
            using var scope = provider.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IOhlcvIngestionService>();
            try
            {
                var result = await ingestion.IngestHistoricalDataAsync(
                    "Binance", symbol, tf, from, DateTime.UtcNow, null, CancellationToken.None);
                total += result.CandlesProcessed;
                Console.WriteLine($"  {symbol,-13} {tf,-4} -> {result.CandlesProcessed,8:N0} candele");
                if (result.CandlesProcessed > 0 && !alive.Contains(symbol)) alive.Add(symbol);
            }
            catch (Exception ex)
            {
                // Un simbolo inesistente o delistato non deve fermare l'espansione: si annota e si prosegue.
                Console.WriteLine($"  {symbol,-13} {tf,-4} -> ERRORE: {ex.Message.Split('\n')[0]}");
            }
        }
    }

    // Watchlist: solo le coppie che hanno DAVVERO restituito dati. Registrare un simbolo che non
    // esiste significherebbe un errore ogni 5 minuti nel worker di sincronizzazione, per sempre.
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        var added = 0;
        foreach (var symbol in alive)
        {
            foreach (var (tf, _) in plan)
            {
                var exists = await db.TrackedSeries.AnyAsync(t =>
                    t.Exchange == ExchangeName.Binance && t.Symbol == symbol && t.Timeframe == tf);
                if (!exists)
                {
                    db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = tf, Enabled = true });
                    added++;
                }
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"\n  Watchlist: {added} nuove serie tracciate su {alive.Count} coppie vive.");
    }

    Console.WriteLine($"\n=== EXPAND2 completata: {total:N0} candele in {sw.Elapsed.TotalMinutes:F1} min ===");
    Console.WriteLine($"    Coppie vive: {string.Join(", ", alive)}");
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
