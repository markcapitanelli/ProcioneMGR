using System.Diagnostics.Metrics;

namespace ProcioneMGR.Services.Observability;

/// <summary>
/// Collettore IN-PROCESSO dei contatori di <see cref="ProcioneMetrics"/>: un
/// <see cref="MeterListener"/> del BCL che accumula i totali (per strumento + tag) e un riassunto
/// dell'istogramma di slippage, così la dashboard può mostrarli SENZA un backend OpenTelemetry
/// (che resta l'export opzionale/spento). I totali sono "dalla partenza del processo": si azzerano
/// a un riavvio. Zero dipendenze esterne, thread-safe.
/// </summary>
public sealed class MetricsCollector : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private MeterListener? _listener;

    // Contatori: chiave = "nome.strumento|k=v,k=v" (tag ordinati).
    private readonly Dictionary<string, long> _counters = new();

    // Istogramma slippage (bps).
    private long _slipCount;
    private double _slipSum, _slipMin = double.MaxValue, _slipMax = double.MinValue;
    private readonly Queue<(DateTime T, double V)> _slipRecent = new();
    private const int SlipRecentMax = 300;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == ProcioneMetrics.MeterName) l.EnableMeasurementEvents(inst);
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void OnLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var key = instrument.Name + "|" + Signature(tags);
        lock (_gate)
        {
            _counters.TryGetValue(key, out var current);
            _counters[key] = current + measurement;
        }
    }

    private void OnDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != "procione.execution.slippage_bps") return;
        lock (_gate)
        {
            _slipCount++;
            _slipSum += measurement;
            _slipMin = Math.Min(_slipMin, measurement);
            _slipMax = Math.Max(_slipMax, measurement);
            _slipRecent.Enqueue((DateTime.UtcNow, measurement));
            while (_slipRecent.Count > SlipRecentMax) _slipRecent.Dequeue();
        }
    }

    private static string Signature(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        var parts = new List<string>(tags.Length);
        foreach (var t in tags) parts.Add($"{t.Key}={t.Value}");
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var mean = _slipCount > 0 ? _slipSum / _slipCount : 0d;
            return new MetricsSnapshot(
                new Dictionary<string, long>(_counters),
                _slipCount,
                mean,
                _slipCount > 0 ? _slipMin : 0d,
                _slipCount > 0 ? _slipMax : 0d,
                _slipRecent.ToList());
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }
}

/// <summary>Fotografia immutabile dei contatori accumulati, per la dashboard.</summary>
public sealed record MetricsSnapshot(
    IReadOnlyDictionary<string, long> Counters,
    long SlippageCount,
    double SlippageMean,
    double SlippageMin,
    double SlippageMax,
    IReadOnlyList<(DateTime T, double V)> SlippageRecent)
{
    /// <summary>Totale di uno strumento (somma su tutte le combinazioni di tag).</summary>
    public long Total(string instrument)
    {
        long sum = 0;
        var prefix = instrument + "|";
        foreach (var (k, v) in Counters)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) sum += v;
        return sum;
    }

    /// <summary>Ripartizione di uno strumento per il valore di un tag (es. "status", "side", "action").</summary>
    public IReadOnlyList<(string Value, long Count)> GroupByTag(string instrument, string tagKey)
    {
        var acc = new Dictionary<string, long>();
        var prefix = instrument + "|";
        foreach (var (k, v) in Counters)
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var sig = k[prefix.Length..];
            var value = "—";
            foreach (var part in sig.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.Split('=', 2);
                if (eq.Length == 2 && eq[0] == tagKey) { value = eq[1]; break; }
            }
            acc.TryGetValue(value, out var cur);
            acc[value] = cur + v;
        }
        return acc.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
