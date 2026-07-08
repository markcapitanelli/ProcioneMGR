using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Ensemble;

public class EnsembleConfiguration
{
    public string ExchangeName { get; set; } = "Binance";
    public string Symbol { get; set; } = "BTC/USDT";
    public string Timeframe { get; set; } = "1h";
    public decimal TotalCapital { get; set; } = 10000m;
    public int RebalanceIntervalDays { get; set; } = 7;
    public int SharpeRollingDays { get; set; } = 30;
    public decimal MinAllocationPercent { get; set; } = 5m;
    public decimal MaxAllocationPercent { get; set; } = 40m;
    public List<EnsembleStrategy> Strategies { get; set; } = new();
    public bool IsEnabled { get; set; }

    /// <summary>
    /// True per operare su Futures perpetui a leva invece che Spot. Campo primitivo (non
    /// l'enum MarketType di Services.Trading) per evitare una dipendenza incrociata
    /// Ensemble→Trading, dato che Trading già dipende da Ensemble (IEnsembleManager).
    /// </summary>
    public bool IsFutures { get; set; }

    /// <summary>Leva richiesta se IsFutures=true (ignorata per lo Spot). Va sotto SafetyConfiguration.MaxLeverageAllowed.</summary>
    public int Leverage { get; set; } = 1;

    /// <summary>
    /// Se true la pesatura è "regime-aware": peso = 0.6·Sharpe rolling (norm) + 0.4·perf nel
    /// regime corrente (norm). Se false usa solo lo Sharpe rolling (comportamento Fase 6).
    /// </summary>
    public bool RegimeAwareWeighting { get; set; }

    /// <summary>
    /// RiskFactor95 Monte-Carlo aggregato dell'ensemble al momento del deploy (dalla
    /// <c>PipelineRecommendation.RiskLimits</c>), memorizzato qui perché il confronto
    /// "corrente vs candidato" del ciclo di ri-applica automatica (<see cref="EnsembleComparator"/>)
    /// possa valutare anche il rischio dell'ensemble già schierato, non solo il suo Sharpe.
    /// 0 = sconosciuto (ensemble configurato prima di questo campo, o a mano) → il comparatore
    /// ricade sul solo Sharpe. Campo JSON, nessuna migrazione: config precedenti restano valide.
    /// </summary>
    public decimal ExpectedRiskFactor95 { get; set; }
}

public class EnsembleStrategy
{
    public string StrategyId { get; set; } = Guid.NewGuid().ToString("N");
    public string StrategyName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Dictionary<string, decimal> Parameters { get; set; } = new();
    public decimal CurrentAllocation { get; set; }
    public decimal CurrentCapital { get; set; }
    public bool IsActive { get; set; } = true;
    public int? SavedStrategyId { get; set; }

    /// <summary>Valorizzato se questa "strategia" è in realtà un modello ML (StrategyName="Ml"): l'Id del SavedMlModel referenziato in Parameters["SavedModelId"], solo per mostrarlo in UI.</summary>
    public int? SavedMlModelId { get; set; }

    /// <summary>
    /// Stop/target validati nel backtest (es. dalla BestStopVariant di un pipeline run), applicati
    /// automaticamente da <see cref="ProcioneMGR.Services.Trading.TradingEngine"/> all'apertura di
    /// ogni posizione per questa gamba — null = nessuno stop automatico (comportamento invariato).
    /// Nullable per retrocompatibilità: ensemble creati prima di questi campi restano validi.
    /// </summary>
    public decimal? StopLossPercent { get; set; }
    public decimal? TakeProfitPercent { get; set; }
    public decimal? TrailingStopPercent { get; set; }

    /// <summary>
    /// Metriche di holdout dal backtest che ha validato questa gamba (es. dal pipeline run o da
    /// una strategia ottimizzata/salvata), usate da <see cref="ProcioneMGR.Services.Monitoring.IStrategyDecayMonitor"/>
    /// come termine di paragone per la performance realizzata dal vivo. Null = nessun confronto
    /// possibile (comportamento invariato: nessun monitoraggio per questa gamba).
    /// </summary>
    public decimal? ExpectedSharpe { get; set; }
    public decimal? ExpectedProfitFactor { get; set; }
    public decimal? ExpectedMaxDrawdown { get; set; }

    /// <summary>
    /// Algoritmo di esecuzione dell'apertura su Testnet/Live: "Twap"|"Vwap"|"Iceberg" per
    /// distribuire l'ordine nel tempo (riduzione impatto), oppure null/"Immediate" per il
    /// comportamento odierno (un solo ordine). Ignorato in Paper e se il master switch
    /// <see cref="ProcioneMGR.Services.Trading.LiveExecutionOptions.Enabled"/> è off. Blob JSON,
    /// nessuna migrazione: config precedenti restano valide (default = Immediate).
    /// </summary>
    public string? ExecutionAlgorithmName { get; set; }

    /// <summary>Finestra di esecuzione in minuti per questa gamba; null = usa il default globale.</summary>
    public int? ExecutionWindowMinutes { get; set; }
}

public class EnsembleStatus
{
    public bool IsRunning { get; set; }
    public DateTime? LastRebalanceUtc { get; set; }
    public DateTime? NextRebalanceUtc { get; set; }
    public decimal TotalCapital { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal TotalPnlPercent { get; set; }
    public List<StrategyStatus> Strategies { get; set; } = new();

    /// <summary>Regime di mercato corrente (se la pesatura regime-aware è attiva e un modello esiste).</summary>
    public int? CurrentRegimeId { get; set; }
    public string? CurrentRegimeLabel { get; set; }
}

public class StrategyStatus
{
    public string StrategyId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal CurrentCapital { get; set; }
    public decimal Allocation { get; set; }
    public decimal Pnl { get; set; }
    public decimal PnlPercent { get; set; }
    public decimal RollingSharpe { get; set; }
    public int TotalTrades { get; set; }
    public decimal WinRate { get; set; }
    public bool IsActive { get; set; }
}

public class EnsemblePerformance
{
    public List<EquityPoint> TotalEquityCurve { get; set; } = new();
    public List<StrategyEquityCurve> StrategyCurves { get; set; } = new();
    public List<RebalanceEvent> RebalanceHistory { get; set; } = new();
    public decimal TotalReturn { get; set; }
    public decimal TotalSharpe { get; set; }
    public decimal MaxDrawdown { get; set; }

    /// <summary>Stato per-strategia a fine simulazione (capitale, allocazione, Sharpe rolling...).</summary>
    public List<StrategyStatus> FinalStatuses { get; set; } = new();

    /// <summary>Regime corrente a fine simulazione (regime-aware).</summary>
    public int? LastRegimeId { get; set; }
    public string? LastRegimeLabel { get; set; }
}

public class StrategyEquityCurve
{
    public string StrategyId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<EquityPoint> EquityCurve { get; set; } = new();
}

public class RebalanceEvent
{
    public DateTime Timestamp { get; set; }
    public List<RebalanceAllocation> Allocations { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public class RebalanceAllocation
{
    public string StrategyId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal PreviousAllocation { get; set; }
    public decimal NewAllocation { get; set; }
    public decimal RollingSharpe { get; set; }
}
