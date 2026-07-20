using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Dosaggio della posizione sulla volatilità realizzata: quando il mercato si agita si espone meno
/// capitale, quando si calma se ne espone di più. È l'unico risultato di ricerca sopravvissuto alle
/// verifiche (vedi <c>docs/REPORT-DOSAGGIO-VOLATILITA.md</c>): sul paniere equipesato, a parità di
/// esposizione MEDIA, dosare invece di tenere fisso porta lo Sharpe da 0,12 a 0,43. Non è "tenere
/// meno mercato": conta QUANDO si è esposti.
///
/// <para><b>Proprietà di sicurezza.</b> <see cref="SafetyConfiguration.MaxExposureMultiplier"/> vale
/// 1,0 di default, quindi il moltiplicatore può solo RIDURRE la dimensione decisa da
/// <see cref="SafetyConfiguration.PositionSizePercent"/>, mai aumentarla. Ne segue che accendere
/// questa funzione non può far superare nessuno dei limiti già validati a StartAsync
/// (MaxPositionSizePercent, MaxTotalExposurePercent): al più li rende più stringenti. Alzare il
/// tetto sopra 1,0 è possibile ma toglie questa garanzia, ed è per questo che non è il default.</para>
///
/// <para><b>Cosa NON fa, misurato e non supposto.</b> Non prevede i rendimenti e non migliora un
/// segnale sbagliato: applicato alle 6 strategie selezionate dalla caccia ne recupera 0 su 6 (fase
/// <c>voloverlay</c>) — dimezza le perdite, non le inverte.</para>
///
/// <para><b>Limite importante del contesto.</b> Il miglioramento di Sharpe (0,12 → 0,43) è misurato
/// su un PANIERE di 24 monete. Su SINGOLI simboli — che è il caso di una corsia di trading, la quale
/// opera su un simbolo solo — il dosaggio batte l'esposizione costante equivalente in appena 2 casi
/// su 12 (fase <c>volsingle</c>), e negli altri peggiora lo Sharpe. Quello che fa in modo affidabile
/// anche sul singolo è ridurre l'ampiezza delle oscillazioni. Va quindi acceso per contenere il
/// drawdown, non per migliorare il rendimento corretto per il rischio.</para>
///
/// <para>La volatilità è quella REALIZZATA sulle ultime barre — la stessa misura validata dalla
/// ricerca. La piattaforma ha anche un GARCH(1,1) in <c>/volatility</c>, che è una previsione e
/// sarebbe plausibilmente migliore, ma non è stato validato per questo uso: usarlo qui
/// significherebbe schierare qualcosa che nessuno ha misurato.</para>
/// </summary>
internal static class VolatilityScaler
{
    /// <summary>
    /// Moltiplicatore da applicare alla dimensione della posizione. Ritorna sempre 1 (comportamento
    /// invariato) se la funzione è spenta, se i dati non bastano o se la volatilità stimata è nulla.
    /// </summary>
    /// <param name="closes">Chiusure in ordine cronologico: solo passato e presente, mai futuro.</param>
    /// <param name="timeframe">Serve ad annualizzare correttamente (una barra 4h non è una barra 1d).</param>
    public static decimal Compute(IReadOnlyList<decimal> closes, string timeframe, SafetyConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return cfg.VolatilityTargetingEnabled
            ? Compute(closes, timeframe, cfg.TargetAnnualVolatilityPercent, cfg.VolatilityLookbackBars,
                      cfg.MinExposureMultiplier, cfg.MaxExposureMultiplier)
            : 1m;
    }

    /// <summary>
    /// Calcolo puro, senza dipendere da <see cref="SafetyConfiguration"/>: lo usa anche il motore di
    /// BACKTEST, così l'effetto del dosaggio si può misurare sui propri dati PRIMA di accenderlo dal
    /// vivo. Se il backtest non sapesse dosare, accendere la funzione aprirebbe un divario
    /// backtest/live — cioè il difetto che questa piattaforma evita ovunque.
    /// </summary>
    public static decimal Compute(
        IReadOnlyList<decimal> closes, string timeframe,
        decimal targetAnnualVolPercent, int lookbackBars, decimal minMultiplier, decimal maxMultiplier)
    {
        if (closes is null) return 1m;

        var lookback = Math.Max(2, lookbackBars);
        // Servono lookback rendimenti, quindi lookback+1 prezzi. Con meno dati non si stima niente:
        // meglio il comportamento invariato che una stima su quattro punti.
        if (closes.Count < lookback + 1) return 1m;

        var realized = RealizedAnnualVolatility(closes, lookback, timeframe);
        if (realized <= 0d) return 1m;

        var target = (double)targetAnnualVolPercent / 100d;
        if (target <= 0d) return 1m;

        var raw = (decimal)(target / realized);
        return Math.Clamp(raw, minMultiplier, maxMultiplier);
    }

    /// <summary>
    /// Volatilità annualizzata dei rendimenti semplici sulle ultime <paramref name="lookback"/> barre.
    /// Rendimenti semplici e non logaritmici per restare identici alla misura della ricerca.
    /// </summary>
    internal static double RealizedAnnualVolatility(IReadOnlyList<decimal> closes, int lookback, string timeframe)
    {
        var rets = new List<double>(lookback);
        for (var i = closes.Count - lookback; i < closes.Count; i++)
        {
            var prev = closes[i - 1];
            if (prev <= 0m) continue;
            rets.Add((double)(closes[i] / prev - 1m));
        }
        if (rets.Count < 2) return 0d;

        var mean = rets.Average();
        var variance = rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1);
        if (variance <= 0d) return 0d;

        return Math.Sqrt(variance) * Math.Sqrt(Statistics.PeriodsPerYear(timeframe));
    }
}
