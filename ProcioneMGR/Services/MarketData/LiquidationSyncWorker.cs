using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.MarketData;

/// <summary>Configurazione dell'accumulo liquidazioni (sezione "Liquidations").</summary>
public sealed class LiquidationsOptions
{
    /// <summary>
    /// Default ON: stream pubblico keyless in sola lettura, e il dato NON è ricostruibile a
    /// posteriori — ogni giorno spento è un giorno di storia perso (stessa logica dell'accumulo
    /// OI/long-short del Sentiment 2.0). Spegnibile per gli ambienti dove non serve.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minuti fra due flush su DB.</summary>
    public int FlushMinutes { get; set; } = 5;

    /// <summary>
    /// Secondi senza messaggi oltre i quali il canale si considera guasto. 900s (15 min), NON 120:
    /// trovato girando dal vivo la prima notte — <c>!forceOrder@arr</c> è un feed di EVENTI sparsi
    /// (le liquidazioni di TUTTO il listino), e in mercato calmo i vuoti di 120s sono NORMALI, non
    /// un guasto. Con 120s il worker riconnetteva di continuo, e ogni riconnessione è una finestra
    /// in cui una raffica va persa. La liveness VERA la garantisce già il ping/pong del protocollo
    /// (ClientWebSocket.KeepAliveInterval 20s) più il ReceiveAsync che ritorna null su socket chiuso;
    /// questa soglia è solo un backstop per il raro half-open che l'OS keepalive non intercetta.
    /// </summary>
    public int StaleSeconds { get; set; } = 900;

    /// <summary>
    /// [2026-07-24] Minuti di pausa quando l'endpoint futures risulta bloccato (connesso ma muto —
    /// vedi il ramo endpointLikelyBlocked). Lungo di proposito: evita il churn di riconnessione a
    /// vuoto quando i dati non arriveranno mai da questa postazione. Testabile piccolo.
    /// </summary>
    public int BlockedRetryMinutes { get; set; } = 60;
}

