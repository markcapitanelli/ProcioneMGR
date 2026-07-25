namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Esito del test di cointegrazione di Engle-Granger fra due serie di prezzi, in LOG-livello.</summary>
public sealed class CointegrationResult
{
    /// <summary>
    /// β della regressione log Y = α + β·log X + spread: un'<b>elasticità adimensionale</b>, non un
    /// rapporto di quantità. β ≈ 1 significa "Y e X si muovono in proporzione", cioè che il
    /// portafoglio stazionario è quello a controvalore uguale sulle due gambe.
    /// </summary>
    public required double HedgeRatio { get; init; }
    public required double Intercept { get; init; }

    /// <summary>Residui della regressione (lo "spread"), allineati per indice alle serie in input.</summary>
    public required IReadOnlyList<double> Spread { get; init; }

    /// <summary>Statistica t del test ADF (Augmented Dickey-Fuller) sullo spread.</summary>
    public required double AdfStatistic { get; init; }

    /// <summary>Valore critico MacKinnon usato per il giudizio (dipende dal livello e dalla lunghezza). Più negativo = più severo.</summary>
    public double CriticalValue { get; init; }

    /// <summary>Livello di significatività (%) del valore critico usato (default 5%).</summary>
    public double SignificanceLevelPercent { get; init; }

    /// <summary>Numero di lag dell'ADF scelto per AIC.</summary>
    public int AdfLags { get; init; }

    /// <summary>True se l'ADF rifiuta l'ipotesi di radice unitaria al livello scelto (statistica &lt; valore critico MacKinnon): spread stazionario -> serie cointegrate.</summary>
    public required bool IsCointegrated { get; init; }

    /// <summary>
    /// True se l'elasticità <see cref="HedgeRatio"/> sta nella banda di plausibilità economica
    /// (vedi <see cref="EngleGrangerCointegrationTest.MinPlausibleElasticity"/>). È un giudizio
    /// SEPARATO da <see cref="IsCointegrated"/>, che resta il verdetto puramente statistico: una
    /// coppia può avere uno spread stazionario e restare comunque non operabile.
    /// </summary>
    public required bool IsHedgeRatioPlausible { get; init; }

    /// <summary>
    /// L'unico criterio che dovrebbe decidere se una coppia entra in produzione: statistica E
    /// plausibilità economica insieme. Su <see cref="IsCointegrated"/> da solo era passata
    /// AAVE/XLM, la peggiore delle otto candidate (−14,14%, maxDD 15,1%).
    /// </summary>
    public bool IsTradeable => IsCointegrated && IsHedgeRatioPlausible;
}

/// <summary>
/// Test di cointegrazione di Engle-Granger (cap. 9): due serie di prezzi NON stazionarie
/// possono comunque muoversi insieme nel lungo periodo (essere "cointegrate") se una loro
/// combinazione lineare (lo spread) È stazionaria — il fondamento statistico del pairs trading.
/// Procedura in due passi: (1) regressione OLS per stimare l'hedge ratio, (2) test ADF
/// sui residui per verificarne la stazionarietà.
///
/// La regressione gira sui LOG dei prezzi, non sui prezzi grezzi. Vedi
/// <see cref="EngleGrangerCointegrationTest"/> per il perché.
/// </summary>
public interface ICointegrationTest
{
    /// <param name="seriesY">Prezzi (livello, strettamente positivi) del primo simbolo.</param>
    /// <param name="seriesX">Prezzi (livello, strettamente positivi) del secondo simbolo, stessa lunghezza di <paramref name="seriesY"/>.</param>
    CointegrationResult Test(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX);
}
