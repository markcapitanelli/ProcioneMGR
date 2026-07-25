namespace ProcioneMGR.Services.Backtesting;

/// <summary>Un evento di funding: timestamp e rate in PERCENTO per 8h (×100, convenzione piattaforma). Firmato.</summary>
public sealed record FundingRatePoint(DateTime TimestampUtc, decimal RatePercentPer8h);

/// <summary>
/// [T0.2 roadmap macchina-ricerca] Lookup a gradini sulla serie storica dei funding rate: il rate
/// applicabile a un istante t è quello dell'ultimo evento di funding ≤ t. Prima del primo evento
/// non si inventa nulla: si torna al fallback (la costante di configurazione), così un backtest che
/// parte prima della storia disponibile degrada in modo dichiarato invece di fingere.
///
/// Il motore applica il rate pro-rata per candela (modello già esistente): qui cambia solo che il
/// rate è quello STORICO e FIRMATO invece di una costante senza segno.
/// </summary>
public sealed class FundingRateLookup
{
    private readonly DateTime[] _times;
    private readonly decimal[] _ratesFrac;   // già /100: frazione per periodo di 8h

    private FundingRateLookup(DateTime[] times, decimal[] ratesFrac)
    {
        _times = times;
        _ratesFrac = ratesFrac;
    }

    /// <summary>Null se la storia è assente o vuota: il chiamante usa la costante come sempre.</summary>
    public static FundingRateLookup? BuildOrNull(IReadOnlyList<FundingRatePoint>? history)
    {
        if (history is null || history.Count == 0) return null;
        var sorted = history.OrderBy(p => p.TimestampUtc).ToArray();
        return new FundingRateLookup(
            sorted.Select(p => p.TimestampUtc).ToArray(),
            sorted.Select(p => p.RatePercentPer8h / 100m).ToArray());
    }

    /// <summary>
    /// Frazione per-8h applicabile a <paramref name="tsUtc"/> (ultimo evento ≤ ts), oppure
    /// <paramref name="fallbackFrac"/> se ts precede il primo evento della storia.
    /// </summary>
    public decimal RateFracAt(DateTime tsUtc, decimal fallbackFrac)
    {
        var idx = Array.BinarySearch(_times, tsUtc);
        if (idx < 0) idx = ~idx - 1;   // ~idx = primo elemento > ts; -1 = ultimo elemento <= ts
        return idx < 0 ? fallbackFrac : _ratesFrac[idx];
    }
}
