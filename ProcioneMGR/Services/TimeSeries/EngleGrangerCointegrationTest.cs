using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Implementazione di <see cref="ICointegrationTest"/> (Engle-Granger a due passi + ADF sui residui).</summary>
public sealed class EngleGrangerCointegrationTest : ICointegrationTest
{
    private const int AdfLags = 1;

    /// <summary>
    /// Valori critici approssimati (asintotici) del test ADF con costante, NON i valori
    /// MacKinnon corretti per piccolo campione specifici della cointegrazione (più negativi
    /// dei valori ADF standard) — semplificazione dichiarata: sufficiente per uno screening
    /// indicativo delle coppie, non per un giudizio statistico rigoroso in produzione.
    /// </summary>
    private const double CriticalValue5Percent = -2.86;

    public CointegrationResult Test(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX)
    {
        ArgumentNullException.ThrowIfNull(seriesY);
        ArgumentNullException.ThrowIfNull(seriesX);
        if (seriesY.Count != seriesX.Count)
        {
            throw new ArgumentException("Le due serie devono avere la stessa lunghezza (allineate per timestamp).", nameof(seriesX));
        }
        var n = seriesY.Count;
        if (n < 30)
        {
            throw new ArgumentException("Servono almeno 30 osservazioni per un test di cointegrazione affidabile.", nameof(seriesY));
        }

        var y = Vector<double>.Build.Dense(n, i => (double)seriesY[i]);
        var x = Vector<double>.Build.Dense(n, i => (double)seriesX[i]);

        // Passo 1: regressione Y = alpha + beta*X + spread (stima dell'hedge ratio).
        var design = Matrix<double>.Build.Dense(n, 2, (row, col) => col == 0 ? 1.0 : x[row]);
        var ols = OlsRegression.Fit(design, y);
        var intercept = ols.Coefficients[0];
        var hedgeRatio = ols.Coefficients[1];
        var spread = ols.Residuals;

        // Passo 2: ADF sui residui -> stazionari? (radice unitaria rifiutata)
        var adfStatistic = AugmentedDickeyFuller(spread, AdfLags);

        return new CointegrationResult
        {
            HedgeRatio = hedgeRatio,
            Intercept = intercept,
            Spread = spread.ToArray(),
            AdfStatistic = adfStatistic,
            IsCointegrated = adfStatistic < CriticalValue5Percent,
        };
    }

    /// <summary>
    /// Test ADF (Augmented Dickey-Fuller) con costante: regressione
    ///   Δyₜ = c + γ·yₜ₋₁ + Σᵢ φᵢ·Δyₜ₋ᵢ + εₜ
    /// La statistica del test è t = γ̂ / SE(γ̂): più negativa -> più forte il rifiuto della
    /// radice unitaria (γ=0), cioè più forte l'evidenza di stazionarietà.
    /// </summary>
    internal static double AugmentedDickeyFuller(Vector<double> series, int lags)
    {
        var n = series.Count;
        var diff = Vector<double>.Build.Dense(n - 1, i => series[i + 1] - series[i]);

        var rows = n - lags - 1;
        if (rows < 10)
        {
            throw new ArgumentException("Serie troppo corta per l'ADF con il numero di lag richiesto.", nameof(series));
        }

        var design = Matrix<double>.Build.Dense(rows, 2 + lags);
        var target = Vector<double>.Build.Dense(rows);

        for (var r = 0; r < rows; r++)
        {
            var t = lags + 1 + r; // t varia da lags+1 a n-1
            target[r] = diff[t - 1];              // Δyₜ
            design[r, 0] = 1.0;                   // costante
            design[r, 1] = series[t - 1];         // yₜ₋₁ (livello)
            for (var i = 1; i <= lags; i++)
            {
                design[r, 1 + i] = diff[t - 1 - i]; // Δyₜ₋ᵢ
            }
        }

        var ols = OlsRegression.Fit(design, target);
        var gamma = ols.Coefficients[1];
        var seGamma = ols.StandardErrors[1];
        return seGamma > 1e-15 ? gamma / seGamma : 0.0;
    }
}
