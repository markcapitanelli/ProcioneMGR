using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ProcioneMGR.Contracts.Common.V1;
using ProcioneMGR.Contracts.Events.V1;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di fumo dei contratti gRPC/Protobuf (Fase 0 microservizi): il C# generato dai .proto di
/// ProcioneMGR.Contracts serializza e deserializza round-trip senza perdita. I contratti NON sono
/// ancora cablati nell'app (accade in Fase 2+): qui si valida solo che siano ben formati.
/// </summary>
public class ContractsSmokeTests
{
    [Fact]
    public void LaneStatus_RoundTripsThroughProtobuf()
    {
        var original = new GetLaneStatusResponse
        {
            LaneId = 2,
            Mode = "Paper",
            Running = true,
            OpenPositions = 3,
            // -1.75: units e nanos con lo stesso segno (convenzione DecimalValue in common.proto)
            RealizedPnl = new DecimalValue { Units = -1, Nanos = -750_000_000 },
            LastTickUtc = Timestamp.FromDateTime(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)),
        };

        var roundTripped = GetLaneStatusResponse.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(original, roundTripped);
        Assert.Equal(-1L, roundTripped.RealizedPnl.Units);
        Assert.Equal(-750_000_000, roundTripped.RealizedPnl.Nanos);
    }

    [Fact]
    public void PredictSignal_RoundTripsThroughProtobuf()
    {
        var request = new PredictSignalRequest
        {
            Instrument = new Instrument { Symbol = "BTCUSDT", Timeframe = "1h" },
            Stage = ModelStage.Champion,
            ModelName = "champ",
        };
        request.Features.AddRange(new[] { 0.12, -0.5, 1.0 });

        var roundTripped = PredictSignalRequest.Parser.ParseFrom(request.ToByteArray());

        Assert.Equal(request, roundTripped);
        Assert.Equal(3, roundTripped.Features.Count);
        Assert.Equal(ModelStage.Champion, roundTripped.Stage);
    }

    [Fact]
    public void MarketDataSyncedEvent_RoundTripsThroughProtobuf()
    {
        var evt = new MarketDataSyncedEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Instrument = new Instrument { Symbol = "ETHUSDT", Timeframe = "15m" },
            CandlesAdded = 42,
            LatestCandleUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            EmittedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        var roundTripped = MarketDataSyncedEvent.Parser.ParseFrom(evt.ToByteArray());

        Assert.Equal(evt, roundTripped);
        Assert.NotEmpty(roundTripped.EventId); // chiave di idempotenza per i consumer
    }
}
