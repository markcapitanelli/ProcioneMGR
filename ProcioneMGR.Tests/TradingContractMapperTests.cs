using ProcioneMGR.Contracts.Common.V1;
using ProcioneMGR.Services.Trading;
using Domain = ProcioneMGR.Services.Trading;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Tests;

/// <summary>
/// Unità della mappatura dominio↔trading.proto (Fase 2b). Copre le due classi di errore che il
/// round-trip gRPC da solo non isolerebbe: la conversione dei decimal (denaro) e la mappatura degli
/// enum (dove un cast ordinale trasformerebbe Paper in Testnet).
/// </summary>
public class TradingContractMapperTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("0.000000001")]   // 1 nano: il minimo rappresentabile
    [InlineData("-0.000000001")]
    [InlineData("1.75")]
    [InlineData("-1.75")]         // units e nanos devono restare concordi
    [InlineData("58123.45")]      // prezzo tipico
    [InlineData("0.00000123")]    // quantità tipica in crypto (8 decimali)
    [InlineData("-99999.999999999")]
    [InlineData("10000")]
    public void DecimalValue_RoundTrips_Exactly(string literal)
    {
        var value = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        var roundTripped = DecimalValueMapper.FromProto(DecimalValueMapper.ToProto(value));

        Assert.Equal(value, roundTripped);
    }

    [Fact]
    public void DecimalValue_NegativeValue_HasNanosWithTheSameSignAsUnits()
    {
        // La convenzione di common.proto. Se i nanos avessero segno opposto, -1.75 verrebbe letto
        // come -0.25 da un consumer conforme: un PnL sbagliato, in silenzio.
        var proto = DecimalValueMapper.ToProto(-1.75m);

        Assert.Equal(-1L, proto.Units);
        Assert.Equal(-750_000_000, proto.Nanos);
    }

    [Fact]
    public void DecimalValue_RoundingCarry_DoesNotProduceInvalidNanos()
    {
        // 0.9999999995 arrotonda a 1.0: il carry deve passare agli units, mai lasciare nanos == 1e9
        // (valore fuori formato, che un consumer conforme leggerebbe come 1.999999999).
        var proto = DecimalValueMapper.ToProto(0.9999999995m);

        Assert.Equal(1L, proto.Units);
        Assert.Equal(0, proto.Nanos);
        Assert.Equal(1m, DecimalValueMapper.FromProto(proto));
    }

    [Fact]
    public void DecimalValue_BeyondNineDecimals_RoundsInsteadOfTruncating()
    {
        // Limite noto e documentato del formato (vedi DecimalValueMapper): oltre la nona cifra si
        // arrotonda. Il test lo fissa come comportamento atteso, così una modifica non lo cambia
        // per caso.
        Assert.Equal(0.000000001m, DecimalValueMapper.FromProto(DecimalValueMapper.ToProto(0.0000000006m)));
        Assert.Equal(0m, DecimalValueMapper.FromProto(DecimalValueMapper.ToProto(0.0000000004m)));
    }

    [Theory]
    [InlineData(-1L, 750_000_000)]   // segni discordi: -1.75 verrebbe letto -0.25
    [InlineData(1L, -750_000_000)]
    [InlineData(0L, 1_000_000_000)]  // |nanos| >= 1e9: la parte intera deve stare in units
    [InlineData(0L, -1_000_000_000)]
    public void DecimalValue_Malformed_Throws(long units, int nanos)
    {
        // Meglio fallire rumorosamente che restituire un importo plausibile e falso: su un PnL,
        // un errore silenzioso di 1.5 è peggio di un'eccezione.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DecimalValueMapper.FromProto(new DecimalValue { Units = units, Nanos = nanos }));
    }

    [Fact]
    public void DecimalValue_ZeroUnits_AllowsEitherSignOfNanos()
    {
        // units=0 non ha segno: -0.25 è {units:0, nanos:-250M} ed è legittimo.
        Assert.Equal(-0.25m, DecimalValueMapper.FromProto(new DecimalValue { Units = 0, Nanos = -250_000_000 }));
        Assert.Equal(0.25m, DecimalValueMapper.FromProto(new DecimalValue { Units = 0, Nanos = 250_000_000 }));
    }

    [Fact]
    public void DecimalValue_Nullable_KeepsAbsentDistinctFromZero()
    {
        Assert.Null(DecimalValueMapper.ToProtoNullable(null));
        Assert.NotNull(DecimalValueMapper.ToProtoNullable(0m));

        Assert.Null(DecimalValueMapper.FromProtoNullable(null));
        Assert.Equal(0m, DecimalValueMapper.FromProtoNullable(new DecimalValue()));
        // Per i campi NON opzionali un message assente vale 0 (default proto3).
        Assert.Equal(0m, DecimalValueMapper.FromProtoOrZero(null));
    }

    [Theory]
    [InlineData(Domain.TradingMode.Paper)]
    [InlineData(Domain.TradingMode.Testnet)]
    [InlineData(Domain.TradingMode.Live)]
    public void TradingMode_RoundTrips(Domain.TradingMode mode) =>
        Assert.Equal(mode, TradingContractMapper.FromProto(TradingContractMapper.ToProto(mode)));

    [Fact]
    public void TradingMode_IsNotMappedByOrdinal()
    {
        // Paper è 0 nel dominio ma 1 nel proto (0 = UNSPECIFIED). È esattamente lo scarto che un
        // cast ordinale sbaglierebbe, trasformando una simulazione in una sessione reale.
        Assert.Equal(Proto.TradingMode.Paper, TradingContractMapper.ToProto(Domain.TradingMode.Paper));
        Assert.Equal(1, (int)Proto.TradingMode.Paper);
        Assert.Equal(0, (int)Domain.TradingMode.Paper);
    }

    [Fact]
    public void TradingMode_Unspecified_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingContractMapper.FromProto(Proto.TradingMode.Unspecified));

    [Theory]
    [InlineData(Domain.MarketType.Spot)]
    [InlineData(Domain.MarketType.Futures)]
    public void MarketType_RoundTrips(Domain.MarketType type) =>
        Assert.Equal(type, TradingContractMapper.FromProto(TradingContractMapper.ToProto(type)));

    [Fact]
    public void MarketType_Unspecified_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingContractMapper.FromProto(Proto.MarketType.Unspecified));

    [Theory]
    [InlineData(Domain.OrderSide.Buy)]
    [InlineData(Domain.OrderSide.Sell)]
    public void OrderSide_RoundTrips(Domain.OrderSide side) =>
        Assert.Equal(side, TradingContractMapper.FromProto(TradingContractMapper.ToProto(side)));

    [Fact]
    public void OrderSide_Unspecified_Throws() =>
        // Un lato "sconosciuto" degradato a Buy sarebbe un ordine nella direzione opposta.
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingContractMapper.FromProto(Proto.OrderSide.Unspecified));

    [Fact]
    public void Timestamp_AcceptsUnspecifiedKind_FromPostgres()
    {
        // I DateTime letti da Postgres hanno Kind=Unspecified (switch legacy timestamp): senza lo
        // SpecifyKind nel mapper, Timestamp.FromDateTime lancerebbe sul primo dato letto dal DB.
        var fromDb = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var roundTripped = TradingContractMapper.FromProto(TradingContractMapper.ToProto(fromDb));

        // Stesso istante, dichiarato UTC: nessuna traslazione di orario.
        Assert.Equal(fromDb.Ticks, roundTripped.Ticks);
        Assert.Equal(DateTimeKind.Utc, roundTripped.Kind);
    }

    [Fact]
    public void OpenPosition_RoundTrips_IncludingOptionalFields()
    {
        var original = new Domain.OpenPosition
        {
            Id = 7,
            LaneId = 2,
            PositionId = "pos-1",
            StrategyId = "rsi",
            Symbol = "ETHUSDT",
            Side = Domain.OrderSide.Sell,
            EntryPrice = 3_120.55m,
            Quantity = 0.75m,
            StopLoss = 3_200m,
            TakeProfit = null,              // assente: deve restare assente
            TrailingStopPercent = 2.5m,
            BestPriceSinceEntry = null,
            OpenedAtUtc = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc),
            CurrentPrice = 3_050m,
            UnrealizedPnl = 52.91m,
            UnrealizedPnlPercent = 2.26m,
            ExchangeOrderId = null,         // null e "" non devono confondersi
            OpenedInMode = Domain.TradingMode.Testnet,
            StopOrderId = "stop-1",
            TakeProfitOrderId = null,
            Leverage = 3,
            LiquidationPrice = 4_000m,
            MarginBalance = 780.14m,
        };

        var r = TradingContractMapper.FromProto(TradingContractMapper.ToProto(original));

        Assert.Equal(original.Id, r.Id);
        Assert.Equal(original.LaneId, r.LaneId);
        Assert.Equal(original.PositionId, r.PositionId);
        Assert.Equal(original.StrategyId, r.StrategyId);
        Assert.Equal(original.Symbol, r.Symbol);
        Assert.Equal(original.Side, r.Side);
        Assert.Equal(original.EntryPrice, r.EntryPrice);
        Assert.Equal(original.Quantity, r.Quantity);
        Assert.Equal(original.StopLoss, r.StopLoss);
        Assert.Null(r.TakeProfit);
        Assert.Equal(original.TrailingStopPercent, r.TrailingStopPercent);
        Assert.Null(r.BestPriceSinceEntry);
        Assert.Equal(original.OpenedAtUtc, r.OpenedAtUtc);
        Assert.Equal(original.CurrentPrice, r.CurrentPrice);
        Assert.Equal(original.UnrealizedPnl, r.UnrealizedPnl);
        Assert.Equal(original.UnrealizedPnlPercent, r.UnrealizedPnlPercent);
        Assert.Null(r.ExchangeOrderId);
        Assert.Equal(original.OpenedInMode, r.OpenedInMode);
        Assert.Equal(original.StopOrderId, r.StopOrderId);
        Assert.Null(r.TakeProfitOrderId);
        Assert.Equal(original.Leverage, r.Leverage);
        Assert.Equal(original.LiquidationPrice, r.LiquidationPrice);
        Assert.Equal(original.MarginBalance, r.MarginBalance);
    }

    [Fact]
    public void Performance_RoundTrips_WithEquityCurveAndTrades()
    {
        var t0 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = new Domain.TradingPerformance
        {
            EquityCurve =
            [
                new() { Timestamp = t0, Capital = 10_000m },
                new() { Timestamp = t0.AddHours(1), Capital = 10_125.5m },
            ],
            TotalReturn = 1.255m,
            SharpeRatio = 1.42m,
            MaxDrawdown = 3.75m,
            TotalTrades = 2,
            WinRate = 50m,
            AverageWin = 250m,
            AverageLoss = -124.5m,
            ProfitFactor = 2.008m,
            Trades =
            [
                new()
                {
                    Id = 1, LaneId = 0, PositionId = "p1", StrategyId = "s", Symbol = "BTCUSDT",
                    Side = Domain.OrderSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1m,
                    Pnl = 10m, PnlPercent = 10m, OpenedAtUtc = t0, ClosedAtUtc = t0.AddHours(2),
                    Duration = TimeSpan.FromHours(2), ExitReason = "TakeProfit",
                    Mode = Domain.TradingMode.Paper, MarketType = Domain.MarketType.Spot,
                    Leverage = 1, WasLiquidated = false,
                },
            ],
        };

        var r = TradingContractMapper.FromProto(TradingContractMapper.ToProto(original));

        Assert.Equal(2, r.EquityCurve.Count);
        Assert.Equal(10_125.5m, r.EquityCurve[1].Capital);
        Assert.Equal(t0.AddHours(1), r.EquityCurve[1].Timestamp);
        Assert.Equal(original.TotalReturn, r.TotalReturn);
        Assert.Equal(original.SharpeRatio, r.SharpeRatio);
        Assert.Equal(original.MaxDrawdown, r.MaxDrawdown);
        Assert.Equal(original.TotalTrades, r.TotalTrades);
        Assert.Equal(original.WinRate, r.WinRate);
        Assert.Equal(original.AverageWin, r.AverageWin);
        Assert.Equal(original.AverageLoss, r.AverageLoss);
        Assert.Equal(original.ProfitFactor, r.ProfitFactor);

        var trade = Assert.Single(r.Trades);
        Assert.Equal("TakeProfit", trade.ExitReason);
        Assert.Equal(TimeSpan.FromHours(2), trade.Duration);
        Assert.Equal(Domain.OrderSide.Buy, trade.Side);
        Assert.Equal(Domain.TradingMode.Paper, trade.Mode);
        Assert.Equal(10m, trade.Pnl);
    }

    [Fact]
    public void LaneStatus_EmptyReason_MapsBackToNull()
    {
        var status = new Domain.TradingEngineStatus
        {
            Mode = Domain.TradingMode.Paper,
            MarketType = Domain.MarketType.Spot,
            IsEmergencyStopped = true,
            EmergencyStopReason = "Daily loss limit",
        };

        var withReason = TradingContractMapper.FromProto(TradingContractMapper.ToProto(status, 0));
        Assert.Equal("Daily loss limit", withReason.EmergencyStopReason);

        status.EmergencyStopReason = null;
        var withoutReason = TradingContractMapper.FromProto(TradingContractMapper.ToProto(status, 0));
        Assert.Null(withoutReason.EmergencyStopReason); // "" sul filo => null nel dominio
    }
}
