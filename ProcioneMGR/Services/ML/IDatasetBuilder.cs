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
    ///
    /// REGIME ONE-HOT (opzionale, default OFF): se <paramref name="regimeIds"/> è fornito (allineato
    /// per indice a <paramref name="candles"/>) e <paramref name="regimeCount"/> &gt; 0, a ogni riga
    /// vengono APPESE K colonne one-hot del regime della sua candela (regime −1/sconosciuto → tutte
    /// zero). Con i default il comportamento è bit-identico a prima. Vedi <c>RegimeAugmentation</c>.
    ///
    /// TARGET (1.V, opzionale, default <see cref="MlTargetKind.ForwardReturn"/> = invariato): cosa
    /// predice il modello — rendimento, |rendimento| o volatilità realizzata forward. Vedi
    /// <see cref="ForwardTargets"/>.
    /// </summary>
    MlDataset Build(IReadOnlyList<OhlcvData> candles, IReadOnlyList<FactorSpec> factors, int forwardHorizon,
        IReadOnlyList<int>? regimeIds = null, int regimeCount = 0,
        MlTargetKind targetKind = MlTargetKind.ForwardReturn);
}
