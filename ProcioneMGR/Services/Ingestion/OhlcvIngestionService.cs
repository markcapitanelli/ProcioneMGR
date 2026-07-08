using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Implementazione dell'ingestione storica OHLCV.
///
/// Strategia:
///  - itera richiedendo all'exchange blocchi di candele (max consentito dal client),
///    avanzando il cursore <c>since</c> finche' non si copre l'intervallo [from, to];
///  - rispetta i rate-limit con un <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
///    tra una richiesta e l'altra;
///  - persiste con UPSERT idempotente (vedi <see cref="UpsertBatchAsync"/>): nessuna
///    candela duplicata grazie all'indice univoco (Symbol, Timeframe, TimestampUtc).
///
/// Usa <see cref="IDbContextFactory{TContext}"/> perche' il loop e' a lunga durata:
/// si crea un DbContext fresco e a vita breve per ogni batch, evitando di tenere
/// aperto un context per tutta l'operazione.
/// </summary>
public sealed class OhlcvIngestionService(
    IExchangeClientFactory exchangeFactory,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<OhlcvIngestionService> logger) : IOhlcvIngestionService
{
    // Pausa tra le richieste per non superare i rate-limit degli exchange.
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromMilliseconds(300);

    public async Task<IngestionResult> IngestHistoricalDataAsync(
        string exchangeName,
        string symbol,
        string timeframe,
        DateTime from,
        DateTime to,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Timeframes.IsSupported(timeframe))
        {
            throw new ArgumentException($"Timeframe non supportato: '{timeframe}'.", nameof(timeframe));
        }
        if (to <= from)
        {
            throw new ArgumentException("L'intervallo non e' valido: 'to' deve essere successivo a 'from'.");
        }

        var client = exchangeFactory.Create(exchangeName);
        var tfMs = Timeframes.ToMilliseconds(timeframe);
        var batchSize = client.MaxCandlesPerRequest;

        var sinceMs = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var estimated = Math.Max(1, (toMs - sinceMs) / tfMs);
        long ingested = 0;

        logger.LogInformation(
            "Avvio ingestione {Exchange} {Symbol} {Timeframe} da {From:u} a {To:u} (~{Estimated} candele).",
            exchangeName, symbol, timeframe, from, to, estimated);

        while (sinceMs < toMs)
        {
            ct.ThrowIfCancellationRequested();

            var candles = await client.FetchOhlcvAsync(symbol, timeframe, sinceMs, batchSize, ct);

            // Tieni solo cio' che ricade nell'intervallo richiesto.
            var inRange = candles
                .Where(c => c.TimestampMs >= sinceMs && c.TimestampMs <= toMs)
                .ToList();

            if (inRange.Count == 0)
            {
                // Nessun dato ulteriore disponibile in questa finestra: fine.
                break;
            }

            var saved = await UpsertBatchAsync(symbol, timeframe, inRange, ct);
            ingested += saved;

            var lastTs = inRange[^1].TimestampMs;
            // Avanza il cursore strettamente in avanti per evitare loop infiniti.
            var nextSince = lastTs + tfMs;
            sinceMs = nextSince > sinceMs ? nextSince : sinceMs + tfMs;

            progress?.Report(new IngestionProgress(ingested, estimated, symbol, timeframe));
            logger.LogInformation(
                "Ingested {Ingested}/{Estimated} candles for {Symbol} {Timeframe}.",
                ingested, estimated, symbol, timeframe);

            // L'exchange ha restituito meno del massimo e abbiamo passato 'to': stop.
            if (candles.Count < batchSize && lastTs + tfMs > toMs)
            {
                break;
            }

            await Task.Delay(RateLimitDelay, ct);
        }

        logger.LogInformation(
            "Ingestione completata: {Ingested} candele per {Symbol} {Timeframe}.",
            ingested, symbol, timeframe);

        return new IngestionResult(ingested, ct.IsCancellationRequested);
    }

    /// <summary>
    /// UPSERT idempotente di un batch contiguo di candele.
    ///
    /// NOTA DI ARCHITETTURA su ExecuteUpdateAsync: <c>ExecuteUpdateAsync</c> imposta gli
    /// STESSI valori a tutte le righe che soddisfano il predicato, quindi NON e' adatto a
    /// un upsert con valori per-riga distinti come questo. L'approccio corretto e
    /// performante e' il change-tracking in batch: una sola <c>SaveChangesAsync</c>
    /// raggruppa INSERT e UPDATE. Quando passeremo a PostgreSQL, l'ottimizzazione
    /// ideale sara' un INSERT ... ON CONFLICT (Symbol,Timeframe,TimestampUtc) DO UPDATE.
    /// </summary>
    private async Task<int> UpsertBatchAsync(string symbol, string timeframe, List<Ohlcv> candles, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Carica le candele gia' presenti nella finestra del batch sfruttando l'indice
        // composto (range scan), evitando una clausola IN su molti parametri.
        var minTs = candles[0].TimestampUtc;
        var maxTs = candles[^1].TimestampUtc;

        var existing = await db.OhlcvData
            .Where(c => c.Symbol == symbol
                        && c.Timeframe == timeframe
                        && c.TimestampUtc >= minTs
                        && c.TimestampUtc <= maxTs)
            .ToDictionaryAsync(c => c.TimestampUtc, ct);

        foreach (var candle in candles)
        {
            if (existing.TryGetValue(candle.TimestampUtc, out var row))
            {
                // UPDATE: la candela esiste gia' -> aggiorna i valori (es. candela non ancora chiusa).
                row.Open = candle.Open;
                row.High = candle.High;
                row.Low = candle.Low;
                row.Close = candle.Close;
                row.Volume = candle.Volume;
            }
            else
            {
                // INSERT
                db.OhlcvData.Add(new OhlcvData
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimestampUtc = candle.TimestampUtc,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    Volume = candle.Volume,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return candles.Count;
    }
}
