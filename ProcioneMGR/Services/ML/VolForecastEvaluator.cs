using ProcioneMGR.Data;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Esito del confronto out-of-sample fra la previsione di volatilità del modello e le due baseline
/// senza ML: EWMA (RiskMetrics, λ=0,94) e naive (la vol realizzata delle ULTIME barre proiettata in
/// avanti). QLIKE è la metrica principale — è la loss robusta standard per le previsioni di varianza
/// (penalizza le sottostime, che per il risk management sono l'errore costoso); l'MSE sulla vol è il
/// contorno intuitivo. Il verdetto onesto: se il modello non batte l'EWMA, il vol-targeting deve
/// continuare a usare la misura semplice.
/// </summary>
public sealed record VolForecastEvaluation(
    int Rows,
    double ModelQlike, double EwmaQlike, double NaiveQlike,
    double ModelMse, double EwmaMse, double NaiveMse)
{
    public bool ModelBeatsEwma => ModelQlike < EwmaQlike;
    public bool ModelBeatsNaive => ModelQlike < NaiveQlike;
}

/// <summary>
/// [1.V fase 2] Calcoli puri per la valutazione delle previsioni di volatilità. Tutte le serie sono
/// CAUSALI: il valore all'indice i usa solo informazione fino a i incluso (le previsioni si
/// confrontano poi col target forward della stessa barra, che guarda avanti per costruzione).
/// </summary>
public static class VolForecastEvaluator
{
    /// <summary>Pavimento per le previsioni non positive (un modello lineare può produrre vol negative).</summary>
    public const double MinForecast = 1e-6;

    /// <summary>
    /// Vol PER-BARRA prevista dall'EWMA RiskMetrics all'indice i, usando i rendimenti fino a i
    /// incluso: σ²_i = λ·σ²_{i-1} + (1−λ)·r_i². Seed = media dei quadrati dei primi
    /// min(20, n) rendimenti (null prima che esista almeno un rendimento).
    /// </summary>
    public static double?[] EwmaPerBarVol(IReadOnlyList<OhlcvData> candles, double lambda = 0.94)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var n = candles.Count;
        var result = new double?[n];
        if (n < 2) return result;

        var returns = new double?[n]; // r_i = close[i]/close[i-1] - 1
        for (var i = 1; i < n; i++)
        {
            var prev = candles[i - 1].Close;
            if (prev > 0m) returns[i] = (double)(candles[i].Close / prev - 1m);
        }

        // Seed della varianza: media dei quadrati dei primi rendimenti disponibili (max 20).
        var seedSquares = new List<double>();
        for (var i = 1; i < n && seedSquares.Count < 20; i++)
        {
            if (returns[i] is { } r) seedSquares.Add(r * r);
        }
        if (seedSquares.Count == 0) return result;

        var variance = seedSquares.Average();
        for (var i = 1; i < n; i++)
        {
            if (returns[i] is { } r)
            {
                variance = lambda * variance + (1 - lambda) * r * r;
            }
            result[i] = Math.Sqrt(variance);
        }
        return result;
    }

    /// <summary>
    /// Baseline naive: la vol realizzata (per-barra, campionaria) delle ULTIME <paramref name="horizon"/>
    /// barre fino a i incluso — "domani come ieri". Null finché la storia non basta.
    /// </summary>
    public static double?[] PastRealizedVol(IReadOnlyList<OhlcvData> candles, int horizon)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizon, 2);

        var n = candles.Count;
        var result = new double?[n];
        for (var i = horizon; i < n; i++)
        {
            var rets = new List<double>(horizon);
            for (var j = i - horizon + 1; j <= i; j++)
            {
                var prev = candles[j - 1].Close;
                if (prev > 0m) rets.Add((double)(candles[j].Close / prev - 1m));
            }
            if (rets.Count < 2) continue;
            var mean = rets.Average();
            var variance = rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1);
            result[i] = Math.Sqrt(variance);
        }
        return result;
    }

    /// <summary>
    /// QLIKE su scala varianza per una coppia (previsione, realizzato) in vol:
    /// L = σ²/h − ln(σ²/h) − 1, con h = pred² (pavimentata a <see cref="MinForecast"/>).
    /// Zero per previsione perfetta, sempre ≥ 0.
    /// </summary>
    public static double Qlike(double predictedVol, double actualVol)
    {
        var h = Math.Max(predictedVol, MinForecast);
        var ratio = (actualVol * actualVol) / (h * h);
        // actual = 0 (candele piatte): il rapporto è 0 e ln(0) diverge — si tratta a monte
        // scartando le righe con realizzato nullo (Score). Qui il clamp difende solo la previsione.
        return ratio - Math.Log(ratio) - 1;
    }

    /// <summary>
    /// Aggrega QLIKE medio e MSE (su scala vol) sulle coppie valide: entrambe le serie non-null e
    /// realizzato &gt; 0 (con vol realizzata nulla il QLIKE diverge e la barra non informa).
    /// </summary>
    public static (double Qlike, double Mse, int Rows) Score(IReadOnlyList<double?> predicted, IReadOnlyList<double?> actual)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(actual);
        if (predicted.Count != actual.Count)
            throw new ArgumentException("Le serie previsione/realizzato devono essere allineate per indice.");

        double qlikeSum = 0, mseSum = 0;
        var rows = 0;
        for (var i = 0; i < predicted.Count; i++)
        {
            if (predicted[i] is not { } p || actual[i] is not { } a || a <= 0) continue;
            qlikeSum += Qlike(p, a);
            var err = Math.Max(p, MinForecast) - a;
            mseSum += err * err;
            rows++;
        }
        return rows == 0 ? (0, 0, 0) : (qlikeSum / rows, mseSum / rows, rows);
    }
}
