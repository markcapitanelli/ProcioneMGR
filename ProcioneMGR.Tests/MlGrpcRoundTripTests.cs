using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prova che il valore attraversa la pipeline gRPC e la (de)serializzazione protobuf senza perdita:
/// la predizione ricevuta sul wire è ESATTAMENTE uguale a quella calcolata in locale. Senza DB:
/// l'IModelRegistry è sostituito con uno fake, la connection string è fittizia (vedi MlTestModel).
/// ATTENZIONE — cosa questo test NON copre: WebApplicationFactory usa il TestServer in-memory, che
/// NON passa da Kestrel. Il trasporto reale (h2c) qui non è esercitato, quindi questo test resta
/// verde anche se gli endpoint Kestrel sono configurati male. È esattamente così che è sfuggito il
/// bug HTTP_1_1_REQUIRED (0xd) corretto in MlHost.Build: gli endpoint erano lasciati al default
/// Http1AndHttp2 e in chiaro (senza ALPN) NESSUNA chiamata gRPC passava in K8s. La configurazione
/// delle porte è coperta contro Kestrel vero da <see cref="MlKestrelTransportTests"/>, non qui.
/// </summary>
public class MlGrpcRoundTripTests
{
    [Fact]
    public async Task PredictSignal_OverRealGrpc_MatchesLocalExactly()
    {
        var (saved, input) = MlTestModel.TrainLinear();

        using var localPredictor = await MlModelLoader.LoadPredictorAsync(saved, CancellationToken.None);
        var localValue = localPredictor.Predict(input);

        await using var factory = new WebApplicationFactory<InferenceServiceImpl>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:PostgresConnection", MlTestModel.UnusedConnectionString);
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
}
