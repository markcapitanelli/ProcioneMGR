using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Data;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Tests;

/// <summary>
/// Requisito centrale della Fase 2a (dual-read): l'inferenza del servizio ml remoto deve essere
/// BYTE-IDENTICA a quella locale per lo stesso input. Qui la prova senza DB né rete: si addestra un
/// predittore in memoria, lo si salva su bytes, e si confronta la predizione locale (via
/// <see cref="MlModelLoader.LoadPredictorAsync"/>) con quella del servizio remoto (chiamata diretta a
/// <see cref="InferenceServiceImpl.PredictSignal"/>). Uguaglianza ESATTA, non a tolleranza.
///
/// La <see cref="Theory"/> copre tutti i ModelType, inclusi Attention/Stacked: prima del fix del
/// bug in MlModelLoader (che li caricava come Linear) questi due casi fallivano l'assert su Name.
/// </summary>
public class MlDeterminismTests
{
    [Theory]
    [InlineData("Linear")]
    [InlineData("RandomForest")]
    [InlineData("GradientBoosting")]
    [InlineData("Mlp")]
    [InlineData("Stacked")]
    [InlineData("Attention")]
    public async Task LocalAndRemote_Predict_ByteIdentical(string modelType)
    {
        var (saved, input) = TrainAndSave(modelType);

        // Locale: stessa via del TradingEngine (LoadPredictorAsync → Predict).
        using var localPredictor = await MlModelLoader.LoadPredictorAsync(saved, CancellationToken.None);
        Assert.Equal(modelType, localPredictor.Name); // il fix: Attention/Stacked NON cadono più su Linear
        var localValue = localPredictor.Predict(input);

        // Remoto: chiamata diretta al servizio gRPC, registry fake, nessun DB (model_id=0 → Champion).
        var service = new InferenceServiceImpl(new FixedChampionRegistry(saved), new UnusedDbFactory(),
            NullLogger<InferenceServiceImpl>.Instance);
        var request = new PredictSignalRequest
        {
            Instrument = new ProcioneMGR.Contracts.Common.V1.Instrument { Symbol = saved.Symbol, Timeframe = saved.Timeframe },
        };
        request.Features.AddRange(input.Select(f => (double)f)); // float→double esatto; il servizio ri-converte a float

        var response = await service.PredictSignal(request, FakeContext());

        Assert.Equal((double)localValue, response.PredictedReturn); // uguaglianza esatta
        Assert.Equal(saved.Id, response.ModelId);
        Assert.Equal(ProcioneMGR.Contracts.Ml.V1.ModelStage.Champion, response.StageUsed);
    }

    [Fact]
    public async Task PredictSignal_WrongFeatureLength_FailsPrecondition()
    {
        var (saved, _) = TrainAndSave("Linear"); // 3 fattori attesi
        var service = new InferenceServiceImpl(new FixedChampionRegistry(saved), new UnusedDbFactory(),
            NullLogger<InferenceServiceImpl>.Instance);
        var request = new PredictSignalRequest
        {
            Instrument = new ProcioneMGR.Contracts.Common.V1.Instrument { Symbol = saved.Symbol, Timeframe = saved.Timeframe },
        };
        request.Features.AddRange(new[] { 1.0, 2.0 }); // 2 invece di 3

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.PredictSignal(request, FakeContext()));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    // --- Helpers ---

    private static (SavedMlModel Saved, float[] Input) TrainAndSave(string modelType)
    {
        var mlContext = new MLContext(seed: 1);
        IReturnPredictor predictor;
        float[] input;
        int factorCount;

        if (modelType == "Attention")
        {
            const int t = 6, f = 2;
            var rows = MakeTemporalRows(160, t, f);
            var view = MlDatasetView.Create(mlContext, rows, t * f);
            var att = new AttentionReturnPredictor(windowLength: t, embedDim: 8, hiddenUnits: 8, epochs: 20, seed: 42);
            att.Fit(mlContext, view);
            predictor = att;
            input = rows[0].Features;   // lunghezza t*f = 12
            factorCount = f;            // irrilevante per i sequence predictor (usano WindowLength*FeaturesPerStep)
        }
        else
        {
            var rows = MakeRows(300);
            var view = MlDatasetView.Create(mlContext, rows, 3);
            predictor = modelType switch
            {
                "Linear" => new LinearReturnPredictor(),
                "RandomForest" => new RandomForestReturnPredictor(),
                "GradientBoosting" => new GradientBoostingReturnPredictor(),
                "Mlp" => new MlpReturnPredictor(),
                "Stacked" => new StackedReturnPredictor(["Linear", "RandomForest"]),
                _ => throw new ArgumentOutOfRangeException(nameof(modelType), modelType, null),
            };
            predictor.Fit(mlContext, view);
            input = rows[0].Features;   // lunghezza 3
            factorCount = 3;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"det_{Guid.NewGuid():N}.zip");
        byte[] bytes;
        try
        {
            predictor.Save(mlContext, tempPath);
            bytes = File.ReadAllBytes(tempPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            predictor.Dispose();
        }

        // FactorsJson: serve solo che sia un array della lunghezza giusta (il servizio ne conta gli
        // elementi per validare l'input dei predittori NON sequenziali).
        var factorsJson = JsonSerializer.Serialize(
            Enumerable.Range(0, factorCount).Select(i => new { FeatureName = $"f{i}" }));

        var saved = new SavedMlModel
        {
            Id = 1,
            Name = "det-test",
            ModelType = modelType,
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            FactorsJson = factorsJson,
            ModelBytes = bytes,
            Stage = ProcioneMGR.Data.ModelStage.Champion,
        };
        return (saved, input);
    }

    private static List<FeatureRow> MakeRows(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() - 0.5);
            var f1 = (float)(rnd.NextDouble() - 0.5);
            var f2 = (float)(rnd.NextDouble() - 0.5);
            var noise = (float)((rnd.NextDouble() - 0.5) * 0.05);
            rows.Add(new FeatureRow { Features = [f0, f1, f2], Label = 2f * f0 - f1 + 0.5f * f2 + noise });
        }
        return rows;
    }

    private static List<FeatureRow> MakeTemporalRows(int n, int t, int f, int seed = 3)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        for (var i = 0; i < n; i++)
        {
            var flat = new float[t * f];
            for (var k = 0; k < flat.Length; k++) flat[k] = (float)(rnd.NextDouble() * 2 - 1);
            var noise = (float)((rnd.NextDouble() - 0.5) * 0.1);
            rows.Add(new FeatureRow { Features = flat, Label = 2f * flat[0] + noise });
        }
        return rows;
    }

    private static ServerCallContext FakeContext() => new FakeServerCallContext();

    /// <summary>ServerCallContext minimale: il servizio usa solo CancellationToken.</summary>
    private sealed class FakeServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "PredictSignal";
        protected override string HostCore => "";
        protected override string PeerCore => "";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
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

    private sealed class UnusedDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            throw new NotSupportedException("DB non usato in questo test: model_id=0 → percorso Champion via registry.");
    }
}
