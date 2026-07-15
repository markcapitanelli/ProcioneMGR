using Grpc.Core;
using Microsoft.Extensions.Options;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Confronto OSSERVATIVO fra la predizione ML locale (già calcolata dal TradingEngine) e quella del
/// servizio remoto procionemgr-ml (Fase 2a, dual-read). Non ritorna nulla al chiamante e non
/// influenza mai una decisione: registra solo l'esito (metrica + log). Ogni errore/timeout del
/// remoto è assorbito qui — mai propagato.
/// </summary>
public interface IMlComparisonClient
{
    Task CompareAsync(int laneId, string symbol, string timeframe, int championModelId,
        float[] localInput, float localPredicted, CancellationToken ct);
}

/// <inheritdoc cref="IMlComparisonClient"/>
public sealed class MlComparisonClient(
    InferenceService.InferenceServiceClient grpc,
    IOptionsMonitor<MlComparisonOptions> options,
    ProcioneMetrics metrics,
    ILogger<MlComparisonClient> logger) : IMlComparisonClient
{
    // Uguaglianza esatta locale/remoto è garantita per costruzione (stesso modello, stesso input
    // float→double→float): una differenza oltre questa soglia numerica minima è un vero mismatch.
    private const double MatchEpsilon = 1e-9;

    public async Task CompareAsync(int laneId, string symbol, string timeframe, int championModelId,
        float[] localInput, float localPredicted, CancellationToken ct)
    {
        var request = new PredictSignalRequest
        {
            Instrument = new ProcioneMGR.Contracts.Common.V1.Instrument { Symbol = symbol, Timeframe = timeframe },
            ModelId = championModelId,
        };
        foreach (var v in localInput) request.Features.Add(v); // float→double: esatto

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(50, options.CurrentValue.TimeoutMs));
        try
        {
            var resp = await grpc.PredictSignalAsync(request, deadline: deadline, cancellationToken: ct);
            var delta = Math.Abs(localPredicted - resp.PredictedReturn);
            if (delta < MatchEpsilon)
            {
                metrics.RecordMlComparison("match");
                logger.LogDebug("Lane {Lane}: predizione ml remota coincide (modello {Id}, {Value}).",
                    laneId, championModelId, localPredicted);
            }
            else
            {
                metrics.RecordMlComparison("mismatch");
                logger.LogWarning("Lane {Lane}: predizione ml remota DIVERGE dalla locale — modello {Id}, locale={Local}, remoto={Remote}, delta={Delta}.",
                    laneId, championModelId, localPredicted, resp.PredictedReturn, delta);
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.DeadlineExceeded)
        {
            metrics.RecordMlComparison("timeout");
            logger.LogDebug("Lane {Lane}: servizio ml remoto oltre deadline ({Ms}ms).", laneId, options.CurrentValue.TimeoutMs);
        }
        catch (Exception ex)
        {
            metrics.RecordMlComparison("error");
            logger.LogDebug(ex, "Lane {Lane}: confronto ml remoto fallito (osservativo, nessun impatto sul trading).", laneId);
        }
    }
}
