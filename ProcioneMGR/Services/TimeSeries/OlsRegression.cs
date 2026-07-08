using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Risultato di una regressione OLS: coefficienti, errori standard e residui.</summary>
internal sealed record OlsResult(Vector<double> Coefficients, Vector<double> StandardErrors, Vector<double> Residuals);

/// <summary>Minimi quadrati ordinari, riusati sia dal test di Engle-Granger che dall'ADF.</summary>
internal static class OlsRegression
{
    public static OlsResult Fit(Matrix<double> design, Vector<double> y)
    {
        var xtx = design.TransposeThisAndMultiply(design);

        // Diagonal loading (ridge): X'X può diventare quasi singolare quando i regressori sono
        // fortemente collineari — esattamente il caso d'uso di questo test (livelli di prezzo di
        // due asset cointegrati). Senza regolarizzazione, Solve()/Inverse() su una matrice quasi
        // singolare possono restituire coefficienti numericamente instabili senza sollevare un
        // errore. Il ridge è scalato sulla diagonale media ed è trascurabile su matrici ben
        // condizionate.
        var ridge = Math.Max(xtx.Diagonal().Average(), 1e-12) * 1e-9;
        var xtxRegularized = xtx + Matrix<double>.Build.DenseIdentity(xtx.RowCount) * ridge;

        var xty = design.TransposeThisAndMultiply(y);
        var beta = xtxRegularized.Solve(xty);

        var residuals = y - design * beta;
        var n = design.RowCount;
        var k = design.ColumnCount;
        var rss = residuals.DotProduct(residuals);
        var sigma2 = rss / Math.Max(n - k, 1);

        var xtxInverse = xtxRegularized.Inverse();
        var standardErrors = Vector<double>.Build.Dense(k, i => Math.Sqrt(Math.Max(xtxInverse[i, i] * sigma2, 0.0)));

        return new OlsResult(beta, standardErrors, residuals);
    }
}
