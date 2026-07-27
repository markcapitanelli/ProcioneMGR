using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Discovery.Dtw;

// =============================================================================================
//  [D4 roadmap scoperta-pattern] DYNAMIC TIME WARPING — ricerca di pattern per FORMA.
//
//  Cosa fa che la distanza euclidea non fa: allinea due sequenze in modo non lineare, quindi
//  riconosce lo stesso pattern anche quando si svolge più in fretta o più lentamente. Un
//  "accumulo" o una "compressione prima del breakout" non hanno una durata fissa, ed è per questo
//  che confrontarli barra-contro-barra non funziona.
//
//  QUATTRO SCELTE, tutte necessarie perché il risultato voglia dire qualcosa:
//
//  1. Z-NORMALIZZAZIONE OBBLIGATORIA. Senza, DTW confronta LIVELLI di prezzo: BTC a 60.000
//     risulterebbe lontanissimo dallo stesso identico pattern a 30.000. Si confrontano forme, e
//     una forma non ha unità di misura.
//  2. BANDA DI SAKOE-CHIBA. Senza vincolo, DTW può allineare l'inizio di una sequenza con la fine
//     dell'altra e trovare somiglianze che non esistono: è il difetto che la letteratura segnala
//     come "allineamenti spurii sul rumore". La banda limita di quanto il tempo si può deformare.
//  3. LOWER BOUND (LB_Keogh) PER IL PRUNING. Scandire ~7,45 M candele con un DTW O(n·m) per ogni
//     posizione non è praticabile. LB_Keogh calcola in O(n) un limite INFERIORE alla distanza
//     vera: se già quello supera la soglia, la finestra si scarta senza calcolare il DTW. Che sia
//     davvero un limite inferiore è una proprietà da dimostrare, non da sperare — c'è un test che
//     la verifica su migliaia di coppie casuali, perché se fosse violata il pruning scarterebbe
//     silenziosamente proprio le corrispondenze migliori.
//  4. NIENTE SOVRAPPOSIZIONI. Un pattern che combacia alla barra i combacia quasi sempre anche a
//     i+1: senza una separazione minima si otterrebbero "200 occorrenze" che sono una sola,
//     contata 200 volte — e un conteggio gonfiato è esattamente ciò che rende un backtest bugiardo.
// =============================================================================================

/// <summary>Un'occorrenza del pattern nella serie.</summary>
public sealed record DtwMatch(
    int StartIndex,
    int EndIndex,
    DateTime StartUtc,
    DateTime EndUtc,
    double Distance);

/// <summary>Parametri della ricerca.</summary>
public sealed class DtwConfig
{
    /// <summary>Ampiezza della banda di Sakoe-Chiba, in % della lunghezza del pattern.</summary>
    public int BandPercent { get; set; } = 10;

    /// <summary>
    /// Distanza massima perché una finestra sia considerata un'occorrenza. È su scala
    /// z-normalizzata: valori tipici 0,5–3 a seconda di quanto si vuole essere selettivi.
    /// </summary>
    public double MaxDistance { get; set; } = 1.5;

    /// <summary>Massimo numero di occorrenze restituite (le migliori per distanza).</summary>
    public int MaxMatches { get; set; } = 500;

    /// <summary>
    /// Separazione minima fra due occorrenze, in barre. Zero = usa la lunghezza del pattern, che è
    /// il default sensato: due occorrenze che si sovrappongono sono la stessa occorrenza.
    /// </summary>
    public int MinSeparationBars { get; set; }
}

/// <summary>Ricerca di pattern per forma via Dynamic Time Warping. Puro e deterministico.</summary>
public interface IDtwMatcher
{
    /// <summary>Distanza DTW fra due sequenze già z-normalizzate, con banda.</summary>
    double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b, int band);

    /// <summary>Limite INFERIORE alla distanza DTW (LB_Keogh): mai maggiore di <see cref="Distance"/>.</summary>
    double LowerBound(IReadOnlyList<double> query, IReadOnlyList<double> candidate, int band);

    /// <summary>Z-normalizza una sequenza (media 0, deviazione 1). Serie costante ⇒ tutti zeri.</summary>
    IReadOnlyList<double> ZNormalize(IReadOnlyList<double> values);

    /// <summary>Occorrenze del pattern nella serie, non sovrapposte, ordinate per posizione.</summary>
    IReadOnlyList<DtwMatch> FindMatches(
        IReadOnlyList<OhlcvData> series, IReadOnlyList<decimal> template, DtwConfig config);

    /// <summary>
    /// Serie booleana allineata alle candele: <c>true</c> sulla barra in cui un'occorrenza si
    /// CHIUDE. È la forma in cui il pattern entra nel motore Discovery come trigger evento — mai
    /// come strategia a sé.
    /// </summary>
    IReadOnlyList<bool> ToEventSeries(int seriesLength, IReadOnlyList<DtwMatch> matches);
}

/// <inheritdoc cref="IDtwMatcher"/>
public sealed class DtwMatcher : IDtwMatcher
{
    public IReadOnlyList<double> ZNormalize(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var n = values.Count;
        if (n == 0) return [];

        double mean = 0;
        for (var i = 0; i < n; i++) mean += values[i];
        mean /= n;

        double sumSq = 0;
        for (var i = 0; i < n; i++) { var d = values[i] - mean; sumSq += d * d; }
        var sd = Math.Sqrt(sumSq / n);

        var result = new double[n];
        // Serie costante: deviazione zero. Dividere darebbe NaN che si propagherebbe in silenzio
        // fino a "distanza 0" — cioè una corrispondenza perfetta con qualunque cosa.
        if (sd < 1e-12) return result;
        for (var i = 0; i < n; i++) result[i] = (values[i] - mean) / sd;
        return result;
    }

