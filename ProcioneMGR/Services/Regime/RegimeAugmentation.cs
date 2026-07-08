using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Arricchimento del vettore di feature con il REGIME di mercato corrente, codificato one-hot
/// (follow-up "regime nel meta-learner dello stacking"). Il regime diventa K colonne one-hot
/// APPESE al vettore di fattori esistente: nessuna modifica a <c>IReturnPredictor</c> né a
/// <c>StackedReturnPredictor.Predict</c> (che si adattano alla dimensione del vettore), solo un
/// vettore più largo — così ogni modello (base o stacking) può condizionare la predizione sul
/// regime.
///
/// PARITÀ TRAIN/SERVE: l'etichetta di regime è calcolata con l'UNICO percorso causale già
/// esistente — <see cref="IMarketFeatureExtractor.ComputeFeatures"/> (feature anti-look-ahead in
/// memoria) seguito da <see cref="IRegimeDetector.LabelFeaturesAsync"/> (smoothing a finestra
/// passata). Lo stesso metodo è usato sia in costruzione dataset (train) sia in
/// <c>MlStrategy</c> (serve): sulla stessa serie producono le stesse etichette, quindi niente
/// train/serve skew. NON si usa <c>PredictRegimeAsync</c> (nearest-centroid grezzo, senza
/// smoothing): darebbe un'etichetta diversa da quella vista in addestramento.
/// </summary>
public static class RegimeAugmentation
{
    /// <summary>
    /// Appende <paramref name="regimeCount"/> colonne one-hot a <paramref name="baseVec"/> per il
    /// regime <paramref name="regimeId"/>. Regime fuori range o sconosciuto (&lt;0, es. warm-up) →
    /// tutte zero: encoding neutro, mai una colonna sbagliata accesa. Se
    /// <paramref name="regimeCount"/> ≤ 0 restituisce il vettore invariato (feature disattivata).
    /// </summary>
    public static float[] Append(float[] baseVec, int regimeId, int regimeCount)
    {
        if (regimeCount <= 0 || baseVec.Length == 0) return baseVec;
        var outv = new float[baseVec.Length + regimeCount];
        Array.Copy(baseVec, outv, baseVec.Length);
        if (regimeId >= 0 && regimeId < regimeCount) outv[baseVec.Length + regimeId] = 1f;
        return outv;
    }

    /// <summary>Nomi delle colonne one-hot (allineati all'ordine di <see cref="Append"/>).</summary>
    public static IReadOnlyList<string> OneHotNames(int regimeCount) =>
        Enumerable.Range(0, Math.Max(0, regimeCount)).Select(r => $"Regime_{r}").ToList();

    /// <summary>
    /// Regime causale per ogni candela, allineato per INDICE alla lista <paramref name="candles"/>
    /// (−1 dove non c'è feature: warm-up iniziale). Percorso identico per train e serve, così
    /// l'etichetta della candela i è la stessa dai due lati sulla stessa serie. Le
    /// <see cref="MarketFeatures"/> partono dopo il warm-up dell'extractor: il rimappaggio a
    /// indice di candela è per timestamp, robusto a qualunque offset.
    /// </summary>
    public static async Task<int[]> LabelByCandleAsync(
        IMarketFeatureExtractor extractor,
        IRegimeDetector detector,
        IReadOnlyList<OhlcvData> candles,
        string timeframe,
        CancellationToken ct = default)
    {
        var ids = new int[candles.Count];
        Array.Fill(ids, -1);
        if (candles.Count == 0) return ids;

        var features = extractor.ComputeFeatures(candles, timeframe, ct);
        if (features.Count == 0) return ids;

        var labeled = await detector.LabelFeaturesAsync(features, ct);

        var tsToIndex = new Dictionary<DateTime, int>(candles.Count);
        for (var i = 0; i < candles.Count; i++)
            tsToIndex[DateTime.SpecifyKind(candles[i].TimestampUtc, DateTimeKind.Utc)] = i;

        foreach (var f in labeled)
        {
            if (f.RegimeId is int r && tsToIndex.TryGetValue(DateTime.SpecifyKind(f.Timestamp, DateTimeKind.Utc), out var idx))
                ids[idx] = r;
        }
        return ids;
    }
}
