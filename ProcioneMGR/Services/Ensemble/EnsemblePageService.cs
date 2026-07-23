using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Ensemble;

/// <summary>Esito della valutazione drift: messaggio riassuntivo per l'operatore.</summary>
public sealed record DriftEvaluationResult(string Message, bool IsError);

/// <summary>
/// Orchestrazione estratta da <c>Components/Pages/Ensemble.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): caricamento di config/status/performance per corsia (keyed DI),
/// costruzione delle gambe (predefinita, salvata, modello ML, Champion), ciclo di vita
/// dell'ensemble (save/start/stop/rebalance), monitor di decadimento, piani di esecuzione e
/// valutazione drift — tutta la logica che prima viveva nel blocco <c>@code</c> del componente
/// senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è
/// intrinsecamente Blazor: binding, PollingTimer di auto-refresh, flag di concorrenza dei bottoni,
/// toast, <c>StateHasChanged</c>.
///
/// La corsia (<c>laneId</c>) NON è stato interno ma un parametro esplicito di ogni metodo — stessa
/// scelta di <see cref="TradingPageService"/>: è una selezione di navigazione della UI, tenerla
/// fuori evita che un'istanza per-circuito "ricordi" una corsia stantia. Lo stato caricato
/// (Config/Status/…) appartiene all'ultima corsia caricata con <see cref="LoadLaneAsync"/>.
/// Registrato Scoped: in Blazor Server uno scope = un circuito, un'istanza per sessione utente.
/// </summary>
public sealed class EnsemblePageService(
    IServiceProvider services,
    IStrategyFactory strategyFactory,
    IFeatureDriftMonitor driftMonitor,
    IModelRegistry registry,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>Finestra "recente" per la valutazione drift (candele).</summary>
    public const int DriftRecentCandles = 200;

    private static readonly string[] Palette = ["#2962FF", "#E53935", "#43A047", "#FB8C00", "#8E24AA", "#00897B"];

    // --- Stato caricato (letto dal markup, mai scritto dal componente) --------------------------

    public EnsembleConfiguration? Config { get; private set; }
    public EnsembleStatus? Status { get; private set; }
    public EnsemblePerformance? Performance { get; private set; }
    public List<IndicatorSeries> PerfSeries { get; private set; } = [];
    public List<SavedStrategy> SavedStrategies { get; private set; } = [];
    public List<SavedMlModel> SavedMlModels { get; private set; } = [];
    public List<DecayReport> DecayReports { get; private set; } = [];
    public List<ExecutionJob> ExecutionJobs { get; private set; } = [];
    public List<FactorDriftReport> DriftReports { get; private set; } = [];
    public SavedMlModel? Champion { get; private set; }

    /// <summary>Ensemble della corsia (keyed DI): risolto ad ogni accesso, mai in cache tra cambi corsia.</summary>
    private IEnsembleManager Manager(int laneId) => services.GetRequiredKeyedService<IEnsembleManager>(laneId);

    // --- Caricamento ----------------------------------------------------------------------------

    /// <summary>
    /// TUTTE le strategie salvate sono deployabili in un ensemble, non solo quelle da walk-forward:
    /// una strategia trovata via Discovery e salvata da /backtest ha parametri validi ma
    /// IsOptimized=false (nessuno Sharpe atteso). Le ottimizzate espongono lo Sharpe atteso
    /// (alimenta il decay monitor); le altre no (decay resta "in attesa", gestito con grazia).
    /// </summary>
    public async Task LoadSavedCatalogsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        SavedStrategies = await db.SavedStrategies
            .OrderByDescending(s => s.IsOptimized)
            .ThenByDescending(s => s.OptimizationDate ?? s.CreatedAt)
            .ToListAsync(ct);
        // [1.V fase 2] Solo modelli DIREZIONALI: un membro ML dell'ensemble produce segnali
        // long/short via MlStrategy — un modello di rischio (vol) qui non ha senso e il loader
        // lo rifiuterebbe comunque a runtime.
        SavedMlModels = await db.SavedMlModels
            .Where(m => m.TargetKind == "ForwardReturn")
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Carica config + Champion della corsia. Status/decadimento/piani di esecuzione si caricano coi
    /// metodi dedicati: il componente li chiama uno a uno, così un errore su un pannello non
    /// impedisce agli altri di popolarsi (stessa granularità di errore dell'originale).
    /// </summary>
    public async Task LoadConfigAndChampionAsync(int laneId, CancellationToken ct = default)
    {
        Config = await Manager(laneId).GetConfigurationAsync(ct);
        Champion = await registry.GetChampionAsync(Config.Symbol, Config.Timeframe, ct);
    }

    public async Task RefreshAsync(int laneId, CancellationToken ct = default)
    {
        Status = await Manager(laneId).GetStatusAsync(ct);
        Performance = await Manager(laneId).GetPerformanceAsync(DateTime.UtcNow.AddDays(-90), ct);
        BuildPerfSeries();
    }

    public async Task LoadDecayReportsAsync(int laneId, CancellationToken ct = default) =>
        DecayReports = (await Manager(laneId).GetDecayReportsAsync(ct)).ToList();

    public async Task LoadExecutionJobsAsync(int laneId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        ExecutionJobs = await db.ExecutionJobs.AsNoTracking()
            .Where(j => j.LaneId == laneId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);
    }

    // --- Composizione delle gambe (mutano Config.Strategies) ------------------------------------

    public void AddPredefined(string strategyName)
    {
        if (Config is null) return;
        var proto = strategyFactory.Prototypes.First(p => p.Name == strategyName);
        Config.Strategies.Add(new EnsembleStrategy
        {
            StrategyName = proto.Name,
            DisplayName = proto.DisplayName,
            Parameters = proto.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default),
        });
    }

    public void AddFromSaved(int savedStrategyId)
    {
        if (Config is null || savedStrategyId == 0) return;
        var saved = SavedStrategies.FirstOrDefault(s => s.Id == savedStrategyId);
        if (saved is null) return;
        Config.Strategies.Add(new EnsembleStrategy
        {
            StrategyName = saved.StrategyName,
            DisplayName = $"{saved.Name} ({(saved.IsOptimized ? "opt" : "salvata")})",
            Parameters = JsonSerializer.Deserialize<Dictionary<string, decimal>>(saved.ParametersJson) ?? new(),
            SavedStrategyId = saved.Id,
            // Solo le strategie ottimizzate (walk-forward) hanno uno Sharpe atteso persistito; per le
            // altre resta null e il decay monitor mostra "in attesa" (nessuna baseline da confrontare).
            ExpectedSharpe = saved.OptimizationSharpe,
        });
    }

    public void AddFromMlModel(int modelId, decimal longThreshold, decimal shortThreshold)
    {
        if (Config is null || modelId == 0) return;
        var model = SavedMlModels.FirstOrDefault(m => m.Id == modelId);
        if (model is null) return;
        Config.Strategies.Add(new EnsembleStrategy
        {
            StrategyName = "Ml",
            DisplayName = $"{model.Name} (ML)",
            Parameters = new Dictionary<string, decimal>
            {
                ["SavedModelId"] = model.Id,
                ["LongThreshold"] = longThreshold,
                ["ShortThreshold"] = shortThreshold,
            },
            SavedMlModelId = model.Id,
        });
    }

    /// <summary>
    /// Il Champion NON è pinnato per Id: è una sentinella risolta a runtime dal registry, così la
    /// corsia segue sempre il modello promosso corrente. Il motore rifiuta l'esecuzione su Live.
    /// No-op se non c'è un Champion o se la corsia ne ha già uno.
    /// </summary>
    public void AddChampion(decimal longThreshold, decimal shortThreshold)
    {
        if (Config is null || Champion is null) return;
        if (Config.Strategies.Any(s => s.StrategyName == TradingEngine.ChampionStrategyName)) return;   // una sola corsia-Champion
        Config.Strategies.Add(new EnsembleStrategy
        {
            StrategyName = TradingEngine.ChampionStrategyName,
            DisplayName = $"🏆 Champion ({Config.Symbol} {Config.Timeframe})",
            Parameters = new Dictionary<string, decimal>
            {
                ["LongThreshold"] = longThreshold,
                ["ShortThreshold"] = shortThreshold,
            },
        });
    }

    public void RemoveStrategy(string strategyId) => Config?.Strategies.RemoveAll(s => s.StrategyId == strategyId);

    // --- Ciclo di vita dell'ensemble ------------------------------------------------------------

    public async Task<string> SaveAsync(int laneId, CancellationToken ct = default)
    {
        if (Config is null) return "Nessuna configurazione caricata.";
        await Manager(laneId).UpdateConfigurationAsync(Config, ct);
        await RefreshAsync(laneId, ct);
        return "Configurazione salvata.";
    }

    public async Task<string> StartEnsembleAsync(int laneId, CancellationToken ct = default)
    {
        if (Config is null) return "Nessuna configurazione caricata.";
        await Manager(laneId).UpdateConfigurationAsync(Config, ct);
        await Manager(laneId).StartAsync(ct);
        Config.IsEnabled = true;
        await RefreshAsync(laneId, ct);
        return "Ensemble avviato.";
    }

    public async Task<string> StopEnsembleAsync(int laneId, CancellationToken ct = default)
    {
        await Manager(laneId).StopAsync(ct);
        if (Config is not null) Config.IsEnabled = false;
        await RefreshAsync(laneId, ct);
        return "Ensemble fermato.";
    }

    public async Task<string> RebalanceNowAsync(int laneId, CancellationToken ct = default)
    {
        if (Config is null) return "Nessuna configurazione caricata.";
        await Manager(laneId).UpdateConfigurationAsync(Config, ct);
        await Manager(laneId).RebalanceAsync("Manual", ct);
        Config = await Manager(laneId).GetConfigurationAsync(ct);
        await RefreshAsync(laneId, ct);
        return "Rebalancing eseguito.";
    }

    // --- Drift ---------------------------------------------------------------------------------

    /// <summary>
    /// Confronta la distribuzione dei fattori del modello nella finestra di training (reference)
    /// con quella delle ultime <see cref="DriftRecentCandles"/> candele (current). Un drift NON è
    /// di per sé un allarme di PnL — è un avviso che gli input sono cambiati.
    /// </summary>
    public async Task<DriftEvaluationResult> EvaluateDriftAsync(int modelId, CancellationToken ct = default)
    {
        DriftReports = [];
        var model = SavedMlModels.FirstOrDefault(m => m.Id == modelId);
        if (model is null) return new DriftEvaluationResult("Modello non trovato.", IsError: true);

        List<OhlcvData> recent;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            recent = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(DriftRecentCandles)
                .ToListAsync(ct);
        }
        recent.Reverse(); // ordine cronologico

        if (recent.Count < 20)
            return new DriftEvaluationResult("Candele recenti insufficienti per una valutazione (servono ≥20).", IsError: true);

        DriftReports = (await driftMonitor.EvaluateAsync(model, recent, ct: ct)).ToList();
        var drift = DriftReports.Count(r => r.Overall != DriftSeverity.None);
        var message = drift == 0
            ? $"Nessun drift rilevante: {DriftReports.Count} fattori valutati sulle ultime {recent.Count} candele."
            : $"{drift}/{DriftReports.Count} fattori in drift sulle ultime {recent.Count} candele (input cambiati: controllare il monitor di decadimento per l'effetto sul PnL).";
        return new DriftEvaluationResult(message, IsError: false);
    }

    // --- Serie di performance -------------------------------------------------------------------

    private void BuildPerfSeries()
    {
        var series = new List<IndicatorSeries>();
        if (Performance is null) { PerfSeries = series; return; }

        static long Ts(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (Performance.TotalEquityCurve.Count > 0)
        {
            series.Add(new IndicatorSeries
            {
                Title = "Totale", Color = "#111827", Type = IndicatorSeriesType.Line,
                Points = Performance.TotalEquityCurve.Select(p => new IndicatorPoint(Ts(p.Timestamp), (double)p.Capital)).ToList(),
            });
        }
        var ci = 0;
        foreach (var sc in Performance.StrategyCurves)
        {
            series.Add(new IndicatorSeries
            {
                Title = sc.DisplayName, Color = Palette[ci++ % Palette.Length], Type = IndicatorSeriesType.Line,
                Points = sc.EquityCurve.Select(p => new IndicatorPoint(Ts(p.Timestamp), (double)p.Capital)).ToList(),
            });
        }
        PerfSeries = series;
    }
}
