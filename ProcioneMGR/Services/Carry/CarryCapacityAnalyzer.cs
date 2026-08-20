using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Carry;

/// <summary>
/// [I16 ≡ F12] Un punto della curva di capacità: che cosa resta del carry a una data taglia e a una
/// data soglia d'ingresso.
/// </summary>
/// <param name="NotionalUsd">Nozionale per gamba, in unità di conto.</param>
/// <param name="EnterThresholdPercent">Soglia d'ingresso (funding annualizzato %).</param>
/// <param name="SlippagePercent">Slippage per gamba EFFETTIVO a questa taglia (base + impatto √).</param>
/// <param name="ParticipationPercent">Quota dell'ADV assorbita da una gamba: il numero che governa l'impatto.</param>
/// <param name="NetAnnualizedPercent">Rendimento netto annualizzato sul periodo intero.</param>
/// <param name="Episodes">Episodi di carry aperti nel periodo.</param>
/// <param name="TradesPerMonth">Episodi al mese. <b>Dichiarato sempre</b>: è la preferenza operativa del proprietario.</param>
/// <param name="MedianHoldDays">Durata MEDIANA di un episodio in giorni. Dichiarata sempre, per la stessa ragione.</param>
/// <param name="TimeInPositionPercent">Quota del tempo in posizione: quanto il capitale sta fermo.</param>
public sealed record CarryCapacityPoint(
    decimal NotionalUsd,
    decimal EnterThresholdPercent,
    decimal SlippagePercent,
    decimal ParticipationPercent,
    decimal NetAnnualizedPercent,
    int Episodes,
    decimal TradesPerMonth,
    decimal MedianHoldDays,
    decimal TimeInPositionPercent);

/// <summary>
/// [I16 ≡ F12] <b>Quanto capitale regge il carry, e a quale soglia conviene aprirlo.</b>
///
/// <para>Il carry è l'unica classe con edge misurato positivo e l'unica che opera davvero — ed è
/// l'unica che nessuno stava dimensionando, mentre il basis si comprime. Questo analizzatore
/// risponde a due domande insieme, perché sono la stessa domanda vista da due lati: <b>a che taglia
/// l'edge si annulla</b> e <b>a che soglia conviene entrare</b>.</para>
///
/// <para><b>Come la taglia entra nel conto.</b> Non come un vincolo esterno ma come <i>slippage</i>:
/// più grande è l'ordine, più cara è l'esecuzione. Si usa la legge empirica √-partecipazione già in
/// repo (<c>MarketImpactModel.SquareRoot</c>, Almgren): l'impatto cresce col volume ma
/// <i>decrescentemente</i>. La partecipazione è il nozionale di UNA gamba diviso l'ADV del mercato.</para>
///
/// <para><b>Perché il costo è la variabile che decide.</b> Un round trip paga quattro fill su due
/// gambe; a 5% annualizzato si incassano ~0,0137% al giorno. Il tempo che serve a coprire il costo
/// è il numero che governa tutto — e quando il funding gira negativo prima di allora, l'episodio
/// chiude in perdita. È per questo che <b>trade/mese e durata mediana</b> non sono contorno: sono
/// il meccanismo.</para>
///
/// <para><b>Quello che questo analizzatore NON è.</b> Il coefficiente d'impatto è dichiarato
/// illustrativo nel repo e non è stato calibrato su fill veri: la curva di capacità è quindi un
/// MODELLO, non una misura. Il premio invece è misurato — sette anni di funding reale. Le due cose
/// non hanno lo stesso statuto e vanno lette diversamente.</para>
///
/// Puro e statico: si prova senza database, senza orologio e senza servizi.
/// </summary>
public static class CarryCapacityAnalyzer
{
    /// <summary>
    /// Impatto √ per gamba, in percentuale, a una data partecipazione all'ADV.
    /// <c>impatto = coefficiente · √(nozionale / ADV) · 100</c>.
    ///
    /// <para>ADV non positivo ⇒ <c>null</c>: senza una scala di liquidità la partecipazione non è
    /// definita, e restituire zero fingerebbe che una taglia qualunque sia gratis — l'errore che
    /// rende una curva di capacità rassicurante a prescindere dalla realtà.</para>
    /// </summary>
    public static decimal? ImpactPercent(decimal notionalUsd, decimal averageDailyVolumeUsd, decimal coefficient)
    {
        if (averageDailyVolumeUsd <= 0m) return null;
        if (notionalUsd <= 0m) return 0m;

        var partecipazione = (double)(notionalUsd / averageDailyVolumeUsd);
        return (decimal)(Math.Sqrt(partecipazione) * (double)coefficient) * 100m;
    }

