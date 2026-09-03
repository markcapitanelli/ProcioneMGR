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
    ILogger<EnsembleManager> logger,
    // [G3, audit 2026-07-31] Fee per-lato usata nei backtest interni (pesi del ribilanciamento e
    // simulazioni di stato): DEVE essere la stessa che il motore paga (SafetyConfiguration.FeePercent,
    // hot-reload — P2-8), altrimenti i pesi si calcolano su costi diversi da quelli reali. È un
    // Func e non IOptionsMonitor<SafetyConfiguration> per non introdurre la dipendenza
    // Ensemble→Trading che questo file dichiara di voler evitare (vedi GetDecayReportsAsync): la
    // composizione avviene nel composition root, che conosce entrambi. Null (vecchi harness di
    // test) ⇒ 0,1%, il valore storico.
    Func<decimal>? liveFeePercent = null,
    // [K54, 2026-09-02] Che cosa ha detto la ricerca DOPO che l'aspettativa è stata scritta.
    // Opzionale come il resto dei collaboratori: assente ⇒ il monitor di decadimento giudica come
    // ha sempre fatto, contro il numero d'origine. Aggiunge, non sottrae.
    Fleet.IExpectationEvidenceReader? expectationEvidence = null) : IEnsembleManager
{
    private const int DefaultWindowDays = 120;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int LaneId => laneId;

    /// <summary>
    /// [K48] Le scritture che il manager fa per conto proprio: accendere/spegnere l'ensemble e il
    /// ribilanciamento dei pesi. Non passano da una porta, quindi si dichiarano da sole.
    /// </summary>
    private static readonly ConfigWriteContext InternalWrite = ConfigWriteContext.Create(
        ConfigWriteSources.EnsembleManagerInternal,
        "interruttore dell'ensemble o ribilanciamento programmato dei pesi");

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

    public async Task UpdateConfigurationAsync(
        EnsembleConfiguration config, ConfigWriteContext writtenBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writtenBy);
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            await SaveConfigAsync(scope, laneId, config, writtenBy, ct);
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
            await SaveConfigAsync(scope, laneId, cfg, InternalWrite, ct);
        }
        finally { _gate.Release(); }
        logger.LogInformation("Ensemble {State}.", enabled ? "avviato" : "fermato");
    }

    public async Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
    {
        var cfg = await GetConfigurationAsync(ct);
        var to = DateTime.UtcNow;
        var start = from ?? to.AddDays(-DefaultWindowDays);
        var windowDays = Math.Max(1, (int)Math.Round((to - start).TotalDays));
        return await GetOrSimulateAsync(cfg, windowDays, ct);
    }

    // ------------------------------------------------------------------ cache simulazione [F1]

    /// <summary>
    /// [F1 PRD Valore] Cache dell'ultima simulazione per finestra. Prima di questa cache, il poll
    /// della pagina Ensemble (15s) rieseguiva DUE simulazioni complete — un backtest intero per
    /// gamba su 120 e 90 giorni — a ogni giro, per ridisegnare numeri che cambiano solo quando
    /// chiude una candela o cambia la configurazione. La chiave dice esattamente questo: ultima
    /// candela della serie + configurazione serializzata + fee viva (G3: un cambio fee cambia i
    /// risultati, quindi invalida). Il risultato in cache va trattato come IMMUTABILE dai
    /// chiamanti — è lo stesso oggetto condiviso fra letture successive.
    /// </summary>
    private readonly Lock _simCacheSync = new();
    private readonly Dictionary<int, (string Key, EnsemblePerformance Perf)> _simCache = new();

    private async Task<EnsemblePerformance> GetOrSimulateAsync(EnsembleConfiguration cfg, int windowDays, CancellationToken ct)
    {
        var to = DateTime.UtcNow;
        var from = to.AddDays(-windowDays);

        // L'ultima candela della serie è ciò che decide se esiste qualcosa di nuovo da simulare:
        // lookup sull'indice (Symbol, Timeframe, TimestampUtc), pochi ms. Che `from` scivoli in
        // avanti fra due hit della cache è irrilevante per costruzione: il bordo sinistro perde al
        // più una candela vecchia, ed è il bordo destro (la candela nuova) a cambiare i verdetti.
        DateTime? lastCandle;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await dbf.CreateDbContextAsync(ct);
            lastCandle = await db.OhlcvData
                .Where(c => c.Symbol == cfg.Symbol && c.Timeframe == cfg.Timeframe)
                .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);
        }

        var key = $"{lastCandle?.Ticks ?? 0}|{liveFeePercent?.Invoke() ?? 0.1m}|{JsonSerializer.Serialize(cfg, Json)}";
        lock (_simCacheSync)
        {
            if (_simCache.TryGetValue(windowDays, out var hit) && hit.Key == key)
            {
                return hit.Perf;
            }
        }

        // Fuori dal lock: una corsa doppia su miss concorrente produce lo stesso risultato due
        // volte (deterministico) e l'ultima scrittura vince — accettabile, mai bloccante.
        var perf = await SimulateAsync(cfg, from, to, ct);
        lock (_simCacheSync)
        {
            _simCache[windowDays] = (key, perf);
        }
        return perf;
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
            // [I13b] Il filtro sul SIMBOLO ATTUALE, che mancava. Le corsie hanno vite precedenti:
            // una riassegnazione, o una coppia cambiata a mano senza riscrivere le gambe, faceva
            // nascere lo Sharpe "realizzato" di una gamba da trade fatti su DUE mercati diversi —
            // e nessuna riga lo diceva. Il criterio e' il simbolo attuale (AF2c-2).
            var legTrades = db.TradeRecords.AsNoTracking()
                .Where(t => t.LaneId == laneId && t.StrategyId == s.StrategyId);

            // [C1b] E il filtro sui fill ROTTI, che il simbolo da solo non copre. La corsia 2 ha in
            // tabella un trade a -227.340%: un fill patologico del testnet del 9 luglio, chiuso dal
            // FillSanityCheck il 18 luglio — ma quel guardiano protegge le righe nuove, non quelle
            // gia' scritte. Una sola riga cosi' decide da sola lo Sharpe "realizzato" di una gamba.
            var tetto = options.MaxPlausibleTradeReturnPercent;
            var plausibili = tetto > 0m
                ? legTrades.Where(t => t.PnlPercent > -tetto && t.PnlPercent < tetto)
                : legTrades;

            // [K39, PRD autonomia-piena — Fase 3, 2026-09-01] IL TERZO FILTRO, CHE MANCAVA:
            // LA GAMBA NON PUO' ESSERE GIUDICATA DA TRADE PIU' VECCHI DI SE STESSA.
            //
            // Simbolo e fill rotti non bastano. `TradeRecords` porta i tempi della CANDELA, e al
            // riavvio del motore il feed rigioca fino a trenta giorni di storia: le righe di replay
            // hanno lo StrategyId e il simbolo ATTUALI, quindi passano entrambi i filtri esistenti.
            //
            // Misurato il 2026-09-01 sulle cinque corsie di flotta: delle 66 righe che questo
            // metodo leggeva, **65 erano precedenti alla creazione della gamba che dicevano di
            // descrivere**. La corsia 4 era l'unica gamba «misurabile» della piattaforma
            // (20 trade su 20 richiesti) e la sua finestra era di venti righe di replay su venti.
            // La pagina scriveva «Sharpe realizzato · trade analizzati: 20» e, a un link di
            // distanza, /trading diceva «operazioni chiuse: 0». Venti contro zero, stessa gamba.
            //
            // L'ancora e' ExpectedSharpeAtUtc, timbrato alla CREAZIONE della gamba (RF0, e lo
            // scrive ogni percorso di schieramento): e' l'istante di nascita di questa ipotesi su
            // questa corsia. E' la stessa correzione che K18 ha fatto al ritiro — numeratore e
            // denominatore dalla stessa storia — applicata al monitor, che non l'aveva avuta.
            //
            // Gamba SENZA timbro: non si misura. E' fail-closed voluto — misurare su una finestra
            // che non si sa dove cominci e' peggio che dire «non lo so», ed e' esattamente cio' che
            // questo metodo faceva finora. Le gambe senza timbro sono quelle delle corsie
            // d'impronta (RF0, item K22 della Fase 3), e questo rende quel lavoro visibile invece
            // di mascherarlo con un numero.
            var ancora = s.ExpectedSharpeAtUtc;
            var ancorati = ancora is DateTime nascita
                ? plausibili.Where(t => t.ClosedAtUtc >= nascita)
                : plausibili.Where(t => false);

            // [K43] E la deduplica, PRIMA del Take: le repliche di replay hanno la stessa gamba,
            // lo stesso simbolo e — se posteriori all'ancora — passano anche il filtro di K39.
            // Deduplicare dopo aver preso le ultime venti darebbe una finestra piu' corta di
            // quella richiesta senza dirlo; deduplicare prima la riempie di trade veri.
            var candidateTrades = await ancorati.Where(t => t.Symbol == cfg.Symbol).ToListAsync(ct);
            var senzaRepliche = Trading.TradeDeduplication.Distinti(candidateTrades);
            var repliche = Trading.TradeDeduplication.Repliche(candidateTrades, senzaRepliche);
            var recentTrades = senzaRepliche
                .OrderByDescending(t => t.ClosedAtUtc)
                .Take(options.WindowTradeCount)
                .ToList();

            // Quanti ne sono stati scartati: un conteggio piu' basso senza spiegazione si legge
            // come un guasto, e la spiegazione qui e' "quella corsia faceva un altro mestiere".
            var excluded = await plausibili.CountAsync(t => t.Symbol != cfg.Symbol, ct);
            // [K39] E quanti sono stati scartati perche' PRECEDENTI alla gamba: e' il numero che
            // spiega perche' una corsia con decine di righe in tabella risulta non misurabile, e
            // senza di esso la pagina direbbe «0 trade» dove la verita' e' «0 trade DI QUESTA
            // gamba, 27 di quelle prima».
            var primaDellaGamba = ancora is DateTime n2
                ? await plausibili.CountAsync(t => t.Symbol == cfg.Symbol && t.ClosedAtUtc < n2, ct)
                : await plausibili.CountAsync(t => t.Symbol == cfg.Symbol, ct);
            var rotti = tetto > 0m
                ? await legTrades.CountAsync(t => t.PnlPercent <= -tetto || t.PnlPercent >= tetto, ct)
                : 0;

            // [K54, 2026-09-02] Che cosa ha detto la ricerca DOPO che l'aspettativa è stata
            // scritta. Senza questo, il rapporto misura quanto era ottimistica la notte in cui la
            // gamba è stata proposta, non come sta andando adesso: la corsia 6 porta 1,8754 del
            // 21 agosto e le undici rivalutazioni successive della stessa identica ipotesi hanno
            // mediana 0,479. Assente o insufficiente ⇒ il verdetto resta quello storico.
            var evidenza = expectationEvidence is null
                ? null
                : await expectationEvidence.ReadAsync(cfg, s, ct);

            // [M5] Il timeframe della corsia porta il realizzato sulla stessa base per-candela
            // dell'atteso (vedi StrategyDecayMonitor.BuildPeriodReturns).
            var report = decayMonitor.Analyze(s, recentTrades, cfg.Timeframe, options, evidenza);
            report.Symbol = cfg.Symbol;
            report.TradesExcludedOtherSymbol = excluded;
            report.TradesExcludedImplausible = rotti;
            report.TradesExcludedBeforeLeg = primaDellaGamba;
            report.LegHasNoBirthStamp = ancora is null;
            report.TradesExcludedDuplicate = repliche;
            reports.Add(report);

            if (rotti > 0)
            {
                logger.LogWarning(
                    "Corsia {Lane}, gamba {StrategyId}: {Rotti} operazioni scartate dal calcolo perché il rendimento riportato supera ±{Tetto}% — sono fill rotti rimasti in tabella, non perdite. Vanno bonificate.",
                    laneId, s.StrategyId, rotti, tetto);
            }

            if (report.IsAlert)
            {
                // [Revisione 2026-09-03] Il log racconta il rapporto su cui il verdetto è stato preso
                // (la stima corrente quando l'evidenza K54 decide), non l'atteso d'origine.
                logger.LogWarning(
                    "Decadimento rilevato per {Strategy} ({StrategyId}): Sharpe realizzato {Realized:F2} vs {Metro} {Expected:F2} ({Ratio:P0}) su {Trades} trade.",
                    s.DisplayName, s.StrategyId, report.RealizedSharpe,
                    report.SharpeRatioVsEvidence is not null ? "stima corrente" : "atteso",
                    report.Evidence is { Giudicabile: true } ev ? ev.Corrente : report.ExpectedSharpe,
                    report.SharpeRatioVsEvidence ?? report.SharpeRatio, report.TradeCount);
            }
        }
        return reports;
    }

    public async Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigurationAsync(ct);
        // [F1] Stessa corsa (e stessa cache) di GetPerformanceAsync sulla finestra di default: lo
        // status è una proiezione della simulazione, non una seconda simulazione.
        var perf = await GetOrSimulateAsync(cfg, DefaultWindowDays, ct);

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

            await SaveConfigAsync(scope, laneId, cfg, InternalWrite, ct);
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
        // Modello DELLA SERIE di questa corsia: prima si chiedeva il più recente fra tutti e lo si
        // scartava se non combaciava, il che rendeva i pesi regime-aware un privilegio della sola
        // corsia che per caso corrispondeva all'ultimo addestramento.
        var model = await regimeDetector.LoadActiveModelAsync(cfg.Symbol, cfg.Timeframe, ct);
        if (model is null)
        {
            return null;
        }

        var feats = await featureExtractor.ExtractFeaturesAsync(cfg.ExchangeName, cfg.Symbol, cfg.Timeframe, from, to, ct);
        if (feats.Count == 0)
        {
            return null;
        }
        await regimeDetector.LabelFeaturesAsync(feats, cfg.Symbol, cfg.Timeframe, ct);

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

    private BacktestConfiguration BuildBtConfig(EnsembleConfiguration cfg, EnsembleStrategy strat) => new()
    {
        ExchangeName = cfg.ExchangeName,
        Symbol = cfg.Symbol,
        Timeframe = cfg.Timeframe,
        // Capitale e size fissi: lo Sharpe che decide i pesi è invariante di scala, contano solo
        // i ritorni relativi. La fee invece NO: è quella viva del motore (vedi liveFeePercent).
        //
        // [RF0, 2026-08-22] Questa affermazione era FALSA fino a oggi, ed è la ragione per cui la
        // si data. Col risk-free al 2% sottratto sull'equity totale, lo Sharpe valeva
        // media/σ − rf/σ, e σ scala con la taglia mentre rf no: la stessa strategia dosata al 100%
        // e al 10% dava due numeri diversi (dazio 0,036 contro 0,365 su serie reali). Da oggi
        // l'invarianza è vera **al prim'ordine**: lo scarto residuo misurato fra taglia 50% e 5% è
        // 0,015–0,077, e viene dal fatto che il nozionale è fissato all'ingresso, non dal rf.
        InitialCapital = 10_000m,
        PositionSizePercent = 100m,
        FeePercent = liveFeePercent?.Invoke() ?? 0.1m,
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

        // [2026-08-06] Mai la barra in FORMAZIONE. I chiamanti passano `to = DateTime.UtcNow`, e la
        // riga della candela corrente è già a database (l'ingestione REST scrive anche l'ultima
        // kline incompleta): finiva in coda alla finestra come se fosse un rendimento compiuto,
        // mentre è un pezzo di barra che cambia a ogni sync. È la stessa classe di difetto che sul
        // motore di trading impediva alle uscite protettive di scattare (vedi
        // TradingWorker.LastClosedBarOpenUtc): qui non è pericolosa, è solo un dato sporco — e
        // lasciarla sarebbe tenere due regole diverse sulla stessa domanda.
        if (Ingestion.SeriesFreshness.LastClosedBarOpenUtc(cfg.Timeframe, DateTime.UtcNow) is DateTime ultimaChiusa
            && toUtc > ultimaChiusa)
        {
            toUtc = ultimaChiusa;
        }

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

    private static async Task SaveConfigAsync(
        IServiceScope scope, int laneId, EnsembleConfiguration config, ConfigWriteContext writtenBy, CancellationToken ct)
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.EnsembleStates.Where(e => e.LaneId == laneId).OrderBy(e => e.Id).FirstOrDefaultAsync(ct);
        var json = JsonSerializer.Serialize(config, Json);
        var precedente = row?.ConfigurationJson;
        var now = DateTime.UtcNow;
        if (row is null)
        {
            db.EnsembleStates.Add(new EnsembleState { LaneId = laneId, ConfigurationJson = json, StatusJson = "{}", LastUpdatedUtc = now });
        }
        else
        {
            row.ConfigurationJson = json;
            row.LastUpdatedUtc = now;
        }

        // [K48, 2026-09-02] LA SCRITTURA SI REGISTRA, NELLA STESSA TRANSAZIONE.
        //
        // Riscrivere la configurazione di una corsia è l'azione meno reversibile della piattaforma:
        // `EnsembleStates` tiene un solo ConfigurationJson, quindi la configurazione precedente non
        // è conservata da nessuna parte e non è ricostruibile guardando la corsia dopo. Eppure fino
        // a oggi la si poteva fare senza lasciare traccia — ed è successo: il 31/08 le corsie 4 e 6
        // sono state riscritte e K37 ha poi dovuto dichiarare la loro provenienza NON ACCERTABILE,
        // su un campo che governa il tetto grigio.
        //
        // Stessa SaveChangesAsync della configurazione, non due passi in fila: un registro che può
        // fallire DOPO l'azione registra solo le azioni fortunate.
        //
        // Si scrive solo quando qualcosa cambia davvero: un Save che non muove nulla (l'operatore
        // apre e salva senza toccare) non è un evento, e riempirne l'audit renderebbe illeggibile
        // il registro proprio a chi cerca l'unica riga che conta.
        if (!string.Equals(precedente, json, StringComparison.Ordinal))
        {
            db.TradingAuditLogs.Add(new Trading.TradingAuditLog
            {
                LaneId = laneId,
                TimestampUtc = now,
                Action = "EnsembleConfigWritten",
                Details = JsonSerializer.Serialize(new
                {
                    source = writtenBy.Source,
                    reason = writtenBy.Reason,
                    symbol = config.Symbol,
                    timeframe = config.Timeframe,
                    legs = config.Strategies.Where(x => x.IsActive).Select(x => x.StrategyName).ToList(),
                    creata = precedente is null,
                }),
            });
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
