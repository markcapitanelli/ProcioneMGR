using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Elementi di analisi ciclica (Trombetta, cap. 5): Activity Factor (volumi medi per ora),
/// bias orario dei prezzi (body medio per ora + robustezza statistica), bias sul giorno
/// della settimana (contributo intraday e overnight) e stagionalita' per giorno dell'anno.
///
/// Metodo del libro: ripetere ogni analisi su piu' periodi (lungo/medio/breve) e dare piu'
/// peso ai periodi lunghi (ComboStats con pesi 3-2-1); un bias e' affidabile solo se il
/// valor medio e' confermato dalla percentuale di occorrenze concordi (percPosNeg &gt; 50%).
/// Qui ogni metodo lavora su UNA serie: il chiamante la ripete su slice temporali diverse
/// e combina i risultati con <see cref="CombineHourlyBias"/>.
/// </summary>
public sealed class CyclicalAnalyzer
{
    /// <summary>
    /// Activity Factor: volume medio scambiato per ciascuna ora del giorno (0-23, UTC).
    /// <c>NormalizedMax</c> divide per il massimo della serie (max = 1) per confronti
    /// tra strumenti o periodi con volumi di ordini di grandezza diversi.
    /// </summary>
    public IReadOnlyList<HourlyActivity> ActivityFactor(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var sum = new decimal[24];
        var count = new int[24];
        foreach (var c in candles)
        {
            var h = c.TimestampUtc.Hour;
            sum[h] += c.Volume;
            count[h]++;
        }

        var avg = new decimal[24];
        decimal max = 0m;
        for (var h = 0; h < 24; h++)
        {
            avg[h] = count[h] == 0 ? 0m : sum[h] / count[h];
            if (avg[h] > max) max = avg[h];
        }

        var result = new List<HourlyActivity>(24);
        for (var h = 0; h < 24; h++)
        {
            result.Add(new HourlyActivity(h, count[h], avg[h], max == 0m ? 0m : avg[h] / max));
        }
        return result;
    }

    /// <summary>
    /// Bias orario dei prezzi: body medio (close-open) per ciascuna ora del giorno, con la
    /// percentuale di occorrenze concordi col segno della media (percPosNeg del libro) e la
    /// versione normalizzata max/min (+1 = miglior ora, -1 = peggior ora, 0 invariato).
    /// Da usare su serie orarie (1h).
    /// </summary>
    public IReadOnlyList<HourlyBias> HourlyPriceBias(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var sums = new decimal[24];
        var counts = new int[24];
        var positives = new int[24];
        var negatives = new int[24];

        foreach (var c in candles)
        {
            var h = c.TimestampUtc.Hour;
            var body = c.Close - c.Open;
            sums[h] += body;
            counts[h]++;
            if (body > 0m) positives[h]++;
            else if (body < 0m) negatives[h]++;
        }

        var avg = new decimal[24];
        decimal maxPos = 0m, minNeg = 0m;
        for (var h = 0; h < 24; h++)
        {
            avg[h] = counts[h] == 0 ? 0m : sums[h] / counts[h];
            if (avg[h] > maxPos) maxPos = avg[h];
            if (avg[h] < minNeg) minNeg = avg[h];
        }

        var result = new List<HourlyBias>(24);
        for (var h = 0; h < 24; h++)
        {
            // normalizeMaxMin: positivi / max positivo, negativi / |min negativo|.
            var normalized = avg[h] > 0m && maxPos > 0m ? avg[h] / maxPos
                           : avg[h] < 0m && minNeg < 0m ? -(avg[h] / minNeg)
                           : 0m;

            // percPosNeg: % dei campioni CONCORDI con il segno della media oraria.
            var concordant = counts[h] == 0 ? 0m
                : avg[h] >= 0m
                    ? (decimal)positives[h] / counts[h] * 100m
                    : (decimal)negatives[h] / counts[h] * 100m;

            result.Add(new HourlyBias(h, counts[h], avg[h], normalized, concordant));
        }
        return result;
    }

