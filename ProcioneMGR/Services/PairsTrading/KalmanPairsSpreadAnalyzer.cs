using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// [C2 roadmap integrazione] Hedge ratio via <b>filtro di Kalman</b>: alternativa alla rolling OLS
/// di <see cref="RollingPairsSpreadAnalyzer"/>, da confrontare in A/B sullo stesso walk-forward
/// (la fase <c>pairs</c> di PlatformExpand) prima di qualunque adozione.
///
/// Modello stato-spazio standard di letteratura (Montana 2009; Chan 2013):
///   stato      θ_t = [α_t, β_t]ᵀ,   θ_t = θ_{t-1} + w_t,  Cov(w) = Q = (δ/(1−δ))·R·I₂
///   osservazione  log Y_t = α_t + β_t·log X_t + v_t,      Var(v) = R
/// β segue una passeggiata aleatoria: si aggiorna a OGNI barra senza parametro di finestra né di
/// ricalibrazione — è il motivo per cui la letteratura lo preferisce alla rolling OLS (β più
/// stabile, spread più stazionario). δ regola quanto in fretta β può muoversi: resta UN parametro,
/// ma adimensionale (Q è scalato su R, così δ non dipende dalla scala dei log-prezzi).
///
/// <b>Causalità</b>: lo spread emesso alla barra t è l'INNOVAZIONE e_t = log Y_t − (α+β·log X_t)
/// valutata sullo stato PREDETTO (cioè filtrato fino a t−1): usa solo informazione passata, come
/// la rolling OLS che stima su [t−W, t−1]. L'hedge ratio emesso a t è il β predetto, per lo stesso
/// motivo. Warm-up identico alla rolling OLS: null fino a <c>warmupWindow</c> barre, poi il filtro
/// parte inizializzato dall'OLS su quelle barre (θ₀ = coefficienti, R = varianza dei residui,
/// P₀ = diag(SE²) — l'approssimazione diagonale incide solo sulla velocità di convergenza iniziale).
///
/// Lo z-score è lo STESSO rolling causale della rolling OLS (<see cref="PairsSpreadSeries"/>):
/// l'A/B deve confrontare l'estimatore dell'hedge ratio, non la definizione di z-score.
/// </summary>
public sealed class KalmanPairsSpreadAnalyzer
{
    /// <summary>δ di default, dalla letteratura (Chan usa 1e-4): β si muove, ma lentamente.</summary>
    public const double DefaultDelta = 1e-4;

    public RollingPairsAnalysis Analyze(
        IReadOnlyList<decimal> seriesY,
        IReadOnlyList<decimal> seriesX,
        int warmupWindow,
        double delta,
        int zScoreLookback)
    {
        ArgumentNullException.ThrowIfNull(seriesY);
        ArgumentNullException.ThrowIfNull(seriesX);
        if (seriesY.Count != seriesX.Count)
        {
            throw new ArgumentException("Le due serie devono avere la stessa lunghezza (allineate per timestamp).", nameof(seriesX));
        }
        if (warmupWindow < 10) throw new ArgumentOutOfRangeException(nameof(warmupWindow), "Servono almeno 10 osservazioni per inizializzare il filtro.");
        if (delta is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(delta), "δ deve stare in (0, 1).");
        if (zScoreLookback < 3) throw new ArgumentOutOfRangeException(nameof(zScoreLookback));

        var n = seriesY.Count;
        var hedgeRatio = new double?[n];
        var spread = new double?[n];

        if (n > warmupWindow)
        {
            // Inizializzazione: OLS sul solo warm-up (stessa finestra del primo fit della rolling OLS).
            var count = warmupWindow;
            var y0 = Vector<double>.Build.Dense(count, k => Log(seriesY[k]));
            var design = Matrix<double>.Build.Dense(count, 2, (row, col) => col == 0 ? 1.0 : Log(seriesX[row]));
            var ols = OlsRegression.Fit(design, y0);

            var a = ols.Coefficients[0];
            var b = ols.Coefficients[1];
            var rss = ols.Residuals.DotProduct(ols.Residuals);
            var r = Math.Max(rss / Math.Max(count - 2, 1), 1e-12);

            // P₀ diagonale dalle SE dell'OLS; Q scalato su R perché δ resti adimensionale.
            double p00 = Math.Max(ols.StandardErrors[0] * ols.StandardErrors[0], 1e-12);
            double p01 = 0.0;
            double p11 = Math.Max(ols.StandardErrors[1] * ols.StandardErrors[1], 1e-12);
            var q = delta / (1.0 - delta) * r;

            for (var i = warmupWindow; i < n; i++)
            {
                // Predict (passeggiata aleatoria): θ invariato, P cresce di Q.
                p00 += q;
                p11 += q;

                var x = Log(seriesX[i]);
                var yObs = Log(seriesY[i]);

                // Valori EMESSI a t: quelli noti PRIMA di vedere y_t (stato predetto).
                var innovation = yObs - (a + b * x);
                hedgeRatio[i] = b;
                spread[i] = innovation;

                // Update: F = H·P·Hᵀ + R con H = [1, x].
                var ph0 = p00 + p01 * x;          // (P·Hᵀ)[0]
                var ph1 = p01 + p11 * x;          // (P·Hᵀ)[1]
                var f = ph0 + ph1 * x + r;        // H·P·Hᵀ + R
                if (f < 1e-12) f = 1e-12;

                var k0 = ph0 / f;
                var k1 = ph1 / f;
                a += k0 * innovation;
                b += k1 * innovation;

                // P ← (I − K·H)·P, forma esplicita 2×2 (H = [1, x]).
                var newP00 = p00 - k0 * ph0;
                var newP01 = p01 - k0 * ph1;
                var newP11 = p11 - k1 * ph1;
                p00 = Math.Max(newP00, 1e-16);
                p01 = newP01;
                p11 = Math.Max(newP11, 1e-16);
            }
        }

        var zScore = PairsSpreadSeries.CausalZScore(spread, zScoreLookback);
        return new RollingPairsAnalysis { HedgeRatio = hedgeRatio, Spread = spread, ZScore = zScore };
    }

    /// <summary>Log del prezzo. Un prezzo non positivo non esiste in un OHLCV sano: meglio fermarsi che propagare -Infinity.</summary>
    private static double Log(decimal price)
        => price > 0m
            ? Math.Log((double)price)
            : throw new ArgumentException($"Prezzo non positivo ({price}): il log-spread richiede prezzi strettamente positivi.");
}