    /// <summary>
    /// La curva: per ogni taglia e ogni soglia, che cosa resta del carry.
    ///
    /// <para>Il motore di backtest è lo STESSO dell'operatività (<see cref="CarryBacktestEngine"/>,
    /// che a sua volta usa <see cref="CarryDecider"/>): questa analisi non re-implementa la regola,
    /// la interroga. Una seconda implementazione della decisione darebbe una capacità misurata su
    /// una strategia che non è quella che opera.</para>
    /// </summary>
    /// <param name="funding">Serie di funding firmata, cronologica.</param>
    /// <param name="baseConfig">Configurazione di riferimento: fee, slippage BASE, isteresi, finestra.</param>
    /// <param name="averageDailyVolumeUsd">ADV del mercato: la scala di liquidità contro cui si misura la partecipazione.</param>
    /// <param name="notionals">Le taglie da provare (nozionale per gamba).</param>
    /// <param name="thresholds">Le soglie d'ingresso da provare (% annualizzato).</param>
    /// <param name="impactCoefficient">Coefficiente della legge √. Illustrativo finché non calibrato su fill veri.</param>
    public static IReadOnlyList<CarryCapacityPoint> Sweep(
        IReadOnlyList<FundingRatePoint> funding,
        CarryConfiguration baseConfig,
        decimal averageDailyVolumeUsd,
        IReadOnlyList<decimal> notionals,
        IReadOnlyList<decimal> thresholds,
        decimal impactCoefficient = 0.1m)
    {
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(notionals);
        ArgumentNullException.ThrowIfNull(thresholds);

        var motore = new CarryBacktestEngine();
        var punti = new List<CarryCapacityPoint>(notionals.Count * thresholds.Count);

        foreach (var taglia in notionals)
        {
            var impatto = ImpactPercent(taglia, averageDailyVolumeUsd, impactCoefficient);
            if (impatto is null) continue;   // senza ADV la riga non esiste, non vale zero

            foreach (var soglia in thresholds)
            {
                var cfg = Clona(baseConfig);
                cfg.EnterAnnualFundingPercent = soglia;
                // L'isteresi si conserva in PROPORZIONE, non in valore assoluto: tenerla fissa
                // mentre la soglia sale la renderebbe via via più larga, e la curva misurerebbe
                // due cose che cambiano insieme invece di una.
                cfg.ExitAnnualFundingPercent = soglia * FrazioneUscita(baseConfig);
                cfg.SlippagePercent = baseConfig.SlippagePercent + impatto.Value;

                var esito = motore.Run(funding, cfg);
                var (alMese, medianaGiorni) = FrequenzaEDurata(esito, funding);

                punti.Add(new CarryCapacityPoint(
                    taglia, soglia, cfg.SlippagePercent,
                    ParticipationPercent: averageDailyVolumeUsd > 0m ? taglia / averageDailyVolumeUsd * 100m : 0m,
                    esito.NetAnnualizedPercent, esito.Episodes, alMese, medianaGiorni,
                    esito.TimeInPositionFraction * 100m));
            }
        }

        return punti;
    }

