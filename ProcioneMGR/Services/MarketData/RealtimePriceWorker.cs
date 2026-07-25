using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Orchestratore del feed real-time: UNO per flotta, non uno per corsia.
///
/// Tiene una connessione per exchange, ricava le sottoscrizioni dalle corsie effettivamente in
/// esecuzione, e instrada:
///  - i TICK verso <see cref="ITradingEngine.ProcessPriceTickAsync"/> delle corsie che operano quel
///    simbolo (solo uscite protettive: il motore non apre mai da un tick);
///  - le CANDELE CHIUSE verso la tabella OHLCV e poi verso il motore, senza attendere il ciclo REST.
///
/// Il feed è ADDITIVO: <c>MarketDataSyncWorker</c> e <c>TradingWorker</c> restano attivi e
/// indipendenti. Non c'è quindi nessun "fallback" da attivare quando il WebSocket cade — il
/// percorso a candele REST non ha mai smesso di funzionare. Quello che serve, e che c'è, è non
/// CREDERSI aggiornati quando non lo si è: da qui la watchdog di staleness che allerta.
/// </summary>
public sealed class RealtimePriceWorker(
    IServiceProvider services,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEnumerable<IExchangeStreamMapper> mappers,
    IWebSocketTransportFactory transportFactory,
    IOptionsMonitor<RealtimeFeedOptions> options,
    ILogger<RealtimePriceWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    INotifier? notifier = null) : BackgroundService
{
    /// <summary>
    /// Coda dei tick: LIMITATA e a scarto del più VECCHIO. Un tick vecchio non ha alcun valore —
    /// decidere un'uscita su un prezzo di dieci secondi fa è peggio che saltarlo — e una coda
    /// illimitata trasformerebbe un motore lento in un backlog che cresce senza fine.
    /// </summary>
    private const int TickQueueCapacity = 256;

    /// <summary>Le candele chiuse sono rare e NON sono sacrificabili: coda ampia, nessuno scarto silenzioso.</summary>
    private const int BarQueueCapacity = 512;

    private readonly Channel<PriceTick> _ticks = Channel.CreateBounded<PriceTick>(
        new BoundedChannelOptions(TickQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Channel<BarClosed> _bars = Channel.CreateBounded<BarClosed>(
        new BoundedChannelOptions(BarQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>Istantanea di ciò che ogni corsia sta operando, aggiornata a ogni refresh.</summary>
    private sealed record LaneRoute(int LaneId, ExchangeName Exchange, string Symbol, string Timeframe, MarketType MarketType);

    private volatile IReadOnlyList<LaneRoute> _routes = [];
    private readonly HashSet<ExchangeName> _staleAlerted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation(
                "Feed real-time DISATTIVATO (MarketData:Realtime:Enabled=false): la piattaforma resta sul solo percorso a candele REST.");
            return;
        }

        var feeds = mappers
            .Select(m => new WebSocketPriceFeed(m, transportFactory, options, logger, metrics))
            .ToList();

        foreach (var feed in feeds)
        {
            feed.TickReceived += tick => _ticks.Writer.TryWrite(tick);
            feed.BarClosed += bar => _bars.Writer.TryWrite(bar);
        }

        logger.LogInformation("Feed real-time avviato per {N} exchange (uscite protettive: {Drive}).",
            feeds.Count, options.CurrentValue.DriveProtectiveExits ? "guidate dai tick" : "SOLO OSSERVAZIONE");

        var tasks = new List<Task>();
        tasks.AddRange(feeds.Select(f => f.RunAsync(stoppingToken)));
        tasks.Add(ConsumeTicksAsync(stoppingToken));
        tasks.Add(ConsumeBarsAsync(stoppingToken));
        tasks.Add(RefreshLoopAsync(feeds, stoppingToken));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown ordinato
        }
    }

    // ------------------------------------------------------------------ sottoscrizioni e salute

    private async Task RefreshLoopAsync(IReadOnlyList<WebSocketPriceFeed> feeds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshSubscriptionsAsync(feeds, ct);
                CheckStaleness(feeds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feed real-time: aggiornamento delle sottoscrizioni fallito; ritento.");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.SubscriptionRefreshSeconds));
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Ricava le sottoscrizioni dalle corsie IN ESECUZIONE. Si legge lo stato persistito invece di
    /// interrogare i motori: una query sola per tutte le corsie, e nessuna dipendenza dal fatto che
    /// il motore sia locale o remoto.
    /// </summary>
    private async Task RefreshSubscriptionsAsync(IReadOnlyList<WebSocketPriceFeed> feeds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var states = await db.TradingEngineStates
            .AsNoTracking()
            .Where(s => s.IsRunning && !s.IsEmergencyStopped)
            .Select(s => new { s.LaneId, s.ExchangeName, s.Symbol, s.Timeframe, s.MarketType })
            .ToListAsync(ct);

        var routes = new List<LaneRoute>();
        foreach (var s in states)
        {
            if (string.IsNullOrWhiteSpace(s.Symbol)) continue;
            if (!Enum.TryParse<ExchangeName>(s.ExchangeName, ignoreCase: true, out var exchange))
            {
                logger.LogWarning("Feed real-time: exchange '{Name}' della corsia {Lane} non riconosciuto; corsia ignorata.",
                    s.ExchangeName, s.LaneId);
                continue;
            }
            routes.Add(new LaneRoute(s.LaneId, exchange, s.Symbol, s.Timeframe, s.MarketType));
        }

        _routes = routes;

        foreach (var feed in feeds)
        {
            var subs = routes
                .Where(r => r.Exchange == feed.Exchange)
                .Select(r => new StreamSubscription(r.Exchange, r.Symbol, r.Timeframe, r.MarketType))
                .ToList();

            if (feed.UpdateSubscriptions(subs))
            {
                logger.LogInformation("Feed {Exchange}: sottoscrizioni aggiornate a {N} serie.", feed.Exchange, subs.Count);
            }
        }
    }

    /// <summary>
    /// Allerta UNA SOLA VOLTA per transizione sano→stale (e informa al ritorno). Notificare a ogni
    /// giro trasformerebbe l'allarme in rumore, che è il modo migliore per non farlo leggere.
    /// </summary>
    private void CheckStaleness(IReadOnlyList<WebSocketPriceFeed> feeds)
    {
        var threshold = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.StaleAfterSeconds));
        var now = DateTime.UtcNow;

        foreach (var feed in feeds)
        {
            var health = feed.Health;

            // Un feed senza sottoscrizioni non è "fermo": non ha nulla da ricevere.
            if (!_routes.Any(r => r.Exchange == feed.Exchange))
            {
                _staleAlerted.Remove(feed.Exchange);
                continue;
            }

            var stale = health.IsStale(threshold, now);
            if (stale && _staleAlerted.Add(feed.Exchange))
            {
                logger.LogError("Feed {Exchange} STALE: nessun messaggio da oltre {Sec}s (ultimo: {Last}).",
                    feed.Exchange, threshold.TotalSeconds, health.LastMessageUtc?.ToString("u") ?? "mai");
                Notify(NotificationSeverity.Warning, $"Feed real-time {feed.Exchange} non risponde",
                    $"Nessun messaggio da oltre {threshold.TotalSeconds:F0}s. Gli stop tornano a reagire solo alla chiusura " +
                    "candela (percorso REST), quindi con un ritardo che può arrivare a diversi minuti.");
            }
            else if (!stale && _staleAlerted.Remove(feed.Exchange))
            {
                logger.LogInformation("Feed {Exchange}: tornato a ricevere.", feed.Exchange);
                Notify(NotificationSeverity.Info, $"Feed real-time {feed.Exchange} ripristinato",
                    "I tick sono tornati: le uscite protettive sono di nuovo reattive.");
            }
        }
    }

    private void Notify(NotificationSeverity severity, string title, string body)
    {
        if (notifier is null) return;
        _ = Task.Run(async () =>
        {
            try { await notifier.NotifyAsync(severity, title, body, CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Feed real-time: notifica non recapitata."); }
        });
    }

    // ------------------------------------------------------------------ instradamento

    private async Task ConsumeTicksAsync(CancellationToken ct)
    {
        await foreach (var tick in _ticks.Reader.ReadAllAsync(ct))
        {
            if (!options.CurrentValue.DriveProtectiveExits)
            {
                continue; // modalità osservativa: si misura il feed, non gli si dà potere
            }

            foreach (var route in _routes)
            {
                if (route.Exchange != tick.Exchange
                    || !string.Equals(route.Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var engine = services.GetRequiredKeyedService<ITradingEngine>(route.LaneId);
                    await engine.ProcessPriceTickAsync(tick.Mid, tick.TimestampUtc, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Feed real-time: tick {Symbol} non elaborato dalla corsia {Lane}.",
                        tick.Symbol, route.LaneId);
                }
            }
        }
    }

    private async Task ConsumeBarsAsync(CancellationToken ct)
    {
        await foreach (var bar in _bars.Reader.ReadAllAsync(ct))
        {
            try
            {
                await PersistBarAsync(bar, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feed real-time: candela {Symbol} {Tf} non persistita.", bar.Symbol, bar.Timeframe);
                continue; // senza la riga a DB non si consegna al motore: si aspetta il ciclo REST
            }

            foreach (var route in _routes)
            {
                if (route.Exchange != bar.Exchange
                    || !string.Equals(route.Symbol, bar.Symbol, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(route.Timeframe, bar.Timeframe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // Consegna anticipata. Se il ciclo REST rileggerà poi la stessa candela dal DB,
                    // il motore la scarterà da solo (dedup su TimestampUtc del proprio buffer):
                    // i due percorsi convergono senza doppioni.
                    var engine = services.GetRequiredKeyedService<ITradingEngine>(route.LaneId);
                    await engine.ProcessCandleAsync(bar.ToOhlcv(), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Feed real-time: candela {Symbol} non elaborata dalla corsia {Lane}.",
                        bar.Symbol, route.LaneId);
                }
            }
        }
    }

    /// <summary>
    /// UPSERT della singola candela chiusa, stessa semantica idempotente di
    /// <c>OhlcvIngestionService.UpsertBatchAsync</c>: la riga può già esistere se il ciclo REST è
    /// arrivato prima, e in quel caso si aggiornano i valori invece di duplicare.
    /// </summary>
    private async Task PersistBarAsync(BarClosed bar, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.OhlcvData.FirstOrDefaultAsync(
            c => c.Symbol == bar.Symbol && c.Timeframe == bar.Timeframe && c.TimestampUtc == bar.OpenTimeUtc, ct);

        if (existing is null)
        {
            db.OhlcvData.Add(bar.ToOhlcv());
        }
        else
        {
            existing.Open = bar.Open;
            existing.High = bar.High;
            existing.Low = bar.Low;
            existing.Close = bar.Close;
            existing.Volume = bar.Volume;
        }

        await db.SaveChangesAsync(ct);
    }
}
