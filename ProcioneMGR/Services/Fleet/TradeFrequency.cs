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
    /// [I14-rev] Giorni in un mese, in UN SOLO posto: 365,25 / 12 = 30,4375.
    ///
    /// <para><b>Perché è una costante condivisa e non un 30 scritto dove serve.</b> L'atteso nasce
    /// dividendo i giorni di holdout per 30,44 (<c>PipelineDateRanges.HoldoutMonths</c>); il tempo
    /// trascorso con cui quell'atteso viene riproporzionato in <see cref="IsStarving"/> divideva per
    /// 30,0. Stessa unità, due aritmetiche, ai due lati della <i>stessa</i> disuguaglianza: una
    /// corsia con esattamente il numero di trade al limite veniva dichiarata affamata, e lo scarto
    /// cresceva dell'1,5% per ogni mese di osservazione. Questa classe esiste per non avere due
    /// regole per la stessa domanda: averne due per la stessa <i>unità</i> era lo stesso difetto un
    /// piano più sotto.</para>
    /// </summary>
    public const decimal DaysPerMonth = 30.4375m;

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

        var mesiTrascorsi = (decimal)observation.TotalDays / DaysPerMonth;
        if (mesiTrascorsi <= 0m) return false;

        var attesi = f * mesiTrascorsi;
        if (observed >= attesi * minFraction) return false;

        // [K16, 2026-09-05] IL SILENZIO SI CONDANNA SOLO SE E' IMPROBABILE SOTTO IL PROPRIO NULLO.
        //
        // La sola frazione condannava sul rumore: con zero trade osservati «0 < attesi × 0,2» e'
        // vero per QUALUNQUE ritmo atteso positivo, quindi ogni corsia muta veniva ritirata al
        // decimo giorno anche quando il suo ritmo dichiarato rendeva quel silenzio la cosa piu'
        // normale del mondo — a 1,65 trade/mese la probabilita' di zero trade in dieci giorni e' del
        // 58% (Poisson, λ = 0,54). Misurato il 2026-09-05 sulla corsia 4 (Composite XLM/USDT 4h),
        // che sarebbe stata fermata l'11/09 mentre rispettava il proprio ritmo.
        //
        // Il nullo e' «la gamba opera al ritmo dichiarato»: il conteggio dei trade in un periodo e'
        // un processo di Poisson con media `attesi`, e la coda inferiore P(X ≤ osservati | attesi)
        // e' la probabilita' di vedere un silenzio almeno cosi' lungo per puro caso. Sotto il 5% si
        // condanna; sopra, si aspetta — e' lo stesso livello di significativita' dei gate di
        // ricerca. Per zero trade la soglia equivale ad attesi ≥ 3,0 (e^-3 = 4,98%): la corsia 4
        // matura verso i 55 giorni, non i 10. Il PRD lo aveva scritto come K16 e rimandato «finche'
        // non ci siano 30 trade vivi per tarare»: il nullo non ha bisogno di taratura, e' aritmetica.
        return PoissonLowerTail(observed, attesi) < MaxProbabilitaDelSilenzio;
    }

    /// <summary>
    /// [K16] Sotto questa probabilita' il silenzio di una gamba non e' piu' compatibile col suo
    /// ritmo dichiarato: 5%, lo stesso livello dei gate di ricerca (DSR, permutazione, gemello nullo).
    /// </summary>
    public const decimal MaxProbabilitaDelSilenzio = 0.05m;

    /// <summary>
    /// P(X ≤ <paramref name="observed"/>) per X ~ Poisson(<paramref name="mean"/>): la probabilita' di
    /// osservare al massimo quei trade quando il ritmo vero e' quello atteso. Puro; con media non
    /// positiva restituisce 1 (nessun silenzio e' improbabile se non ci si aspetta nulla).
    /// </summary>
    public static decimal PoissonLowerTail(int observed, decimal mean)
    {
        if (mean <= 0m) return 1m;
        if (observed < 0) return 0m;
        var lambda = (double)mean;
        // Termini calcolati in scala logaritmica: λ^k/k! trabocca gia' a k ≈ 170 in double, e i
        // fuzz della flotta passano ritmi assurdi di proposito.
        var logLambda = Math.Log(lambda);
        var cumulative = 0.0;
        var logTerm = -lambda; // log(e^-λ · λ^0 / 0!)
        for (var k = 0; k <= observed; k++)
        {
            if (k > 0) logTerm += logLambda - Math.Log(k);
            cumulative += Math.Exp(logTerm);
            if (cumulative >= 1.0) return 1m;
        }
        return (decimal)Math.Clamp(cumulative, 0.0, 1.0);
    }

    /// <summary>La spiegazione del verdetto di inedia, per il journal: un ritiro senza il suo perché è un ordine, non una decisione.</summary>
    public static string DescribeStarvation(decimal? expectedPerMonth, int observed, TimeSpan observation, decimal minFraction)
    {
        if (expectedPerMonth is not decimal f || f <= 0m) return "frequenza attesa non nota: nessun giudizio di inedia";
        var mesi = (decimal)observation.TotalDays / DaysPerMonth;
        var attesiEsatti = f * mesi;
        var attesi = Math.Round(attesiEsatti, 1);
        // [K16] La probabilita' va detta insieme al conteggio: «0 trade contro 0,5 attesi» e «0
        // contro 9,9» sono la stessa frazione e due verdetti opposti.
        var p = PoissonLowerTail(observed, attesiEsatti);
        return $"{observed} trade in {observation.TotalDays:F0} giorni contro ~{attesi:0.#} attesi "
               + $"(~{f:0.##}/mese): sotto il {minFraction:P0} dell'atteso, e un silenzio cosi' lungo ha probabilita' "
               + $"{p:P1} sotto il ritmo dichiarato (soglia {MaxProbabilitaDelSilenzio:P0})";
    }
}
