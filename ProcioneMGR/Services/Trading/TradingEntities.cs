namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Stato persistito del trading engine (riga singola). Garantisce idempotenza: al restart
/// il sistema ricostruisce lo stato (running/mode/capitale/emergency) dal DB.
/// </summary>
public class TradingEngineState
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia). Ogni corsia ha la propria istanza di TradingEngine/EnsembleManager, mai condivise.</summary>
    public int LaneId { get; set; }

    public TradingMode Mode { get; set; }
    public MarketType MarketType { get; set; } = MarketType.Spot;

    /// <summary>Leva della sessione (1 per Spot; impostata via SetLeverageAsync all'avvio per Futures).</summary>
    public int Leverage { get; set; } = 1;

    public bool IsRunning { get; set; }
    public string ExchangeName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1h";

    public decimal TotalCapital { get; set; }
    public decimal AvailableCapital { get; set; }
    public decimal RealizedPnl { get; set; }

    /// <summary>Equity massima raggiunta (per il calcolo del drawdown).</summary>
    public decimal PeakEquity { get; set; }

    /// <summary>PnL realizzato nelle ultime 24h (rolling), per il safety check daily-loss.</summary>
    public decimal DailyPnl { get; set; }
    public DateTime DailyAnchorUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? LastOrderUtc { get; set; }
    public bool IsEmergencyStopped { get; set; }
    public string? EmergencyStopReason { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Audit trail: ogni azione di trading (ordine, chiusura, emergency, start/stop) è loggata.</summary>
public class TradingAuditLog
{
    public int Id { get; set; }

    /// <summary>Corsia di trading che ha generato questa voce di audit (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public DateTime TimestampUtc { get; set; }

    /// <summary>"PlaceOrder", "OrderRejected", "ClosePosition", "EmergencyStop", "StartEngine", "StopEngine".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>JSON con i dettagli dell'azione.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>Utente che ha eseguito l'azione (null per il background worker).</summary>
    public string? UserId { get; set; }

    public TradingMode Mode { get; set; }
}
