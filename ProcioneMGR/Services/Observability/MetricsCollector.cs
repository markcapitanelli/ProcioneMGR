using System.Diagnostics.Metrics;

namespace ProcioneMGR.Services.Observability;

/// <summary>
/// Collettore IN-PROCESSO dei contatori di <see cref="ProcioneMetrics"/>: un
/// <see cref="MeterListener"/> del BCL che accumula i totali (per strumento + tag) e un riassunto
/// degli istogrammi, così la dashboard può mostrarli SENZA un backend OpenTelemetry (che resta
/// l'export opzionale/spento). I totali sono "dalla partenza del processo": si azzerano a un
/// riavvio. Zero dipendenze esterne, thread-safe.
///
/// [Fase 1] Gli istogrammi seguiti sono passati da uno a tre (slippage dei job a fette, shortfall
/// degli ordini di corsia, latenza dell'ordine), quindi l'accumulo è stato generalizzato in
/// <see cref="HistogramAccumulator"/> invece di restare replicato campo per campo.
/// </summary>
public sealed class MetricsCollector : IHostedService, IDisposable
{
    /// <summary>Slippage degli ordini eseguiti a fette (TWAP/VWAP/Iceberg).</summary>
    public const string ExecutionSlippageInstrument = "procione.execution.slippage_bps";

    /// <summary>[Fase 1] Shortfall degli ordini di corsia — la stragrande maggioranza degli ordini.</summary>
    public const string TradingSlippageInstrument = "procione.trading.slippage_bps";

    /// <summary>[Fase 1] Latenza invio→risposta dell'ordine.</summary>
    public const string OrderLatencyInstrument = "procione.trading.order_latency_ms";

    private static readonly string[] TrackedHistograms =
        [ExecutionSlippageInstrument, TradingSlippageInstrument, OrderLatencyInstrument];

    private readonly object _gate = new();
    private MeterListener? _listener;

    // Contatori: chiave = "nome.strumento|k=v,k=v" (tag ordinati).
    private readonly Dictionary<string, long> _counters = new();

    private readonly Dictionary<string, HistogramAccumulator> _histograms =
        TrackedHistograms.ToDictionary(name => name, _ => new HistogramAccumulator());

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
        lock (_gate)
        {
            if (_histograms.TryGetValue(instrument.Name, out var acc)) acc.Add(measurement);
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
            return new MetricsSnapshot(
                new Dictionary<string, long>(_counters),
                _histograms.ToDictionary(kv => kv.Key, kv => kv.Value.Summarize()));
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>
    /// Accumulo di un istogramma: totali esatti (conteggio/somma/min/max) più una finestra scorrevole
    /// degli ultimi campioni. I percentili si calcolano su quella finestra e NON su tutta la sessione:
    /// tenere ogni campione per un processo che gira per giorni non è accettabile, e per la domanda
    /// che questi percentili devono servire — "quanto è andata male la coda di recente" — la finestra
    /// è anche la risposta più utile. La distinzione è dichiarata anche in UI, per non far leggere
    /// come "P99 di sempre" un numero che è "P99 degli ultimi campioni".
    /// </summary>
    private sealed class HistogramAccumulator
    {
        private const int RecentMax = 300;
        private const int SampleMax = 2_000;

        private long _count;
        private double _sum, _min = double.MaxValue, _max = double.MinValue;
        private readonly Queue<(DateTime T, double V)> _recent = new();
        private readonly Queue<double> _samples = new();

        public void Add(double value)
        {
            _count++;
            _sum += value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);

            _recent.Enqueue((DateTime.UtcNow, value));
            while (_recent.Count > RecentMax) _recent.Dequeue();

            _samples.Enqueue(value);
            while (_samples.Count > SampleMax) _samples.Dequeue();
        }

        public HistogramSummary Summarize()
        {
            if (_count == 0) return HistogramSummary.Empty;

            var ordered = _samples.ToArray();
            Array.Sort(ordered);
            return new HistogramSummary(
                _count, _sum / _count, _min, _max,
                Percentile(ordered, 0.50), Percentile(ordered, 0.95), Percentile(ordered, 0.99),
                _recent.ToList());
        }

        /// <summary>Percentile per interpolazione lineare sui campioni ordinati (nearest-rank smussato).</summary>
        private static double Percentile(double[] sorted, double q)
        {
            if (sorted.Length == 0) return 0d;
            if (sorted.Length == 1) return sorted[0];

            var position = q * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }
    }
}

/// <summary>Riassunto di un istogramma. <see cref="P50"/>/<see cref="P95"/>/<see cref="P99"/> sono
/// calcolati sulla finestra dei campioni recenti (vedi <c>HistogramAccumulator</c>).</summary>
public sealed record HistogramSummary(
    long Count,
    double Mean,
    double Min,
    double Max,
    double P50,
    double P95,
    double P99,
    IReadOnlyList<(DateTime T, double V)> Recent)
{
    public static readonly HistogramSummary Empty = new(0, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>Fotografia immutabile dei contatori accumulati, per la dashboard.</summary>
public sealed record MetricsSnapshot(
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, HistogramSummary> Histograms)
{
    /// <summary>Riassunto di un istogramma seguito; vuoto se lo strumento non ha mai registrato.</summary>
    public HistogramSummary Histogram(string instrument) =>
        Histograms.TryGetValue(instrument, out var h) ? h : HistogramSummary.Empty;

    /// <summary>Slippage dei job a fette — scorciatoie storiche usate dalla dashboard.</summary>
    public long SlippageCount => Histogram(MetricsCollector.ExecutionSlippageInstrument).Count;

    public double SlippageMean => Histogram(MetricsCollector.ExecutionSlippageInstrument).Mean;

    public double SlippageMin => Histogram(MetricsCollector.ExecutionSlippageInstrument).Min;

    public double SlippageMax => Histogram(MetricsCollector.ExecutionSlippageInstrument).Max;

    public IReadOnlyList<(DateTime T, double V)> SlippageRecent =>
        Histogram(MetricsCollector.ExecutionSlippageInstrument).Recent;

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