    /// <summary>
    /// ComboStats del libro: media pesata della robustezza percentuale del bias orario su
    /// piu' periodi (tipicamente lungo/medio/breve con pesi 3-2-1). Le serie devono essere
    /// output di <see cref="HourlyPriceBias"/>; pesi e serie allineati per indice.
    /// </summary>
    public IReadOnlyList<HourlyComboStat> CombineHourlyBias(
        IReadOnlyList<IReadOnlyList<HourlyBias>> periods,
        IReadOnlyList<decimal>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(periods);
        if (periods.Count == 0) throw new ArgumentException("Serve almeno un periodo.", nameof(periods));

        weights ??= periods.Count switch
        {
            1 => [1m],
            2 => [2m, 1m],
            _ => [.. Enumerable.Range(0, periods.Count).Select(i => (decimal)(periods.Count - i))],
        };
        if (weights.Count != periods.Count)
        {
            throw new ArgumentException("Numero di pesi diverso dal numero di periodi.", nameof(weights));
        }

        var totalWeight = weights.Sum();
        var result = new List<HourlyComboStat>(24);
        for (var h = 0; h < 24; h++)
        {
            decimal combo = 0m;
            for (var p = 0; p < periods.Count; p++)
            {
                combo += periods[p][h].ConcordantPercent * weights[p];
            }
            result.Add(new HourlyComboStat(h, totalWeight == 0m ? 0m : combo / totalWeight));
        }
        return result;
    }

    /// <summary>
    /// Bias sul giorno della settimana su serie DAILY: contributo medio intraday
    /// ((close-open)/open) e overnight ((open-close[-1])/close[-1]), ciascuno con la
    /// percentuale di occorrenze concordi col segno della media.
    /// </summary>
    public IReadOnlyList<DayOfWeekBias> DayOfWeekBias(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var intraday = new List<decimal>[7];
        var overnight = new List<decimal>[7];
        for (var d = 0; d < 7; d++)
        {
            intraday[d] = [];
            overnight[d] = [];
        }

        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var dow = (int)c.TimestampUtc.DayOfWeek;
            if (c.Open > 0m)
            {
                intraday[dow].Add((c.Close - c.Open) / c.Open * 100m);
            }
            if (i > 0 && candles[i - 1].Close > 0m)
            {
                overnight[dow].Add((c.Open - candles[i - 1].Close) / candles[i - 1].Close * 100m);
            }
        }

