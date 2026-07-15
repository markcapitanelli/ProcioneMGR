using System.Diagnostics.Metrics;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prova di sicurezza centrale della Fase 2a: il confronto dual-read col servizio ml remoto è
/// PURAMENTE osservativo. Qualunque risposta (match/mismatch), timeout o errore del remoto deve
/// essere assorbito — MAI un'eccezione che risalga verso il ciclo di trading — e registrato con
/// l'esito corretto sulla metrica <c>procione.ml.comparisons</c>.
/// </summary>
public class MlComparisonClientTests
{
    [Fact]
    public async Task Compare_Match_RecordsMatch_AndDoesNotThrow()
    {
        var outcome = await RunAsync(() => Respond(2.5), localPredicted: 2.5f);
        Assert.Equal("match", outcome);
    }

    [Fact]
    public async Task Compare_Divergence_RecordsMismatch_AndDoesNotThrow()
    {
        var outcome = await RunAsync(() => Respond(9.99), localPredicted: 2.5f);
        Assert.Equal("mismatch", outcome);
    }

    [Fact]
    public async Task Compare_DeadlineExceeded_RecordsTimeout_AndDoesNotThrow()
    {
        var outcome = await RunAsync(() => throw new RpcException(new Status(StatusCode.DeadlineExceeded, "slow")), localPredicted: 1f);
        Assert.Equal("timeout", outcome);
    }

    [Fact]
    public async Task Compare_RemoteError_RecordsError_AndDoesNotThrow()
    {
        var outcome = await RunAsync(() => throw new RpcException(new Status(StatusCode.Internal, "boom")), localPredicted: 1f);
        Assert.Equal("error", outcome);
    }

    [Fact]
    public async Task Compare_UnexpectedException_IsAbsorbed_AsError()
    {
        var outcome = await RunAsync(() => throw new InvalidOperationException("kaboom"), localPredicted: 1f);
        Assert.Equal("error", outcome);
    }

    // --- Harness ---

    /// <summary>Esegue CompareAsync col responder dato e ritorna l'unico esito registrato sulla metrica.</summary>
    private static async Task<string> RunAsync(Func<PredictSignalResponse> responder, float localPredicted)
    {
        using var metrics = new ProcioneMetrics();
        var outcomes = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == ProcioneMetrics.MeterName && inst.Name == "procione.ml.comparisons")
                    l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "outcome" && t.Value is string s) outcomes.Add(s);
        });
        listener.Start();

        var options = new StaticOptions(new MlComparisonOptions { Enabled = true, TimeoutMs = 300 });
        var client = new MlComparisonClient(new FakeInferenceClient(responder), options, metrics,
            NullLogger<MlComparisonClient>.Instance);

        // Non deve MAI lanciare, qualunque cosa faccia il remoto.
        await client.CompareAsync(0, "TEST/USDT", "1h", championModelId: 1,
            localInput: [0.1f, 0.2f], localPredicted: localPredicted, CancellationToken.None);

        return Assert.Single(outcomes);
    }

    private static PredictSignalResponse Respond(double predicted) => new() { PredictedReturn = predicted, ModelId = 1 };

    /// <summary>Client gRPC generato, sottoclassato: PredictSignalAsync esegue il responder di test.</summary>
    private sealed class FakeInferenceClient(Func<PredictSignalResponse> responder) : InferenceService.InferenceServiceClient
    {
        public override AsyncUnaryCall<PredictSignalResponse> PredictSignalAsync(
            PredictSignalRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var responseTask = Task.Run(responder); // se il responder lancia, il task fallisce → await propaga → catch
            return new AsyncUnaryCall<PredictSignalResponse>(
                responseTask, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
    }

    private sealed class StaticOptions(MlComparisonOptions value) : IOptionsMonitor<MlComparisonOptions>
    {
        public MlComparisonOptions CurrentValue { get; } = value;
        public MlComparisonOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MlComparisonOptions, string?> listener) => null;
    }
}
