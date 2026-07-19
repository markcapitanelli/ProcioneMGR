using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Fotografia completa del form di <c>Backtest.razor</c> — usata per i preset/memoria dell'ultima
/// configurazione, per l'handoff da/verso Optimization e come input di <see cref="BacktestPageService.RunAsync"/>.
/// </summary>
public sealed record BacktestConfigSnapshot(
    ExchangeName Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
    decimal InitialCapital, decimal PositionSizePercent, decimal FeePercent,
    decimal StopLossPercent, decimal TakeProfitPercent, decimal TrailingStopPercent,
    decimal Leverage, decimal SlippagePercent, decimal FundingPercent,
    string StrategyName, IReadOnlyDictionary<string, decimal> Parameters);

/// <summary>Esito di un'azione con messaggio per l'operatore.</summary>
public sealed record BacktestActionResult(string Message, bool IsError)
{
    public static BacktestActionResult Ok(string message) => new(message, false);
    public static BacktestActionResult Error(string message) => new(message, true);
}

/// <summary>Esito di "Suggerisci SL/TP": i livelli sono null quando il suggerimento fallisce.</summary>
public sealed record BracketSuggestion(string Message, bool IsError, decimal? StopLossPercent, decimal? TakeProfitPercent);

/// <summary>Contesto opzionale arrivato via query string (handoff dall'Optimization).</summary>
public sealed record BacktestHandoffQuery(
    string? Exchange, string? Symbol, string? Timeframe, string? Strategy,
    string? From, string? To, string? ParametersJson);

/// <summary>Strategia salvata caricata dal DB, coi parametri già fusi sui default della strategia.</summary>
public sealed record LoadedSavedStrategy(string Name, string StrategyName, IReadOnlyDictionary<string, decimal> Parameters);

