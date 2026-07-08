namespace ProcioneMGR.Services.ML;

/// <summary>
/// PCA sui rendimenti di più simboli per estrarre <b>risk factor statistici</b> (cap. 13):
/// componenti principali ortogonali che spiegano la varianza comune del paniere, utili sia
/// come feature de-correlate per i modelli sia per capire l'esposizione al rischio sistemico.
/// </summary>
public interface IRiskFactorPca
{
    /// <summary>
    /// Calcola le prime <paramref name="componentCount"/> componenti principali sui rendimenti
    /// (standardizzati per simbolo: PCA sulla matrice di correlazione, non di covarianza, per
    /// non far dominare gli asset più volatili solo per scala).
    /// </summary>
    /// <param name="returnsBySymbol">Serie di rendimenti per simbolo, tutte della STESSA lunghezza e allineate per indice temporale.</param>
    RiskFactorPcaResult Compute(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, int componentCount);
}
