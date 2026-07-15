using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Data;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Ml;

/// <summary>
/// Implementazione gRPC del servizio di inferenza (Fase 2a). Riceve un vettore di feature GIA'
/// calcolato dal chiamante, carica il modello (per id o Champion del registry), ne fa inferenza e
/// restituisce il rendimento predetto. SOLA LETTURA: nessuna scrittura sul DB nel path di predict.
/// </summary>
public sealed class InferenceServiceImpl(
    IModelRegistry registry,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<InferenceServiceImpl> logger) : InferenceService.InferenceServiceBase
{
    public override async Task<PredictSignalResponse> PredictSignal(PredictSignalRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // model_id (PK) ha precedenza; altrimenti il Champion per (symbol, timeframe).
        var saved = request.ModelId > 0
            ? await LoadByIdAsync(request.ModelId, ct)
            : await registry.GetChampionAsync(request.Instrument.Symbol, request.Instrument.Timeframe, ct);

        if (saved is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                request.ModelId > 0
                    ? $"Nessun modello con id {request.ModelId}."
                    : $"Nessun Champion per {request.Instrument.Symbol} {request.Instrument.Timeframe}."));
        }

        using var predictor = await MlModelLoader.LoadPredictorAsync(saved, ct);

        var expected = ExpectedInputLength(predictor, saved);
        if (request.Features.Count != expected)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Attesi {expected} valori di input per il modello {saved.Id} ({saved.ModelType}), ricevuti {request.Features.Count}."));
        }

        var input = new float[request.Features.Count];
        for (var i = 0; i < input.Length; i++) input[i] = (float)request.Features[i];
        var predicted = predictor.Predict(input);

        logger.LogDebug("Inferenza modello {Id} ({Type}) {Symbol} {Tf}: {Value}.",
            saved.Id, saved.ModelType, saved.Symbol, saved.Timeframe, predicted);

        return new PredictSignalResponse
        {
            PredictedReturn = predicted,
            ModelId = saved.Id,
            ModelName = saved.Name,
            StageUsed = MlStageMapper.ToProto(saved.Stage),
            GeneratedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
    }

    private async Task<SavedMlModel?> LoadByIdAsync(int modelId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SavedMlModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct);
    }

    /// <summary>
    /// Lunghezza attesa del vettore di input, derivata dai metadati del modello caricato — senza
    /// ricostruire i fattori: per un predittore sequenziale è WindowLength*FeaturesPerStep (esposti
    /// dal modello dopo Load); altrimenti è il numero di fattori (lunghezza dell'array FactorsJson).
    /// </summary>
    private static int ExpectedInputLength(IReturnPredictor predictor, SavedMlModel saved)
    {
        if (predictor is ISequencePredictor seq) return seq.WindowLength * seq.FeaturesPerStep;
        using var doc = JsonDocument.Parse(saved.FactorsJson);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }
}
