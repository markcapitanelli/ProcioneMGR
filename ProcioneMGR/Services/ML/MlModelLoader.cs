using System.Text.Json;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Materializza un <see cref="SavedMlModel"/> in una <see cref="MlStrategy"/> pronta all'uso
/// (predittore deserializzato dal blob + <see cref="FactorSpec"/> ricostruiti dai fattori salvati).
/// UNICO punto di verità del caricamento: lo usano sia il backtest (batch) sia il TradingEngine
/// (streaming, Champion su lane Paper/Testnet), così un modello produce lo STESSO segnale nei due
/// contesti — parità batch/stream per costruzione, nessuna logica duplicata che possa divergere.
/// </summary>
public static class MlModelLoader
{
    /// <summary>Mappa <c>SavedMlModel.ModelType</c> al predittore concreto (default: lineare).</summary>
    public static IReturnPredictor CreatePredictor(string modelType) => modelType switch
    {
        "RandomForest" => new RandomForestReturnPredictor(),
        "GradientBoosting" => new GradientBoostingReturnPredictor(),
        "Mlp" => new MlpReturnPredictor(),
        // Attention/Stacked mancavano: un Champion di questi tipi veniva caricato in silenzio come
        // Linear (modello vuoto/sbagliato). Entrambi hanno un costruttore di caricamento senza
        // argomenti; lo stato reale arriva da Load(blob).
        "Attention" => new AttentionReturnPredictor(),
        "Stacked" => new StackedReturnPredictor(),
        _ => new LinearReturnPredictor(),
    };

    /// <summary>
    /// Carica il solo <see cref="IReturnPredictor"/> dal blob, senza ricostruire <see cref="MlStrategy"/>
    /// né i <see cref="FactorSpec"/>. Usata dal servizio ml (Fase 2a), che riceve il vettore di input
    /// già pronto e non calcola mai fattori: evita a quell'host la dipendenza da
    /// <see cref="IAlphaFactorFactory"/>/<see cref="IFactorCache"/>/Alpha158.
    /// </summary>
    public static async Task<IReturnPredictor> LoadPredictorAsync(SavedMlModel saved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saved);

        var predictor = CreatePredictor(saved.ModelType);

        // Il predittore ML.NET si carica solo da file: blob → file temporaneo → Load → cleanup.
        var tempPath = Path.Combine(Path.GetTempPath(), $"mlmodel_load_{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(tempPath, saved.ModelBytes, ct);
            predictor.Load(new MLContext(), tempPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return predictor;
    }

    public static async Task<(MlStrategy Strategy, IReturnPredictor Predictor)> LoadAsync(
        SavedMlModel saved, IAlphaFactorFactory alphaFactorFactory, IFactorCache? factorCache, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(alphaFactorFactory);

        var predictor = await LoadPredictorAsync(saved, ct);

        var factorsDto = JsonSerializer.Deserialize<List<SavedFactorSpecDto>>(saved.FactorsJson) ?? [];
        var factors = factorsDto
            .Select(dto => new FactorSpec(dto.FeatureName, alphaFactorFactory.Create(dto.FactorName), dto.Parameters))
            .ToList();

        return (new MlStrategy(predictor, factors, factorCache), predictor);
    }
}
