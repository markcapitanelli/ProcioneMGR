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
/// Una connessione WebSocket verso un exchange, mantenuta viva a oltranza.
///
/// Responsabilità: connettere, sottoscrivere, leggere, riconnettere con backoff esponenziale e
/// jitter, e ripresentare le sottoscrizioni dopo ogni riconnessione. Il PARSING è del mapper, il
/// ROUTING è del worker: qui si emettono solo eventi già tipizzati.
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

    private volatile bool _connected;
    private long _messages;
    private int _reconnects;
    private DateTime? _lastMessageUtc;
    private string? _lastError;

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
    /// Aggiorna l'insieme delle sottoscrizioni. Ritorna true se è CAMBIATO rispetto a quello attivo:
    /// il chiamante usa l'esito per decidere se serve riciclare la connessione (Binance codifica le
    /// sottoscrizioni nell'URL, quindi cambiarle richiede riconnettere).
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

        lock (_sync)
        {
            if (ordered.SequenceEqual(_subscriptions))
            {
                return false;
            }
            _subscriptions = ordered;
            _byExchangeSymbol = ordered.ToDictionary(ExchangeSymbolOf, s => s, StringComparer.OrdinalIgnoreCase);
            return true;
        }
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
            lock (_sync)
            {
                subs = _subscriptions;
                index = _byExchangeSymbol;
            }

            if (subs.Count == 0)
            {
                // Nessuna corsia attiva: non si tiene aperta una connessione inutile.
                await SafeDelayAsync(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await using var transport = transportFactory.Create();
                await transport.ConnectAsync(mapper.BuildEndpoint(subs), ct);

                foreach (var frame in mapper.BuildSubscribeFrames(subs))
                {
                    await transport.SendAsync(frame, ct);
                }

                MarkConnected();
                attempt = 0; // la connessione ha retto: il backoff riparte da zero
                logger.LogInformation("Feed {Exchange}: connesso, {N} sottoscrizioni.", mapper.Exchange, subs.Count);

                await PumpAsync(transport, index, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                logger.LogWarning(ex, "Feed {Exchange}: connessione fallita o interrotta.", mapper.Exchange);
            }
            finally
            {
                MarkDisconnected();
            }

            if (ct.IsCancellationRequested) break;

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
        catch (OperationCanceledException) { /* shutdown */ }
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
