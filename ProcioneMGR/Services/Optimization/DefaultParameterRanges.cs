using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Optimization;

/// <summary>
/// Genera i range di sweep di default (grid/Bayesian) a partire dalle
/// <see cref="StrategyParameterDefinition"/> di una strategia.
///
/// Storia: la logica originale (in Optimization.razor) partiva dal solo <c>Default</c>
/// (Min=Default, Max=Default+4*step) e ignorava i bound Min/Max della definizione. Per i
/// parametri flag/booleani e i selettori categorici questo era degenere: un flag con Default=0
/// generava il range 0..0.04 (passo 0.01) e, non essendo marcato come intero, non testava MAI il
/// valore 1 (es. "AllowShort" -> short mai attivato); simmetricamente un flag con Default=1
/// non testava mai lo 0. Qui usiamo esplicitamente i bound della definizione ed enumeriamo
/// interamente i domini interi piccoli (0/1, 0..5, 0..9, ...).
/// </summary>
public static class DefaultParameterRanges
{
    /// <summary>
    /// Soglia massima dell'ampiezza (Max-Min) entro cui un parametro con bound interi viene
    /// trattato come dominio discreto ed enumerato per intero con passo 1 (copre flag 0/1 e i
    /// selettori categorici come EventType 0..5, Direction 0..2, UpStrategy 0..9).
    /// </summary>
    private const decimal DiscreteDomainSpan = 9m;

    public static List<ParameterRange> Build(IEnumerable<StrategyParameterDefinition> definitions)
        => definitions.Select(Build).ToList();

    public static ParameterRange Build(StrategyParameterDefinition d)
    {
        var span = d.Max - d.Min;
        var boundsAreWhole = d.Min == decimal.Truncate(d.Min) && d.Max == decimal.Truncate(d.Max);

        // Parametro discreto (flag/selettore): enumera TUTTO il dominio intero [Min, Max], passo 1.
        // È il cuore del fix: garantisce che entrambi gli estremi (0 e 1 per i flag) siano testati.
        if (boundsAreWhole && span >= 1m && span <= DiscreteDomainSpan)
        {
            return new ParameterRange
            {
                Name = d.Key,
                Min = d.Min,
                Max = d.Max,
                Step = 1m,
                IsInteger = true,
            };
        }

        // Parametro continuo o periodo: griglia locale attorno al Default, confinata nei bound
        // della definizione (in precedenza il tetto poteva sforare Max).
        var isInt = d.Key.Contains("Period") || d.Key.Contains("Lookback");
        var step = isInt ? Math.Max(1m, Math.Round(d.Default * 0.2m)) : Math.Round(d.Default * 0.1m, 4);
        if (step <= 0m) step = isInt ? 1m : 0.01m;

        var min = Math.Max(d.Min, d.Default);
        var max = Math.Min(d.Max, d.Default + 4 * step);
        if (max <= min) max = d.Max; // Default già al limite superiore: apri fino al bound.

        return new ParameterRange
        {
            Name = d.Key,
            Min = min,
            Max = max,
            Step = step,
            IsInteger = isInt,
        };
    }
}