        var result = new List<DayOfWeekBias>(7);
        for (var d = 0; d < 7; d++)
        {
            var (iAvg, iConc) = AvgAndConcordance(intraday[d]);
            var (oAvg, oConc) = AvgAndConcordance(overnight[d]);
            result.Add(new DayOfWeekBias((DayOfWeek)d, intraday[d].Count, iAvg, iConc, oAvg, oConc));
        }
        return result;
    }

    /// <summary>
    /// Stagionalita' per giorno dell'anno su serie DAILY: variazione % media close-su-close
    /// per ciascun giorno (1..366) e curva cumulata (la "equity" della stagionalita').
    /// </summary>
    public IReadOnlyList<SeasonalityPoint> Seasonality(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var sums = new decimal[367]; // indice 1..366
        var counts = new int[367];

        for (var i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1];
            if (prev.Close <= 0m) continue;
            var change = (candles[i].Close - prev.Close) / prev.Close * 100m;
            var doy = candles[i].TimestampUtc.DayOfYear;
            sums[doy] += change;
            counts[doy]++;
        }

        var result = new List<SeasonalityPoint>(366);
        decimal cumulative = 0m;
        for (var doy = 1; doy <= 366; doy++)
        {
            var avg = counts[doy] == 0 ? 0m : sums[doy] / counts[doy];
            cumulative += avg;
            result.Add(new SeasonalityPoint(doy, counts[doy], avg, cumulative));
        }
        return result;
    }

    /// <summary>
    /// Test di una finestra stagionale (cap. 5, PeriodStats): per ogni anno dello storico,
    /// somma le variazioni % close-su-close tra (startMonth, startDay) e (endMonth, endDay)
    /// e verifica la concordanza con la direzione ipotizzata. Restituisce l'esito per anno,
    /// la percentuale di anni concordi e la variazione media.
    /// </summary>
    /// <param name="candles">Serie daily.</param>
    /// <param name="isLong">true = ci si aspetta una finestra rialzista, false = ribassista.</param>
    public SeasonalWindowResult TestSeasonalWindow(
        IReadOnlyList<OhlcvData> candles,
        int startMonth, int startDay, int endMonth, int endDay, bool isLong)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (startMonth is < 1 or > 12 || endMonth is < 1 or > 12 || startDay is < 1 or > 31 || endDay is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(null, "Date della finestra stagionale non valide.");
        }

        // Somma per anno delle variazioni % la cui data cade nella finestra.
        var byYear = new SortedDictionary<int, decimal>();
        for (var i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1];
            if (prev.Close <= 0m) continue;
            var d = candles[i].TimestampUtc;
            if (!InWindow(d, startMonth, startDay, endMonth, endDay)) continue;

            var change = (candles[i].Close - prev.Close) / prev.Close * 100m;
            // Le finestre a cavallo di capodanno vengono attribuite all'anno di INIZIO.
            var windowYear = startMonth > endMonth && d.Month <= endMonth ? d.Year - 1 : d.Year;
            byYear[windowYear] = byYear.GetValueOrDefault(windowYear) + change;
        }

        var years = byYear.Select(kv => new SeasonalYearOutcome(
            kv.Key, kv.Value, isLong ? kv.Value > 0m : kv.Value < 0m)).ToList();

        var successCount = years.Count(y => y.IsSuccess);
        return new SeasonalWindowResult
        {
            Years = years,
            YearsTested = years.Count,
            SuccessPercent = years.Count == 0 ? 0m : (decimal)successCount / years.Count * 100m,
            AverageChangePercent = years.Count == 0 ? 0m : years.Sum(y => y.ChangePercent) / years.Count,
        };
    }

    private static bool InWindow(DateTime d, int startMonth, int startDay, int endMonth, int endDay)
    {
        var md = d.Month * 100 + d.Day;
        var start = startMonth * 100 + startDay;
        var end = endMonth * 100 + endDay;
        // Finestra normale (es. 1/1 -> 24/2) o a cavallo di capodanno (es. 15/12 -> 15/1).
        return start <= end ? md >= start && md <= end : md >= start || md <= end;
    }

    private static (decimal Avg, decimal ConcordantPercent) AvgAndConcordance(List<decimal> samples)
    {
        if (samples.Count == 0) return (0m, 0m);
        var avg = samples.Sum() / samples.Count;
        var concordant = avg >= 0m ? samples.Count(s => s > 0m) : samples.Count(s => s < 0m);
        return (avg, (decimal)concordant / samples.Count * 100m);
    }
}

/// <summary>Volume medio per ora del giorno (Activity Factor).</summary>
public sealed record HourlyActivity(int Hour, int Samples, decimal AverageVolume, decimal NormalizedMax);

/// <summary>Bias orario: body medio, versione normalizzata [-1,1] e % di occorrenze concordi.</summary>
public sealed record HourlyBias(int Hour, int Samples, decimal AverageBody, decimal Normalized, decimal ConcordantPercent);

/// <summary>Robustezza combinata multi-periodo del bias orario (ComboStats).</summary>
public sealed record HourlyComboStat(int Hour, decimal WeightedConcordantPercent);

/// <summary>Bias del giorno della settimana: contributi intraday e overnight con concordanza.</summary>
public sealed record DayOfWeekBias(
    DayOfWeek Day,
    int Samples,
    decimal IntradayAvgPercent,
    decimal IntradayConcordantPercent,
    decimal OvernightAvgPercent,
    decimal OvernightConcordantPercent);

/// <summary>Punto della curva di stagionalita' (giorno dell'anno 1..366).</summary>
public sealed record SeasonalityPoint(int DayOfYear, int Samples, decimal AvgChangePercent, decimal CumulativePercent);

/// <summary>Esito della finestra stagionale per un singolo anno.</summary>
public sealed record SeasonalYearOutcome(int Year, decimal ChangePercent, bool IsSuccess);

/// <summary>Esito complessivo del test di una finestra stagionale.</summary>
public sealed record SeasonalWindowResult
{
    public IReadOnlyList<SeasonalYearOutcome> Years { get; init; } = [];
    public int YearsTested { get; init; }
    public decimal SuccessPercent { get; init; }
    public decimal AverageChangePercent { get; init; }
}
