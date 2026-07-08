namespace ProcioneMGR.Services.ML;

/// <summary>
/// Marca un <see cref="IReturnPredictor"/> che ragiona su una SEQUENZA di timestep (non su un solo
/// vettore di feature): il vettore che riceve in <c>Fit</c>/<c>Predict</c> è una finestra di
/// <see cref="WindowLength"/> passi × <see cref="FeaturesPerStep"/> fattori, appiattita in ordine
/// temporale (dal più vecchio al più recente). Serve a <c>MlStrategy</c> per costruire la finestra
/// a inferenza senza stato interno (niente buffer fragili): la strategia vede questa interfaccia e
/// impacchetta gli ultimi T vettori di fattori prima di chiamare <c>Predict</c>.
/// </summary>
public interface ISequencePredictor
{
    /// <summary>Numero di timestep della finestra (T).</summary>
    int WindowLength { get; }

    /// <summary>Numero di fattori per timestep (F).</summary>
    int FeaturesPerStep { get; }
}
