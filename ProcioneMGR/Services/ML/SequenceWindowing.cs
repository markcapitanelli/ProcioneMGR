namespace ProcioneMGR.Services.ML;

/// <summary>
/// Trasforma un dataset puntuale (una riga = i fattori di UNA candela, in ordine temporale) in un
/// dataset a SEQUENZE per i modelli che ragionano su una finestra (es.
/// <see cref="AttentionReturnPredictor"/>): la riga i diventa la concatenazione dei vettori di
/// fattori delle candele [i−T+1 .. i], con label quella della candela i. Layout appiattito in ordine
/// temporale (dal più vecchio al più recente), coerente con ciò che l'attention si aspetta.
///
/// I fattori restano gli stessi (nessun cambiamento a <c>DatasetBuilder</c> o al FactorsJson salvato):
/// la finestra è una vista sulle stesse feature, non nuove feature.
///
/// CONTIGUITÀ TEMPORALE: si emette una finestra SOLO se le T candele sono contigue nel tempo
/// (spaziatura uniforme, nessuna lacuna). Questo rende il windowing di training identico a quello
/// che <c>MlStrategy</c> costruisce a inferenza — se il dataset compattato salta candele (fattori
/// non calcolabili a metà serie), la finestra a cavallo del salto viene esclusa da ENTRAMBI i lati,
/// evitando una divergenza silenziosa fra train e inference.
/// </summary>
public static class SequenceWindowing
{
    public static MlDataset Build(MlDataset pointwise, int windowLength)
    {
        ArgumentNullException.ThrowIfNull(pointwise);
        if (windowLength < 1) throw new ArgumentOutOfRangeException(nameof(windowLength));

        var f = pointwise.FeatureCount;
        var rows = new List<FeatureRow>();
        var timestamps = new List<DateTime>();

        var stepTicks = InferStepTicks(pointwise.Timestamps);

        for (var i = windowLength - 1; i < pointwise.RowCount; i++)
        {
            if (!IsContiguous(pointwise.Timestamps, i - windowLength + 1, i, stepTicks)) continue;

            var flat = new float[windowLength * f];
            for (var t = 0; t < windowLength; t++)
            {
                var src = pointwise.Rows[i - windowLength + 1 + t].Features;
                Array.Copy(src, 0, flat, t * f, f);
            }
            rows.Add(new FeatureRow { Features = flat, Label = pointwise.Rows[i].Label });
            timestamps.Add(pointwise.Timestamps[i]);
        }

        // Nomi espansi: "<fattore>@t-<lag>" (lag 0 = candela corrente).
        var names = new List<string>(windowLength * f);
        for (var t = 0; t < windowLength; t++)
        {
            var lag = windowLength - 1 - t;
            foreach (var name in pointwise.FeatureNames) names.Add($"{name}@t-{lag}");
        }

        return new MlDataset { Rows = rows, FeatureNames = names, Timestamps = timestamps };
    }

    /// <summary>
    /// Passo temporale della serie = minima differenza positiva fra timestamp consecutivi (il
    /// timeframe, quando le candele sono sulla griglia). <c>0</c> se indeterminabile.
    /// </summary>
    public static long InferStepTicks(IReadOnlyList<DateTime> timestamps)
    {
        long step = long.MaxValue;
        for (var k = 1; k < timestamps.Count; k++)
        {
            var d = (timestamps[k] - timestamps[k - 1]).Ticks;
            if (d > 0 && d < step) step = d;
        }
        return step == long.MaxValue ? 0 : step;
    }

    /// <summary>
    /// True se i timestamp negli indici [<paramref name="start"/>..<paramref name="end"/>] sono
    /// contigui, cioè ogni coppia consecutiva dista esattamente <paramref name="stepTicks"/>.
    /// </summary>
    public static bool IsContiguous(IReadOnlyList<DateTime> timestamps, int start, int end, long stepTicks)
    {
        if (stepTicks <= 0) return true; // passo indeterminato: nessun vincolo
        for (var t = start + 1; t <= end; t++)
            if ((timestamps[t] - timestamps[t - 1]).Ticks != stepTicks) return false;
        return true;
    }
}
