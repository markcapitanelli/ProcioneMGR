using System.Collections.Concurrent;
using System.Text;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

/// <summary>Opzioni della cache dei fattori (sezione config "FactorCache").</summary>
public sealed class FactorCacheOptions
{
    /// <summary>Numero massimo di serie memorizzate; oltre, si sfrattano le più vecchie (FIFO). Default 512.</summary>
    public int MaxEntries { get; set; } = 512;
}

/// <summary>
/// Cache trasparente delle serie di fattori (Fase 4): memoizza <c>IAlphaFactor.Compute</c> per una
/// coppia (fattore+parametri, insieme di candele), evitando il ricalcolo ripetuto degli stessi
/// fattori (es. training ripetuti, dataset di discovery, backtest identici) e garantendo che
/// <b>train e serve vedano la STESSA serie</b> per gli stessi input. È un semplice memoizzatore:
/// non altera il valore calcolato (invariante <c>cache == ricalcolo</c>), quindi non introduce
/// look-ahead né skew. Thread-safe. Rif. Fase 4 (coerenza train-serve).
///
/// CHIAVE = impronta del fattore (nome + parametri ordinati) + impronta dei dati
/// (symbol, timeframe, numero candele, primo/ultimo timestamp). Se arrivano nuove candele
/// (cambia numero o ultimo timestamp) la chiave cambia ⇒ miss ⇒ ricalcolo: nessun dato stantìo.
/// </summary>
public interface IFactorCache
{
    /// <summary>Serie del fattore per (parametri, candele): calcolata e messa in cache al primo accesso.</summary>
    IReadOnlyList<decimal?> GetOrCompute(
        IAlphaFactor factor, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyList<OhlcvData> candles);

    long Hits { get; }
    long Misses { get; }
    int Count { get; }
    void Clear();
}

/// <inheritdoc cref="IFactorCache"/>
public sealed class FactorCache : IFactorCache
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyParams = new Dictionary<string, decimal>();

    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, IReadOnlyList<decimal?>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private long _hits;
    private long _misses;

    public FactorCache(FactorCacheOptions? options = null)
        => _maxEntries = Math.Max(16, options?.MaxEntries ?? 512);

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int Count => _entries.Count;

    public IReadOnlyList<decimal?> GetOrCompute(
        IAlphaFactor factor, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(factor);
        var p = parameters ?? EmptyParams;

        // Serie vuota: nessuna impronta dati significativa, si calcola senza cache.
        if (candles is null || candles.Count == 0) return factor.Compute(candles ?? [], p);

        var key = BuildKey(factor, p, candles);
        if (_entries.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        // Miss: calcola (deterministico) e prova a inserire. In caso di corsa, entrambi calcolano
        // lo stesso risultato e uno solo vince l'inserimento — nessun problema di correttezza.
        var computed = factor.Compute(candles, p);
        Interlocked.Increment(ref _misses);
        if (_entries.TryAdd(key, computed))
        {
            _insertionOrder.Enqueue(key);
            EvictIfNeeded();
        }
        return computed;
    }

    public void Clear()
    {
        _entries.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    private void EvictIfNeeded()
    {
        // Sfratto FIFO fino a rientrare nella capacità. Best-effort (concorrenza tollerata).
        while (_entries.Count > _maxEntries && _insertionOrder.TryDequeue(out var oldKey))
            _entries.TryRemove(oldKey, out _);
    }

    private static string BuildKey(IAlphaFactor factor, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyList<OhlcvData> candles)
    {
        var first = candles[0];
        var last = candles[^1];

        var sb = new StringBuilder(96);
        sb.Append(factor.Name).Append('|');
        foreach (var kv in parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        sb.Append('#')
          .Append(first.Symbol).Append('|')
          .Append(first.Timeframe).Append('|')
          .Append(candles.Count).Append('|')
          .Append(first.TimestampUtc.Ticks).Append('|')
          .Append(last.TimestampUtc.Ticks);
        return sb.ToString();
    }
}
