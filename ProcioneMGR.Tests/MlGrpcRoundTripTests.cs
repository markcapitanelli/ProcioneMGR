using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.ML;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Data;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prova che il servizio ml serve davvero via gRPC su HTTP/2 (host reale ProcioneMGR.Ml, non una
/// chiamata C# diretta) e che il valore attraversa la (de)serializzazione protobuf senza perdita:
/// la predizione ricevuta sul wire è ESATTAMENTE uguale a quella calcolata in locale. Senza DB:
/// l'IModelRegistry è sostituito con uno fake, la connection string è fittizia (Npgsql non si
/// connette a startup e il registry fake non tocca il DB).
/// </summary>
public class MlGrpcRoundTripTests
{
    [Fact]
    public async Task PredictSignal_OverRealGrpc_MatchesLocalExactly()
    {
        var (saved, input) = TrainLinear();

        using var localPredictor = await MlModelLoader.LoadPredictorAsync(saved, CancellationToken.None);
        var localValue = localPredictor.Predict(input);

        await using var factory = new WebApplicationFactory<InferenceServiceImpl>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:PostgresConnection", "Host=localhost;Database=unused;Username=x;Password=x");
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IModelRegistry>();
                    services.AddSingleton<IModelRegistry>(new FixedChampionRegistry(saved));
                });
            });

        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var client = new InferenceService.InferenceServiceClient(channel);

        var request = new PredictSignalRequest
        {
            Instrument = new ProcioneMGR.Contracts.Common.V1.Instrument { Symbol = saved.Symbol, Timeframe = saved.Timeframe },
        };
        request.Features.AddRange(input.Select(f => (double)f));

        var response = await client.PredictSignalAsync(request);

        Assert.Equal((double)localValue, response.PredictedReturn); // esatto attraverso il wire protobuf
        Assert.Equal(saved.Id, response.ModelId);
        Assert.Equal(ProcioneMGR.Contracts.Ml.V1.ModelStage.Champion, response.StageUsed);
    }

    private static (SavedMlModel, float[]) TrainLinear()
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

    private sealed class FixedChampionRegistry(SavedMlModel model) : IModelRegistry
    {
        public Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)
            => Task.FromResult<SavedMlModel?>(model);
        public Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
