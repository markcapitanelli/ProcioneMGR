using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Implementazione di <see cref="ICointegrationTest"/> (Engle-Granger a due passi + ADF sui residui).
///
/// P0-1: i valori critici sono quelli di <b>MacKinnon (2010) specifici per la COINTEGRAZIONE</b>
/// (residuo di una regressione STIMATA), non i valori ADF standard (−2.86 al 5%). Questi ultimi sono
/// troppo permissivi per uno spread stimato: accetterebbero troppe coppie NON cointegrate (falsi
/// positivi → divergenze → perdite nel pairs trading). Inoltre il numero di lag dell'ADF è scelto per
/// <b>AIC</b> (non più fisso a 1), su un campione comune così che i modelli siano confrontabili.
///
/// <para><b>Perché sui LOG dei prezzi.</b> Sui prezzi grezzi β ha le unità di "prezzo di Y per
/// prezzo di X", quindi il suo ordine di grandezza dice soprattutto quanto costa una moneta rispetto
/// all'altra: AAVE/XLM è stata accettata con β = 575 solo perché AAVE vale ~1000× XLM. Un tetto su
/// |β| grezzo sarebbe quindi arbitrario — boccerebbe coppie sane fra monete di prezzo diverso e
/// lascerebbe passare quelle rotte fra monete di prezzo simile.</para>
///
/// <para>Sui log, β è un'<b>elasticità adimensionale</b> e il suo valore di riferimento è 1 per
/// qualunque coppia, indipendentemente dalla scala dei prezzi. Non è solo comodo: log Y − β·log X
/// stazionario equivale a Y/X^β costante, e per β = 1 quel portafoglio è ESATTAMENTE quello a
/// controvalore uguale sulle due gambe — cioè quello che <c>PairsBacktestEngine</c> apre davvero
/// (dollar-neutral). Con i prezzi grezzi il segnale sorvegliava una combinazione β-pesata mentre
/// l'esecuzione ne apriva un'altra; sui log le due cose coincidono in β = 1, e lo scostamento di β
/// da 1 misura di quanto l'esecuzione dollar-neutral si discosta dalla copertura vera.</para>
/// </summary>
public sealed class EngleGrangerCointegrationTest : ICointegrationTest
{
    /// <summary>Livello di significatività del giudizio di cointegrazione (%).</summary>
    private const double SignificancePercent = 5.0;

    /// <summary>
    /// Banda di plausibilità dell'elasticità. È un filtro di SANITÀ, volutamente largo: β = 0,5
    /// (o 2) descrive una coppia in cui la copertura corretta è il doppio/la metà del controvalore
    /// che le gambe aprono davvero, e oltre quella soglia il portafoglio negoziato non è più
    /// ragionevolmente quello di cui si è testata la stazionarietà. Non pretende di selezionare le
    /// coppie buone: serve a escludere quelle in cui la specificazione stessa non regge.
    /// </summary>
    public const double MinPlausibleElasticity = 0.5;

    /// <inheritdoc cref="MinPlausibleElasticity"/>
    public const double MaxPlausibleElasticity = 2.0;

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

        // I log richiedono prezzi strettamente positivi: un solo zero produrrebbe -Infinity e
        // avvelenerebbe silenziosamente l'intera regressione, che è il modo peggiore di fallire.
        var y = Vector<double>.Build.Dense(n, i => Log(seriesY[i], nameof(seriesY)));
        var x = Vector<double>.Build.Dense(n, i => Log(seriesX[i], nameof(seriesX)));

        // Passo 1: regressione log Y = alpha + beta*log X + spread (stima dell'elasticità).
        var design = Matrix<double>.Build.Dense(n, 2, (row, col) => col == 0 ? 1.0 : x[row]);
        var ols = OlsRegression.Fit(design, y);
        var intercept = ols.Coefficients[0];
        var hedgeRatio = ols.Coefficients[1];
        var spread = ols.Residuals;

        // Passo 2: ADF sui residui, con lag scelto per AIC, giudizio contro il valore critico MacKinnon.
        var (adfStatistic, chosenLags) = AugmentedDickeyFullerAutoLag(spread, MaxLags(spread.Count));
        var criticalValue = MacKinnonCriticalValue(SignificancePercent, spread.Count);

