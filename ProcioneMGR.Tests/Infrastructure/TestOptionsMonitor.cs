using Microsoft.Extensions.Options;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// <see cref="IOptionsMonitor{T}"/> statico per i test: valore fisso, nessuna notifica di change.
/// Nato con il passaggio delle opzioni di autonomia (Drift/Llm/AutoReapply/PromotionEvaluator)
/// da POCO singleton a <c>Configure&lt;T&gt;</c> + monitor (hot-reload da /admin/autonomy).
/// </summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

public static class TestOptionsExtensions
{
    /// <summary>Avvolge un POCO di opzioni in un monitor statico: <c>new DriftMonitorOptions().AsMonitor()</c>.</summary>
    public static IOptionsMonitor<T> AsMonitor<T>(this T value) => new StaticOptionsMonitor<T>(value);
}
