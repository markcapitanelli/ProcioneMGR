using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// Matrice di rendimenti ALLINEATI per timestamp: tutte le serie hanno la stessa lunghezza e
/// l'osservazione i-esima di ogni simbolo si riferisce allo stesso periodo. È il contratto
/// d'ingresso di <see cref="IPortfolioOptimizer.Optimize"/> e <see cref="ML.IRiskFactorPca"/>.
/// </summary>
/// <param name="ReturnsBySymbol">Rendimenti semplici (close/close−1) per simbolo, stessi indici temporali.</param>
/// <param name="Timestamps">Timestamp (UTC) della barra di ARRIVO di ciascun rendimento.</param>
public sealed record AlignedReturnMatrix(
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> ReturnsBySymbol,
    IReadOnlyList<DateTime> Timestamps)
{
    public int SymbolCount => ReturnsBySymbol.Count;

    /// <summary>Numero di rendimenti per serie (0 = allineamento impossibile).</summary>
    public int ReturnCount => Timestamps.Count;
}

/// <summary>
/// Costruisce la matrice dei rendimenti da candele di più simboli con un <b>inner-join sui
/// TimestampUtc</b>: si tengono SOLO i periodi presenti per TUTTI i simboli. Con storici di
/// lunghezza diversa (simbolo quotato dopo, buchi di ingestione) troncare alla coda comune o
/// riempire i buchi con 0 sfalserebbe covarianze e correlazioni — l'inner-join è l'unico
/// allineamento che non inventa dati. Puro e testabile: nessun accesso a DB.
/// </summary>
public static class ReturnMatrixBuilder
{
    /// <summary>
    /// Allinea le candele per timestamp e calcola i rendimenti semplici da Close. Le candele con
    /// <c>Close ≤ 0</c> (dati sporchi) sono scartate PRIMA dell'intersezione, come se mancassero.
    /// L'ordine di arrivo delle candele è irrilevante (si riordina internamente).
    /// </summary>
    public static AlignedReturnMatrix BuildAlignedReturns(IReadOnlyDictionary<string, IReadOnlyList<OhlcvData>> candlesBySymbol)
    {
        ArgumentNullException.ThrowIfNull(candlesBySymbol);

        // Close per timestamp, per simbolo (duplicati: vince l'ultimo, come una re-ingestione).
        var closesBySymbol = new Dictionary<string, Dictionary<DateTime, decimal>>(candlesBySymbol.Count);
        foreach (var (symbol, candles) in candlesBySymbol)
        {
            var closes = new Dictionary<DateTime, decimal>(candles.Count);
            foreach (var c in candles)
            {
                if (c.Close > 0m)
                {
                    closes[c.TimestampUtc] = c.Close;
                }
            }
            closesBySymbol[symbol] = closes;
        }

        if (closesBySymbol.Count == 0 || closesBySymbol.Values.Any(m => m.Count == 0))
        {
            return Empty(closesBySymbol.Keys);
        }

        // Inner-join: intersezione dei timestamp, partendo dal simbolo più corto (meno lavoro).
        var ordered = closesBySymbol.Values.OrderBy(m => m.Count).ToList();
        var common = new HashSet<DateTime>(ordered[0].Keys);
        foreach (var closes in ordered.Skip(1))
        {
            common.IntersectWith(closes.Keys);
            if (common.Count == 0)
            {
                return Empty(closesBySymbol.Keys);
            }
        }

        var timeline = common.Order().ToList();
        if (timeline.Count < 2)
        {
            return Empty(closesBySymbol.Keys);
        }

        var returnsBySymbol = new Dictionary<string, IReadOnlyList<decimal>>(closesBySymbol.Count);
        foreach (var (symbol, closes) in closesBySymbol)
        {
            var returns = new decimal[timeline.Count - 1];
            for (var i = 1; i < timeline.Count; i++)
            {
                returns[i - 1] = closes[timeline[i]] / closes[timeline[i - 1]] - 1m;
            }
            returnsBySymbol[symbol] = returns;
        }

        return new AlignedReturnMatrix(returnsBySymbol, timeline.Skip(1).ToList());
    }

    private static AlignedReturnMatrix Empty(IEnumerable<string> symbols) =>
        new(symbols.ToDictionary(s => s, _ => (IReadOnlyList<decimal>)Array.Empty<decimal>()), []);
}
