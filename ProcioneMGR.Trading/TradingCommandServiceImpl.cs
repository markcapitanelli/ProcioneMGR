using Grpc.Core;
using ProcioneMGR.Contracts.Trading.V1;
using ProcioneMGR.Services.Trading;
using Domain = ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Trading;

/// <summary>
/// Implementazione gRPC dei comandi di trading (Fase 2b). Ogni RPC risolve l'istanza keyed di
/// <see cref="ITradingEngine"/> della lane indicata nella request e le delega la chiamata: nessuna
/// logica di dominio vive qui, è un adattatore fra il filo e il motore riusato verbatim.
///
/// SICUREZZA: <see cref="ConfirmOrder"/> sblocca il piazzamento REALE di un ordine Live e
/// <see cref="StartLane"/> può avviare una sessione con soldi veri. A questo livello NON c'è alcun
/// controllo di autorizzazione — nel monolite il gate è l'[Authorize] di Trading.razor, che qui non
/// esiste. L'unico confine è di rete: la NetworkPolicy che accetta ingress solo dal pod
/// procionemgr-ui (infra/k8s/trading/networkpolicy.yaml). Non esporre questo servizio altrove.
/// </summary>
public sealed class TradingCommandServiceImpl(
    IServiceProvider serviceProvider,
    ILogger<TradingCommandServiceImpl> logger) : TradingCommandService.TradingCommandServiceBase
{
    /// <summary>
    /// Le lane sono registrate keyed su 0..TradingLanes.Count-1: GetRequiredKeyedService su una
    /// chiave inesistente lancerebbe un InvalidOperationException opaco (→ Unknown sul filo). Il
    /// range si controlla prima, per rispondere NotFound con un messaggio che dice cosa è successo.
    /// </summary>
    private ITradingEngine Engine(int laneId)
    {
        if (laneId < 0 || laneId >= TradingLanes.Count)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Lane {laneId} inesistente: le corsie valide sono 0..{TradingLanes.Count - 1}."));
        }
        return serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId);
    }

    /// <summary>
    /// Il motore segnala i rifiuti di dominio con InvalidOperationException: master key ancora il
    /// placeholder di sviluppo, leva oltre il limite di sicurezza, sizing incoerente coi cap del
    /// SafetyChecker, credenziali mancanti. Sono precondizioni violate, non bug: vanno sul filo come
    /// FailedPrecondition con il messaggio originale (che spiega all'operatore cosa correggere),
    /// mai come eccezione grezza — un Unknown con stack trace direbbe "è rotto" invece di "non puoi".
    /// </summary>
    private async Task<T> DomainGuard<T>(Func<Task<T>> action, string operation, int laneId)
    {
        try
        {
            return await action();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Lane {Lane}: {Operation} rifiutata dal dominio.", laneId, operation);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    // ------------------------------------------------------------------------------- lettura

    public override async Task<GetLaneStatusResponse> GetLaneStatus(GetLaneStatusRequest request, ServerCallContext context)
    {
        var status = await Engine(request.LaneId).GetStatusAsync(context.CancellationToken);
        return TradingContractMapper.ToProto(status, request.LaneId);
    }

    public override async Task<GetOpenPositionsResponse> GetOpenPositions(GetOpenPositionsRequest request, ServerCallContext context)
    {
        var positions = await Engine(request.LaneId).GetOpenPositionsAsync(context.CancellationToken);
        var response = new GetOpenPositionsResponse();
        response.Positions.AddRange(positions.Select(TradingContractMapper.ToProto));
        return response;
    }

    public override async Task<GetPerformanceResponse> GetPerformance(GetPerformanceRequest request, ServerCallContext context)
    {
        // from_utc assente = tutto lo storico della lane (stessa semantica del parametro from? locale).
        var from = TradingContractMapper.FromProtoNullable(request.FromUtc);
        var perf = await Engine(request.LaneId).GetPerformanceAsync(from, context.CancellationToken);
        return TradingContractMapper.ToProto(perf);
    }

    // ------------------------------------------------------------------------------- comandi

    public override async Task<StartLaneResponse> StartLane(StartLaneRequest request, ServerCallContext context)
    {
        // FromProto lancia su UNSPECIFIED invece di indovinare: avviare la lane nella modalità
        // sbagliata significherebbe soldi veri al posto di una simulazione. Qui l'eccezione va
        // tradotta a mano: è un difetto della REQUEST (InvalidArgument), non una precondizione di
        // dominio violata (FailedPrecondition), e senza questo try diventerebbe un Unknown con lo
        // stack trace del server addosso — l'eccezione grezza sul filo che vogliamo evitare.
        Domain.TradingMode mode;
        try
        {
            mode = TradingContractMapper.FromProto(request.Mode);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        logger.LogInformation("Lane {Lane}: avvio richiesto in modalità {Mode}.", request.LaneId, mode);

        return await DomainGuard(async () =>
        {
            await Engine(request.LaneId).StartAsync(mode, context.CancellationToken);
            return new StartLaneResponse();
        }, "StartLane", request.LaneId);
    }

    public override async Task<StopLaneResponse> StopLane(StopLaneRequest request, ServerCallContext context)
    {
        await Engine(request.LaneId).StopAsync(context.CancellationToken);
        logger.LogInformation("Lane {Lane}: fermata.", request.LaneId);
        return new StopLaneResponse();
    }

    public override async Task<EmergencyStopResponse> EmergencyStop(EmergencyStopRequest request, ServerCallContext context)
    {
        await Engine(request.LaneId).EmergencyStopAsync(request.Reason, context.CancellationToken);
        logger.LogWarning("Lane {Lane}: EMERGENCY STOP ({Reason}).", request.LaneId, request.Reason);
        return new EmergencyStopResponse();
    }

    public override async Task<ClosePositionResponse> ClosePosition(ClosePositionRequest request, ServerCallContext context) =>
        await DomainGuard(async () =>
        {
            await Engine(request.LaneId).ClosePositionAsync(request.PositionId, context.CancellationToken);
            return new ClosePositionResponse();
        }, "ClosePosition", request.LaneId);

    public override async Task<CloseAllPositionsResponse> CloseAllPositions(CloseAllPositionsRequest request, ServerCallContext context)
    {
        // Best-effort per contratto (ITradingEngine): una chiusura può fallire e la posizione resta
        // aperta per il retry. Chiamato anche dal LanePromoter prima di un cambio di modalità.
        await Engine(request.LaneId).CloseAllPositionsAsync(request.Reason, context.CancellationToken);
        logger.LogInformation("Lane {Lane}: flatten richiesto ({Reason}).", request.LaneId, request.Reason);
        return new CloseAllPositionsResponse();
    }

    public override async Task<SetStopLossTakeProfitResponse> SetStopLossTakeProfit(
        SetStopLossTakeProfitRequest request, ServerCallContext context) =>
        await DomainGuard(async () =>
        {
            // Tri-stato preservato: campo assente sul filo → null → "non toccare"; presente a zero
            // → 0m → "azzera lo stop esistente". FromProtoNullable è ciò che tiene distinti i due.
            await Engine(request.LaneId).SetStopLossTakeProfitAsync(
                request.PositionId,
                DecimalValueMapper.FromProtoNullable(request.StopLoss),
                DecimalValueMapper.FromProtoNullable(request.TakeProfit),
                DecimalValueMapper.FromProtoNullable(request.TrailingStopPercent),
                context.CancellationToken);
            return new SetStopLossTakeProfitResponse();
        }, "SetStopLossTakeProfit", request.LaneId);

    // ----------------------------------------------------------------- conferma ordini Live

    public override async Task<ConfirmOrderResponse> ConfirmOrder(ConfirmOrderRequest request, ServerCallContext context) =>
        await DomainGuard(async () =>
        {
            // user_id stringa vuota = assente (proto3 non ha stringhe nullable): il dominio vuole null.
            var userId = string.IsNullOrEmpty(request.UserId) ? null : request.UserId;
            logger.LogInformation("Lane {Lane}: conferma ordine {Order} da {User}.",
                request.LaneId, request.OrderId, userId ?? "?");
            await Engine(request.LaneId).ConfirmOrderAsync(request.OrderId, userId, context.CancellationToken);
            return new ConfirmOrderResponse();
        }, "ConfirmOrder", request.LaneId);

    public override async Task<RejectOrderResponse> RejectOrder(RejectOrderRequest request, ServerCallContext context) =>
        await DomainGuard(async () =>
        {
            var userId = string.IsNullOrEmpty(request.UserId) ? null : request.UserId;
            await Engine(request.LaneId).RejectOrderAsync(request.OrderId, userId, context.CancellationToken);
            return new RejectOrderResponse();
        }, "RejectOrder", request.LaneId);
}