/// <summary>
/// [F4 roadmap frontiere-profitto] Accumula le liquidazioni forzate del mercato futures Binance
/// (stream pubblico <c>!forceOrder@arr</c>, un socket per tutto il listino) in
/// <see cref="SentimentMetricPoint"/> per (simbolo, ora): nozionale e conteggio per lato.
///
/// È un INVESTIMENTO, non una feature: oggi il dato non decide nulla (nessun consumo decisionale);
/// fra mesi le serie datano le cascate per l'event-study e alimentano feature di fragilità —
/// che passeranno dal gate come ogni altra ipotesi. La retention del sentiment ESENTA questa
/// fonte (vedi <c>SentimentSyncWorker.PurgeAsync</c>): l'accumulo è l'intero valore.
///
/// Robustezza: riconnessione con backoff esponenziale (5s→60s), canale silente oltre
/// <see cref="LiquidationsOptions.StaleSeconds"/> trattato come guasto, flush IDEMPOTENTE
/// (upsert del totale del secchio, mai delta) così un crash fra due flush non duplica nulla.
/// </summary>
public sealed class LiquidationSyncWorker(
    IWebSocketTransportFactory transportFactory,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptions<LiquidationsOptions> options,
    ILogger<LiquidationSyncWorker> logger) : BackgroundService
{
    internal LiquidationAggregator Aggregator { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            logger.LogInformation("Accumulo liquidazioni disabilitato (Liquidations:Enabled=false).");
            return;
        }

        var backoff = TimeSpan.FromSeconds(5);
        var totalMessagesEver = 0L;
        var consecutiveSilentConnects = 0;
        var blockAnnounced = false;
        while (!ct.IsCancellationRequested)
        {
            var messagesThisConnection = 0;
            try
            {
                await using var transport = transportFactory.Create();
                await transport.ConnectAsync(new Uri(BinanceLiquidationMapper.StreamUri), ct);
                logger.LogInformation("Accumulo liquidazioni: connesso a {Uri}.", BinanceLiquidationMapper.StreamUri);
                backoff = TimeSpan.FromSeconds(5); // connessione riuscita: il backoff riparte

                var lastFlush = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    // Staleness = BACKSTOP, non liveness primaria: la liveness vera è il ping/pong del
                    // protocollo. Soglia larga (15 min di default) perché su questo feed di eventi
                    // sparsi i silenzi di minuti sono legittimi — vedi StaleSeconds.
                    using var staleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    staleCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, opt.StaleSeconds)));
                    string? message;
                    try
                    {
                        message = await transport.ReceiveAsync(staleCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Accumulo liquidazioni: canale silente oltre {S}s, riconnetto.", opt.StaleSeconds);
                        break;
                    }
                    if (message is null) break; // canale chiuso: si riconnette

                    messagesThisConnection++;
                    totalMessagesEver++;
                    ProcessMessage(message);

                    if (DateTime.UtcNow - lastFlush >= TimeSpan.FromMinutes(Math.Max(1, opt.FlushMinutes)))
                    {
                        await FlushAsync(ct);
                        lastFlush = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Accumulo liquidazioni: errore di canale, riconnessione fra {Backoff}.", backoff);
            }

            if (ct.IsCancellationRequested) break;

            // [2026-07-24, trovato dal vivo] Connesso ma PERENNEMENTE MUTO: gli stream futures Binance
            // (fstream) NON consegnano dati da alcune postazioni EEA — la restrizione MiCA sui derivati
            // vale anche per il market-data futures (handshake OK, zero frame; lo SPOT invece inonda).
            // Diagnosticato con `streamdiag`. Senza questo ramo il worker riconnetterebbe a vuoto ogni
            // ~15 min all'infinito, sporcando i log. Dopo 3 connessioni consecutive a zero messaggi e
            // MAI un messaggio ricevuto, lo si dichiara UNA volta e si passa a una pausa lunga (il feed
            // si auto-ripristina se l'app gira da una postazione non bloccata).
            if (messagesThisConnection == 0) consecutiveSilentConnects++;
            else consecutiveSilentConnects = 0;

            var endpointLikelyBlocked = IsEndpointLikelyBlocked(totalMessagesEver, consecutiveSilentConnects);
            if (endpointLikelyBlocked)
            {
                if (!blockAnnounced)
                {
                    logger.LogWarning(
                        "Accumulo liquidazioni INATTIVO: lo stream futures Binance ({Uri}) si connette ma non consegna " +
                        "alcun dato (0 messaggi in {N} connessioni). Causa probabile: blocco EEA/MiCA del market-data " +
                        "derivati Binance da questa postazione (verificabile con `streamdiag`: lo SPOT funziona, i FUTURES no). " +
                        "Passo a retry orario; l'accumulo riparte da solo se l'app gira da una postazione non bloccata.",
                        BinanceLiquidationMapper.StreamUri, consecutiveSilentConnects);
                    blockAnnounced = true;
                }
                try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opt.BlockedRetryMinutes)), ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }

        // Flush finale best-effort: non perdere l'ultima finestra all'arresto ordinato.
        try { await FlushAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Accumulo liquidazioni: flush finale fallito."); }
    }

    internal void ProcessMessage(string json)
    {
        var e = BinanceLiquidationMapper.Parse(json);
        if (e is not null) Aggregator.Add(e);
    }

    /// <summary>
    /// [2026-07-24] L'endpoint è "probabilmente bloccato" (connesso ma muto) quando NON è mai arrivato
    /// un solo messaggio E si sono incatenate ≥3 connessioni chiuse a zero messaggi. Le DUE condizioni
    /// insieme: un feed genuinamente raro può avere una connessione muta, ma non 3 di fila senza aver
    /// MAI ricevuto nulla. Puro e testabile — è la regola che decide fra "raro" e "bloccato".
    /// </summary>
    internal static bool IsEndpointLikelyBlocked(long totalMessagesEver, int consecutiveSilentConnects)
        => totalMessagesEver == 0 && consecutiveSilentConnects >= 3;

    /// <summary>
    /// Upsert dei secchi correnti: la riga (Source, Metric, Symbol, ora) riceve il TOTALE del
    /// secchio — idempotente per costruzione. Dopo il flush, i secchi delle ore chiuse da più di
    /// 2 ore vengono ritirati dalla memoria (il loro valore in DB è ormai definitivo).
    /// </summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        var snapshot = Aggregator.Snapshot();
        if (snapshot.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var minHour = snapshot[0].HourUtc;
        var existing = await db.SentimentMetricPoints
            .Where(p => p.Source == SentimentMetricSources.BinanceLiquidations && p.TimestampUtc >= minHour)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(p => (p.Metric, p.Symbol, p.TimestampUtc));

        foreach (var b in snapshot)
        {
            Upsert(db, byKey, SentimentMetrics.LongLiquidationNotional, b.BaseTicker, b.HourUtc, b.LongNotional);
            Upsert(db, byKey, SentimentMetrics.ShortLiquidationNotional, b.BaseTicker, b.HourUtc, b.ShortNotional);
            Upsert(db, byKey, SentimentMetrics.LongLiquidationCount, b.BaseTicker, b.HourUtc, b.LongCount);
            Upsert(db, byKey, SentimentMetrics.ShortLiquidationCount, b.BaseTicker, b.HourUtc, b.ShortCount);
        }
        await db.SaveChangesAsync(ct);

        // Prune in TEMPO-EVENTO (non a orologio di parete): un secchio resta vivo finché la sua ora
        // è entro 2h dall'attività più recente vista sullo stream. Con l'orologio di parete un
        // backlog o un test su dati storici ritirerebbero il secchio ancora attivo, e il flush
        // successivo ripartirebbe da un parziale.
        var maxHour = snapshot[^1].HourUtc;
        Aggregator.PruneBefore(maxHour.AddHours(-2));
    }

    private static void Upsert(
        ApplicationDbContext db,
        Dictionary<(string Metric, string Symbol, DateTime Ts), SentimentMetricPoint> byKey,
        string metric, string symbol, DateTime hourUtc, decimal value)
    {
        if (value == 0m && !byKey.ContainsKey((metric, symbol, hourUtc))) return; // niente righe di zeri
        if (byKey.TryGetValue((metric, symbol, hourUtc), out var row))
        {
            // Monotono: dentro la vita di un secchio i totali possono solo crescere. Se un evento
            // TARDIVO arriva dopo la prune, il secchio riparte da un parziale — il max impedisce
            // che un flush successivo REGREDISCA il valore definitivo già scritto.
            row.Value = Math.Max(row.Value, value);
            return;
        }
        var fresh = new SentimentMetricPoint
        {
            Source = SentimentMetricSources.BinanceLiquidations,
            Metric = metric,
            Symbol = symbol,
            TimestampUtc = hourUtc,
            Value = value,
        };
        db.SentimentMetricPoints.Add(fresh);
        byKey[(metric, symbol, hourUtc)] = fresh;
    }
}