/// <summary>
/// Orchestrazione estratta da <c>Components/Pages/Backtest.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): validazione, esecuzione del backtest con analitiche derivate (trade report,
/// Kelly, consulente leva, Montecarlo, Performance Control), suggerimento SL/TP dai percentili di
/// escursione, CRUD delle strategie salvate, handoff da Optimization e (de)serializzazione validata
/// dei preset — tutta la logica che prima viveva nel blocco <c>@code</c> del componente senza test
/// indipendenti da Blazor. Il componente resta responsabile solo di ciò che è intrinsecamente
/// Blazor: binding del form, ciclo di vita, spinner/CTS di annullamento, <c>StateHasChanged</c>.
///
/// Lo stato del "run corrente" (risultato, report, analisi di rischio) vive qui perché è stato
/// applicativo condiviso fra i passi run→Montecarlo/PerfControl→handoff, non stato di UI.
/// Registrato Scoped: in Blazor Server uno scope = un circuito, un'istanza per sessione utente.
/// </summary>
public sealed class BacktestPageService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IBacktestEngine engine,
    IStrategyFactory strategyFactory,
    IExperimentTracker tracker,
    MonteCarloAnalyzer monteCarlo,
    PerformanceControlService perfControl,
    KellyCalculator kelly,
    LeverageAdvisor levAdvisor,
    ExcursionAnalyzer excursion)
{
    // --- Stato caricato / del run corrente (letto dal markup, mai scritto dal componente) ------

    public IReadOnlyList<string> KnownSymbols { get; private set; } = [];

    public BacktestResult? Result { get; private set; }
    public TradeReport? TradeReport { get; private set; }
    public KellySuggestion? Kelly { get; private set; }
    public LeverageAdvice? LeverageAdvice { get; private set; }
    public MonteCarloResult? McResult { get; private set; }
    public EquityControlResult? PcResult { get; private set; }
    public List<IndicatorSeries> EquitySeries { get; private set; } = [];

    public async Task LoadKnownSymbolsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        KnownSymbols = await db.OhlcvData.Select(c => c.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    // --- Catalogo strategie --------------------------------------------------------------------

    /// <summary>Definizioni dei parametri della strategia; fallback al primo prototipo se il nome non esiste.</summary>
    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitionsFor(string strategyName) =>
        (strategyFactory.Prototypes.FirstOrDefault(p => p.Name == strategyName) ?? strategyFactory.Prototypes[0])
        .ParameterDefinitions;

    /// <summary>Default della strategia con gli <paramref name="overrides"/> applicati alle sole chiavi note.</summary>
    private Dictionary<string, decimal> MergedParameters(string strategyName, IReadOnlyDictionary<string, decimal>? overrides)
    {
        var merged = ParameterDefinitionsFor(strategyName).ToDictionary(d => d.Key, d => d.Default);
        if (overrides is not null)
        {
            foreach (var key in merged.Keys.ToList())
            {
                if (overrides.TryGetValue(key, out var v)) merged[key] = v;
            }
        }
        return merged;
    }

    // --- Preset: (de)serializzazione VALIDATA --------------------------------------------------

    /// <summary>Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili.</summary>
    private sealed record ConfigDto(
        string Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
        decimal InitialCapital, decimal PositionSizePercent, decimal FeePercent,
        decimal StopLossPercent, decimal TakeProfitPercent, decimal TrailingStopPercent,
        decimal Leverage, decimal SlippagePercent, decimal FundingPercent,
        string StrategyName, Dictionary<string, decimal> Parameters);

    public string SerializeConfig(BacktestConfigSnapshot cfg) => JsonSerializer.Serialize(new ConfigDto(
        cfg.Exchange.ToString(), cfg.Symbol.Trim(), cfg.Timeframe, cfg.From, cfg.To,
        cfg.InitialCapital, cfg.PositionSizePercent, cfg.FeePercent,
        cfg.StopLossPercent, cfg.TakeProfitPercent, cfg.TrailingStopPercent,
        cfg.Leverage, cfg.SlippagePercent, cfg.FundingPercent,
        cfg.StrategyName, new Dictionary<string, decimal>(cfg.Parameters)));

    /// <summary>
    /// Applica un preset a <paramref name="current"/>: i campi con vincolo di catalogo
    /// (exchange/timeframe/strategia) sono presi dal preset solo se ancora validi; i parametri sono
    /// i default della strategia finale con overlay dei valori del preset (chiavi sconosciute
    /// ignorate); JSON malformato ⇒ <paramref name="current"/> invariato. Stessa semantica del
    /// vecchio <c>ApplyConfigJson</c>, ora testabile in isolamento.
    /// </summary>
    public BacktestConfigSnapshot ApplyConfig(string json, BacktestConfigSnapshot current)
    {
        ConfigDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigDto>(json); }
        catch (JsonException) { return current; }
        if (dto is null) return current;

        var exchange = Enum.TryParse<ExchangeName>(dto.Exchange, ignoreCase: true, out var ex) ? ex : current.Exchange;
        var symbol = string.IsNullOrWhiteSpace(dto.Symbol) ? current.Symbol : dto.Symbol;
        var timeframe = Timeframes.Supported.ContainsKey(dto.Timeframe) ? dto.Timeframe : current.Timeframe;
        var strategy = strategyFactory.Prototypes.Any(p => p.Name == dto.StrategyName) ? dto.StrategyName : current.StrategyName;

        return current with
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            From = dto.From.Date,
            To = dto.To.Date,
            InitialCapital = dto.InitialCapital,
            PositionSizePercent = dto.PositionSizePercent,
            FeePercent = dto.FeePercent,
            StopLossPercent = dto.StopLossPercent,
            TakeProfitPercent = dto.TakeProfitPercent,
            TrailingStopPercent = dto.TrailingStopPercent,
            Leverage = dto.Leverage,
            SlippagePercent = dto.SlippagePercent,
            FundingPercent = dto.FundingPercent,
            StrategyName = strategy,
            Parameters = MergedParameters(strategy, dto.Parameters),
        };
    }

    // --- Handoff dall'Optimization (query string) ----------------------------------------------

    /// <summary>
    /// Applica il contesto arrivato via query string. Valori assenti o malformati lasciano i
    /// correnti: il link è una comodità, mai un requisito. Il messaggio è non-null solo quando è
    /// arrivato davvero un contesto (symbol presente).
    /// </summary>
    public (BacktestConfigSnapshot Snapshot, string? Message) ApplyHandoff(BacktestHandoffQuery q, BacktestConfigSnapshot current)
    {
        var symbol = string.IsNullOrWhiteSpace(q.Symbol) ? current.Symbol : q.Symbol.Trim();
        var timeframe = !string.IsNullOrWhiteSpace(q.Timeframe) && Timeframes.Supported.ContainsKey(q.Timeframe) ? q.Timeframe : current.Timeframe;
        var exchange = Enum.TryParse<ExchangeName>(q.Exchange, ignoreCase: true, out var ex) ? ex : current.Exchange;
        var from = DateTime.TryParse(q.From, out var f) ? f.Date : current.From;
        var to = DateTime.TryParse(q.To, out var t) ? t.Date : current.To;
        var strategy = !string.IsNullOrWhiteSpace(q.Strategy) && strategyFactory.Prototypes.Any(p => p.Name == q.Strategy)
            ? q.Strategy : current.StrategyName;

        // Parametri: default della strategia finale + overlay del JSON di query (malformato ⇒ solo default).
        Dictionary<string, decimal>? overrides = null;
        if (!string.IsNullOrWhiteSpace(q.ParametersJson))
        {
            try { overrides = JsonSerializer.Deserialize<Dictionary<string, decimal>>(q.ParametersJson); }
            catch (JsonException) { /* parametri malformati: restano i default */ }
        }

        var snapshot = current with
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            From = from,
            To = to,
            StrategyName = strategy,
            Parameters = MergedParameters(strategy, overrides),
        };
        var message = string.IsNullOrWhiteSpace(q.Symbol)
            ? null
            : $"Configurazione importata dall'Optimization: {symbol} {timeframe}, strategia {strategy}.";
        return (snapshot, message);
    }

    /// <summary>Link a Optimization precompilata col contesto di questo backtest.</summary>
    public static string OptimizationHandoffUrl(BacktestConfigSnapshot cfg)
    {
        var parameters = JsonSerializer.Serialize(new Dictionary<string, decimal>(cfg.Parameters));
        return "optimization"
             + $"?exchange={Uri.EscapeDataString(cfg.Exchange.ToString())}"
             + $"&symbol={Uri.EscapeDataString(cfg.Symbol.Trim())}"
             + $"&timeframe={Uri.EscapeDataString(cfg.Timeframe)}"
             + $"&strategy={Uri.EscapeDataString(cfg.StrategyName)}"
             + $"&from={cfg.From:yyyy-MM-dd}&to={cfg.To:yyyy-MM-dd}"
             + $"&parameters={Uri.EscapeDataString(parameters)}";
    }

    // --- Esecuzione ----------------------------------------------------------------------------

    /// <summary>
    /// Esegue il backtest e calcola le analitiche derivate (equity series, trade report, Kelly,
    /// consulente leva) + experiment tracking best-effort. L'annullamento
    /// (<see cref="OperationCanceledException"/>) propaga al chiamante, che possiede il CTS.
    /// </summary>
    public async Task<BacktestActionResult> RunAsync(BacktestConfigSnapshot cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Symbol) || cfg.To <= cfg.From)
            return BacktestActionResult.Error("Controlla symbol e intervallo date.");

        ResetRun();

        var config = new BacktestConfiguration
        {
            ExchangeName = cfg.Exchange.ToString(),
            Symbol = cfg.Symbol.Trim(),
            Timeframe = cfg.Timeframe,
            From = cfg.From.Date,
            To = cfg.To.Date.AddDays(1).AddSeconds(-1),
            InitialCapital = cfg.InitialCapital,
            PositionSizePercent = cfg.PositionSizePercent,
            FeePercent = cfg.FeePercent,
            StrategyName = cfg.StrategyName,
            StrategyParameters = new Dictionary<string, decimal>(cfg.Parameters),
            StopLossPercent = Math.Max(0m, cfg.StopLossPercent),
            TakeProfitPercent = Math.Max(0m, cfg.TakeProfitPercent),
            TrailingStopPercent = Math.Max(0m, cfg.TrailingStopPercent),
            Leverage = Math.Clamp(cfg.Leverage, 1m, 125m),
            SlippagePercent = Math.Max(0m, cfg.SlippagePercent),
            FundingRatePercentPer8h = Math.Max(0m, cfg.FundingPercent),
        };

        var result = await engine.RunBacktestAsync(config, ct);
        Result = result;

        if (result.CandlesEvaluated == 0)
            return BacktestActionResult.Error("Nessuna candela nel range: fai prima un Fetch/Sync di questi dati.");

        EquitySeries =
        [
            new IndicatorSeries
            {
                Title = "Equity",
                Color = "#2962FF",
                Type = IndicatorSeriesType.Line,
                Points = result.EquityCurve
                    .Select(p => new IndicatorPoint(
                        new DateTimeOffset(DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                        (double)p.Capital))
                    .ToList(),
            },
        ];
        TradeReport = TradeStatistics.ComputeTradeReport(result.Trades, result.EquityCurve);
        Kelly = kelly.FromTradeHistory(result.Trades);
        LeverageAdvice = result.Trades.Count >= 20
            ? levAdvisor.Advise(result.Trades, marginFraction: Math.Clamp(cfg.PositionSizePercent / 100m, 0.01m, 1m))
            : null;

        // Experiment tracking (best-effort): un run per backtest, confrontabile con gli altri.
        var initialCap = result.EquityCurve.Count > 0 ? result.EquityCurve[0].Capital : cfg.InitialCapital;
        var finalCap = result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Capital : cfg.InitialCapital;
        var btRunId = await tracker.SafeStartRunAsync(
            "Backtest",
            $"{cfg.StrategyName} · {cfg.Symbol.Trim()} · {cfg.Timeframe}",
            new
            {
                config.StrategyName,
                config.Symbol,
                config.Timeframe,
                config.From,
                config.To,
                config.InitialCapital,
                config.PositionSizePercent,
                config.FeePercent,
                config.Leverage,
                config.StopLossPercent,
                config.TakeProfitPercent,
                config.TrailingStopPercent,
                Parameters = config.StrategyParameters,
            },
            config.Symbol, config.Timeframe);
        await tracker.SafeLogMetricsAsync(btRunId, new Dictionary<string, decimal>
        {
            ["CandlesEvaluated"] = result.CandlesEvaluated,
            ["Trades"] = result.Trades.Count,
            ["FinalCapital"] = finalCap,
            ["TotalReturnPct"] = initialCap > 0m ? (finalCap / initialCap - 1m) * 100m : 0m,
        });
        await tracker.SafeCompleteAsync(btRunId, "Completed");

        return BacktestActionResult.Ok($"Backtest completato su {result.CandlesEvaluated} candele.");
    }

    // --- Analisi di rischio sul run corrente ---------------------------------------------------

    /// <summary>Montecarlo evoluta sui PnL del run corrente (no-op senza trade). Seed fisso: riproducibile tra un click e l'altro.</summary>
    public void RunMonteCarlo(int shuffles, decimal noisePercent)
    {
        if (Result is null || Result.Trades.Count == 0) return;
        var pnls = Result.Trades.Select(t => t.Pnl).ToList();
        McResult = monteCarlo.Run(pnls, new MonteCarloConfig
        {
            NumberOfShuffles = Math.Clamp(shuffles, 1, 10_000),
            NoisePercent = Math.Max(0m, noisePercent),
            Seed = 42,
        });
    }

    /// <summary>Performance Control (profitto a finestra) sui trade del run corrente (no-op senza trade).</summary>
    public void RunPerformanceControl(int windowSize, decimal threshold)
    {
        if (Result is null || Result.Trades.Count == 0) return;
        PcResult = perfControl.ApplyWindowProfitControl(Result.Trades, Math.Max(1, windowSize), threshold);
    }

    // --- Suggerimento SL/TP dai dati -----------------------------------------------------------

    /// <summary>
    /// Calcola SL e TP dai dati della coppia/timeframe nella finestra selezionata (media dei
    /// percentili 95° di escursione avversa/favorevole tra long e short). Livelli ampi che scattano
    /// solo sugli outlier: proteggono senza tagliare le operazioni normali.
    /// </summary>
    public async Task<BracketSuggestion> SuggestBracketAsync(BacktestConfigSnapshot cfg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Symbol) || cfg.To <= cfg.From)
            return new BracketSuggestion("Controlla symbol e intervallo date.", IsError: true, null, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var from = cfg.From.Date;
        var to = cfg.To.Date.AddDays(1).AddSeconds(-1);
        var candles = await db.OhlcvData
            .Where(c => c.Symbol == cfg.Symbol && c.Timeframe == cfg.Timeframe && c.TimestampUtc >= from && c.TimestampUtc <= to)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(ct);
        if (candles.Count < 100)
            return new BracketSuggestion($"Dati insufficienti ({candles.Count} candele) per suggerire SL/TP.", IsError: true, null, null);

        var sl = excursion.SuggestStopLoss(candles);
        var tp = excursion.SuggestTakeProfit(candles);
        static decimal Avg(decimal a, decimal b)
        {
            var v = new[] { a, b }.Where(x => x > 0m).ToList();
            return v.Count > 0 ? Math.Round(v.Average(), 2) : 0m;
        }
        var stopLoss = Avg(sl.LongStopPercentile95, sl.ShortStopPercentile95);
        var takeProfit = Avg(tp.LongTakeProfitPercentile95, tp.ShortTakeProfitPercentile95);
        return new BracketSuggestion(
            $"SL/TP suggeriti dai dati (percentile 95° di escursione su {candles.Count} candele): "
            + $"stop {stopLoss:0.##}%, target {takeProfit:0.##}%. Modificabili prima di lanciare.",
            IsError: false, stopLoss, takeProfit);
    }

    // --- Strategie salvate ---------------------------------------------------------------------

    public async Task<BacktestActionResult> SaveStrategyAsync(string name, string strategyName, IReadOnlyDictionary<string, decimal> parameters, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BacktestActionResult.Error("Inserisci un nome per salvare la strategia.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.SavedStrategies.Add(new SavedStrategy
        {
            UserId = userId,
            Name = name.Trim(),
            StrategyName = strategyName,
            ParametersJson = JsonSerializer.Serialize(new Dictionary<string, decimal>(parameters)),
        });
        await db.SaveChangesAsync(ct);
        return BacktestActionResult.Ok($"Strategia '{name}' salvata.");
    }

    /// <summary>Carica una strategia salvata dell'utente; null se non trovata (o di un altro utente).</summary>
    public async Task<LoadedSavedStrategy?> LoadSavedStrategyAsync(int id, string? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = await db.SavedStrategies.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (saved is null) return null;

        Dictionary<string, decimal>? parameters = null;
        try { parameters = JsonSerializer.Deserialize<Dictionary<string, decimal>>(saved.ParametersJson); }
        catch (JsonException) { /* parametri corrotti: restano i default */ }
        return new LoadedSavedStrategy(saved.Name, saved.StrategyName, MergedParameters(saved.StrategyName, parameters));
    }

    private void ResetRun()
    {
        Result = null;
        TradeReport = null;
        Kelly = null;
        LeverageAdvice = null;
        McResult = null;
        PcResult = null;
        EquitySeries = [];
    }
}
