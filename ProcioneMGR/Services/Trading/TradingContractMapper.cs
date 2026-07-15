using Google.Protobuf.WellKnownTypes;
using ProcioneMGR.Services.Backtesting;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Mappatura fra i modelli di dominio del trading e i messaggi di trading.proto (Fase 2b). Usata da
/// ENTRAMBI i lati del filo — <c>TradingCommandServiceImpl</c> (servizio) e
/// <see cref="RemoteTradingEngineClient"/> (monolite) — così la proiezione è definita una volta sola
/// e non può divergere fra chi scrive e chi legge.
///
/// Gli enum sono mappati a switch esaustivo, mai per cast ordinale (stesso patto di MlStageMapper in
/// Fase 2a): TradingMode.Paper vale 0 in C# ma TRADING_MODE_PAPER vale 1 in proto3 (lo zero è
/// riservato a UNSPECIFIED), quindi un cast diretto trasformerebbe Paper in Testnet — cioè una
/// simulazione in una sessione con soldi veri sull'exchange. Uno switch esplicito che lancia
/// sull'ignoto rende impossibile quella classe di errore.
/// </summary>
public static class TradingContractMapper
{
    // ---------------------------------------------------------------------------- enum

    public static Proto.TradingMode ToProto(TradingMode mode) => mode switch
    {
        TradingMode.Paper => Proto.TradingMode.Paper,
        TradingMode.Testnet => Proto.TradingMode.Testnet,
        TradingMode.Live => Proto.TradingMode.Live,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "TradingMode di dominio non mappato al contratto proto."),
    };

    public static TradingMode FromProto(Proto.TradingMode mode) => mode switch
    {
        Proto.TradingMode.Paper => TradingMode.Paper,
        Proto.TradingMode.Testnet => TradingMode.Testnet,
        Proto.TradingMode.Live => TradingMode.Live,
        // Unspecified qui significherebbe "avvia la lane in una modalità che non so": mai indovinare.
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "TradingMode proto non mappabile al dominio (Unspecified?)."),
    };

    public static Proto.MarketType ToProto(MarketType type) => type switch
    {
        MarketType.Spot => Proto.MarketType.Spot,
        MarketType.Futures => Proto.MarketType.Futures,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "MarketType di dominio non mappato al contratto proto."),
    };

    public static MarketType FromProto(Proto.MarketType type) => type switch
    {
        Proto.MarketType.Spot => MarketType.Spot,
        Proto.MarketType.Futures => MarketType.Futures,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "MarketType proto non mappabile al dominio (Unspecified?)."),
    };

    public static Proto.OrderSide ToProto(OrderSide side) => side switch
    {
        OrderSide.Buy => Proto.OrderSide.Buy,
        OrderSide.Sell => Proto.OrderSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "OrderSide di dominio non mappato al contratto proto."),
    };

    public static OrderSide FromProto(Proto.OrderSide side) => side switch
    {
        Proto.OrderSide.Buy => OrderSide.Buy,
        Proto.OrderSide.Sell => OrderSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "OrderSide proto non mappabile al dominio (Unspecified?)."),
    };

    // ------------------------------------------------------------------------ timestamp

    /// <summary>
    /// Timestamp.FromDateTime PRETENDE Kind=Utc, ma i DateTime che arrivano da Postgres hanno
    /// Kind=Unspecified (switch "legacy timestamp behavior": semantica naive-UTC, i tick sono già
    /// UTC). Senza questo SpecifyKind la conversione lancerebbe a runtime sul primo dato letto dal
    /// DB. Nessuna traslazione di orario: si dichiara ciò che il valore già è.
    /// </summary>
    public static Timestamp ToProto(DateTime utc) =>
        Timestamp.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static Timestamp? ToProtoNullable(DateTime? utc) => utc is null ? null : ToProto(utc.Value);

    /// <summary>Ritorna Kind=Utc (non Unspecified come il DB): il valore è esplicitamente UTC.</summary>
    public static DateTime FromProto(Timestamp ts) => ts.ToDateTime();

    public static DateTime? FromProtoNullable(Timestamp? ts) => ts is null ? null : ts.ToDateTime();

    // ----------------------------------------------------------------------- lane status

    public static Proto.GetLaneStatusResponse ToProto(TradingEngineStatus s, int laneId) => new()
    {
        LaneId = laneId,
        Mode = ToProto(s.Mode),
        MarketType = ToProto(s.MarketType),
        Leverage = s.Leverage,
        Running = s.IsRunning,
        ExchangeName = s.ExchangeName,
        Symbol = s.Symbol,
        TotalCapital = DecimalValueMapper.ToProto(s.TotalCapital),
        AvailableCapital = DecimalValueMapper.ToProto(s.AvailableCapital),
        UsedCapital = DecimalValueMapper.ToProto(s.UsedCapital),
        TotalPnl = DecimalValueMapper.ToProto(s.TotalPnl),
        TotalPnlPercent = DecimalValueMapper.ToProto(s.TotalPnlPercent),
        DailyPnl = DecimalValueMapper.ToProto(s.DailyPnl),
        MaxDrawdown = DecimalValueMapper.ToProto(s.MaxDrawdown),
        TotalTrades = s.TotalTrades,
        OpenPositionCount = s.OpenPositionCount,
        WinRate = DecimalValueMapper.ToProto(s.WinRate),
        StartedAtUtc = ToProtoNullable(s.StartedAtUtc),
        LastOrderUtc = ToProtoNullable(s.LastOrderUtc),
        EmergencyStopped = s.IsEmergencyStopped,
        // proto3 non ha stringhe nullable: "" sul filo == null nel dominio (vedi FromProto).
        EmergencyStopReason = s.EmergencyStopReason ?? string.Empty,
    };

    public static TradingEngineStatus FromProto(Proto.GetLaneStatusResponse r) => new()
    {
        Mode = FromProto(r.Mode),
        MarketType = FromProto(r.MarketType),
        Leverage = r.Leverage,
        IsRunning = r.Running,
        ExchangeName = r.ExchangeName,
        Symbol = r.Symbol,
        TotalCapital = DecimalValueMapper.FromProtoOrZero(r.TotalCapital),
        AvailableCapital = DecimalValueMapper.FromProtoOrZero(r.AvailableCapital),
        UsedCapital = DecimalValueMapper.FromProtoOrZero(r.UsedCapital),
        TotalPnl = DecimalValueMapper.FromProtoOrZero(r.TotalPnl),
        TotalPnlPercent = DecimalValueMapper.FromProtoOrZero(r.TotalPnlPercent),
        DailyPnl = DecimalValueMapper.FromProtoOrZero(r.DailyPnl),
        MaxDrawdown = DecimalValueMapper.FromProtoOrZero(r.MaxDrawdown),
        TotalTrades = r.TotalTrades,
        OpenPositionCount = r.OpenPositionCount,
        WinRate = DecimalValueMapper.FromProtoOrZero(r.WinRate),
        StartedAtUtc = FromProtoNullable(r.StartedAtUtc),
        LastOrderUtc = FromProtoNullable(r.LastOrderUtc),
        IsEmergencyStopped = r.EmergencyStopped,
        EmergencyStopReason = string.IsNullOrEmpty(r.EmergencyStopReason) ? null : r.EmergencyStopReason,
    };

    // ------------------------------------------------------------------------- posizioni

    public static Proto.OpenPosition ToProto(OpenPosition p) => new()
    {
        Id = p.Id,
        LaneId = p.LaneId,
        PositionId = p.PositionId,
        StrategyId = p.StrategyId,
        Symbol = p.Symbol,
        Side = ToProto(p.Side),
        EntryPrice = DecimalValueMapper.ToProto(p.EntryPrice),
        Quantity = DecimalValueMapper.ToProto(p.Quantity),
        StopLoss = DecimalValueMapper.ToProtoNullable(p.StopLoss),
        TakeProfit = DecimalValueMapper.ToProtoNullable(p.TakeProfit),
        TrailingStopPercent = DecimalValueMapper.ToProtoNullable(p.TrailingStopPercent),
        BestPriceSinceEntry = DecimalValueMapper.ToProtoNullable(p.BestPriceSinceEntry),
        OpenedAtUtc = ToProto(p.OpenedAtUtc),
        CurrentPrice = DecimalValueMapper.ToProto(p.CurrentPrice),
        UnrealizedPnl = DecimalValueMapper.ToProto(p.UnrealizedPnl),
        UnrealizedPnlPercent = DecimalValueMapper.ToProto(p.UnrealizedPnlPercent),
        ExchangeOrderId = p.ExchangeOrderId ?? string.Empty,
        OpenedInMode = ToProto(p.OpenedInMode),
        StopOrderId = p.StopOrderId ?? string.Empty,
        TakeProfitOrderId = p.TakeProfitOrderId ?? string.Empty,
        Leverage = p.Leverage,
        LiquidationPrice = DecimalValueMapper.ToProtoNullable(p.LiquidationPrice),
        MarginBalance = DecimalValueMapper.ToProto(p.MarginBalance),
    };

    public static OpenPosition FromProto(Proto.OpenPosition p) => new()
    {
        Id = p.Id,
        LaneId = p.LaneId,
        PositionId = p.PositionId,
        StrategyId = p.StrategyId,
        Symbol = p.Symbol,
        Side = FromProto(p.Side),
        EntryPrice = DecimalValueMapper.FromProtoOrZero(p.EntryPrice),
        Quantity = DecimalValueMapper.FromProtoOrZero(p.Quantity),
        StopLoss = DecimalValueMapper.FromProtoNullable(p.StopLoss),
        TakeProfit = DecimalValueMapper.FromProtoNullable(p.TakeProfit),
        TrailingStopPercent = DecimalValueMapper.FromProtoNullable(p.TrailingStopPercent),
        BestPriceSinceEntry = DecimalValueMapper.FromProtoNullable(p.BestPriceSinceEntry),
        OpenedAtUtc = FromProto(p.OpenedAtUtc),
        CurrentPrice = DecimalValueMapper.FromProtoOrZero(p.CurrentPrice),
        UnrealizedPnl = DecimalValueMapper.FromProtoOrZero(p.UnrealizedPnl),
        UnrealizedPnlPercent = DecimalValueMapper.FromProtoOrZero(p.UnrealizedPnlPercent),
        ExchangeOrderId = string.IsNullOrEmpty(p.ExchangeOrderId) ? null : p.ExchangeOrderId,
        OpenedInMode = FromProto(p.OpenedInMode),
        StopOrderId = string.IsNullOrEmpty(p.StopOrderId) ? null : p.StopOrderId,
        TakeProfitOrderId = string.IsNullOrEmpty(p.TakeProfitOrderId) ? null : p.TakeProfitOrderId,
        Leverage = p.Leverage,
        LiquidationPrice = DecimalValueMapper.FromProtoNullable(p.LiquidationPrice),
        MarginBalance = DecimalValueMapper.FromProtoOrZero(p.MarginBalance),
    };

    // ----------------------------------------------------------------------- performance

    public static Proto.GetPerformanceResponse ToProto(TradingPerformance p)
    {
        var r = new Proto.GetPerformanceResponse
        {
            TotalReturn = DecimalValueMapper.ToProto(p.TotalReturn),
            SharpeRatio = DecimalValueMapper.ToProto(p.SharpeRatio),
            MaxDrawdown = DecimalValueMapper.ToProto(p.MaxDrawdown),
            TotalTrades = p.TotalTrades,
            WinRate = DecimalValueMapper.ToProto(p.WinRate),
            AverageWin = DecimalValueMapper.ToProto(p.AverageWin),
            AverageLoss = DecimalValueMapper.ToProto(p.AverageLoss),
            ProfitFactor = DecimalValueMapper.ToProto(p.ProfitFactor),
        };
        r.EquityCurve.AddRange(p.EquityCurve.Select(e => new Proto.EquityPoint
        {
            Timestamp = ToProto(e.Timestamp),
            Capital = DecimalValueMapper.ToProto(e.Capital),
        }));
        r.Trades.AddRange(p.Trades.Select(ToProto));
        return r;
    }

    public static TradingPerformance FromProto(Proto.GetPerformanceResponse r) => new()
    {
        EquityCurve = r.EquityCurve.Select(e => new EquityPoint
        {
            Timestamp = FromProto(e.Timestamp),
            Capital = DecimalValueMapper.FromProtoOrZero(e.Capital),
        }).ToList(),
        TotalReturn = DecimalValueMapper.FromProtoOrZero(r.TotalReturn),
        SharpeRatio = DecimalValueMapper.FromProtoOrZero(r.SharpeRatio),
        MaxDrawdown = DecimalValueMapper.FromProtoOrZero(r.MaxDrawdown),
        TotalTrades = r.TotalTrades,
        WinRate = DecimalValueMapper.FromProtoOrZero(r.WinRate),
        AverageWin = DecimalValueMapper.FromProtoOrZero(r.AverageWin),
        AverageLoss = DecimalValueMapper.FromProtoOrZero(r.AverageLoss),
        ProfitFactor = DecimalValueMapper.FromProtoOrZero(r.ProfitFactor),
        Trades = r.Trades.Select(FromProto).ToList(),
    };

    public static Proto.TradeRecord ToProto(TradeRecord t) => new()
    {
        Id = t.Id,
        LaneId = t.LaneId,
        PositionId = t.PositionId,
        StrategyId = t.StrategyId,
        Symbol = t.Symbol,
        Side = ToProto(t.Side),
        EntryPrice = DecimalValueMapper.ToProto(t.EntryPrice),
        ExitPrice = DecimalValueMapper.ToProto(t.ExitPrice),
        Quantity = DecimalValueMapper.ToProto(t.Quantity),
        Pnl = DecimalValueMapper.ToProto(t.Pnl),
        PnlPercent = DecimalValueMapper.ToProto(t.PnlPercent),
        OpenedAtUtc = ToProto(t.OpenedAtUtc),
        ClosedAtUtc = ToProto(t.ClosedAtUtc),
        DurationSeconds = (long)t.Duration.TotalSeconds,
        ExitReason = t.ExitReason ?? string.Empty,
        Mode = ToProto(t.Mode),
        MarketType = ToProto(t.MarketType),
        Leverage = t.Leverage,
        WasLiquidated = t.WasLiquidated,
    };

    public static TradeRecord FromProto(Proto.TradeRecord t) => new()
    {
        Id = t.Id,
        LaneId = t.LaneId,
        PositionId = t.PositionId,
        StrategyId = t.StrategyId,
        Symbol = t.Symbol,
        Side = FromProto(t.Side),
        EntryPrice = DecimalValueMapper.FromProtoOrZero(t.EntryPrice),
        ExitPrice = DecimalValueMapper.FromProtoOrZero(t.ExitPrice),
        Quantity = DecimalValueMapper.FromProtoOrZero(t.Quantity),
        Pnl = DecimalValueMapper.FromProtoOrZero(t.Pnl),
        PnlPercent = DecimalValueMapper.FromProtoOrZero(t.PnlPercent),
        OpenedAtUtc = FromProto(t.OpenedAtUtc),
        ClosedAtUtc = FromProto(t.ClosedAtUtc),
        Duration = TimeSpan.FromSeconds(t.DurationSeconds),
        ExitReason = string.IsNullOrEmpty(t.ExitReason) ? null : t.ExitReason,
        Mode = FromProto(t.Mode),
        MarketType = FromProto(t.MarketType),
        Leverage = t.Leverage,
        WasLiquidated = t.WasLiquidated,
    };
}