    /// <summary>
    /// [I16] <b>Il tempo che serve a ripagare un round trip</b>, in giorni, al premio dato.
    ///
    /// <para>È il numero che governa tutto il resto: se il funding gira sotto la soglia d'uscita
    /// prima di questo termine, l'episodio chiude in perdita per costruzione. Confrontarlo con la
    /// durata mediana degli episodi dice se la soglia è troppo bassa <i>a prescindere</i> da quanto
    /// rende in media.</para>
    ///
    /// <para><c>null</c> se il premio non è positivo: senza incasso non c'è pareggio, e restituire
    /// un numero grande farebbe passare «mai» per «tardi».</para>
    /// </summary>
    public static decimal? BreakEvenDays(decimal annualPremiumPercent, CarryConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (annualPremiumPercent <= 0m) return null;

        var costoRoundTrip = 2m * (config.SpotFeePercent + config.SlippagePercent)
                           + 2m * (config.PerpFeePercent + config.SlippagePercent);
        var alGiorno = annualPremiumPercent / 365m;
        return Math.Round(costoRoundTrip / alGiorno, 1);
    }

    /// <summary>
    /// Il verdetto, <b>scritto anche quando dice che va già bene</b>: è ciò che il gate dell'item
    /// pretende, e la ragione è che un'analisi senza conclusione si legge come «non abbiamo trovato
    /// niente» invece che «abbiamo guardato e la risposta è questa».
    /// </summary>
    public static string Verdict(IReadOnlyList<CarryCapacityPoint> curva, decimal sogliaAttuale)
    {
        ArgumentNullException.ThrowIfNull(curva);
        if (curva.Count == 0) return "Nessun punto calcolabile: manca la scala di liquidità (ADV) o la serie di funding.";

        // [correzione trovata dalla misura sui dati veri, 2026-08-20] «Migliore» significa fra le
        // soglie che HANNO APERTO. Una soglia che non apre mai rende 0,00% netto, e su un periodo in
        // cui il carry perde — l'ultimo anno su BTC — lo zero batte ogni alternativa: il verdetto
        // avrebbe presentato «soglia migliore: 12%» quando 12% vuol dire «non fare niente».
        //
        // Far passare un astenersi per un ottimo e' la classe «controllo che rassicura a prescindere
        // dalla realta'» applicata a un verdetto: la risposta giusta e' che NESSUNA soglia funziona,
        // e va detta con quelle parole.
        var operative = curva.Where(p => p.Episodes > 0).ToList();
        if (operative.Count == 0)
        {
            return "NESSUNA delle soglie provate apre mai un episodio sul periodo esaminato: "
                 + "il funding non arriva alla soglia piu' bassa della griglia. Non e' una questione di "
                 + "taglia ne' di soglia — il premio non c'e'.";
        }

        var migliore = operative.OrderByDescending(p => p.NetAnnualizedPercent).First();
        var attuale = curva      // qui NON si filtra: se la soglia in vigore non apre, va detto
            .Where(p => p.EnterThresholdPercent == sogliaAttuale)
            .OrderByDescending(p => p.NetAnnualizedPercent)
            .FirstOrDefault();

        // La taglia oltre cui il netto smette di essere positivo: la capacità, nel senso letterale.
        var capacita = operative
            .Where(p => p.EnterThresholdPercent == migliore.EnterThresholdPercent)
            .OrderBy(p => p.NotionalUsd)
            .TakeWhile(p => p.NetAnnualizedPercent > 0m)
            .Select(p => (decimal?)p.NotionalUsd)
            .LastOrDefault();

        var testo = migliore.NetAnnualizedPercent <= 0m
            ? "NESSUNA soglia che apra e' profittevole sul periodo esaminato. "
            : string.Empty;

        testo += $"Soglia migliore fra quelle che aprono: {migliore.EnterThresholdPercent:0.#}% "
                  + $"({migliore.NetAnnualizedPercent:0.##}% netto annualizzato, "
                  + $"{migliore.TradesPerMonth:0.##} trade/mese, durata mediana {migliore.MedianHoldDays:0.#} giorni, "
                  + $"in posizione il {migliore.TimeInPositionPercent:0.#}% del tempo). ";

        if (attuale is not null && migliore.EnterThresholdPercent == sogliaAttuale)
        {
            // [correzione 2026-08-20] «Non c'e' niente da cambiare» e' vero SOLO se la migliore
            // guadagna. Quando la migliore perde, la stessa frase contraddice quella prima —
            // e la lettura giusta e' che la soglia non e' la leva: il premio non c'e'.
            testo += migliore.NetAnnualizedPercent > 0m
                ? $"È GIÀ la soglia in vigore ({sogliaAttuale:0.#}%): non c'è niente da cambiare, "
                  + "e questo è il verdetto, non l'assenza di uno."
                : $"È GIÀ la soglia in vigore ({sogliaAttuale:0.#}%), ed è la MENO PEGGIO fra quelle "
                  + "che aprono: la soglia non è la leva, il premio non c'è.";
        }
        else if (attuale is not null)
        {
            var delta = migliore.NetAnnualizedPercent - attuale.NetAnnualizedPercent;
            testo += $"La soglia in vigore è {sogliaAttuale:0.#}% e rende {attuale.NetAnnualizedPercent:0.##}%: "
                   + $"spostarla vale {delta:0.##} punti di netto annualizzato.";
        }

        testo += capacita is decimal c
            ? $" Capacità: il netto resta positivo fino a {c:N0} per gamba."
            : " Capacità: il netto NON è positivo a nessuna delle taglie provate.";

        return testo;
    }

