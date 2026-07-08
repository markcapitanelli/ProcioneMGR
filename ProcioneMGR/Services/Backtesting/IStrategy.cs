using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>Descrizione di un parametro di strategia, usata per generare la UI dinamica.</summary>
public sealed record StrategyParameterDefinition(string Key, string Label, decimal Default, decimal Min, decimal Max);

/// <summary>
/// Strategia di trading. Ciclo di vita:
///  1. <see cref="InitializeAsync"/> pre-calcola UNA volta gli indicatori necessari
///     (array allineati per indice alle candele) -> hot-loop O(1), niente ricalcolo.
///  2. <see cref="EvaluateSignal"/> viene chiamata per ogni candela e restituisce il segnale.
///
/// Nota di design: lo spec prevedeva EvaluateSignal(IndicatorValues, price, ts); ho
/// "interiorizzato" gli IndicatorValues nello stato della strategia (calcolati in
/// InitializeAsync) per evitare allocazioni nel loop e per O(1) sull'indice corrente.
/// </summary>
public interface IStrategy
{
    /// <summary>Nome tecnico (chiave), es. "EmaCross".</summary>
    string Name { get; }

    /// <summary>Nome leggibile per la UI, es. "EMA Cross".</summary>
    string DisplayName { get; }

    IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; }

    /// <param name="candles">
    /// Candele OHLCV complete, allineate per indice a <paramref name="closes"/>. Aggiunto per le
    /// strategie basate su fattori alpha (<c>MlStrategy</c>) che richiedono volume/high/low, non
    /// solo il close; le strategie a soli indicatori di prezzo possono ignorarlo.
    /// </param>
    Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct);

    Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp);
}

/// <summary>Helper comuni alle strategie.</summary>
public static class StrategyParametersExtensions
{
    public static decimal GetOrDefault(this IReadOnlyDictionary<string, decimal> p, string key, decimal fallback)
        => p is not null && p.TryGetValue(key, out var v) ? v : fallback;
}
