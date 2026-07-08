using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Barra aggregata a soglia (volume o controvalore) costruita da candele temporali di base.
/// </summary>
public sealed record AggregatedBar(
    DateTime StartUtc,
    DateTime EndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    /// <summary>Controvalore scambiato: somma di (prezzo tipico x volume) delle candele di base.</summary>
    decimal DollarValue,
    /// <summary>VWAP approssimato dal prezzo tipico (H+L+C)/3 delle candele di base.</summary>
    decimal Vwap,
    int SourceCandles);

/// <summary>
/// Costruzione di barre non temporali (Jansen ML4T, cap. 2): le barre a tempo fisso campionano
/// il mercato in modo disomogeneo (poche informazioni di notte, troppe nei momenti concitati).
/// Aggregare per VOLUME costante ("volume bars") o CONTROVALORE costante ("dollar bars")
/// produce serie con proprieta' statistiche piu' vicine alla normalita' (meno eteroschedasticita'),
/// migliori come input per i modelli ML.
///
/// Qui l'aggregazione parte dalle candele temporali di base gia' in piattaforma (non dai tick,
/// che non ingestiamo): la granularita' minima della soglia e' quindi quella della candela
/// sorgente — usare la serie base piu' fine disponibile (es. 1m/5m).
/// </summary>
public sealed class BarBuilder
{
    /// <summary>Barre a volume costante: chiude la barra quando il volume cumulato raggiunge la soglia.</summary>
    public IReadOnlyList<AggregatedBar> BuildVolumeBars(IReadOnlyList<OhlcvData> candles, decimal volumePerBar)
        => Build(candles, volumePerBar, c => c.Volume);

    /// <summary>Barre a controvalore costante: soglia sul cumulato di (prezzo tipico x volume).</summary>
    public IReadOnlyList<AggregatedBar> BuildDollarBars(IReadOnlyList<OhlcvData> candles, decimal dollarPerBar)
        => Build(candles, dollarPerBar, DollarValueOf);

    /// <summary>
    /// Soglia di volume che produce circa <paramref name="targetBarCount"/> barre sull'intera
    /// serie (l'equivalente del "trades_per_min" del libro: volume totale / barre desiderate).
    /// </summary>
    public decimal SuggestVolumeThreshold(IReadOnlyList<OhlcvData> candles, int targetBarCount)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetBarCount, 1);
        var total = candles.Sum(c => c.Volume);
        return total <= 0m ? 0m : total / targetBarCount;
    }

    /// <summary>Soglia di controvalore che produce circa <paramref name="targetBarCount"/> barre.</summary>
    public decimal SuggestDollarThreshold(IReadOnlyList<OhlcvData> candles, int targetBarCount)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetBarCount, 1);
        var total = candles.Sum(DollarValueOf);
        return total <= 0m ? 0m : total / targetBarCount;
    }

    /// <summary>
    /// Converte le barre aggregate in <see cref="OhlcvData"/> "sintetici" (timestamp = fine
    /// barra) riusabili da indicatori/fattori/analisi esistenti, che sono agnostici rispetto
    /// alla spaziatura temporale.
    /// </summary>
    public IReadOnlyList<OhlcvData> ToOhlcv(IReadOnlyList<AggregatedBar> bars, string symbol, string timeframeLabel)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars.Select(b => new OhlcvData
        {
            Symbol = symbol,
            Timeframe = timeframeLabel,
            TimestampUtc = b.EndUtc,
            Open = b.Open,
            High = b.High,
            Low = b.Low,
            Close = b.Close,
            Volume = b.Volume,
        }).ToList();
    }

    private static IReadOnlyList<AggregatedBar> Build(
        IReadOnlyList<OhlcvData> candles, decimal threshold, Func<OhlcvData, decimal> measure)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (threshold <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "La soglia deve essere positiva.");
        }

        var bars = new List<AggregatedBar>();
        DateTime start = default;
        decimal open = 0m, high = 0m, low = 0m, close = 0m;
        decimal volume = 0m, dollar = 0m, vwapNumerator = 0m, accumulated = 0m;
        var count = 0;

        foreach (var c in candles)
        {
            if (count == 0)
            {
                start = c.TimestampUtc;
                open = c.Open;
                high = c.High;
                low = c.Low;
            }
            else
            {
                if (c.High > high) high = c.High;
                if (c.Low < low) low = c.Low;
            }

            close = c.Close;
            volume += c.Volume;
            var dv = DollarValueOf(c);
            dollar += dv;
            vwapNumerator += dv; // = tipico * volume
            accumulated += measure(c);
            count++;

            if (accumulated >= threshold)
            {
                bars.Add(new AggregatedBar(
                    StartUtc: start,
                    EndUtc: c.TimestampUtc,
                    Open: open, High: high, Low: low, Close: close,
                    Volume: volume,
                    DollarValue: dollar,
                    Vwap: volume > 0m ? vwapNumerator / volume : close,
                    SourceCandles: count));

                accumulated = 0m;
                volume = 0m;
                dollar = 0m;
                vwapNumerator = 0m;
                count = 0;
            }
        }

        // La coda incompleta viene scartata (barra non ancora "piena": includerla creerebbe
        // un'ultima barra non confrontabile con le altre).
        return bars;
    }

    private static decimal DollarValueOf(OhlcvData c)
        => (c.High + c.Low + c.Close) / 3m * c.Volume;
}
