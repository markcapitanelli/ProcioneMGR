namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [I11] <b>Il denominatore condiviso: quanti trade ci si aspetta, e fra quanto si potrà giudicare.</b>
///
/// <para>Esiste come componente a sé perché lo consumano in <b>due</b>: il ritiro per inedia della
/// flotta (I12) e il freno per gamba (I13). Se ognuno se lo calcolasse per conto proprio avremmo
/// due regole che rispondono alla stessa domanda, e prima o poi due verdetti sulla stessa corsia —
/// il difetto già pagato in D2 e con <c>SeriesFreshness</c>, e trovato quattro volte nella Fase 2 di
/// questa stessa ondata.</para>
///
/// <para><b>La domanda che risolve.</b> La regola di ritiro esistente pretende
/// <c>Sharpe &lt; soglia</c> dopo tre settimane <b>E ≥20 trade</b>, e chi produce zero trade non
/// arriva mai a 20: al 2026-08-19 le corsie di flotta 3-7 avevano chiuso <b>1 trade ciascuna o
/// zero</b> in 13-15 giorni, quindi non sarebbero state ritirabili mai — e una corsia che non si
/// libera mai blocca la flotta e, a monte, il comitato. Serve un secondo criterio, e un secondo
/// criterio ha bisogno di sapere <b>quanti trade quella gamba doveva fare</b>.</para>
///
/// <para><b>Il tempo-al-verdetto è la parte che nessuno dichiarava</b> (criterio AF2c-3): a ~2
/// trade/mese servono <b>dieci mesi</b> per accumulare i 20 trade che la regola di ritiro pretende.
/// Non è un divieto — il giornaliero resta schierabile — ma va detto <i>allo schieramento</i>,
/// altrimenti si mette in corsa qualcosa che non potrà essere giudicato entro un orizzonte utile e
/// nessuno se ne accorge finché non sono passati mesi.</para>
///
/// Puro e statico: si prova senza database, senza orologio e senza servizi.
/// </summary>
public static class TradeFrequency
{
    /// <summary>
    /// Trade al mese attesi, dai trade osservati su una finestra di <paramref name="months"/> mesi.
    /// <c>null</c> se la finestra non è positiva: senza finestra la frequenza è un'illusione, e
    /// restituire zero la farebbe passare per «rarissima» invece che per «non derivabile».
    /// </summary>
    public static decimal? PerMonth(int trades, decimal months)
    {
        if (months <= 0m) return null;
        if (trades < 0) return null;
        return Math.Round(trades / months, 2);
    }

    /// <summary>
    /// Mesi necessari perché una gamba accumuli <paramref name="requiredTrades"/> trade alla
    /// frequenza attesa. <c>null</c> se la frequenza non è nota o è zero — e <b>zero non è
    /// «subito»</b>: è «mai», ed è il caso che l'errore di lettura più facile trasforma in un numero
    /// piccolo.
    /// </summary>
    public static decimal? MonthsToVerdict(decimal? perMonth, int requiredTrades)
    {
        if (perMonth is not decimal f || f <= 0m) return null;
        if (requiredTrades <= 0) return 0m;
        return Math.Round(requiredTrades / f, 1);
    }

    /// <summary>
    /// La frase da mostrare allo schieramento e accanto alla corsia. Dichiara sempre <b>entrambe</b>
    /// le cose — la frequenza e il tempo — perché una senza l'altra non permette di decidere:
    /// «2 trade/mese» sembra poco preoccupante finché non si traduce in «dieci mesi prima di poterlo
    /// giudicare».
    /// </summary>
    public static string Describe(decimal? perMonth, int requiredTrades)
    {
        if (perMonth is not decimal f)
        {
            return "frequenza attesa non derivabile (finestra di holdout assente): questa gamba non ha un tempo-al-verdetto dichiarabile";
        }
        if (f <= 0m)
        {
            return "frequenza attesa ZERO trade/mese: alla regola di ritiro per Sharpe non arriverà MAI, qualunque sia il tempo";
        }

        var mesi = MonthsToVerdict(f, requiredTrades);
        return mesi is not decimal m
            ? $"~{f:0.##} trade/mese attesi"
            : $"~{f:0.##} trade/mese attesi: servono ~{m:0.#} mesi per i {requiredTrades} trade che la regola di ritiro pretende";
    }

    /// <summary>
    /// La gamba è <b>in inedia</b>? Vero quando ha prodotto meno della frazione dichiarata dei trade
    /// che ci si aspettava nel tempo trascorso.
    ///
    /// <para>Il confronto è contro l'ATTESO NEL PERIODO OSSERVATO, non contro un numero fisso: una
    /// corsia da 30 trade/mese ferma da tre settimane è un guasto, una da 2 trade/mese con un solo
    /// trade in tre settimane è nella norma. Un solo numero per entrambe direbbe la cosa sbagliata su
    /// una delle due.</para>
    ///
    /// <para>Restituisce <c>false</c> quando la frequenza attesa non è nota: <b>l'ignoranza non
    /// condanna</b>. È la stessa regola per cui la sonda degli agenti tiene «non determinabile»
    /// separato da «spento», e per cui il monitor di deriva non salta un check senza dirlo.</para>
    /// </summary>
    /// <param name="expectedPerMonth">Frequenza attesa dall'holdout, sul simbolo ATTUALE della corsia.</param>
    /// <param name="observed">Trade chiusi davvero, sul simbolo attuale.</param>
    /// <param name="observation">Da quanto la gamba è in corsa.</param>
    /// <param name="minFraction">Frazione dell'atteso sotto cui si dichiara l'inedia (es. 0,2 = 20%).</param>
    /// <param name="minObservation">Osservazione minima prima di poter giudicare: sotto, si tace.</param>
    public static bool IsStarving(
        decimal? expectedPerMonth,
        int observed,
        TimeSpan observation,
        decimal minFraction,
        TimeSpan minObservation)
    {
        if (expectedPerMonth is not decimal f || f <= 0m) return false; // ignoranza: non condanna
        if (observation < minObservation) return false;                 // troppo presto per un giudizio
        if (minFraction <= 0m) return false;                            // criterio disattivato

        var mesiTrascorsi = (decimal)(observation.TotalDays / 30.0);
        if (mesiTrascorsi <= 0m) return false;

        var attesi = f * mesiTrascorsi;
        return observed < attesi * minFraction;
    }

    /// <summary>La spiegazione del verdetto di inedia, per il journal: un ritiro senza il suo perché è un ordine, non una decisione.</summary>
    public static string DescribeStarvation(decimal? expectedPerMonth, int observed, TimeSpan observation, decimal minFraction)
    {
        if (expectedPerMonth is not decimal f || f <= 0m) return "frequenza attesa non nota: nessun giudizio di inedia";
        var mesi = (decimal)(observation.TotalDays / 30.0);
        var attesi = Math.Round(f * mesi, 1);
        return $"{observed} trade in {observation.TotalDays:F0} giorni contro ~{attesi:0.#} attesi "
               + $"(~{f:0.##}/mese): sotto il {minFraction:P0} dell'atteso";
    }
}
