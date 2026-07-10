using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Implementazione dell'ensemble per UNA corsia di trading isolata (<paramref name="laneId"/>).
/// Thread-safe via <see cref="SemaphoreSlim"/>: la configurazione è letta/scritta in modo
/// serializzato; le simulazioni girano su uno snapshot locale della config (fuori dal lock) per
/// non bloccare letture concorrenti (UI polling + worker).
///
/// Registrato come Keyed Singleton (una istanza per corsia, vedi Program.cs) invece di un
/// singolo Singleton globale come prima del supporto multi-corsia: ogni istanza filtra/imposta
/// <see cref="EnsembleState.LaneId"/>/<see cref="EnsembleRebalanceHistory.LaneId"/> con il PROPRIO
/// <paramref name="laneId"/>, così due corsie non vedono/toccano mai le righe l'una dell'altra.
/// Le righe esistenti PRIMA di questo supporto hanno LaneId=0 (default di migrazione): sono
/// automaticamente la corsia 0, senza bisogno di alcuna migrazione dati.
///
/// I servizi scoped (DbContext, BacktestEngine) sono risolti per-operazione via
/// <see cref="IServiceScopeFactory"/> (il manager è Singleton per-corsia).
/// </summary>
public sealed class EnsembleManager(
    int laneId,
    IServiceScopeFactory scopeFactory,
    IRegimeDetector regimeDetector,
    IMarketFeatureExtractor featureExtractor,
    IStrategyDecayMonitor decayMonitor,
    ILogger<EnsembleManager> logger) : IEnsembleManager
{
    private const int DefaultWindowDays = 120;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int LaneId => laneId;

    public async Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            return await LoadConfigAsync(scope, laneId, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateConfigurationAsync(EnsembleConfiguration config, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            await SaveConfigAsync(scope, laneId, config, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default) => await SetEnabledAsync(true, ct);
    public async Task StopAsync(CancellationToken ct = default) => await SetEnabledAsync(false, ct);

    private async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var cfg = await LoadConfigAsync(scope, laneId, ct);
            cfg.IsEnabled = enabled;
            await SaveConfigAsync(scope, laneId, cfg, ct);
        }
        finally { _gate.Release(); }
        logger.LogInformation("Ensemble {State}.", enabled ? "avviato" : "fermato");
    }

    public async Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
    {
        var cfg = await GetConfigurationAsync(ct);
        var to = DateTime.UtcNow;
        var start = from ?? to.AddDays(-DefaultWindowDays);
        return await SimulateAsync(cfg, start, to, ct);
    }

    /// <summary>
    /// Confronta la performance REALIZZATA (trade chiusi dal vivo, Paper/Testnet/Live — non una
    /// ri-simulazione come <see cref="GetStatusAsync"/>) di ogni gamba attiva con quella attesa
    /// dal backtest/holdout, via <see cref="IStrategyDecayMonitor"/>. Interroga TradeRecords
    /// direttamente (non passa da ITradingEngine) per non introdurre una dipendenza
    /// Ensemble→Trading: oggi è già l'opposto (Trading dipende da Ensemble).
    ///
    /// Una query per gamba, filtrata e limitata al DB (non un unico caricamento dell'intera
    /// tabella): con lo storico trade che cresce nel tempo (operatività quotidiana), scaricare
    /// tutto in memoria a ogni refresh diventerebbe un collo di bottiglia reale — qui il costo
    /// resta O(gambe), non O(storico).
    /// </summary>
    public async Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigurationAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);

        var options = new DecayMonitorOptions();
        var reports = new List<DecayReport>(cfg.Strategies.Count);
        foreach (var s in cfg.Strategies)
        {
            var recentTrades = await db.TradeRecords.AsNoTracking()
                .Where(t => t.LaneId == laneId && t.StrategyId == s.StrategyId)
                .OrderByDescending(t => t.ClosedAtUtc)
                .Take(options.WindowTradeCount)
                .ToListAsync(ct);
            var report = decayMonitor.Analyze(s, recentTrades, options);
            reports.Add(report);

            if (report.IsAlert)
            {
                logger.LogWarning(
                    "Decadimento rilevato per {Strategy} ({StrategyId}): Sharpe realizzato {Realized:F2} vs atteso {Expected:F2} ({Ratio:P0}) su {Trades} trade.",
                    s.DisplayName, s.StrategyId, report.RealizedSharpe, report.ExpectedSharpe, report.SharpeRatio, report.TradeCount);
            }
        }
        return reports;
    }

    public async Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigurationAsync(ct);
        var to = DateTime.UtcNow;
        var perf = await SimulateAsync(cfg, to.AddDays(-DefaultWindowDays), to, ct);

        DateTime? last = perf.RebalanceHistory.Count > 0 ? perf.RebalanceHistory[^1].Timestamp : null;
        var status = new EnsembleStatus
        {
            IsRunning = cfg.IsEnabled,
            TotalCapital = cfg.TotalCapital + (cfg.TotalCapital * perf.TotalReturn / 100m),
            TotalPnl = cfg.TotalCapital * perf.TotalReturn / 100m,
            TotalPnlPercent = perf.TotalReturn,
            LastRebalanceUtc = last,
            NextRebalanceUtc = last?.AddDays(cfg.RebalanceIntervalDays),
            Strategies = perf.StrategyCurves.Select(sc =>
            {
                var s = perf.FinalStatuses.First(x => x.StrategyId == sc.StrategyId);
                return s;
            }).ToList(),
            CurrentRegimeId = perf.LastRegimeId,
            CurrentRegimeLabel = perf.LastRegimeLabel,
        };
        return status;
    }

    public async Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var cfg = await LoadConfigAsync(scope, laneId, ct);
            var active = cfg.Strategies.Where(s => s.IsActive).ToList();
            if (active.Count == 0)
            {
                logger.LogWarning("Rebalance saltato: nessuna strategia attiva.");
                return;
            }

            var to = DateTime.UtcNow;
            var from = to.AddDays(-(cfg.SharpeRollingDays + 5));
            var (candles, _) = await LoadCandlesAsync(scope, cfg, from, to, ct);

            var allocations = new List<RebalanceAllocation>();
            var sharpes = new List<decimal>();
            var obsCounts = new List<int>();
            var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
            var ppy = Statistics.PeriodsPerYear(cfg.Timeframe);

            foreach (var strat in active)
            {
                var eq = candles.Count == 0
                    ? new List<EquityPoint>()
                    : (await engine.RunBacktestAsync(BuildBtConfig(cfg, strat), candles, ct)).EquityCurve;
                sharpes.Add(Statistics.SharpeRatio(eq, ppy));
                obsCounts.Add(eq.Count);
            }

            // Pesatura composita regime-aware se attiva.
            IReadOnlyList<decimal> scores = sharpes;
            if (cfg.RegimeAwareWeighting && candles.Count > 0)
            {
                var regimeCtx = await BuildRegimeContextAsync(cfg, from, to, ct);
                var lastTs = DateTime.SpecifyKind(candles[^1].TimestampUtc, DateTimeKind.Utc);
                if (regimeCtx is not null && regimeCtx.RegimeByTimestamp.TryGetValue(lastTs, out var reg))
                {
                    var regimePerf = active.Select(a => regimeCtx.RegimeSharpe(reg.RegimeId, a.StrategyName)).ToArray();
                    scores = CompositeScores([.. sharpes], regimePerf);
                    logger.LogInformation("Rebalancing in regime '{Label}' (ID {Id}).", reg.Label, reg.RegimeId);
                }
            }

            // Shrinkage degli Sharpe verso l'equipeso (riduce il rumore delle stime prima di allocare).
            scores = EnsembleAllocator.ShrinkSharpes(scores, cfg.SharpeShrinkage, obsCounts, cfg.MinSharpeObservations);

            var weights = EnsembleAllocator.ComputeWeights(scores, cfg.MinAllocationPercent / 100m, cfg.MaxAllocationPercent / 100m);

            for (var i = 0; i < active.Count; i++)
            {
                var strat = active[i];
                var newAlloc = weights[i] * 100m;
                allocations.Add(new RebalanceAllocation
                {
                    StrategyId = strat.StrategyId,
                    DisplayName = strat.DisplayName,
                    PreviousAllocation = strat.CurrentAllocation,
                    NewAllocation = newAlloc,
                    RollingSharpe = sharpes[i],
                });
                strat.CurrentAllocation = newAlloc;
                strat.CurrentCapital = weights[i] * cfg.TotalCapital;
            }

            await SaveConfigAsync(scope, laneId, cfg, ct);
            await SaveRebalanceHistoryAsync(scope, laneId, new RebalanceEvent { Timestamp = to, Allocations = allocations, Reason = reason }, ct);

            logger.LogInformation("Rebalanced ensemble ({Reason}): {Allocs}", reason,
                string.Join(", ", allocations.Select(a => $"{a.DisplayName} {a.NewAllocation:F0}%")));
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ simulazione

    /// <summary>
    /// Simula l'ensemble sulla finestra [from, to]: backtesta ogni strategia una volta,
    /// poi cammina candela per candela componendo i capitali allocati e riallocando ogni
    /// <c>RebalanceIntervalDays</c> in base allo Sharpe rolling su <c>SharpeRollingDays</c>.
    /// </summary>
    private async Task<EnsemblePerformance> SimulateAsync(EnsembleConfiguration cfg, DateTime from, DateTime to, CancellationToken ct)
    {
        var perf = new EnsemblePerformance();
        var active = cfg.Strategies.Where(s => s.IsActive).ToList();
        if (active.Count == 0)
        {
            return perf;
        }

        using var scope = scopeFactory.CreateScope();
        var (candles, ppy) = await LoadCandlesAsync(scope, cfg, from, to, ct);
        var n = candles.Count;
        if (n < 3)
        {
            return perf;
        }

        var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();

        // Backtest di ogni strategia -> equity standalone + returns periodici + trade/winrate.
        var standalone = new List<EquityPoint>[active.Count];
        var returns = new decimal[active.Count][];
        var trades = new int[active.Count];
        var winRate = new decimal[active.Count];
        for (var s = 0; s < active.Count; s++)
        {
            var res = await engine.RunBacktestAsync(BuildBtConfig(cfg, active[s]), candles, ct);
            var eq = res.EquityCurve;
            standalone[s] = eq;
            trades[s] = res.TotalTrades;
            winRate[s] = res.WinRate;
            var r = new decimal[eq.Count];
            for (var t = 1; t < eq.Count; t++)
            {
                r[t] = eq[t - 1].Capital > 0m ? eq[t].Capital / eq[t - 1].Capital : 1m;
            }
            returns[s] = r;
        }

        var times = candles.Select(c => DateTime.SpecifyKind(c.TimestampUtc, DateTimeKind.Utc)).ToArray();

        // Contesto regime (se la pesatura regime-aware è attiva e un modello esiste).
        var regimeCtx = cfg.RegimeAwareWeighting ? await BuildRegimeContextAsync(cfg, from, to, ct) : null;
        int? lastRegimeId = null;
        string? lastRegimeLabel = null;

        // Capitale allocato per strategia (start: equipeso).
        var capital = new decimal[active.Count];
        var equal = cfg.TotalCapital / active.Count;
        for (var s = 0; s < active.Count; s++) capital[s] = equal;

        var stratCurves = active.Select(a => new StrategyEquityCurve { StrategyId = a.StrategyId, DisplayName = a.DisplayName }).ToList();
        var totalCurve = new List<EquityPoint>(n);
        var rebalanceHistory = new List<RebalanceEvent>();
        var lastSharpe = new decimal[active.Count];

        // Rebalance iniziale (allocazione equa).
        rebalanceHistory.Add(new RebalanceEvent
        {
            Timestamp = times[0],
            Reason = "Initial",
            Allocations = active.Select((a, i) => new RebalanceAllocation
            {
                StrategyId = a.StrategyId, DisplayName = a.DisplayName,
                PreviousAllocation = 0m, NewAllocation = 100m / active.Count, RollingSharpe = 0m,
            }).ToList(),
        });
        var lastRebalance = times[0];

        for (var t = 0; t < n; t++)
        {
            if (t > 0)
            {
                for (var s = 0; s < active.Count; s++)
                {
                    capital[s] *= returns[s][t];
                }
            }

            var total = capital.Sum();
            for (var s = 0; s < active.Count; s++)
            {
                stratCurves[s].EquityCurve.Add(new EquityPoint { Timestamp = times[t], Capital = capital[s] });
            }
            totalCurve.Add(new EquityPoint { Timestamp = times[t], Capital = total });

            // Rebalance periodico (non sull'ultima candela).
            if (t > 0 && t < n - 1 && (times[t] - lastRebalance).TotalDays >= cfg.RebalanceIntervalDays)
            {
                var sharpes = new decimal[active.Count];
                for (var s = 0; s < active.Count; s++)
                {
                    sharpes[s] = RollingSharpe(standalone[s], times[t], cfg.SharpeRollingDays, ppy);
                    lastSharpe[s] = sharpes[s];
                }

                // Pesatura: composita (regime-aware) se disponibile, altrimenti solo Sharpe rolling.
                decimal[] scores = sharpes;
                if (regimeCtx is not null && regimeCtx.RegimeByTimestamp.TryGetValue(times[t], out var reg))
                {
                    lastRegimeId = reg.RegimeId;
                    lastRegimeLabel = reg.Label;
                    var regimePerf = active.Select(a => regimeCtx.RegimeSharpe(reg.RegimeId, a.StrategyName)).ToArray();
                    scores = CompositeScores(sharpes, regimePerf);
                }

                // Stesso shrinkage del percorso live, per parità simulazione↔operatività.
                scores = EnsembleAllocator.ShrinkSharpes(scores, cfg.SharpeShrinkage);

                var weights = EnsembleAllocator.ComputeWeights(scores, cfg.MinAllocationPercent / 100m, cfg.MaxAllocationPercent / 100m);
                var prevAlloc = capital.Select(c => total > 0m ? c / total * 100m : 0m).ToArray();

                rebalanceHistory.Add(new RebalanceEvent
                {
                    Timestamp = times[t],
                    Reason = "Scheduled",
                    Allocations = active.Select((a, i) => new RebalanceAllocation
                    {
                        StrategyId = a.StrategyId, DisplayName = a.DisplayName,
                        PreviousAllocation = prevAlloc[i], NewAllocation = weights[i] * 100m, RollingSharpe = sharpes[i],
                    }).ToList(),
                });

                for (var s = 0; s < active.Count; s++)
                {
                    capital[s] = weights[s] * total;
                }
                lastRebalance = times[t];
            }
        }

        var finalTotal = capital.Sum();
        perf.TotalEquityCurve = totalCurve;
        perf.StrategyCurves = stratCurves;
        perf.RebalanceHistory = rebalanceHistory;
        perf.TotalReturn = cfg.TotalCapital > 0m ? (finalTotal - cfg.TotalCapital) / cfg.TotalCapital * 100m : 0m;
        perf.TotalSharpe = Statistics.SharpeRatio(totalCurve, ppy);
        perf.MaxDrawdown = MaxDrawdown(totalCurve);
        perf.LastRegimeId = lastRegimeId;
        perf.LastRegimeLabel = lastRegimeLabel;

        perf.FinalStatuses = active.Select((a, i) => new StrategyStatus
        {
            StrategyId = a.StrategyId,
            DisplayName = a.DisplayName,
            CurrentCapital = capital[i],
            Allocation = finalTotal > 0m ? capital[i] / finalTotal * 100m : 0m,
            Pnl = capital[i] - equal,
            PnlPercent = equal > 0m ? (capital[i] - equal) / equal * 100m : 0m,
            RollingSharpe = lastSharpe[i],
            TotalTrades = trades[i],
            WinRate = winRate[i],
            IsActive = true,
        }).ToList();

        return perf;
    }

    private static decimal RollingSharpe(List<EquityPoint> equity, DateTime asOf, int rollingDays, int ppy)
    {
        var start = asOf.AddDays(-rollingDays);
        var slice = equity.Where(p => p.Timestamp >= start && p.Timestamp <= asOf).ToList();
        return Statistics.SharpeRatio(slice, ppy);
    }

    private static decimal MaxDrawdown(List<EquityPoint> curve)
    {
        decimal peak = decimal.MinValue, maxDd = 0m;
        foreach (var p in curve)
        {
            if (p.Capital > peak) peak = p.Capital;
            if (peak > 0m)
            {
                var dd = (peak - p.Capital) / peak * 100m;
                if (dd > maxDd) maxDd = dd;
            }
        }
        return maxDd;
    }

    // ------------------------------------------------------------------ regime-aware

    private sealed record RegimeContext(
        Dictionary<DateTime, (int RegimeId, string Label)> RegimeByTimestamp,
        List<RegimeProfile> Profiles)
    {
        public decimal RegimeSharpe(int regimeId, string strategyName)
        {
            var p = Profiles.FirstOrDefault(x => x.RegimeId == regimeId);
            return p is not null && p.StrategyPerformances.TryGetValue(strategyName, out var perf)
                ? perf.AverageSharpe : 0m;
        }
    }

    private async Task<RegimeContext?> BuildRegimeContextAsync(EnsembleConfiguration cfg, DateTime from, DateTime to, CancellationToken ct)
    {
        var model = await regimeDetector.LoadLatestModelAsync(ct);
        if (model is null || model.Symbol != cfg.Symbol || model.Timeframe != cfg.Timeframe)
        {
            return null;
        }

        var feats = await featureExtractor.ExtractFeaturesAsync(cfg.ExchangeName, cfg.Symbol, cfg.Timeframe, from, to, ct);
        if (feats.Count == 0)
        {
            return null;
        }
        await regimeDetector.LabelFeaturesAsync(feats);

        var map = new Dictionary<DateTime, (int, string)>(feats.Count);
        foreach (var f in feats)
        {
            if (f.RegimeId is int rid)
            {
                map[f.Timestamp] = (rid, f.RegimeLabel ?? $"Regime {rid}");
            }
        }
        // I profili sono serializzati dal RegimeDetector con opzioni di default (PascalCase).
        var profiles = JsonSerializer.Deserialize<List<RegimeProfile>>(model.RegimeProfilesJson) ?? new();
        return new RegimeContext(map, profiles);
    }

    /// <summary>peso composito = 0.6·Sharpe rolling (norm 0-1) + 0.4·perf nel regime (norm 0-1).</summary>
    private static decimal[] CompositeScores(decimal[] rollingSharpe, decimal[] regimePerf)
    {
        var nr = Normalize01(rollingSharpe);
        var rp = Normalize01(regimePerf);
        var comp = new decimal[rollingSharpe.Length];
        for (var i = 0; i < comp.Length; i++)
        {
            comp[i] = 0.6m * nr[i] + 0.4m * rp[i];
        }
        return comp;
    }

    private static decimal[] Normalize01(decimal[] values)
    {
        var res = new decimal[values.Length];
        if (values.Length == 0) return res;
        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        for (var i = 0; i < values.Length; i++)
        {
            res[i] = range > 0m ? (values[i] - min) / range : 0.5m;
        }
        return res;
    }

    private static BacktestConfiguration BuildBtConfig(EnsembleConfiguration cfg, EnsembleStrategy strat) => new()
    {
        ExchangeName = cfg.ExchangeName,
        Symbol = cfg.Symbol,
        Timeframe = cfg.Timeframe,
        InitialCapital = 10_000m,
        PositionSizePercent = 100m,
        FeePercent = 0.1m,
        StrategyName = strat.StrategyName,
        StrategyParameters = new Dictionary<string, decimal>(strat.Parameters),
    };

    private static async Task<(List<OhlcvData> Candles, int Ppy)> LoadCandlesAsync(
        IServiceScope scope, EnsembleConfiguration cfg, DateTime from, DateTime to, CancellationToken ct)
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var candles = await db.OhlcvData
            .Where(c => c.Symbol == cfg.Symbol && c.Timeframe == cfg.Timeframe && c.TimestampUtc >= fromUtc && c.TimestampUtc <= toUtc)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(ct);
        return (candles, Statistics.PeriodsPerYear(cfg.Timeframe));
    }

    // ------------------------------------------------------------------ persistenza

    private static async Task<EnsembleConfiguration> LoadConfigAsync(IServiceScope scope, int laneId, CancellationToken ct)
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.EnsembleStates.Where(e => e.LaneId == laneId).OrderBy(e => e.Id).FirstOrDefaultAsync(ct);
        if (row is null || string.IsNullOrWhiteSpace(row.ConfigurationJson))
        {
            return new EnsembleConfiguration();
        }
        return JsonSerializer.Deserialize<EnsembleConfiguration>(row.ConfigurationJson, Json) ?? new EnsembleConfiguration();
    }

    private static async Task SaveConfigAsync(IServiceScope scope, int laneId, EnsembleConfiguration config, CancellationToken ct)
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.EnsembleStates.Where(e => e.LaneId == laneId).OrderBy(e => e.Id).FirstOrDefaultAsync(ct);
        var json = JsonSerializer.Serialize(config, Json);
        if (row is null)
        {
            db.EnsembleStates.Add(new EnsembleState { LaneId = laneId, ConfigurationJson = json, StatusJson = "{}", LastUpdatedUtc = DateTime.UtcNow });
        }
        else
        {
            row.ConfigurationJson = json;
            row.LastUpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SaveRebalanceHistoryAsync(IServiceScope scope, int laneId, RebalanceEvent ev, CancellationToken ct)
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        db.EnsembleRebalanceHistory.Add(new EnsembleRebalanceHistory
        {
            LaneId = laneId,
            Timestamp = ev.Timestamp,
            Reason = ev.Reason,
            AllocationsJson = JsonSerializer.Serialize(ev.Allocations, Json),
        });
        await db.SaveChangesAsync(ct);
    }
}
