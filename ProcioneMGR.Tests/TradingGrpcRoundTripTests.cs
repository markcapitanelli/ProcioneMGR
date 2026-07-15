using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Contracts.Trading.V1;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Trading;
using Domain = ProcioneMGR.Services.Trading;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prova che il servizio di trading serve davvero via gRPC su HTTP/2 (host reale
/// ProcioneMGR.Trading, non una chiamata C# diretta) e che i comandi attraversano la
/// (de)serializzazione protobuf conservando la loro semantica — in particolare il TRI-STATO di
/// SetStopLossTakeProfit, dove collassare "assente" e "zero" significherebbe disarmare uno stop loss
/// per sbaglio.
///
/// Il motore vero è sostituito da un fake che REGISTRA le chiamate: qui si verifica il filo (wire,
/// mapping, codici di stato), non la logica di esecuzione — quella è già coperta dai test del
/// TradingEngine e non cambia in Fase 2b, dato che il motore è riusato verbatim. Nessun DB.
/// </summary>
public class TradingGrpcRoundTripTests
{
    /// <summary>Motore fake: registra l'ultima chiamata ricevuta e restituisce valori fissati.</summary>
    private sealed class RecordingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;

        public TradingEngineStatus Status { get; set; } = new();
        public Exception? StartThrows { get; set; }

        public Domain.TradingMode? StartedWith { get; private set; }
        public (string PositionId, decimal? Sl, decimal? Tp, decimal? Trail)? LastSlTp { get; private set; }
        public (string OrderId, string? UserId)? LastConfirm { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

        public Task StartAsync(Domain.TradingMode mode, CancellationToken ct = default)
        {
            if (StartThrows is not null) throw StartThrows;
            StartedWith = mode;
            return Task.CompletedTask;
        }

        public Task SetStopLossTakeProfitAsync(
            string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
        {
            LastSlTp = (positionId, stopLoss, takeProfit, trailingStopPercent);
            return Task.CompletedTask;
        }

        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)
        {
            LastConfirm = (orderId, userId);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        // Domain.OpenPosition: il nome esiste sia nel dominio sia nei contratti generati.
        public Task<List<Domain.OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<Domain.OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static WebApplicationFactory<TradingCommandServiceImpl> CreateHost(Action<RecordingEngine>? configure = null)
    {
        // WebApplicationFactory<TradingCommandServiceImpl> e non <Program>: ProcioneMGR.Ml e
        // ProcioneMGR.Trading dichiarano entrambi un `public partial class Program` nel namespace
        // globale e questo progetto di test li referenzia entrambi — nominare `Program` sarebbe
        // ambiguo. Un tipo dell'assembly identifica l'entry point senza collisioni.
        return new WebApplicationFactory<TradingCommandServiceImpl>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresConnection", "Host=localhost;Database=unused;Username=x;Password=x");
            // Chiave valida ma fittizia: il servizio registra l'AesGcmEncryptionService vero (gli
            // serve per le credenziali exchange) e senza chiave il DbContext non si costruirebbe.
            // Nessun test qui cifra o decifra alcunché.
            b.UseSetting("Security:MasterKey", Convert.ToBase64String(new byte[32]));

            b.ConfigureTestServices(services =>
            {
                // Le lane sono keyed: RemoveAll<T>() agisce solo sui descriptor non-keyed e le
                // lascerebbe in piedi, facendo vincere il motore vero (che qui cercherebbe il DB).
                foreach (var d in services
                             .Where(d => d.ServiceType == typeof(ITradingEngine) && d.IsKeyedService)
                             .ToList())
                {
                    services.Remove(d);
                }

                for (var lane = 0; lane < TradingLanes.Count; lane++)
                {
                    var engine = new RecordingEngine(lane);
                    configure?.Invoke(engine);
                    services.AddKeyedSingleton<ITradingEngine>(lane, engine);
                }
            });
        });
    }

    private static TradingCommandService.TradingCommandServiceClient ClientFor(WebApplicationFactory<TradingCommandServiceImpl> factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        return new TradingCommandService.TradingCommandServiceClient(channel);
    }

    [Fact]
    public async Task GetLaneStatus_OverRealGrpc_PreservesEveryFieldExactly()
    {
        var status = new TradingEngineStatus
        {
            Mode = Domain.TradingMode.Testnet,
            MarketType = Domain.MarketType.Futures,
            Leverage = 5,
            IsRunning = true,
            ExchangeName = "Bitget",
            Symbol = "BTCUSDT",
            TotalCapital = 10_000m,
            AvailableCapital = 7_500.25m,
            UsedCapital = 2_499.75m,
            TotalPnl = -1.75m, // negativo: units e nanos devono restare concordi
            TotalPnlPercent = -0.0175m,
            DailyPnl = -0.5m,
            MaxDrawdown = 12.5m,
            TotalTrades = 42,
            OpenPositionCount = 3,
            WinRate = 61.9m,
            StartedAtUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            LastOrderUtc = new DateTime(2026, 7, 15, 18, 30, 0, DateTimeKind.Utc),
            IsEmergencyStopped = false,
        };

        await using var factory = CreateHost(e => e.Status = status);
        var client = ClientFor(factory);

        var response = await client.GetLaneStatusAsync(new GetLaneStatusRequest { LaneId = 1 });
        // Rimappato a dominio: è il giro completo che fa RemoteTradingEngineClient.
        var roundTripped = TradingContractMapper.FromProto(response);

        Assert.Equal(1, response.LaneId);
        Assert.Equal(Domain.TradingMode.Testnet, roundTripped.Mode);
        Assert.Equal(Domain.MarketType.Futures, roundTripped.MarketType);
        Assert.Equal(5, roundTripped.Leverage);
        Assert.True(roundTripped.IsRunning);
        Assert.Equal("Bitget", roundTripped.ExchangeName);
        Assert.Equal("BTCUSDT", roundTripped.Symbol);
        Assert.Equal(10_000m, roundTripped.TotalCapital);
        Assert.Equal(7_500.25m, roundTripped.AvailableCapital);
        Assert.Equal(2_499.75m, roundTripped.UsedCapital);
        Assert.Equal(-1.75m, roundTripped.TotalPnl);
        Assert.Equal(-0.0175m, roundTripped.TotalPnlPercent);
        Assert.Equal(-0.5m, roundTripped.DailyPnl);
        Assert.Equal(12.5m, roundTripped.MaxDrawdown);
        Assert.Equal(42, roundTripped.TotalTrades);
        Assert.Equal(3, roundTripped.OpenPositionCount);
        Assert.Equal(61.9m, roundTripped.WinRate);
        Assert.Equal(status.StartedAtUtc, roundTripped.StartedAtUtc);
        Assert.Equal(status.LastOrderUtc, roundTripped.LastOrderUtc);
        Assert.Null(roundTripped.EmergencyStopReason); // "" sul filo torna null nel dominio
    }

    [Fact]
    public async Task GetLaneStatus_UnknownLane_IsNotFound()
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetLaneStatusAsync(new GetLaneStatusRequest { LaneId = 99 }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ConfirmOrder_OverRealGrpc_ReachesTheEngineWithTheOperator()
    {
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        await client.ConfirmOrderAsync(new ConfirmOrderRequest { LaneId = 0, OrderId = "ord-1", UserId = "mark" });

        Assert.Equal(("ord-1", "mark"), lane0!.LastConfirm);
    }

    [Fact]
    public async Task ConfirmOrder_EmptyUserId_ArrivesAsNull()
    {
        // proto3 non ha stringhe nullable: "" sul filo deve tornare null nel dominio, non "".
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        await client.ConfirmOrderAsync(new ConfirmOrderRequest { LaneId = 0, OrderId = "ord-2" });

        Assert.Equal("ord-2", lane0!.LastConfirm!.Value.OrderId);
        Assert.Null(lane0.LastConfirm.Value.UserId);
    }

    [Fact]
    public async Task SetStopLossTakeProfit_PreservesTriState_AcrossTheWire()
    {
        // IL TEST PIÙ DELICATO DEL CONTRATTO: stop_loss presente a ZERO ("azzera lo stop") e
        // take_profit ASSENTE ("non toccare") non devono collassare nella stessa cosa passando da
        // protobuf. Se collassassero, un "azzera" diventerebbe un "lascia com'è" (o viceversa) su
        // una protezione di una posizione reale.
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        await client.SetStopLossTakeProfitAsync(new SetStopLossTakeProfitRequest
        {
            LaneId = 0,
            PositionId = "pos-1",
            StopLoss = DecimalValueMapper.ToProto(0m), // presente a zero => azzera
            // TakeProfit e TrailingStopPercent non assegnati => assenti => non toccare
        });

        var call = lane0!.LastSlTp!.Value;
        Assert.Equal("pos-1", call.PositionId);
        Assert.Equal(0m, call.Sl);   // arrivato come 0m, NON come null
        Assert.Null(call.Tp);        // arrivato come null, NON come 0m
        Assert.Null(call.Trail);
    }

    [Fact]
    public async Task SetStopLossTakeProfit_PassesRealValues()
    {
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        await client.SetStopLossTakeProfitAsync(new SetStopLossTakeProfitRequest
        {
            LaneId = 0,
            PositionId = "pos-2",
            StopLoss = DecimalValueMapper.ToProto(58_123.45m),
            TakeProfit = DecimalValueMapper.ToProto(71_000.5m),
            TrailingStopPercent = DecimalValueMapper.ToProto(2.5m),
        });

        var call = lane0!.LastSlTp!.Value;
        Assert.Equal(58_123.45m, call.Sl);
        Assert.Equal(71_000.5m, call.Tp);
        Assert.Equal(2.5m, call.Trail);
    }

    [Fact]
    public async Task StartLane_MapsModeExplicitly_NotByOrdinal()
    {
        // Paper vale 0 in C# e 1 in proto3 (lo zero è UNSPECIFIED): un cast ordinale trasformerebbe
        // Paper in Testnet, cioè una simulazione in una sessione reale sull'exchange.
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        await client.StartLaneAsync(new StartLaneRequest { LaneId = 0, Mode = Proto.TradingMode.Paper });

        Assert.Equal(Domain.TradingMode.Paper, lane0!.StartedWith);
    }

    [Fact]
    public async Task StartLane_UnspecifiedMode_IsInvalidArgument_NotARawException()
    {
        // Mai indovinare la modalità: lo zero-value proto3 non è un default sensato quando la
        // risposta sbagliata sarebbe "avvia con soldi veri". Il codice conta: InvalidArgument dice
        // "la tua request è sbagliata"; un Unknown (che è ciò che si otterrebbe lasciando sfuggire
        // l'ArgumentOutOfRangeException del mapper) direbbe "il server è rotto", con tanto di stack
        // trace sul filo.
        RecordingEngine? lane0 = null;
        await using var factory = CreateHost(e => { if (e.LaneId == 0) lane0 = e; });
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.StartLaneAsync(new StartLaneRequest { LaneId = 0 }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Null(lane0!.StartedWith); // e soprattutto: la lane NON è partita
    }

    [Fact]
    public async Task StartLane_DomainRefusal_BecomesFailedPrecondition()
    {
        // Leva oltre il limite, sizing incoerente, master key placeholder: il motore li segnala con
        // InvalidOperationException. Sul filo devono diventare FailedPrecondition col messaggio
        // originale ("non puoi, ecco perché"), non un Unknown con stack trace ("è rotto").
        const string reason = "Leva richiesta 20x oltre il limite di sicurezza 5x";
        await using var factory = CreateHost(e => e.StartThrows = new InvalidOperationException(reason));
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.StartLaneAsync(new StartLaneRequest { LaneId = 0, Mode = Proto.TradingMode.Live }).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains(reason, ex.Status.Detail);
    }
}