    // --- Impalcatura -----------------------------------------------------------------------------

    /// <summary>La frazione soglia-uscita / soglia-entrata della configurazione di riferimento (isteresi relativa).</summary>
    private static decimal FrazioneUscita(CarryConfiguration c) =>
        c.EnterAnnualFundingPercent > 0m ? c.ExitAnnualFundingPercent / c.EnterAnnualFundingPercent : 0m;

    /// <summary>
    /// Trade/mese e durata MEDIANA, dagli episodi veri. La mediana e non la media perché la
    /// distribuzione delle durate ha una coda lunga — un episodio da sei mesi sposterebbe la media
    /// e racconterebbe una strategia che non è quella che si osserva.
    /// </summary>
    internal static (decimal PerMonth, decimal MedianDays) FrequenzaEDurata(
        CarryBacktestResult esito, IReadOnlyList<FundingRatePoint> funding)
    {
        if (esito.EpisodeList.Count == 0 || funding.Count < 2) return (0m, 0m);

        var giorniTotali = (decimal)(funding[^1].TimestampUtc - funding[0].TimestampUtc).TotalDays;
        var perMese = giorniTotali > 0m
            ? Math.Round(esito.EpisodeList.Count / (giorniTotali / Fleet.TradeFrequency.DaysPerMonth), 2)
            : 0m;

        var durate = esito.EpisodeList
            .Select(e => (decimal)(e.ClosedUtc - e.OpenedUtc).TotalDays)
            .OrderBy(d => d)
            .ToList();
        var mediana = durate.Count % 2 == 1
            ? durate[durate.Count / 2]
            : (durate[durate.Count / 2 - 1] + durate[durate.Count / 2]) / 2m;

        return (perMese, Math.Round(mediana, 1));
    }

    private static CarryConfiguration Clona(CarryConfiguration c) => new()
    {
        InitialCapital = c.InitialCapital,
        PositionSizePercent = c.PositionSizePercent,
        EnterAnnualFundingPercent = c.EnterAnnualFundingPercent,
        ExitAnnualFundingPercent = c.ExitAnnualFundingPercent,
        TrailingFundingEvents = c.TrailingFundingEvents,
        FundingEventsPerDay = c.FundingEventsPerDay,
        SpotFeePercent = c.SpotFeePercent,
        PerpFeePercent = c.PerpFeePercent,
        SlippagePercent = c.SlippagePercent,
    };
}