        return new CointegrationResult
        {
            HedgeRatio = hedgeRatio,
            Intercept = intercept,
            Spread = spread.ToArray(),
            AdfStatistic = adfStatistic,
            CriticalValue = criticalValue,
            SignificanceLevelPercent = SignificancePercent,
            AdfLags = chosenLags,
            IsCointegrated = adfStatistic < criticalValue,
            IsHedgeRatioPlausible = hedgeRatio is >= MinPlausibleElasticity and <= MaxPlausibleElasticity,
        };
    }

    private static double Log(decimal price, string paramName)
        => price > 0m
            ? Math.Log((double)price)
            : throw new ArgumentException(
                $"La cointegrazione gira sui log dei prezzi: servono valori strettamente positivi, trovato {price}.", paramName);

    /// <summary>
    /// Valori critici di MacKinnon (2010) per il test di cointegrazione Engle-Granger, caso "con
    /// costante, senza trend", n=2 variabili I(1). Superficie di risposta CV(T) = β∞ + β1/T + β2/T²,
    /// sensibilmente più severa dei valori ADF standard (al 5%: ≈ −3.34 vs −2.86).
    /// </summary>
    internal static double MacKinnonCriticalValue(double significancePercent, int sampleSize)
    {
        var (bInf, b1, b2) = significancePercent switch
        {
            <= 1.0 => (-3.90001, -10.534, -30.03),
            <= 5.0 => (-3.33613, -5.967, -8.98),
            _ => (-3.04445, -4.069, -5.73),
        };
        var t = Math.Max(sampleSize, 2);
        return bInf + b1 / t + b2 / ((double)t * t);
    }

    /// <summary>Numero massimo di lag ADF da esplorare (regola di Schwert, limitata dai dati).</summary>
    private static int MaxLags(int n)
        => Math.Clamp((int)Math.Floor(12.0 * Math.Pow(n / 100.0, 0.25)), 1, Math.Max(1, n / 5));

    /// <summary>
    /// ADF con selezione del numero di lag per AIC su 0..<paramref name="maxLags"/>. Tutti i modelli
    /// usano lo STESSO campione (righe da <c>maxLags+1</c>) perché l'AIC sia confrontabile. Ritorna la
    /// statistica t = γ̂/SE(γ̂) del modello scelto e il numero di lag selezionato.
    /// </summary>
    internal static (double Statistic, int Lags) AugmentedDickeyFullerAutoLag(Vector<double> series, int maxLags)
    {
        var bestAic = double.PositiveInfinity;
        var bestStat = 0.0;
        var bestLags = 0;
        for (var lags = 0; lags <= maxLags; lags++)
        {
            if (TryAdfRegression(series, lags, firstT: maxLags + 1, out var stat, out var aic) && aic < bestAic)
            {
                bestAic = aic;
                bestStat = stat;
                bestLags = lags;
            }
        }
        return (bestStat, bestLags);
    }

    /// <summary>
    /// Regressione ADF con costante: Δyₜ = c + γ·yₜ₋₁ + Σᵢ φᵢ·Δyₜ₋ᵢ + εₜ, sulle righe t = firstT..n-1.
    /// La statistica del test è t = γ̂/SE(γ̂): più negativa -> più forte l'evidenza di stazionarietà.
    /// Ritorna anche l'AIC del modello (per la selezione lag). false se i gradi di libertà sono troppo pochi.
    /// </summary>
    private static bool TryAdfRegression(Vector<double> series, int lags, int firstT, out double statistic, out double aic)
    {
        statistic = 0.0;
        aic = double.PositiveInfinity;

        var n = series.Count;
        var rows = n - firstT;
        var cols = 2 + lags;
        if (rows < cols + 3)
        {
            return false; // troppo pochi gradi di libertà per una stima affidabile
        }

        var diff = Vector<double>.Build.Dense(n - 1, i => series[i + 1] - series[i]);
        var design = Matrix<double>.Build.Dense(rows, cols);
        var target = Vector<double>.Build.Dense(rows);
        for (var r = 0; r < rows; r++)
        {
            var t = firstT + r; // t varia da firstT a n-1
            target[r] = diff[t - 1];              // Δyₜ
            design[r, 0] = 1.0;                    // costante
            design[r, 1] = series[t - 1];         // yₜ₋₁ (livello)
            for (var i = 1; i <= lags; i++)
            {
                design[r, 1 + i] = diff[t - 1 - i]; // Δyₜ₋ᵢ
            }
        }

        var ols = OlsRegression.Fit(design, target);
        var gamma = ols.Coefficients[1];
        var seGamma = ols.StandardErrors[1];
        statistic = seGamma > 1e-15 ? gamma / seGamma : 0.0;

        var rss = ols.Residuals.DotProduct(ols.Residuals);
        aic = rows * Math.Log(Math.Max(rss / rows, 1e-300)) + 2.0 * cols;
        return true;
    }
}
