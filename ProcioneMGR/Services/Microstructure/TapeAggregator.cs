namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3 / C5 §9.2] Aggregazione del tape all'ORIGINE.
//
//  È la correzione numero 1 del piano C5: il tape grezzo dei tre simboli più liquidi vale 124 volte
//  tutto lo storico OHLCV della piattaforma, mentre aggregato a 10 secondi costa ~31 volte meno —
//  e la compressione CRESCE quando il mercato accelera, che è ciò che rende il costo prevedibile.
//
//  Qui l'aggregazione serve a misurare, non a raccogliere: gli stessi secchi che il collettore
//  scriverebbe dal vivo si costruiscono dai dump storici. Se il gate dirà no, non avremo raccolto
//  nulla per scoprirlo.
//
//  GRIGLIA REGOLARE, BARRE VUOTE INCLUSE. Una barra senza scambi esiste e va rappresentata: se si
//  omettessero, la barra successiva erediterebbe la posizione della precedente e ogni misura di
//  "ultimi 10 secondi" scivolerebbe indietro nel tempo di quanto è durato il silenzio.
// =============================================================================================

/// <summary>Aggrega i trade del tape in barre di durata fissa su una griglia allineata all'epoch.</summary>
public static class TapeAggregator
{
    /// <summary>
    /// Aggrega <paramref name="trades"/> in barre di <paramref name="bucket"/>, dalla prima barra che
    /// contiene <paramref name="fromUtc"/> (inclusa) fino a quella che contiene
    /// <paramref name="toUtc"/> (esclusa). I trade fuori intervallo si ignorano: un dump giornaliero
    /// contiene occasionalmente il primo trade del giorno successivo.
    /// </summary>
    public static IReadOnlyList<TapeBar> Aggregate(
        IEnumerable<AggTrade> trades, TimeSpan bucket, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (bucket <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucket), "La durata della barra deve essere positiva.");
        if (toUtc <= fromUtc) return [];

        var start = Floor(fromUtc, bucket);
        var count = (int)Math.Ceiling((toUtc - start) / bucket);
        if (count <= 0) return [];

        var buy = new decimal[count];
        var sell = new decimal[count];
        var trad = new int[count];
        var last = new decimal?[count];

        foreach (var t in trades)
        {
            if (t.TimestampUtc < start || t.TimestampUtc >= toUtc) continue;
            var idx = (int)((t.TimestampUtc - start).Ticks / bucket.Ticks);
            if (idx < 0 || idx >= count) continue;

            if (t.IsTakerBuy) buy[idx] += t.Quantity; else sell[idx] += t.Quantity;
            trad[idx]++;
            last[idx] = t.Price; // il file è cronologico: l'ultimo che passa è la chiusura della barra
        }

        var bars = new List<TapeBar>(count);
        for (var i = 0; i < count; i++)
        {
            bars.Add(new TapeBar(start.AddTicks(bucket.Ticks * i), bucket, buy[i], sell[i], trad[i], last[i]));
        }
        return bars;
    }

    /// <summary>
    /// Raggruppa barre fini in barre più larghe (es. sei barre da 10s in una da 1 minuto). Serve al
    /// confronto del gate: il proxy vive sulla barra larga (una candela), i candidati fini dentro.
    /// Il fattore fra le due durate deve essere intero, altrimenti il raggruppamento taglierebbe
    /// a metà una barra fine e nessuno se ne accorgerebbe.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TapeBar>> GroupBy(IReadOnlyList<TapeBar> fine, TimeSpan coarse)
    {
        ArgumentNullException.ThrowIfNull(fine);
        if (fine.Count == 0) return [];

        var fineDuration = fine[0].Duration;
        if (coarse.Ticks % fineDuration.Ticks != 0)
        {
            throw new ArgumentException(
                $"La barra larga ({coarse}) deve essere un multiplo intero di quella fine ({fineDuration}).",
                nameof(coarse));
        }

        var perGroup = (int)(coarse.Ticks / fineDuration.Ticks);
        var groups = new List<IReadOnlyList<TapeBar>>(fine.Count / perGroup + 1);

        // La griglia è allineata all'epoch, quindi il primo gruppo comincia dove comincia la prima
        // barra larga completa: si scartano le barre fini iniziali che appartengono a un gruppo
        // troncato, invece di mescolarle nel primo gruppo buono.
        var firstAligned = 0;
        while (firstAligned < fine.Count && fine[firstAligned].StartUtc.Ticks % coarse.Ticks != 0) firstAligned++;

        for (var i = firstAligned; i + perGroup <= fine.Count; i += perGroup)
        {
            groups.Add([.. fine.Skip(i).Take(perGroup)]);
        }
        return groups;
    }

    /// <summary>Sbilanciamento aggregato di un gruppo di barre: (buy − sell)/(buy + sell), null se non c'è volume.</summary>
    public static decimal? Imbalance(IReadOnlyList<TapeBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        decimal buy = 0m, sell = 0m;
        foreach (var b in bars) { buy += b.BuyVolume; sell += b.SellVolume; }
        var total = buy + sell;
        return total > 0m ? (buy - sell) / total : null;
    }

    private static DateTime Floor(DateTime t, TimeSpan bucket) =>
        new(t.Ticks - t.Ticks % bucket.Ticks, DateTimeKind.Utc);
}
