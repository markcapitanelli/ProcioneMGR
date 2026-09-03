namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [K43, PRD autonomia-piena — Fase 3, 2026-09-01] <b>Una riga di <c>TradeRecords</c> non è un
/// trade.</b>
///
/// <para><b>Il fatto.</b> Al 2026-09-01 le 367 righe Paper della tabella erano <b>301 trade logici
/// distinti</b>: 66 righe sono re-inserimenti dello stesso trade, prodotti da run ripetuti dello
/// stesso backtest o dal recupero di candele storiche che il motore rigioca a ogni avvio. Esempio
/// misurato: la stessa posizione DOT/USDT aperta il 2026-06-29 12:00 compare con gli <c>Id</c>
/// 222, 227 e 234 — stesso prezzo d'ingresso, stessa quantità, stesso PnL a sei decimali.</para>
///
/// <para><b>Perché il controllo che c'era non poteva vederlo.</b> <c>COUNT(DISTINCT PositionId)</c>
/// dava 367 su 367, cioè «nessun duplicato» — ed è cieco <i>per costruzione</i>: il
/// <c>PositionId</c> è coniato a ogni esecuzione, quindi tre run dello stesso backtest producono
/// tre identificatori diversi per lo stesso trade. Un controllo che non può fallire non è un
/// controllo.</para>
///
/// <para><b>La chiave d'entità</b> è ciò che identifica il trade nel mondo, non nella tabella:
/// <c>(LaneId, StrategyId, Symbol, Side, OpenedAtUtc, ClosedAtUtc)</c>. Due righe con questa chiave
/// uguale sono la stessa operazione contata due volte.</para>
///
/// <para><b>E vince la PRIMA scritta</b> (<c>Id</c> minore). Non è una convenzione neutra e va
/// dichiarata: <b>25 gruppi su 301 hanno repliche con <c>Pnl</c> DIVERSO</b> — un replay su una
/// finestra di dati diversa dà un numero diverso per lo stesso trade. La prima è quella che il
/// motore ha prodotto quando l'operazione è avvenuta; le successive sono ricostruzioni. Scegliere
/// «l'ultima» significherebbe far riscrivere la storia a un riavvio.</para>
///
/// <para>Perché in memoria e non in SQL: i chiamanti materializzano già la lista (centinaia di
/// righe per corsia), e una funzione pura si prova senza database — mentre un <c>NOT EXISTS</c>
/// correlato sarebbe una seconda definizione della stessa chiave, in un linguaggio diverso.</para>
/// </summary>
public static class TradeDeduplication
{
    /// <summary>
    /// I trade DISTINTI della lista, in ordine di chiusura. A parità di chiave d'entità sopravvive
    /// la riga scritta per prima (<c>Id</c> minore).
    /// </summary>
    public static List<TradeRecord> Distinti(IEnumerable<TradeRecord> righe)
    {
        ArgumentNullException.ThrowIfNull(righe);
        return righe
            .GroupBy(t => (t.LaneId, t.StrategyId, t.Symbol, t.Side, t.OpenedAtUtc, t.ClosedAtUtc))
            .Select(g => g.OrderBy(t => t.Id).First())
            .OrderBy(t => t.ClosedAtUtc).ThenBy(t => t.Id)
            .ToList();
    }

    /// <summary>
    /// Quante righe sono state scartate perché repliche. Serve a <b>dichiararlo</b>: un conteggio
    /// più basso senza spiegazione si legge come un guasto, ed è la stessa disciplina degli altri
    /// scarti del monitor di decadimento.
    /// </summary>
    public static int Repliche(IReadOnlyCollection<TradeRecord> righe, IReadOnlyCollection<TradeRecord> distinti)
    {
        ArgumentNullException.ThrowIfNull(righe);
        ArgumentNullException.ThrowIfNull(distinti);
        return righe.Count - distinti.Count;
    }

    /// <summary>
    /// [K41 chiuso, revisione 2026-09-03/04] <b>Un trade è VIVO se è stato scritto quando è
    /// avvenuto.</b> <c>RecordedAtUtc</c> (ora di parete, messa dal database) meno <c>ClosedAtUtc</c>
    /// (ora di candela) è il ritardo di scrittura: per un trade eseguito dal vivo vale al più una
    /// barra e qualche secondo; per un trade fabbricato dal replay di candele storiche vale giorni.
    ///
    /// <para><b>Il caso che la deduplica non vede.</b> Una corsia Paper fermata per giorni e
    /// riavviata con la stessa gamba riparte con <c>LastCandleUtc</c> nullo e rigioca trenta giorni
    /// di candele: le righe dei giorni in cui era FERMA non hanno un originale da cui essere
    /// dedotte, hanno tempi di candela dopo l'àncora della gamba, e finivano nel ritiro e nel
    /// monitor di decadimento come trade veri. La colonna che le distingue esisteva da K41 e non
    /// aveva lettori.</para>
    ///
    /// <para><b>La tolleranza</b> è tre barre più mezz'ora: due ordini di grandezza sotto il replay
    /// misurato (giorni) e sopra il ritardo normale del motore (una barra). Le righe SENZA
    /// <c>RecordedAtUtc</c> — le 371 storiche, precedenti a K41 — restano: non si può giudicare ciò
    /// che non è stato misurato, e scartarle azzererebbe la storia delle corsie d'impronta.</para>
    /// </summary>
    public static List<TradeRecord> Vivi(IEnumerable<TradeRecord> righe, string timeframe)
    {
        ArgumentNullException.ThrowIfNull(righe);
        var tolleranza = TolleranzaDiScrittura(timeframe);
        return righe.Where(t => t.RecordedAtUtc is not DateTime scritto || scritto - t.ClosedAtUtc <= tolleranza).ToList();
    }

    /// <summary>Quante righe sono state scartate perché scritte troppo tardi per essere trade vivi.</summary>
    public static int Replay(IReadOnlyCollection<TradeRecord> righe, IReadOnlyCollection<TradeRecord> vivi)
    {
        ArgumentNullException.ThrowIfNull(righe);
        ArgumentNullException.ThrowIfNull(vivi);
        return righe.Count - vivi.Count;
    }

    /// <summary>Tre barre del timeframe più mezz'ora; con un timeframe ignoto vale un giorno.</summary>
    public static TimeSpan TolleranzaDiScrittura(string timeframe)
    {
        // Stessa tabella di Statistics.PeriodsPerYear (anno di 365 giorni): 1d → 1 giorno, 5m → 5 minuti.
        var ppy = Optimization.Statistics.PeriodsPerYear(timeframe);
        var barra = ppy > 0 ? TimeSpan.FromDays(365.0 / ppy) : TimeSpan.FromHours(8);
        return barra * 3 + TimeSpan.FromMinutes(30);
    }
}
