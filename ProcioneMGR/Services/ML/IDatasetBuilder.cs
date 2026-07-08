using ProcioneMGR.Data;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Costruisce dataset supervisionati per i modelli di previsione dei rendimenti a partire da
/// una libreria di fattori alpha e un orizzonte di rendimento forward (il target).
/// </summary>
public interface IDatasetBuilder
{
    /// <summary>
    /// Calcola i fattori su tutta la serie, allinea i valori al rendimento forward a
    /// <paramref name="forwardHorizon"/> candele, e scarta le righe incomplete (warm-up di
    /// qualsiasi fattore, o coda finale senza rendimento forward disponibile). Il risultato è
    /// pronto per l'addestramento ML.NET e per la cross-validation temporale.
    /// </summary>
    MlDataset Build(IReadOnlyList<OhlcvData> candles, IReadOnlyList<FactorSpec> factors, int forwardHorizon);
}
