using System.Text.Json;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Tests;

/// <summary>
/// Modello ML minimo (già addestrato, serializzato in memoria) + registry fake che lo restituisce
/// sempre come Champion: bastano a far rispondere InferenceService SENZA DB. Condivisi fra i test
/// del microservizio di inferenza (round-trip in-memory e trasporto Kestrel reale).
/// </summary>
internal static class MlTestModel
{
    /// <summary>
    /// Connection string fittizia: AddProcioneDatabase fa fail-fast se manca, ma Npgsql non si
    /// connette a startup e il registry fake non tocca mai il DB, quindi nessuno la usa davvero.
    /// </summary>
    public const string UnusedConnectionString = "Host=localhost;Database=unused;Username=x;Password=x";

    /// <summary>Addestra un modello lineare e lo restituisce con un vettore di input di prova.</summary>
    public static (SavedMlModel Model, float[] Input) TrainLinear()
    {
        var mlContext = new MLContext(seed: 1);
        var rnd = new Random(7);
        var rows = new List<FeatureRow>(300);
        for (var i = 0; i < 300; i++)
        {
            var f0 = (float)(rnd.NextDouble() - 0.5);
            var f1 = (float)(rnd.NextDouble() - 0.5);
            var f2 = (float)(rnd.NextDouble() - 0.5);
            rows.Add(new FeatureRow { Features = [f0, f1, f2], Label = 2f * f0 - f1 + 0.5f * f2 });
        }
        var predictor = new LinearReturnPredictor();
        predictor.Fit(mlContext, MlDatasetView.Create(mlContext, rows, 3));

        var tempPath = Path.Combine(Path.GetTempPath(), $"grpc_{Guid.NewGuid():N}.zip");
        byte[] bytes;
        try { predictor.Save(mlContext, tempPath); bytes = File.ReadAllBytes(tempPath); }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); predictor.Dispose(); }

        var saved = new SavedMlModel
        {
            Id = 7,
            Name = "grpc-test",
            ModelType = "Linear",
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            FactorsJson = JsonSerializer.Serialize(new[] { new { FeatureName = "f0" }, new { FeatureName = "f1" }, new { FeatureName = "f2" } }),
            ModelBytes = bytes,
            Stage = ProcioneMGR.Data.ModelStage.Champion,
        };
        return (saved, rows[0].Features);
    }
}

/// <summary>IModelRegistry fake: un solo modello, sempre Champion, nessun DB dietro.</summary>
internal sealed class FixedChampionRegistry(SavedMlModel model) : IModelRegistry
{
    public Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)
        => Task.FromResult<SavedMlModel?>(model);
    public Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default) => throw new NotSupportedException();
}
