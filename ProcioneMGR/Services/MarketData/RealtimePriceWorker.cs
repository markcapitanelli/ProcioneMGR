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
    INotifier? notifier = null,
    TimeSpan? switchPollInterval = null) : BackgroundService
{
    /// <summary>
    /// Ogni quanto si rilegge l'interruttore. Parametro (con default) e non costante: i test del
    /// ciclo acceso→spento→acceso devono poter girare in millisecondi invece che in decine di
    /// secondi, senza introdurre stato statico mutabile condiviso fra test paralleli.
    /// </summary>
    private readonly TimeSpan _switchPoll = switchPollInterval ?? TimeSpan.FromSeconds(5);

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

    /// <summary>[G2] Serie già segnalate come ferme (exchange + simbolo maiuscolo): allarme sulla transizione.</summary>
    private readonly HashSet<(ExchangeName Exchange, string Symbol)> _staleAlerted = [];

    // ---- [2026-08-13] Anti-raffica sulle NOTIFICHE di staleness. ----------------------------
    // Il log segnala ogni transizione (è diagnostica, e deve restare fitta). La NOTIFICA no: su
    // STX/USDT — simbolo illiquido, dove un silenzio di un paio di minuti è il ritmo normale e non
    // un guasto — la coppia «non risponde»/«ripristinato» partiva ogni 1-2 minuti, fino a saturare
    // il rate-limit del canale («+2 notifiche soppresse» osservato su Telegram il 2026-08-13).
    // Il danno non è il fastidio: è che le notifiche VERE (corsia in quarantena, posizioni orfane)
    // condividono quel budget di 20 messaggi/ora e sarebbero state soppresse dal rumore.

    /// <summary>Controlli consecutivi oltre soglia per serie: la notifica pretende PERSISTENZA, non un campione.</summary>
    private readonly Dictionary<(ExchangeName Exchange, string Symbol), int> _staleStreak = [];

    /// <summary>Ultima notifica inviata per serie: cooldown, così una serie che oscilla parla una volta all'ora.</summary>
    private readonly Dictionary<(ExchangeName Exchange, string Symbol), DateTime> _staleNotifiedUtc = [];

    /// <summary>Serie per cui è partita davvero una NOTIFICA: solo per queste si annuncia il rientro.</summary>
    private readonly HashSet<(ExchangeName Exchange, string Symbol)> _staleNotified = [];

    /// <summary>
    /// Controlli consecutivi oltre soglia prima di notificare. Il giro è
    /// <see cref="RealtimeFeedOptions.SubscriptionRefreshSeconds"/> (30s di default), quindi con 3
    /// il silenzio deve durare la soglia PIÙ un paio di giri: un simbolo che consegna a strappi
    /// non allarma, uno stream davvero morto sì (con al più un minuto e mezzo di ritardo).
    /// </summary>
    private const int StaleChecksBeforeNotify = 3;

    /// <summary>Fra due notifiche sulla STESSA serie: un guasto che dura resta vero, ma si dice una volta all'ora.</summary>
    private static readonly TimeSpan StaleNotifyCooldown = TimeSpan.FromHours(1);

    /// <summary>
    /// Inizio della sessione di feed corrente: serve a distinguere "non ha ancora cominciato" da
    /// "ha smesso di ricevere" nella watchdog di staleness. Si azzera a ogni riaccensione.
    /// </summary>
    private DateTime _sessionStartedUtc = DateTime.UtcNow;

    /// <summary>
    /// Vero mentre una sessione di feed è attiva. È l'osservabile con cui
    /// <c>RealtimeFeedSwitchTests</c> verifica che l'interruttore apra e chiuda DAVVERO le
    /// connessioni: senza, quella prova si ridurrebbe a «il metodo non ha lanciato eccezioni».
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Ciclo esterno: sorveglia l'INTERRUTTORE e apre/chiude una sessione di feed di conseguenza.
    ///
    /// <para>Prima del 2026-07-29 questo metodo usciva subito con <c>Enabled=false</c> e non
    /// tornava più: accendere il feed richiedeva un riavvio del processo — cioè, col motore in
    /// cluster, un riavvio del pod che sta operando. Una manopola che per funzionare pretende di
    /// riavviare il motore non è una manopola.</para>
    ///
    /// <para>La sessione ha un suo <see cref="CancellationTokenSource"/> figlio: spegnere il feed
    /// cancella quello e lascia intatto lo <paramref name="stoppingToken"/> dell'host. Le code
    /// vengono SVUOTATE alla fine di ogni sessione — un tick sopravvissuto allo spegnimento
    /// arriverebbe al motore con un prezzo vecchio di minuti, ed è esattamente il tipo di dato con
    /// cui non si decide un'uscita.</para>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var announcedOff = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!options.CurrentValue.Enabled)
            {
                if (!announcedOff)
                {
                    logger.LogInformation(
                        "Feed real-time DISATTIVATO (MarketData:Realtime:Enabled=false): la piattaforma resta sul solo percorso a candele REST.");
                    announcedOff = true;
                }
                if (!await DelayAsync(_switchPoll, stoppingToken)) break;
                continue;
            }
            announcedOff = false;

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var run = RunSessionAsync(session.Token);

            // Si resta qui finché l'interruttore regge, l'host non si ferma e la sessione è viva.
            while (!stoppingToken.IsCancellationRequested && options.CurrentValue.Enabled && !run.IsCompleted)
            {
                if (!await DelayAsync(_switchPoll, stoppingToken)) break;
            }

            await session.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) { /* chiusura richiesta: non è un guasto */ }
            finally
            {
                IsRunning = false;
                DrainQueues();
            }

            if (!stoppingToken.IsCancellationRequested && !options.CurrentValue.Enabled)
            {
                logger.LogInformation("Feed real-time FERMATO su richiesta: connessioni chiuse, code svuotate.");
            }
        }
    }

    /// <summary>Una sessione di feed: connessioni, consumatori, refresh delle sottoscrizioni.</summary>
    private async Task RunSessionAsync(CancellationToken ct)
    {
        // Azzerato QUI, non nel costruttore: la grazia della watchdog vale per ogni riaccensione,
        // non solo per la prima dopo l'avvio del processo.
        _sessionStartedUtc = DateTime.UtcNow;
        _staleAlerted.Clear();
        _staleStreak.Clear();
        _staleNotified.Clear();
        _staleNotifiedUtc.Clear();

        var feeds = mappers
            .Select(m => new WebSocketPriceFeed(m, transportFactory, options, logger, metrics))
            .ToList();

        foreach (var feed in feeds)
        {
            feed.TickReceived += tick => _ticks.Writer.TryWrite(tick);
            feed.BarClosed += bar => _bars.Writer.TryWrite(bar);
        }

        logger.LogInformation("Feed real-time avviato per {N} exchange (uscite protettive: {Drive}).",
            feeds.Count,
            options.CurrentValue.DriveProtectiveExits
                ? "guidate dai tick"
                : "guidate dalle candele — i tick osservano soltanto (sentinella d'ombra B3)");
        IsRunning = true;

        var tasks = new List<Task>();
        tasks.AddRange(feeds.Select(f => f.RunAsync(ct)));
        tasks.Add(ConsumeTicksAsync(ct));
        tasks.Add(ConsumeBarsAsync(ct));
        tasks.Add(RefreshLoopAsync(feeds, ct));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // chiusura ordinata della sessione (host che si ferma o interruttore abbassato)
        }
    }

    /// <summary>
    /// Svuota le code fra due sessioni. Non è igiene generica: un tick rimasto in coda mentre il
    /// feed era spento verrebbe consumato alla riaccensione con un prezzo ormai vecchio, e con
    /// <c>DriveProtectiveExits</c> acceso quel prezzo può chiudere una posizione.
    /// </summary>
    private void DrainQueues()
    {
        while (_ticks.Reader.TryRead(out _)) { }
        while (_bars.Reader.TryRead(out _)) { }
    }

    /// <summary>Attesa che restituisce false quando l'host si sta fermando (invece di lanciare).</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
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
                // Il riciclo lo fa il feed da sé (C1): qui si dice cosa sta succedendo davvero,
                // non «aggiornate» — che rassicurava mentre la connessione restava sul set vecchio.
                logger.LogInformation("Feed {Exchange}: sottoscrizioni cambiate ({N} serie), riciclo la connessione.",
                    feed.Exchange, subs.Count);
            }
        }
    }

    /// <summary>
    /// [G2] Allerta PER SERIE, una sola volta per transizione sano→stale (e informa al ritorno).
    /// La versione per-feed guardava <see cref="FeedHealth.LastMessageUtc"/> dell'intero canale:
    /// bastava UN simbolo vivo a coprire il silenzio di tutti gli altri — è il complice che ha
    /// reso invisibile C1, e resta un buco anche col riciclo a posto (uno stream che l'exchange
    /// smette di consegnare per blocco regionale tace mentre i vicini parlano). La grazia è
    /// per-serie: dal momento della SUA sottoscrizione, non dall'inizio della sessione.
    /// Notifiche AGGREGATE per giro (pattern SeriesFreshnessWatchWorker): un guasto di rete che
    /// ammutolisce venti serie insieme deve produrre un messaggio, non venti.
    /// </summary>
    private void CheckStaleness(IReadOnlyList<WebSocketPriceFeed> feeds)
    {
        var threshold = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.StaleAfterSeconds));
        var now = DateTime.UtcNow;

        var newlyStale = new List<string>();
        var recovered = new List<string>();

        foreach (var feed in feeds)
        {
            var routedSymbols = _routes
                .Where(r => r.Exchange == feed.Exchange)
                .Select(r => r.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var series in feed.SeriesHealthSnapshot)
            {
                var key = (feed.Exchange, series.Symbol.ToUpperInvariant());

                // Una serie non più instradata non è "ferma": nessuna corsia la opera. Il feed la
                // toglie dal set al prossimo refresh; qui si riarma solo l'allarme.
                if (!routedSymbols.Contains(series.Symbol))
                {
                    ForgetSeries(key);
                    continue;
                }

                // La grazia parte dal momento più recente fra inizio sessione e sottoscrizione
                // della serie: una serie aggiunta a sessione viva non ha ancora avuto modo di
                // consegnare, e la sessione appena riaccesa azzera la storia di tutte.
                var graceStart = series.SubscribedSinceUtc > _sessionStartedUtc ? series.SubscribedSinceUtc : _sessionStartedUtc;
                var stale = ShouldAlertStale(series.LastEventUtc, threshold, now, graceStart);

                if (stale)
                {
                    // Il LOG segue ogni transizione: è la diagnostica, e deve restare fitta.
                    if (_staleAlerted.Add(key))
                    {
                        logger.LogWarning("Serie {Exchange} {Symbol} STALE sul feed: nessun evento da oltre {Sec}s (ultimo: {Last}).",
                            feed.Exchange, series.Symbol, threshold.TotalSeconds, series.LastEventUtc?.ToString("u") ?? "mai");
                    }

                    var streak = _staleStreak.GetValueOrDefault(key) + 1;
                    _staleStreak[key] = streak;

                    if (ShouldNotifyStale(key, streak, series.LastEventUtc is null, now))
                    {
                        _staleNotifiedUtc[key] = now;
                        _staleNotified.Add(key);
                        newlyStale.Add($"{feed.Exchange} {series.Symbol} (ultimo: {series.LastEventUtc?.ToString("HH:mm:ss") ?? "mai"})");
                    }
                }
                else
                {
                    _staleStreak.Remove(key);
                    if (_staleAlerted.Remove(key))
                    {
                        logger.LogInformation("Serie {Exchange} {Symbol}: tornata a ricevere sul feed.", feed.Exchange, series.Symbol);
                        // Il rientro si annuncia SOLO a chi aveva ricevuto l'allarme: un «ripristinato»
                        // senza il guasto corrispondente è rumore puro (metà dei messaggi su STX).
                        if (_staleNotified.Remove(key)) recovered.Add($"{feed.Exchange} {series.Symbol}");
                    }
                }
            }
        }

        if (newlyStale.Count > 0)
        {
            // Il testo dice la conseguenza VERA nell'assetto corrente. Quello precedente («per
            // queste serie gli stop reagiscono solo alla chiusura candela») descriveva come guasto
            // ciò che, con le uscite guidate dalle candele, è il comportamento normale e deliberato
            // di TUTTE le serie (B3, 24 configurazioni su 24): allarmante e falso insieme.
            var conseguenza = options.CurrentValue.DriveProtectiveExits
                ? "Le uscite protettive sono guidate dai tick: finché il feed tace, per queste serie restano "
                  + "solo le chiusure candela del percorso REST, con un ritardo che può arrivare a minuti."
                : "Il feed è in sola osservazione (le uscite le guidano le candele), quindi gli stop non cambiano "
                  + "comportamento: la serie però non ha MAI consegnato in questa sessione, il che indica uno "
                  + "stream bloccato o un simbolo non instradato.";

            Notify(NotificationSeverity.Warning,
                $"{newlyStale.Count} serie del feed real-time non rispondono",
                $"Nessun tick/candela da oltre {threshold.TotalSeconds:F0}s su: {string.Join(", ", newlyStale)} "
                + $"(silenzio confermato per {StaleChecksBeforeNotify} controlli consecutivi). {conseguenza}");
        }
        if (recovered.Count > 0)
        {
            Notify(NotificationSeverity.Info,
                $"Feed real-time ripristinato per {recovered.Count} serie",
                $"Tornati gli eventi su: {string.Join(", ", recovered)}.");
        }
    }

    /// <summary>
    /// Quando un canale silenzioso merita un allarme.
    ///
    /// <para>«Non ha ancora cominciato» NON è «ha smesso». Appena connesso, il primo messaggio può
    /// tardare più della soglia in tutta legittimità — mercato calmo, handshake, sottoscrizioni
    /// appena inviate — e allertare lì è un falso allarme garantito a ogni avvio. Da quando il feed
    /// si accende e si spegne dal pannello, quel falso allarme scatterebbe a ogni singolo toggle,
    /// con tanto di notifica all'operatore: rumore che insegna a ignorare gli allarmi veri.</para>
    ///
    /// <para>Si concede quindi una grazia pari alla soglia, contata dall'inizio della SESSIONE.
    /// Senza buttare via il caso che conta: un endpoint che si connette e non consegna MAI è un
    /// guasto reale (è il blocco EEA/MiCA visto sulle liquidazioni), e continua ad allertare — solo
    /// dopo la grazia invece che subito.</para>
    /// </summary>
    internal static bool ShouldAlertStale(FeedHealth health, TimeSpan threshold, DateTime nowUtc, DateTime sessionStartedUtc)
        => ShouldAlertStale(health.LastMessageUtc, threshold, nowUtc, sessionStartedUtc);

    /// <summary>
    /// [G2] Il cuore della regola, riusato per-feed (overload sopra, coi suoi test) e per-serie:
    /// silenzio da sempre dentro la grazia ⇒ non ancora un allarme; tutto il resto del silenzio
    /// oltre soglia ⇒ allarme, incluso chi non ha MAI consegnato una volta scaduta la grazia.
    /// </summary>
    internal static bool ShouldAlertStale(DateTime? lastUtc, TimeSpan threshold, DateTime nowUtc, DateTime graceStartUtc)
    {
        if (lastUtc is null && nowUtc - graceStartUtc < threshold) return false;
        return lastUtc is not DateTime last || nowUtc - last > threshold;
    }

    /// <summary>
    /// [2026-08-13] Se la staleness di questa serie merita una NOTIFICA (il log l'ha già detta).
    /// Tre filtri, in ordine di severità del giudizio:
    ///
    /// <para><b>Persistenza</b>: un solo campione oltre soglia non è un guasto. Su un simbolo
    /// illiquido come STX/USDT un silenzio di poco più di un minuto è il ritmo normale, e la coppia
    /// «non risponde»/«ripristinato» partiva ogni 1-2 minuti.</para>
    ///
    /// <para><b>Azionabilità</b>: con le uscite guidate dalle candele (<c>DriveProtectiveExits</c>
    /// false, che è il default PER MISURA — B3) un'intermittenza del feed non ha conseguenze
    /// operative: gli stop passano dal percorso REST per tutte le serie, sempre. Notificarla è
    /// rumore che consuma il budget del canale (20 messaggi/ora) condiviso con gli allarmi veri —
    /// corsia in quarantena, posizioni orfane — che verrebbero soppressi. Resta invece azionabile
    /// anche in osservazione il caso STRUTTURALE: uno stream che non ha MAI consegnato è rotto o
    /// bloccato (è il blocco EEA/MiCA visto sulle liquidazioni), e quello si dice.</para>
    ///
    /// <para><b>Cooldown</b>: un guasto che dura resta vero, ma va ripetuto una volta all'ora, non
    /// a ogni giro.</para>
    /// </summary>
    internal static bool ShouldNotifyStale(
        int streak, bool drivesProtectiveExits, bool neverDelivered,
        DateTime? lastNotifiedUtc, DateTime nowUtc,
        int checksBeforeNotify = StaleChecksBeforeNotify, TimeSpan? cooldown = null)
    {
        if (streak < checksBeforeNotify) return false;
        if (!drivesProtectiveExits && !neverDelivered) return false;
        if (lastNotifiedUtc is DateTime last && nowUtc - last < (cooldown ?? StaleNotifyCooldown)) return false;
        return true;
    }

    /// <summary>Applica la regola sopra allo stato tenuto per questa serie.</summary>
    private bool ShouldNotifyStale(
        (ExchangeName Exchange, string Symbol) key, int streak, bool neverDelivered, DateTime nowUtc)
        => ShouldNotifyStale(
            streak,
            options.CurrentValue.DriveProtectiveExits,
            neverDelivered,
            _staleNotifiedUtc.TryGetValue(key, out var last) ? last : null,
            nowUtc);

    /// <summary>Dimentica ogni traccia di una serie non più instradata (nessuna corsia la opera).</summary>
    private void ForgetSeries((ExchangeName Exchange, string Symbol) key)
    {
        _staleAlerted.Remove(key);
        _staleStreak.Remove(key);
        _staleNotified.Remove(key);
        _staleNotifiedUtc.Remove(key);
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
            // [B3] I tick vengono SEMPRE instradati, anche in assetto osservativo. Prima venivano
            // scartati qui, ed è la ragione per cui il gate B3 chiedeva un confronto tick-vs-candela
            // che nessuno poteva produrre: senza tick al motore non esiste un lato "tick" da
            // confrontare. È il MOTORE a decidere cosa farne — esegue l'uscita se
            // DriveProtectiveExits è acceso, altrimenti la osserva soltanto (sentinella d'ombra,
            // rigorosamente in sola lettura sullo stato delle posizioni).
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
