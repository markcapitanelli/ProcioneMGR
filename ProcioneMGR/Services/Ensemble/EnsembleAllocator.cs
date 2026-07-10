namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Calcola i pesi di allocazione a partire dagli Sharpe rolling, con vincoli Min/Max.
///
/// Algoritmo (water-filling vincolato):
///  1. Sharpe negativi -> 0 (non contribuiscono al peso).
///  2. Pesi grezzi proporzionali agli Sharpe (se tutti 0 -> equipesi).
///  3. Si fissa al proprio bound IL SINGOLO peso che viola di più (Max o Min),
///     si ridistribuisce il budget rimanente tra i restanti, e si ripete.
///  4. Garantisce somma = 1 e rispetto dei vincoli (quando geometricamente possibile).
///
/// NB: l'esempio nello spec (EMA 40%, RSI 45%, MACD 15%) viola il proprio Max (45% > 40%);
/// qui i vincoli sono rispettati: con Max=40% si ottiene es. 40/40/20 (il "leftover" va a
/// chi può ancora assorbirlo), che somma a 100% senza superare i limiti.
/// </summary>
public static class EnsembleAllocator
{
    private const decimal Eps = 0.0000001m;

    /// <summary>
    /// Riduce il rumore delle stime di Sharpe tirandole verso la media trasversale (equipeso)
    /// PRIMA dell'allocazione. Le stime di Sharpe hanno varianza alta; su dati OOS l'equipeso
    /// batte spesso il puro Sharpe-weighting, quindi conviene "credere solo in parte" agli scarti
    /// dalla media. Da usare a monte di <see cref="ComputeWeights"/>.
    ///
    ///  shrinkage = 0  → nessuna modifica (puro Sharpe-weighting, comportamento storico).
    ///  shrinkage = 1  → tutti gli Sharpe uguali alla media → allocazione equipesata.
    ///  intermedi      → interpolazione lineare: s' = mean + (1 − shrinkage)·(s − mean).
    ///
    /// Se <paramref name="observationCounts"/> è fornito, una gamba con meno di
    /// <paramref name="minObservations"/> osservazioni ha uno Sharpe "non affidabile" e viene
    /// portata interamente alla media (equipeso), a prescindere da <paramref name="shrinkage"/>.
    /// </summary>
    public static decimal[] ShrinkSharpes(
        IReadOnlyList<decimal> sharpes,
        decimal shrinkage,
        IReadOnlyList<int>? observationCounts = null,
        int minObservations = 0)
    {
        var n = sharpes.Count;
        var result = new decimal[n];
        if (n == 0)
        {
            return result;
        }

        shrinkage = Math.Clamp(shrinkage, 0m, 1m);
        var keep = 1m - shrinkage;
        var mean = sharpes.Average();

        for (var i = 0; i < n; i++)
        {
            var untrusted = observationCounts is not null
                && i < observationCounts.Count
                && observationCounts[i] < minObservations;
            result[i] = untrusted
                ? mean                                   // troppo pochi dati: non fidarsi, equipeso
                : mean + keep * (sharpes[i] - mean);     // shrinkage verso la media
        }
        return result;
    }

    /// <summary>
    /// Restituisce i pesi (frazioni, somma 1) allineati per indice agli Sharpe in input.
    /// </summary>
    public static decimal[] ComputeWeights(IReadOnlyList<decimal> sharpes, decimal minFraction, decimal maxFraction)
    {
        var n = sharpes.Count;
        var result = new decimal[n];
        if (n == 0)
        {
            return result;
        }
        if (n == 1)
        {
            result[0] = 1m; // unica strategia: prende tutto (il Max serve a diversificare tra più strategie)
            return result;
        }

        // Vincoli geometricamente fattibili.
        var min = Math.Clamp(minFraction, 0m, 1m / n);          // se min*n > 1 non è soddisfacibile
        var max = Math.Clamp(maxFraction, 1m / n, 1m);          // se max*n < 1 non si raggiunge il 100%

        // Sharpe negativi azzerati.
        var scores = sharpes.Select(s => s > 0m ? s : 0m).ToArray();

        var isFixed = new bool[n];
        var remaining = 1m;

        for (var guard = 0; guard < n + 2; guard++)
        {
            var activeIdx = Enumerable.Range(0, n).Where(i => !isFixed[i]).ToList();
            if (activeIdx.Count == 0)
            {
                break;
            }

            var activeScore = activeIdx.Sum(i => scores[i]);

            // Pesi proporzionali correnti per gli attivi.
            decimal Weight(int i) => activeScore > 0m
                ? remaining * scores[i] / activeScore
                : remaining / activeIdx.Count;

            // Trova il peggior violatore (overflow su Max o underflow su Min).
            var worst = -1;
            var worstSeverity = 0m;
            decimal worstBound = 0m;
            foreach (var i in activeIdx)
            {
                var w = Weight(i);
                if (w > max + Eps)
                {
                    var sev = w - max;
                    if (sev > worstSeverity) { worstSeverity = sev; worst = i; worstBound = max; }
                }
                else if (w < min - Eps)
                {
                    var sev = min - w;
                    if (sev > worstSeverity) { worstSeverity = sev; worst = i; worstBound = min; }
                }
            }

            if (worst < 0)
            {
                // Nessuna violazione: assegna i pesi proporzionali agli attivi e termina.
                foreach (var i in activeIdx)
                {
                    result[i] = Weight(i);
                }
                break;
            }

            // Fissa il peggiore al suo bound e riduci il budget.
            result[worst] = worstBound;
            isFixed[worst] = true;
            remaining -= worstBound;
        }

        // Normalizzazione di sicurezza (per residui numerici o casi tutti-fissati).
        var sum = result.Sum();
        if (sum > Eps && Math.Abs(sum - 1m) > Eps)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = result[i] / sum;
            }
        }
        return result;
    }
}
