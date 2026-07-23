using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Parametri dell'event-study. Le finestre sono in BARRE della serie passata (1h ⇒ ore, 1d ⇒ giorni).
/// </summary>
/// <param name="EstimationBars">Finestra di stima della baseline (rendimento medio "normale"), PRIMA del gap.</param>
/// <param name="GapBars">Cuscinetto fra stima e finestra evento: l'anticipazione non contamina la baseline.</param>
/// <param name="PreBars">Barre PRIMA dell'evento incluse nello studio: misura anticipazione/leakage.</param>
/// <param name="PostBars">Barre dall'evento in poi (la barra 0 è la prima all'evento o dopo).</param>
/// <param name="PlaceboSamples">Insiemi di pseudo-eventi a date CASUALI (stessa numerosità) per il p-value.</param>
/// <param name="Seed">Determinismo del placebo.</param>
public sealed record EventStudyConfig(
    int EstimationBars = 60,
    int GapBars = 5,
    int PreBars = 5,
    int PostBars = 10,
    int PlaceboSamples = 200,
    int Seed = 42);

/// <summary>
/// Esito: AAR/CAAR per offset (da −Pre a +Post), la CAAR pre-evento (anticipazione), la CAAR
/// post-evento con t cross-evento, e il p-value placebo — la frazione di insiemi di date CASUALI
/// che producono una |CAAR post| almeno pari a quella osservata. È il placebo, non la t, il
/// verdetto primario: incorpora autocorrelazione e code grasse senza assumere normalità.
/// </summary>
public sealed record EventStudyResult(
    int EventsSupplied,
    int EventsUsable,
    IReadOnlyList<double> Aar,
    IReadOnlyList<double> Caar,
    double CaarPre,
    double CaarPost,
    double TStatPost,
    double PlaceboPValue,
    int PlaceboSamples,
    int PreBars)
{
    /// <summary>Offset (in barre dall'evento) dell'elemento <paramref name="index"/> di Aar/Caar.</summary>
    public int OffsetAt(int index) => index - PreBars;

    public bool IsSignificant(double alpha = 0.05) => EventsUsable > 0 && PlaceboPValue <= alpha;
}

