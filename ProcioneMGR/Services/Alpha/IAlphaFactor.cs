using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

/// <summary>Descrizione di un parametro di un fattore, per UI dinamica (come per le strategie).</summary>
public sealed record FactorParameterDefinition(string Key, string Label, decimal Default, decimal Min, decimal Max);

/// <summary>Categoria del fattore, utile per raggruppamento in UI e per ridurre la ridondanza.</summary>
public enum FactorCategory
{
    Momentum,
    MeanReversion,
    Volatility,
    Volume,
    Technical,
    Sentiment,
}

/// <summary>
/// Un <b>fattore alpha</b>: dato uno storico di candele, produce un valore numerico per ogni
/// candela (allineato per indice alla serie), che rappresenta un segnale predittivo CANDIDATO
/// dei rendimenti futuri. È l'analogo di <c>IStrategy</c> ma NON emette ordini: emette una
/// grandezza continua che verrà (a) valutata statisticamente (Information Coefficient) e
/// (b) usata come <i>feature</i> dei modelli ML delle fasi successive.
///
/// CONTRATTO ANTI-LOOK-AHEAD (invariante fondamentale, come nel <c>MarketFeatureExtractor</c>):
/// il valore all'indice <c>i</c> dipende ESCLUSIVAMENTE da <c>candles[0..i]</c>. Non legge mai
/// <c>candles[i+1]</c> o dati futuri. Conseguenza verificabile: il valore alla candela i è
/// identico sia calcolato sull'intera serie sia su una serie troncata dopo i.
///
/// ALLINEAMENTO: la lista restituita ha SEMPRE la stessa lunghezza dell'input; i primi elementi
/// (warm-up della finestra) sono <c>null</c>, così la serie resta allineata per indice ai prezzi.
/// </summary>
public interface IAlphaFactor
{
    /// <summary>Nome tecnico (chiave), es. "Momentum".</summary>
    string Name { get; }

    /// <summary>Nome leggibile per la UI, es. "Momentum (skip)".</summary>
    string DisplayName { get; }

    /// <summary>Categoria del fattore.</summary>
    FactorCategory Category { get; }

    IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; }

    /// <summary>
    /// Calcola il valore del fattore per ogni candela. Restituisce una lista lunga quanto
    /// <paramref name="candles"/>: <c>null</c> dove non calcolabile (warm-up o divisione per
    /// zero), altrimenti il valore del fattore. Puro e stateless.
    /// </summary>
    IReadOnlyList<decimal?> Compute(
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters);
}

/// <summary>Helper comuni ai fattori (lettura parametri con default).</summary>
public static class FactorParametersExtensions
{
    public static decimal GetOrDefault(this IReadOnlyDictionary<string, decimal> p, string key, decimal fallback)
        => p is not null && p.TryGetValue(key, out var v) ? v : fallback;

    public static int GetIntOrDefault(this IReadOnlyDictionary<string, decimal> p, string key, int fallback)
        => p is not null && p.TryGetValue(key, out var v) ? (int)v : fallback;
}
