namespace ProcioneMGR.Services.Optimization.Bayesian;

/// <summary>Esito di una ricerca bayesiana: il punto migliore, il suo punteggio e lo storico completo.</summary>
public sealed record BayesianSearchResult(double[] BestParameters, double BestScore, IReadOnlyList<EvaluatedPoint> History);

/// <summary>
/// Driver "ask-tell" della ricerca bayesiana: campiona alcuni punti iniziali a caso, poi chiede
/// ripetutamente all'<see cref="IHyperparameterOptimizer"/> il prossimo punto e valuta l'obiettivo,
/// finché non esaurisce le iterazioni. L'obiettivo è un delegate qualunque, così può avvolgere un
/// backtest walk-forward il cui punteggio è il <b>Deflated Sharpe</b> (Fase 1) — l'ottimizzatore
/// resta agnostico. Deterministico a parità di seme (inizializzazione + ottimizzatore seedati).
/// </summary>
public sealed class BayesianSearch(IHyperparameterOptimizer optimizer)
{
    public async Task<BayesianSearchResult> MaximizeAsync(
        ParameterSpace space, Func<double[], Task<double>> objective, int iterations, int initialRandom = 5, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(objective);

        var rng = new Random(seed);
        var history = new List<EvaluatedPoint>();

        // Fase esplorativa: punti iniziali casuali (servono al GP per avere qualcosa su cui interpolare).
        for (var i = 0; i < Math.Max(1, initialRandom); i++)
        {
            var p = space.Denormalize(SampleUnit(rng, space.Dimensions.Count));
            history.Add(new EvaluatedPoint(p, await objective(p)));
        }

        // Fase guidata: ogni passo massimizza l'Expected Improvement sul surrogato aggiornato.
        for (var i = 0; i < Math.Max(0, iterations); i++)
        {
            var next = optimizer.SuggestNext(history, space);
            history.Add(new EvaluatedPoint(next, await objective(next)));
        }

        var best = history[0];
        foreach (var pt in history) if (pt.Score > best.Score) best = pt;
        return new BayesianSearchResult(best.Parameters, best.Score, history);
    }

    private static double[] SampleUnit(Random rng, int d)
    {
        var x = new double[d];
        for (var i = 0; i < d; i++) x[i] = rng.NextDouble();
        return x;
    }
}