    public double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b, int band)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var n = a.Count;
        var m = b.Count;
        if (n == 0 || m == 0) return double.PositiveInfinity;

        // La banda non può essere più stretta della differenza di lunghezza, altrimenti non
        // esisterebbe alcun cammino ammissibile e la distanza sarebbe infinita per costruzione.
        var w = Math.Max(band, Math.Abs(n - m));

        var prev = new double[m + 1];
        var curr = new double[m + 1];
        Array.Fill(prev, double.PositiveInfinity);
        prev[0] = 0;

        for (var i = 1; i <= n; i++)
        {
            Array.Fill(curr, double.PositiveInfinity);
            var lo = Math.Max(1, i - w);
            var hi = Math.Min(m, i + w);
            for (var j = lo; j <= hi; j++)
            {
                var cost = (a[i - 1] - b[j - 1]) * (a[i - 1] - b[j - 1]);
                var best = Math.Min(prev[j], Math.Min(curr[j - 1], prev[j - 1]));
                curr[j] = cost + best;
            }
            (prev, curr) = (curr, prev);
        }

        var total = prev[m];
        return double.IsPositiveInfinity(total) ? total : Math.Sqrt(total);
    }

    public double LowerBound(IReadOnlyList<double> query, IReadOnlyList<double> candidate, int band)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        var n = Math.Min(query.Count, candidate.Count);
        if (n == 0) return 0;

        // Inviluppo superiore/inferiore del CANDIDATO entro la banda: il cammino DTW non può
        // uscirne, quindi la distanza dal punto della query all'inviluppo è un limite inferiore.
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - band);
            var hi = Math.Min(candidate.Count - 1, i + band);
            double upper = double.NegativeInfinity, lower = double.PositiveInfinity;
            for (var k = lo; k <= hi; k++)
            {
                if (candidate[k] > upper) upper = candidate[k];
                if (candidate[k] < lower) lower = candidate[k];
            }

            var q = query[i];
            if (q > upper) { var d = q - upper; sum += d * d; }
            else if (q < lower) { var d = lower - q; sum += d * d; }
        }
        return Math.Sqrt(sum);
    }

    public IReadOnlyList<DtwMatch> FindMatches(
        IReadOnlyList<OhlcvData> series, IReadOnlyList<decimal> template, DtwConfig config)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(template);
        config ??= new DtwConfig();

        var len = template.Count;
        if (len < 3 || series.Count < len) return [];

        var query = ZNormalize(template.Select(v => (double)v).ToList());
        var band = Math.Max(1, len * Math.Clamp(config.BandPercent, 1, 100) / 100);
        var closes = new double[series.Count];
        for (var i = 0; i < series.Count; i++) closes[i] = (double)series[i].Close;

        var candidates = new List<DtwMatch>();
        var window = new double[len];

        for (var start = 0; start + len <= series.Count; start++)
        {
            Array.Copy(closes, start, window, 0, len);
            var normalized = ZNormalize(window);

            // Pruning: se il limite inferiore già sfora, il DTW vero non può che sforare di più.
            if (LowerBound(query, normalized, band) > config.MaxDistance) continue;

            var distance = Distance(query, normalized, band);
            if (distance > config.MaxDistance) continue;

            var end = start + len - 1;
            candidates.Add(new DtwMatch(
                start, end, series[start].TimestampUtc, series[end].TimestampUtc, distance));
        }

        return SelectNonOverlapping(candidates, config.MinSeparationBars > 0 ? config.MinSeparationBars : len, config.MaxMatches);
    }

    /// <summary>
    /// Sceglie le occorrenze migliori per distanza scartando quelle troppo vicine a una già scelta.
    /// Greedy sulla distanza: fra due occorrenze sovrapposte si tiene la più somigliante, non la
    /// prima in ordine di tempo.
    /// </summary>
    private static List<DtwMatch> SelectNonOverlapping(List<DtwMatch> candidates, int minSeparation, int maxMatches)
    {
        var chosen = new List<DtwMatch>();
        foreach (var c in candidates.OrderBy(c => c.Distance).ThenBy(c => c.StartIndex))
        {
            if (chosen.Count >= maxMatches) break;
            var clashes = false;
            foreach (var s in chosen)
            {
                if (Math.Abs(c.StartIndex - s.StartIndex) < minSeparation) { clashes = true; break; }
            }
            if (!clashes) chosen.Add(c);
        }
        return chosen.OrderBy(c => c.StartIndex).ToList();
    }

    public IReadOnlyList<bool> ToEventSeries(int seriesLength, IReadOnlyList<DtwMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var events = new bool[Math.Max(0, seriesLength)];
        foreach (var m in matches)
        {
            // L'evento vive sulla barra di CHIUSURA del pattern: prima di quella il pattern non è
            // ancora completo, e segnarlo all'inizio sarebbe look-ahead bello e buono.
            if (m.EndIndex >= 0 && m.EndIndex < events.Length) events[m.EndIndex] = true;
        }
        return events;
    }
}
