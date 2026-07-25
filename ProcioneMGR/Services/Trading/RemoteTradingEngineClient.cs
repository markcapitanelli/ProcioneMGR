using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Implementazione di <see cref="ITradingEngine"/> che delega l'esecuzione al microservizio
/// <c>procionemgr-trading</c> via gRPC (Fase 2b microservizi). Attiva nel monolite solo con
/// <c>Trading:UseRemoteTrading=true</c>, dove SOSTITUISCE il <see cref="TradingEngine"/> locale
/// (mai affiancarlo: due motori sulla stessa corsia aprirebbero ordini in doppio).
///
/// Implementa l'interfaccia INTERA di proposito: i consumer — Trading.razor ma soprattutto gli
/// automatismi <see cref="LanePromoter"/>/<see cref="PromotionEvaluator"/>/<see cref="PromotionWorker"/>,
/// che promuovono e retrocedono corsie da soli — risolvono <see cref="ITradingEngine"/> keyed e non
/// sanno (né devono sapere) cosa c'è dietro. Un'implementazione parziale romperebbe in silenzio
/// quell'automazione di sicurezza.
/// </summary>
public sealed class RemoteTradingEngineClient(
    int laneId,
    Proto.TradingCommandService.TradingCommandServiceClient client,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<RemoteTradingEngineClient> logger) : ITradingEngine
{
    public int LaneId => laneId;

    // ------------------------------------------------------------------- lettura via gRPC

    public async Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await client.GetLaneStatusAsync(new Proto.GetLaneStatusRequest { LaneId = laneId }, cancellationToken: ct);
        return TradingContractMapper.FromProto(r);
    }

    public async Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        var r = await client.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest { LaneId = laneId }, cancellationToken: ct);
        return r.Positions.Select(TradingContractMapper.FromProto).ToList();
    }

    public async Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
    {
        var request = new Proto.GetPerformanceRequest { LaneId = laneId };
        if (from is DateTime f) request.FromUtc = TradingContractMapper.ToProto(f);
        var r = await client.GetPerformanceAsync(request, cancellationToken: ct);
        return TradingContractMapper.FromProto(r);
    }

    // ------------------------------------------------------------------- comandi via gRPC

    public async Task StartAsync(TradingMode mode, CancellationToken ct = default)
    {
        await client.StartLaneAsync(new Proto.StartLaneRequest
        {
            LaneId = laneId,
            Mode = TradingContractMapper.ToProto(mode),
        }, cancellationToken: ct);
        logger.LogInformation("Lane {Lane}: avvio remoto in modalità {Mode}.", laneId, mode);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await client.StopLaneAsync(new Proto.StopLaneRequest { LaneId = laneId }, cancellationToken: ct);
        logger.LogInformation("Lane {Lane}: stop remoto.", laneId);
    }

    public async Task EmergencyStopAsync(string reason, CancellationToken ct = default)
    {
        await client.EmergencyStopAsync(new Proto.EmergencyStopRequest { LaneId = laneId, Reason = reason }, cancellationToken: ct);
        logger.LogWarning("Lane {Lane}: emergency stop remoto ({Reason}).", laneId, reason);
    }

    public async Task ClosePositionAsync(string positionId, CancellationToken ct = default) =>
        await client.ClosePositionAsync(new Proto.ClosePositionRequest
        {
            LaneId = laneId,
            PositionId = positionId,
        }, cancellationToken: ct);

    public async Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) =>
        await client.CloseAllPositionsAsync(new Proto.CloseAllPositionsRequest
        {
            LaneId = laneId,
            Reason = reason,
        }, cancellationToken: ct);

    public async Task SetStopLossTakeProfitAsync(
        string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
    {
        var request = new Proto.SetStopLossTakeProfitRequest { LaneId = laneId, PositionId = positionId };
        // Tri-stato preservato: si assegna SOLO se il chiamante ha passato un valore. Un null resta
        // un campo assente sul filo ("non toccare"), uno 0m resta uno 0 presente ("azzera").
        if (stopLoss is decimal sl) request.StopLoss = DecimalValueMapper.ToProto(sl);
        if (takeProfit is decimal tp) request.TakeProfit = DecimalValueMapper.ToProto(tp);
        if (trailingStopPercent is decimal ts) request.TrailingStopPercent = DecimalValueMapper.ToProto(ts);

        await client.SetStopLossTakeProfitAsync(request, cancellationToken: ct);
    }

    public async Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)
    {
        await client.ConfirmOrderAsync(new Proto.ConfirmOrderRequest
        {
            LaneId = laneId,
            OrderId = orderId,
            UserId = userId ?? string.Empty,
        }, cancellationToken: ct);
        logger.LogInformation("Lane {Lane}: ordine {Order} confermato da {User} (remoto).", laneId, orderId, userId ?? "?");
    }

    public async Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)
    {
        await client.RejectOrderAsync(new Proto.RejectOrderRequest
        {
            LaneId = laneId,
            OrderId = orderId,
            UserId = userId ?? string.Empty,
        }, cancellationToken: ct);
        logger.LogInformation("Lane {Lane}: ordine {Order} rifiutato da {User} (remoto).", laneId, orderId, userId ?? "?");
    }

    // ------------------------------------------------------- lettura ordini: bypass di gRPC
    // Questi due NON passano dal servizio: sono query pure su Postgres, senza alcuno stato in
    // memoria del motore (a differenza di GetOpenPositions/GetPerformance, dove il prezzo
    // mark-to-market vive solo nell'engine). Il monolite ha già il DbContextFactory sullo stesso
    // database: un salto di rete servirebbe solo ad aggiungere un modo di fallire.
    //
    // Il CRITERIO delle query vive in TradingOrderQueries, lo stesso usato dal TradingEngine: in
    // origine qui c'era una copia riga-per-riga con l'avvertimento "se cambia là, aggiorna qua" —
    // la composizione condivisa rende la deriva impossibile per costruzione invece che vietata per
    // convenzione. I test head-to-head (RemoteTradingEngineClientTests) restano come cintura.

    public async Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TradingOrderQueries.History(db.Orders, laneId, from).ToListAsync(ct);
    }

    public async Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TradingOrderQueries.PendingLive(db.Orders.AsNoTracking(), laneId).ToListAsync(ct);
    }

    // ------------------------------------------------------------------- cicli del worker

    // Il ciclo delle candele e quello delle fette di esecuzione vivono DENTRO procionemgr-trading
    // (TradingWorker/ExecutionWorker sono registrati là). In modalità remota il monolite non li
    // registra affatto, quindi questi due metodi non hanno chiamanti: lanciare è meglio che
    // simulare un no-op silenzioso, che nasconderebbe un errore di composizione DI facendo sembrare
    // che le candele vengano elaborate mentre nessuno le elabora. Stesso patto di
    // RemoteMarketDataSyncService.SyncAllEnabledAsync (Fase 1).

    public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Le candele sono elaborate dal TradingWorker del servizio di trading remoto " +
            "(Trading:UseRemoteTrading=true); il worker non è registrato nel monolite in questa " +
            "modalità, quindi ProcessCandleAsync non deve essere invocato sul client remoto.");

    /// <summary>
    /// [R1] I tick di prezzo NON attraversano mai il confine gRPC: sarebbe una chiamata di rete per
    /// ogni tick, e la latenza che si vorrebbe eliminare tornerebbe dentro dal lato sbagliato. Il
    /// feed real-time è registrato nello STESSO host del motore (vedi AddTradingLanes, ramo
    /// !useRemote) e chiama l'engine locale in-process — stessa regola "un scrittore, un host" già
    /// applicata a LaneInvariantWatchdog e all'EnsembleRebalanceWorker.
    /// </summary>
    public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "I tick real-time sono elaborati dal feed del servizio di trading remoto " +
            "(Trading:UseRemoteTrading=true): il feed è co-locato col motore e non è registrato nel " +
            "monolite in questa modalità, quindi ProcessPriceTickAsync non deve essere invocato sul " +
            "client remoto.");

    public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Le fette di esecuzione sono avanzate dall'ExecutionWorker del servizio di trading " +
            "remoto (Trading:UseRemoteTrading=true); l'ExecutionWorker non è registrato nel " +
            "monolite in questa modalità, quindi ProcessDueExecutionSlicesAsync non deve essere " +
            "invocato sul client remoto.");
}
