namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Esito del test di cointegrazione di Engle-Granger fra due serie di prezzi (in livello, non rendimenti).</summary>
public sealed class CointegrationResult
{
    /// <summary>β della regressione Y = α + β·X + spread: il rapporto di copertura (hedge ratio) fra le due gambe.</summary>
    public required double HedgeRatio { get; init; }
    public required double Intercept { get; init; }

    /// <summary>Residui della regressione (lo "spread"), allineati per indice alle serie in input.</summary>
    public required IReadOnlyList<double> Spread { get; init; }

    /// <summary>Statistica t del test ADF (Augmented Dickey-Fuller) sullo spread.</summary>
    public required double AdfStatistic { get; init; }

    /// <summary>True se l'ADF rifiuta l'ipotesi di radice unitaria al 5%: lo spread è stazionario -> le serie sono cointegrate.</summary>
    public required bool IsCointegrated { get; init; }
}

/// <summary>
/// Test di cointegrazione di Engle-Granger (cap. 9): due serie di prezzi NON stazionarie
/// possono comunque muoversi insieme nel lungo periodo (essere "cointegrate") se una loro
/// combinazione lineare (lo spread) È stazionaria — il fondamento statistico del pairs trading.
/// Procedura in due passi: (1) regressione OLS Y su X per stimare l'hedge ratio, (2) test ADF
/// sui residui per verificarne la stazionarietà.
/// </summary>
public interface ICointegrationTest
{
    /// <param name="seriesY">Prezzi (livello) del primo simbolo.</param>
    /// <param name="seriesX">Prezzi (livello) del secondo simbolo, stessa lunghezza di <paramref name="seriesY"/>.</param>
    CointegrationResult Test(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX);
}