/// <summary>
/// [T2.7 roadmap macchina-ricerca] Event-study RIGOROSO, in contrasto con le medie post-evento
/// semplici di <c>NewsImpactAnalyzer</c>:
///
///  1. <b>Abnormal return</b>: ogni rendimento della finestra evento è confrontato con la baseline
///     del titolo stimata su una finestra precedente separata da un gap — "dopo l'evento è salito"
///     non significa niente se saliva comunque;
///  2. <b>Finestra pre-evento</b>: una CAAR già positiva PRIMA dell'evento segnala anticipazione o
///     leakage del timestamp (notizie retrodatate, calendario noto in anticipo);
///  3. <b>Placebo temporale</b>: la stessa statistica su insiemi di date casuali (lezione T1.5: la
///     randomizzazione onesta è lungo il tempo). Se le date a caso "reagiscono" quanto le vere,
///     l'effetto è rumore.
///
/// Puro e deterministico a parità di seme. Richiede candele ordinate cronologicamente.
/// </summary>
public static class EventStudy
{
    public static EventStudyResult Run(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<DateTime> eventTimesUtc, EventStudyConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(eventTimesUtc);
        var cfg = config ?? new EventStudyConfig();
        if (cfg.EstimationBars < 10) throw new ArgumentOutOfRangeException(nameof(config), "Servono almeno 10 barre di stima.");
        if (cfg.PreBars < 0 || cfg.PostBars < 1 || cfg.GapBars < 0) throw new ArgumentOutOfRangeException(nameof(config));

        var n = candles.Count;
        var returns = new double?[n];
        for (var i = 1; i < n; i++)
        {
            var prev = candles[i - 1].Close;
            if (prev > 0m) returns[i] = (double)(candles[i].Close / prev - 1m);
        }

        // Indici-evento utilizzabili (finestra completa dentro la serie).
        var minIdx = 1 + cfg.EstimationBars + cfg.GapBars + cfg.PreBars;
        var maxIdx = n - 1 - cfg.PostBars;
        var eventIdx = new List<int>();
        foreach (var ts in eventTimesUtc)
        {
            var idx = FirstIndexAtOrAfter(candles, ts);
            if (idx is { } i && i >= minIdx && i <= maxIdx) eventIdx.Add(i);
        }

        var width = cfg.PreBars + cfg.PostBars + 1;
        if (eventIdx.Count == 0 || maxIdx < minIdx)
        {
            return new EventStudyResult(eventTimesUtc.Count, 0,
                new double[width], new double[width], 0, 0, 0, 1.0, cfg.PlaceboSamples, cfg.PreBars);
        }

        // AAR per offset + CAR post per evento.
        var (aar, carPost, carPre) = Study(returns, eventIdx, cfg);

        var caar = new double[width];
        var cum = 0d;
        for (var k = 0; k < width; k++) { cum += aar[k]; caar[k] = cum; }

        var meanPost = carPost.Average();
        double tStat = 0;
        if (carPost.Count > 2)
        {
            var sd = Math.Sqrt(carPost.Sum(v => (v - meanPost) * (v - meanPost)) / (carPost.Count - 1));
            if (sd > 1e-12) tStat = meanPost / (sd / Math.Sqrt(carPost.Count));
        }

        // Placebo: insiemi di pseudo-eventi di pari numerosità a indici casuali VALIDI.
        // p con correzione add-one (mai zero esatto da un campione finito).
        var rng = new Random(cfg.Seed);
        var extreme = 0;
        for (var s = 0; s < cfg.PlaceboSamples; s++)
        {
            var pseudo = new List<int>(eventIdx.Count);
            for (var e = 0; e < eventIdx.Count; e++) pseudo.Add(rng.Next(minIdx, maxIdx + 1));
            var (_, placeboCarPost, _) = Study(returns, pseudo, cfg);
            if (placeboCarPost.Count > 0 && Math.Abs(placeboCarPost.Average()) >= Math.Abs(meanPost)) extreme++;
        }
        var pValue = (1.0 + extreme) / (1.0 + cfg.PlaceboSamples);

        return new EventStudyResult(
            eventTimesUtc.Count, eventIdx.Count, aar, caar,
            CaarPre: cfg.PreBars > 0 ? caar[cfg.PreBars - 1] : 0d,
            CaarPost: caar[^1] - (cfg.PreBars > 0 ? caar[cfg.PreBars - 1] : 0d),
            TStatPost: tStat,
            PlaceboPValue: pValue,
            PlaceboSamples: cfg.PlaceboSamples,
            PreBars: cfg.PreBars);
    }

    /// <summary>AAR per offset (−Pre..+Post) e CAR pre/post PER EVENTO, con baseline per-evento.</summary>
    private static (double[] Aar, List<double> CarPost, List<double> CarPre) Study(
        double?[] returns, IReadOnlyList<int> eventIdx, EventStudyConfig cfg)
    {
        var width = cfg.PreBars + cfg.PostBars + 1;
        var sum = new double[width];
        var count = new int[width];
        var carPost = new List<double>(eventIdx.Count);
        var carPre = new List<double>(eventIdx.Count);

        foreach (var i in eventIdx)
        {
            // Baseline: media dei rendimenti in [i-Pre-Gap-Est, i-Pre-Gap).
            var estEnd = i - cfg.PreBars - cfg.GapBars;
            var estStart = estEnd - cfg.EstimationBars;
            double baseSum = 0; var baseCount = 0;
            for (var j = Math.Max(1, estStart); j < estEnd; j++)
            {
                if (returns[j] is { } v) { baseSum += v; baseCount++; }
            }
            if (baseCount < cfg.EstimationBars / 2) continue; // baseline troppo bucata: evento scartato
            var mu = baseSum / baseCount;

            double post = 0, pre = 0;
            for (var k = -cfg.PreBars; k <= cfg.PostBars; k++)
            {
                if (returns[i + k] is not { } r) continue;
                var abnormal = r - mu;
                var slot = k + cfg.PreBars;
                sum[slot] += abnormal;
                count[slot]++;
                if (k < 0) pre += abnormal; else post += abnormal;
            }
            carPost.Add(post);
            carPre.Add(pre);
        }

        var aar = new double[width];
        for (var k = 0; k < width; k++) aar[k] = count[k] > 0 ? sum[k] / count[k] : 0d;
        return (aar, carPost, carPre);
    }

    private static int? FirstIndexAtOrAfter(IReadOnlyList<OhlcvData> candles, DateTime t)
    {
        int lo = 0, hi = candles.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (candles[mid].TimestampUtc < t) lo = mid + 1; else hi = mid;
        }
        return lo < candles.Count ? lo : null;
    }
}
