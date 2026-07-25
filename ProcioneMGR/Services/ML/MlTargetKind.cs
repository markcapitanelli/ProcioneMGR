using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// [1.V roadmap macchina-ricerca] Cosa predice il modello. Storicamente solo il rendimento forward;
/// dopo 445.280 combinazioni direzionali a zero sopravvissuti, la mossa onesta è predire il
/// RISCHIO: la volatilità è persistente (stesso fatto stilizzato dietro il GARCH già in piattaforma)
/// ed è prevedibile anche quando la direzione non lo è.
/// </summary>
public enum MlTargetKind
{
    /// <summary>Rendimento a orizzonte fisso (default storico, comportamento invariato).</summary>
    ForwardReturn,

    /// <summary>|rendimento| a orizzonte fisso: proxy semplice del rischio realizzato.</summary>
    ForwardAbsReturn,

    /// <summary>
    /// Deviazione standard dei rendimenti PER-BARRA dentro l'orizzonte forward: la volatilità
    /// realizzata che il vol-targeting e il LeverageAdvisor consumano. Richiede orizzonte ≥ 2.
    /// </summary>
    ForwardRealizedVol,
}

/// <summary>
/// Calcolo dei target forward. Un TARGET guarda avanti per costruzione (è l'etichetta): il
/// contratto anti-look-ahead riguarda le FEATURE, che a indice i vedono solo candles[0..i].
/// Il valore a i usa esclusivamente le barre (i, i+orizzonte] — mai oltre l'orizzonte dichiarato.
/// </summary>
public static class ForwardTargets
{
    public static IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, int horizon, MlTargetKind kind)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (horizon < 1) throw new ArgumentOutOfRangeException(nameof(horizon));

        switch (kind)
        {
            case MlTargetKind.ForwardReturn:
                return new FactorEvaluator().ForwardReturns(candles, horizon);

            case MlTargetKind.ForwardAbsReturn:
            {
                var fwd = new FactorEvaluator().ForwardReturns(candles, horizon);
                return fwd.Select(v => v.HasValue ? Math.Abs(v.Value) : (decimal?)null).ToArray();
            }

            case MlTargetKind.ForwardRealizedVol:
            {
                if (horizon < 2)
                    throw new ArgumentOutOfRangeException(nameof(horizon),
                        "ForwardRealizedVol richiede orizzonte >= 2: con una sola barra non esiste una deviazione standard.");

                var n = candles.Count;
                var result = new decimal?[n];
                for (var i = 0; i + horizon < n; i++)
                {
                    // Rendimenti per-barra dentro (i, i+horizon]: r_j = close[j]/close[j-1] - 1.
                    var rets = new double[horizon];
                    var valid = true;
                    for (var j = 1; j <= horizon; j++)
                    {
                        var prev = candles[i + j - 1].Close;
                        if (prev <= 0m) { valid = false; break; }
                        rets[j - 1] = (double)(candles[i + j].Close / prev - 1m);
                    }
                    if (!valid) continue;

                    var mean = rets.Average();
                    var variance = rets.Sum(r => (r - mean) * (r - mean)) / (horizon - 1);
                    result[i] = (decimal)Math.Sqrt(variance);
                }
                return result;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
