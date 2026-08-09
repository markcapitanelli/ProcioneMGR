using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.MarketData;

/// <summary>Stato osservabile di una connessione del feed, per UI, metriche e watchdog.</summary>
public sealed record FeedHealth(
    ExchangeName Exchange,
    bool IsConnected,
    DateTime? LastMessageUtc,
    int Reconnects,
    long MessagesReceived,
    string? LastError)
{
    /// <summary>True se il canale tace da troppo: la fonte non è più considerabile viva.</summary>
    public bool IsStale(TimeSpan threshold, DateTime nowUtc) =>
        LastMessageUtc is not DateTime last || nowUtc - last > threshold;
}

/// <summary>
/// [G2] Salute di UNA serie sottoscritta: ultimo evento utile (tick/candela) e istante di
/// sottoscrizione. Complementa <see cref="FeedHealth"/>: quello dice se il CANALE è vivo, questo
/// se il SIMBOLO consegna — e basta un simbolo vivo a mascherare il silenzio degli altri.
/// </summary>
public sealed record SeriesHealth(
    ExchangeName Exchange,
    string Symbol,
    DateTime? LastEventUtc,
    DateTime SubscribedSinceUtc);

/// <summary>
/// Una connessione WebSocket verso un exchange, mantenuta viva a oltranza.
///
/// Responsabilità: connettere, sottoscrivere, leggere, riconnettere con backoff esponenziale e
/// jitter, ripresentare le sottoscrizioni dopo ogni riconnessione, e RICICLARE la connessione
/// quando il set di sottoscrizioni cambia (gli exchange le negoziano solo al connect: senza
/// riciclo un cambio resterebbe lettera morta). Il PARSING è del mapper, il ROUTING è del worker:
/// qui si emettono solo eventi già tipizzati.
///
/// Il jitter sul backoff non è ornamentale: senza, tre corsie che perdono la connessione insieme
/// (tipico — la rete cade per tutte) ritenterebbero nello stesso istante a ogni giro, martellando
/// l'exchange in sincrono proprio mentre è in difficoltà.
/// </summary>
public sealed class WebSocketPriceFeed(
    IExchangeStreamMapper mapper,
    IWebSocketTransportFactory transportFactory,
    IOptionsMonitor<RealtimeFeedOptions> options,
    ILogger logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _sync = new();

    private IReadOnlyList<StreamSubscription> _subscriptions = [];
    private Dictionary<string, StreamSubscription> _byExchangeSymbol = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [C1] CTS della connessione IN CORSO, linked al token di <see cref="RunAsync"/>.
    /// <see cref="UpdateSubscriptions"/> lo cancella quando il set cambia: è ciò che trasforma un
    /// aggiornamento di sottoscrizioni in un riciclo immediato della connessione, invece di un
    /// cambio di stato che nessuno applica finché il socket non cade da solo.
    /// </summary>
    private CancellationTokenSource? _connectionCts;

    private volatile bool _connected;
    private long _messages;
    private int _reconnects;
    private DateTime? _lastMessageUtc;
    private string? _lastError;

    /// <summary>
    /// [G2] Ultimo evento UTILE (tick o candela) per SIMBOLO sottoscritto, più l'istante in cui il
    /// simbolo è entrato nel set. Il feed-level <see cref="Health"/> non basta alla watchdog: basta
    /// UN simbolo vivo per coprire il silenzio di tutti gli altri — è il complice che ha reso
    /// invisibile C1, e resta un buco anche col riciclo a posto (uno stream che l'exchange smette
    /// di consegnare per blocco regionale tace mentre gli altri parlano). Chiavi = simbolo
    /// canonico ("BTC/USDT"): è ciò che i mapper mettono negli eventi (sub.Symbol).
    /// </summary>
    private readonly Dictionary<string, DateTime> _lastBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _subscribedSinceBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public ExchangeName Exchange => mapper.Exchange;

    /// <summary>Emesso per ogni tick valido. I gestori NON devono lanciare: un'eccezione qui è loggata e ignorata.</summary>
    public event Action<PriceTick>? TickReceived;

    /// <summary>Emesso per ogni candela CHIUSA (solo sugli exchange che la segnalano esplicitamente).</summary>
    public event Action<BarClosed>? BarClosed;

    public FeedHealth Health
    {
        get
        {
            lock (_sync)
            {
                return new FeedHealth(mapper.Exchange, _connected, _lastMessageUtc, _reconnects, _messages, _lastError);
            }
        }
    }

    /// <summary>
    /// [G2] Salute PER SERIE: per ogni simbolo sottoscritto, l'ultimo evento utile ricevuto (null =
    /// mai, da quando è sottoscritto) e l'istante di sottoscrizione — il riferimento della grazia:
    /// un simbolo appena aggiunto non ha ancora avuto il tempo di consegnare, e allertarlo subito
    /// sarebbe il falso allarme a ogni avvio di corsia.
    /// </summary>
    public IReadOnlyList<SeriesHealth> SeriesHealthSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _subscribedSinceBySymbol
                    .Select(kv => new SeriesHealth(
                        mapper.Exchange, kv.Key,
                        _lastBySymbol.TryGetValue(kv.Key, out var last) ? last : null,
                        kv.Value))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Aggiorna l'insieme delle sottoscrizioni. Se è CAMBIATO rispetto a quello attivo, la
    /// connessione corrente viene riciclata QUI, cancellandone il CTS: Binance codifica le
    /// sottoscrizioni nell'URL e Bitget invia i frame solo al connect, quindi un cambio a
    /// connessione viva non avrebbe altrimenti alcun effetto finché il socket non cade da solo
    /// (Binance lo ricicla ogni 24 ore: possono volerci ORE). Ritorna true in quel caso — al
    /// chiamante serve solo per loggare, non per agire.
    /// </summary>
    public bool UpdateSubscriptions(IReadOnlyList<StreamSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        var ordered = subscriptions
            .Where(s => s.Exchange == mapper.Exchange)
            .DistinctBy(s => (s.Symbol, s.Timeframe, s.MarketType))
            .OrderBy(s => s.Symbol, StringComparer.Ordinal)
            .ThenBy(s => s.Timeframe, StringComparer.Ordinal)
            .ToList();

        CancellationTokenSource? active;
        lock (_sync)
        {
            if (ordered.SequenceEqual(_subscriptions))
            {
                return false;
            }
            // Indice del PARSING (simbolo dello stream -> sottoscrizione), chiave = SOLO simbolo:
            // più grossolana dell'identità del set (simbolo, timeframe, mercato). Due corsie sullo
            // stesso simbolo con timeframe o mercato diversi sono LEGITTIME e qui collidono: il
            // ToDictionary usato fino al 2026-08-09 lanciava ArgumentException ("Key: DOTUSDT",
            // visto dal vivo nel pod con due corsie su DOT/USDT) — e siccome _subscriptions era già
            // stato aggiornato, il refresh successivo non vedeva più alcun cambio: indice e
            // connessione restavano sul set vecchio fino al riavvio del pod, mentre il log
            // prometteva «ritento». Si tiene la PRIMA sottoscrizione nell'ordine deterministico di
            // `ordered`: al parser serve solo risalire al simbolo canonico, e il timeframe vero
            // della candela lo dichiara lo stream stesso (vince sulla sottoscrizione: vedi
            // BinanceStreamMapper.ParseKline). L'indice si costruisce PRIMA di toccare i campi,
            // così nessun errore può più lasciare lo stato a metà.
            var bySymbol = new Dictionary<string, StreamSubscription>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in ordered)
            {
                bySymbol.TryAdd(ExchangeSymbolOf(sub), sub);
            }

            _subscriptions = ordered;
            _byExchangeSymbol = bySymbol;

            // [G2] Contabilità per-serie allineata al set: i simboli nuovi partono ADESSO (è il
            // riferimento della loro grazia), quelli usciti si dimenticano — lasciare voci orfane
            // significherebbe una watchdog che sorveglia serie che nessuna corsia opera più.
            var now = _time.GetUtcNow().UtcDateTime;
            var current = ordered.Select(s => s.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in current)
            {
                _subscribedSinceBySymbol.TryAdd(symbol, now);
            }
            foreach (var gone in _subscribedSinceBySymbol.Keys.Where(k => !current.Contains(k)).ToList())
            {
                _subscribedSinceBySymbol.Remove(gone);
                _lastBySymbol.Remove(gone);
            }

            active = _connectionCts;
        }

        // Il Cancel sta FUORI dal lock: cancellare esegue i callback registrati sul token, e
        // farlo in sezione critica significherebbe correre codice arbitrario (le continuazioni
        // del pump) tenendo il lock.
        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La connessione è appena finita da sola: non c'è più nulla da riciclare — RunAsync
            // rilegge comunque il set aggiornato prima di riconnettersi.
        }

        return true;
    }

    private string ExchangeSymbolOf(StreamSubscription s) => mapper switch
    {
        BinanceStreamMapper => BinanceStreamMapper.ToStreamSymbol(s.Symbol).ToUpperInvariant(),
        BitgetStreamMapper => BitgetStreamMapper.ToStreamSymbol(s.Symbol),
        _ => s.Symbol.Replace("/", string.Empty).ToUpperInvariant(),
    };

    /// <summary>
    /// Ciclo di vita della connessione: gira finché non viene cancellato. Ogni caduta è un evento
    /// ATTESO, non un errore fatale — si riprova, per sempre, con attesa crescente.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<StreamSubscription> subs;
            Dictionary<string, StreamSubscription> index;

            // [C1] Snapshot e registrazione del CTS nello STESSO lock: un UpdateSubscriptions
            // arrivato un istante dopo trova già il CTS da cancellare — nessun cambio può cadere
            // nella fessura fra la lettura del set e l'apertura della connessione.
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_sync)
            {
                subs = _subscriptions;
                index = _byExchangeSymbol;
                _connectionCts = connectionCts;
            }

            var recycle = false;
            try
            {
                if (subs.Count == 0)
                {
                    // Nessuna corsia attiva: non si tiene aperta una connessione inutile. L'attesa
                    // è sul token di connessione: la prima sottoscrizione che arriva la interrompe
                    // e si connette subito, senza scontare il resto dei cinque secondi.
                    await SafeDelayAsync(TimeSpan.FromSeconds(5), connectionCts.Token);
                    continue;
                }

                await using var transport = transportFactory.Create();
                await transport.ConnectAsync(mapper.BuildEndpoint(subs), connectionCts.Token);

                foreach (var frame in mapper.BuildSubscribeFrames(subs))
                {
                    await transport.SendAsync(frame, connectionCts.Token);
                }

                MarkConnected();
                attempt = 0; // la connessione ha retto: il backoff riparte da zero
                logger.LogInformation("Feed {Exchange}: connesso, {N} sottoscrizioni.", mapper.Exchange, subs.Count);

                await PumpAsync(transport, index, connectionCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
            {
                // Riciclo chiesto da UpdateSubscriptions: NON è un guasto. Si riconnette subito
                // col set nuovo — niente backoff e niente conteggio fra le riconnessioni, che
                // misurano la salute della rete, non i cambi di configurazione.
                recycle = true;
                logger.LogInformation("Feed {Exchange}: connessione riciclata per cambio sottoscrizioni.", mapper.Exchange);
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                logger.LogWarning(ex, "Feed {Exchange}: connessione fallita o interrotta.", mapper.Exchange);
            }
            finally
            {
                MarkDisconnected();
                lock (_sync)
                {
                    // Si toglie di mezzo PRIMA che l'using lo smaltisca: un Cancel su un CTS
                    // pubblicato ma già smaltito è l'unica corsa possibile, e così resta stretta.
                    if (ReferenceEquals(_connectionCts, connectionCts))
                    {
                        _connectionCts = null;
                    }
                }
            }

            if (ct.IsCancellationRequested) break;

            if (recycle) continue;

            attempt++;
            Interlocked.Increment(ref _reconnects);
            metrics?.RecordRealtimeReconnect(mapper.Exchange.ToString());
            var delay = BackoffDelay(attempt);
            logger.LogInformation("Feed {Exchange}: riconnessione fra {Delay}ms (tentativo {Attempt}).",
                mapper.Exchange, delay.TotalMilliseconds, attempt);
            await SafeDelayAsync(delay, ct);
        }

        logger.LogInformation("Feed {Exchange}: fermato.", mapper.Exchange);
    }

    /// <summary>Legge finché il canale regge, emettendo gli eventi. Ritorna alla caduta del canale.</summary>
    private async Task PumpAsync(
        IWebSocketTransport transport,
        IReadOnlyDictionary<string, StreamSubscription> index,
        CancellationToken ct)
    {
        using var heartbeat = StartHeartbeat(transport, ct);

        while (!ct.IsCancellationRequested)
        {
            var raw = await transport.ReceiveAsync(ct);
            if (raw is null)
            {
                return; // canale chiuso: si esce e il chiamante riconnette
            }

            lock (_sync)
            {
                _messages++;
                _lastMessageUtc = _time.GetUtcNow().UtcDateTime;
            }

            var evt = mapper.Parse(raw, index);
            if (evt.IsEmpty) continue;

            // [G2] Freschezza per-serie: conta l'evento UTILE (tick o candela), non il frame
            // generico — un canale che consegna solo pong e conferme non tiene vivo nessun simbolo.
            var eventSymbol = evt.Tick?.Symbol ?? evt.Bar?.Symbol;
            if (eventSymbol is not null)
            {
                lock (_sync)
                {
                    _lastBySymbol[eventSymbol] = _time.GetUtcNow().UtcDateTime;
                }
            }

            if (evt.Tick is PriceTick tick)
            {
                if (!tick.IsPlausible(options.CurrentValue.MaxSpreadPercent))
                {
                    logger.LogDebug("Feed {Exchange}: tick {Symbol} scartato (bid {Bid}, ask {Ask}).",
                        mapper.Exchange, tick.Symbol, tick.Bid, tick.Ask);
                    continue;
                }
                Emit(() => TickReceived?.Invoke(tick), nameof(TickReceived));
            }

            if (evt.Bar is BarClosed bar)
            {
                Emit(() => BarClosed?.Invoke(bar), nameof(BarClosed));
            }
        }
    }

    /// <summary>
    /// Keep-alive applicativo, per gli exchange che lo pretendono (Bitget). Un fallimento nell'invio
    /// non viene propagato: la conseguenza reale è che il server chiuderà il canale, e la chiusura è
    /// già gestita dal ciclo di riconnessione.
    /// </summary>
    private CancellationTokenSource StartHeartbeat(IWebSocketTransport transport, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (mapper.HeartbeatFrame is not string frame)
        {
            return cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(mapper.HeartbeatInterval, _time, cts.Token);
                    await transport.SendAsync(frame, cts.Token);
                }
            }
            catch (OperationCanceledException) { /* fine normale */ }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Feed {Exchange}: heartbeat fallito.", mapper.Exchange);
            }
        }, cts.Token);

        return cts;
    }

    /// <summary>
    /// Un gestore che lancia non deve poter abbattere la connessione: il feed è infrastruttura, e la
    /// sua sopravvivenza non può dipendere dalla correttezza dei consumatori.
    /// </summary>
    private void Emit(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed {Exchange}: gestore di {What} ha lanciato; ignorato.", mapper.Exchange, what);
        }
    }

    /// <summary>Backoff esponenziale con jitter pieno, limitato dal tetto configurato.</summary>
    internal TimeSpan BackoffDelay(int attempt)
    {
        var opt = options.CurrentValue;
        var exponent = Math.Min(attempt - 1, 16); // oltre, il double trabocca senza aggiungere nulla
        var raw = opt.ReconnectInitialDelayMs * Math.Pow(2, Math.Max(0, exponent));
        var capped = Math.Min(raw, opt.ReconnectMaxDelayMs);
        var jittered = capped * (0.5 + Random.Shared.NextDouble() * 0.5); // 50%..100% del tetto
        return TimeSpan.FromMilliseconds(Math.Max(opt.ReconnectInitialDelayMs, jittered));
    }

    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, _time, ct); }
        catch (OperationCanceledException) { /* shutdown o riciclo: decide il chiamante */ }
    }

    private void MarkConnected()
    {
        lock (_sync)
        {
            _connected = true;
            _lastError = null;
            _lastMessageUtc = _time.GetUtcNow().UtcDateTime;
        }
    }

    private void MarkDisconnected()
    {
        lock (_sync) { _connected = false; }
    }

    private void RecordError(string message)
    {
        lock (_sync) { _lastError = message; }
    }
}
