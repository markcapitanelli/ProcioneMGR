using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// COMPOSITE strategy: combines up to 3 elementary signals from <see cref="SignalCatalog"/>
/// with AND/OR logic into an entry rule, plus up to 2 OR-combined exit conditions. This is the
/// backbone of the creative-discovery layer: because every signal is normalized to 0-100, the
/// whole "spec" is expressible as PLAIN DECIMAL PARAMETERS — which makes generated strategies
/// natively sweepable by OptimizationEngine, rankable by Discovery, savable as SavedStrategy
/// and tradable by the live engine, with ZERO changes to any of those modules.
///
/// Parameter encoding (all decimal):
///   Logic        0 = AND (all entry conditions), 1 = OR (any entry condition)
///   Direction    0 = entry opens Long, 1 = entry opens Short
///   EntryCount   1..3 — how many Entry{Sig,Op,Thr}i triplets are active
///   EntrySigN    signal id (0..SignalCatalog.SignalCount-1)
///   EntryOpN     0 = "signal &lt; threshold", 1 = "signal &gt; threshold"
///   EntryThrN    threshold on the normalized 0-100 scale
///   ExitCount    0..2 — exit triplets (OR-combined); 0 = rely on entry flip / engine stops
///   ExitSigN/OpN/ThrN as above → emits Close.
///
/// Exits are checked BEFORE entries (protect the open position first — same precedence as
/// DonchianBreakout). Anti-look-ahead is inherited from the SignalCatalog (causal by design).
/// </summary>
public sealed class CompositeSignalStrategy : IStrategy
{
    public string Name => "Composite";
    public string DisplayName => "Composite Signals (creative)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } = BuildDefinitions();

    private static IReadOnlyList<StrategyParameterDefinition> BuildDefinitions()
    {
        var defs = new List<StrategyParameterDefinition>
        {
            new("Logic", "Logica entry (0=AND, 1=OR)", 0m, 0m, 1m),
            new("Direction", "Direzione (0=long, 1=short)", 0m, 0m, 1m),
            new("EntryCount", "Numero condizioni di ingresso", 2m, 1m, 3m),
        };
        for (var i = 1; i <= 3; i++)
        {
            defs.Add(new($"EntrySig{i}", $"Segnale entry {i}", i == 1 ? 0m : 4m, 0m, SignalCatalog.SignalCount - 1));
            defs.Add(new($"EntryOp{i}", $"Operatore entry {i} (0=<, 1=>)", i == 1 ? 0m : 1m, 0m, 1m));
            defs.Add(new($"EntryThr{i}", $"Soglia entry {i} (0-100)", i == 1 ? 30m : 70m, 0m, 100m));
        }
        defs.Add(new("ExitCount", "Numero condizioni di uscita", 1m, 0m, 2m));
        for (var i = 1; i <= 2; i++)
        {
            defs.Add(new($"ExitSig{i}", $"Segnale exit {i}", 0m, 0m, SignalCatalog.SignalCount - 1));
            defs.Add(new($"ExitOp{i}", $"Operatore exit {i} (0=<, 1=>)", 1m, 0m, 1m));
            defs.Add(new($"ExitThr{i}", $"Soglia exit {i} (0-100)", 70m, 0m, 100m));
        }
        return defs;
    }

    private readonly record struct Condition(int Signal, bool GreaterThan, decimal Threshold)
    {
        public bool Evaluate(decimal?[][] matrix, int index)
            => matrix[Signal][index] is decimal v && (GreaterThan ? v > Threshold : v < Threshold);

        public bool HasValue(decimal?[][] matrix, int index) => matrix[Signal][index].HasValue;
    }

    private decimal?[][] _matrix = [];
    private Condition[] _entries = [];
    private Condition[] _exits = [];
    private bool _logicOr;
    private bool _short;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        _logicOr = parameters.GetOrDefault("Logic", 0m) >= 0.5m;
        _short = parameters.GetOrDefault("Direction", 0m) >= 0.5m;

        var entryCount = (int)parameters.GetOrDefault("EntryCount", 2m);
        var exitCount = (int)parameters.GetOrDefault("ExitCount", 1m);
        if (entryCount is < 1 or > 3 || exitCount is < 0 or > 2)
        {
            throw new ArgumentException("Parametri Composite non validi: EntryCount in [1,3], ExitCount in [0,2].");
        }

        _entries = ReadConditions(parameters, "Entry", entryCount);
        _exits = ReadConditions(parameters, "Exit", exitCount);

        // Plausibility guard: under AND, two conditions on the SAME signal must not be
        // mutually exclusive ("sig < 30 AND sig > 70" can never fire — an absurd spec).
        if (!_logicOr)
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                for (var j = i + 1; j < _entries.Length; j++)
                {
                    if (_entries[i].Signal == _entries[j].Signal &&
                        _entries[i].GreaterThan != _entries[j].GreaterThan)
                    {
                        var less = _entries[i].GreaterThan ? _entries[j] : _entries[i];
                        var greater = _entries[i].GreaterThan ? _entries[i] : _entries[j];
                        if (greater.Threshold >= less.Threshold)
                        {
                            throw new ArgumentException(
                                $"Spec Composite contraddittoria: segnale {_entries[i].Signal} richiesto sia < {less.Threshold} sia > {greater.Threshold} in AND.");
                        }
                    }
                }
            }
        }

        _matrix = await SignalCatalog.GetMatrixAsync(candles, indicators, ct);
    }

    private static Condition[] ReadConditions(IReadOnlyDictionary<string, decimal> parameters, string prefix, int count)
    {
        var conditions = new Condition[count];
        for (var i = 1; i <= count; i++)
        {
            var signal = (int)parameters.GetOrDefault($"{prefix}Sig{i}", 0m);
            if (signal < 0 || signal >= SignalCatalog.SignalCount)
            {
                throw new ArgumentException($"Segnale {prefix}Sig{i}={signal} fuori catalogo [0,{SignalCatalog.SignalCount - 1}].");
            }
            conditions[i - 1] = new Condition(
                signal,
                parameters.GetOrDefault($"{prefix}Op{i}", 0m) >= 0.5m,
                parameters.GetOrDefault($"{prefix}Thr{i}", 50m));
        }
        return conditions;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        // Warm-up: every referenced signal must have a value before any decision.
        foreach (var c in _entries)
        {
            if (!c.HasValue(_matrix, index))
            {
                return Signal.Hold;
            }
        }

        // Exits first (protect the position in course).
        foreach (var c in _exits)
        {
            if (c.HasValue(_matrix, index) && c.Evaluate(_matrix, index))
            {
                return Signal.Close;
            }
        }

        var entry = _logicOr
            ? _entries.Any(c => c.Evaluate(_matrix, index))
            : _entries.All(c => c.Evaluate(_matrix, index));
        if (entry)
        {
            return _short ? Signal.Short : Signal.Long;
        }
        return Signal.Hold;
    }
}
