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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IBacktestEngine backtestEngine,
    ProcioneMGR.Services.Analysis.ExcursionAnalyzer excursionAnalyzer)
{
    /// <summary>Finestra "recente" per la valutazione drift (candele).</summary>
    public const int DriftRecentCandles = 200;

    /// <summary>Finestra (giorni) su cui si misura la ridondanza fra le gambe della corsia.</summary>
    public const int CorrelationWindowDays = 90;

    /// <summary>Tetto dei candidati grigi proposti come fonte gamba (i più recenti prima).</summary>
    public const int MaxGreyChoices = 20;

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

    /// <summary>[T3] Candidati grigi compatibili con la corsia (stessa coppia e timeframe), dall'archivio della caccia.</summary>
    public List<Research.ResearchCandidate> GreyCandidates { get; private set; } = [];

    /// <summary>
    /// La coppia/timeframe PER CUI la lista grigia è stata caricata: l'intestazione del pannello
    /// mostra questa, non il valore live dei campi (che l'operatore può editare senza salvare) —
    /// altrimenti l'etichetta mentirebbe sui candidati elencati (review 2026-08-14).
    /// </summary>
    public (string Symbol, string Timeframe)? GreyCandidatesFor { get; private set; }

    /// <summary>[T2] Ultimo report di ridondanza calcolato con <see cref="EvaluateLegCorrelationAsync"/>.</summary>
    public Portfolio.LegCorrelationReport? LegCorrelation { get; private set; }

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

    // --- [T3] Fascia grigia come fonte gamba ----------------------------------------------------

    /// <summary>
    /// Candidati grigi della STESSA coppia/timeframe della corsia, dall'indice dell'archivio
    /// (<c>ResearchCandidates</c>). Dedup per chiave identità tenendo il run più recente: lo
    /// stesso candidato riscoperto da tre cacce è UN candidato, non tre. Stesso principio del
    /// filtro dei modelli ML compatibili: una gamba di un'altra coppia qui non ha senso.
    /// </summary>
    public async Task LoadGreyCandidatesAsync(CancellationToken ct = default)
    {
        GreyCandidates = [];
        GreyCandidatesFor = null;
        if (Config is null || string.IsNullOrEmpty(Config.Symbol)) return;
        var symbol = Config.Symbol;
        var timeframe = Config.Timeframe;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var raw = await db.ResearchCandidates.AsNoTracking()
            .Where(c => c.IsGrey && c.Symbol == symbol && c.Timeframe == timeframe)
            .OrderByDescending(c => c.RunCompletedUtc)
            .ToListAsync(ct);
        GreyCandidates = DedupGreyChoices(raw, MaxGreyChoices);
        GreyCandidatesFor = (symbol, timeframe);
    }

    /// <summary>
    /// Dedup per chiave identità sull'input ordinato dal run più recente: la prima occorrenza di
    /// ogni chiave è la misura più fresca dello stesso candidato. Statica e pura per essere
    /// testabile senza il circuito.
    /// </summary>
    internal static List<Research.ResearchCandidate> DedupGreyChoices(
        IReadOnlyList<Research.ResearchCandidate> rawNewestFirst, int max) =>
        rawNewestFirst
            .GroupBy(c => c.CandidateKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(c => c.HoldoutSharpe)
            .Take(max)
            .ToList();

    /// <summary>Esito di un'azione della pagina: messaggio + gravità, così il toast non può mai colorare di verde un fallimento.</summary>
    public sealed record ActionOutcome(string Message, bool IsError);

    /// <summary>
    /// Aggiunge un candidato grigio come gamba: parametri esatti del candidato, attese holdout per
    /// il decay monitor, bracket SL/TP data-driven dalle escursioni (stesso <c>AutoBracket</c> del
    /// GreyDeployer — un forward test senza protezioni non si aggiunge da un click), e
    /// <c>SourceVerdict="Grey"</c> perché il badge non dipenda dal percorso. Il messaggio dichiara
    /// il bracket applicato o la sua assenza; i fallimenti tornano con IsError=true.
    /// </summary>
    public async Task<ActionOutcome> AddFromGreyAsync(long researchCandidateId, CancellationToken ct = default)
    {
        if (Config is null) return new("Nessuna configurazione caricata.", IsError: true);
        var c = GreyCandidates.FirstOrDefault(x => x.Id == researchCandidateId);
        if (c is null) return new("Candidato non trovato fra i grigi compatibili.", IsError: true);
        // [Review 2026-08-14] La lista grigia è stata caricata per la coppia SALVATA della
        // corsia; se l'operatore ha editato Symbol/Timeframe senza salvare, aggiungere ora
        // creerebbe una gamba cross-coppia con attese di un'altra serie. Fail-closed.
        if (GreyCandidatesFor is not { } loadedFor
            || loadedFor.Symbol != Config.Symbol || loadedFor.Timeframe != Config.Timeframe)
        {
            return new($"La lista dei grigi è per {GreyCandidatesFor?.Symbol} {GreyCandidatesFor?.Timeframe}, "
                + $"ma la corsia ora dice {Config.Symbol} {Config.Timeframe}: salva (o ricarica) la corsia prima di aggiungere.", IsError: true);
        }
        // Identità canonica del candidato (PipelineCandidateKey), mai un confronto fatto a mano
        // sui parametri: la chiave è già l'impronta dei parametri.
        bool AlreadyPresent() => Config.Strategies.Any(s =>
            Pipeline.PipelineCandidateKey.Build(s.StrategyName, Config.Symbol, Config.Timeframe, s.Parameters) == c.CandidateKey);
        if (AlreadyPresent()) return new("Questa gamba grigia è già nella corsia.", IsError: true);

        var (sl, tp) = await Pipeline.AutoBracket.ComputeAsync(dbFactory, excursionAnalyzer, c.Symbol, c.Timeframe, ct);
        // Ricontrollo DOPO l'await: un doppio click fa partire due handler e il secondo passa il
        // primo controllo mentre il primo è ancora dentro AutoBracket (review 2026-08-14).
        if (AlreadyPresent()) return new("Questa gamba grigia è già nella corsia.", IsError: true);
        Config.Strategies.Add(new EnsembleStrategy
        {
            StrategyName = c.StrategyName,
            DisplayName = $"{c.StrategyName} (fascia grigia, run {c.RunId.ToString()[..8]})",
            Parameters = ParseParams(c.ParametersJson),
            StopLossPercent = sl > 0m ? sl : null,
            TakeProfitPercent = tp > 0m ? tp : null,
            ExpectedSharpe = c.HoldoutSharpe != 0m ? c.HoldoutSharpe : null,
            ExpectedProfitFactor = c.HoldoutProfitFactor != 0m ? c.HoldoutProfitFactor : null,
            ExpectedMaxDrawdown = c.HoldoutMaxDrawdown != 0m ? c.HoldoutMaxDrawdown : null,
            SourceVerdict = "Grey",
        });
        return sl > 0m || tp > 0m
            ? new($"Gamba grigia aggiunta con bracket dalle escursioni (SL {sl:F2}% / TP {tp:F2}%). Ricorda: Save per persistere.", IsError: false)
            : new("Gamba grigia aggiunta SENZA bracket (escursioni non derivabili: impostare SL/TP a mano prima di avviare).", IsError: false);
    }

    private static Dictionary<string, decimal> ParseParams(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    // --- [T2] Ridondanza fra le gambe della corsia ----------------------------------------------

    /// <summary>
    /// Misura la correlazione dei rendimenti giornalieri fra le gambe ATTIVE della corsia,
    /// backtestandole sulla stessa finestra recente (<see cref="CorrelationWindowDays"/> giorni) —
    /// stessa formula dell'assemblaggio in pipeline (<see cref="Portfolio.ReturnCorrelation"/>).
    /// Gambe non backtestabili da qui (es. Champion, che è una sentinella del motore) vengono
    /// SALTATE e dichiarate nel report, mai conteggiate come "non correlate". Gambe e contesto
    /// (exchange/coppia/timeframe) sono FOTOGRAFATI all'avvio: un cambio corsia a metà misura non
    /// deve mischiare due corsie nello stesso report (review 2026-08-14).
    /// </summary>
    public async Task<Portfolio.LegCorrelationReport> EvaluateLegCorrelationAsync(CancellationToken ct = default)
    {
        var report = new Portfolio.LegCorrelationReport
        {
            Window = $"ultimi {CorrelationWindowDays} giorni",
        };
        LegCorrelation = report;
        if (Config is null || string.IsNullOrEmpty(Config.Symbol))
        {
            report.Note = "Nessuna configurazione caricata.";
            return report;
        }

        var (exchangeName, symbol, timeframe) = (Config.ExchangeName, Config.Symbol, Config.Timeframe);
        var active = Config.Strategies.Where(s => s.IsActive).ToList();
        if (active.Count < 2)
        {
            report.Note = "Servono almeno 2 gambe attive per misurare una ridondanza.";
            return report;
        }

        var to = DateTime.UtcNow;
        var from = to.AddDays(-CorrelationWindowDays);
        var returnsByLeg = new Dictionary<string, Dictionary<DateTime, decimal>>();
        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var leg in active)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await backtestEngine.RunBacktestAsync(new BacktestConfiguration
                {
                    ExchangeName = exchangeName,
                    Symbol = symbol,
                    Timeframe = timeframe,
                    From = from,
                    To = to,
                    InitialCapital = 10_000m,
                    StrategyName = leg.StrategyName,
                    StrategyParameters = new(leg.Parameters),
                }, ct);
                returnsByLeg[leg.StrategyId] = Portfolio.ReturnCorrelation.DailyReturns(result.EquityCurve);
                display[leg.StrategyId] = leg.DisplayName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add($"{leg.DisplayName} ({ex.Message})");
            }
        }

        if (returnsByLeg.Count < 2)
        {
            report.Note = "Meno di 2 gambe backtestabili da qui"
                + (skipped.Count > 0 ? $" — saltate: {string.Join("; ", skipped)}" : ".");
            return report;
        }

        var commonDates = returnsByLeg.Values
            .Select(d => d.Keys.AsEnumerable())
            .Aggregate((a, b) => a.Intersect(b))
            .OrderBy(d => d)
            .ToList();
        if (commonDates.Count < Portfolio.ReturnCorrelation.MinObservations)
        {
            report.Note = $"Solo {commonDates.Count} giorni comuni fra le gambe "
                + $"(minimo {Portfolio.ReturnCorrelation.MinObservations}): ρ sarebbe rumore.";
            return report;
        }

        report.Pairs = Portfolio.ReturnCorrelation.AllPairs(
            returnsByLeg.Select(kv => (kv.Key, display[kv.Key],
                (IReadOnlyList<decimal>)commonDates.Select(d => kv.Value[d]).ToList())).ToList());
        report.Window = $"ultimi {CorrelationWindowDays} giorni, {commonDates.Count} giorni comuni";
        if (skipped.Count > 0)
        {
            report.Note = $"Gambe saltate (non backtestabili da qui): {string.Join("; ", skipped)}";
        }
        return report;
    }

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
