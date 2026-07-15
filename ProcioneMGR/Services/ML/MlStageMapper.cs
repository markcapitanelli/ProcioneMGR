using DataStage = ProcioneMGR.Data.ModelStage;
using ProtoStage = ProcioneMGR.Contracts.Ml.V1.ModelStage;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Mappatura esplicita bidirezionale fra <see cref="DataStage"/> (dominio/DB) e
/// <see cref="ProtoStage"/> (contratto gRPC). Switch, NON cast ordinale: un cast si romperebbe in
/// silenzio se uno dei due enum venisse riordinato o esteso: qui invece un valore non gestito
/// lancia rumorosamente (fail-loud), così una divergenza fra i due enum diventa un errore di
/// compilazione/test invece di una predizione servita col modello sbagliato.
/// </summary>
public static class MlStageMapper
{
    public static ProtoStage ToProto(DataStage stage) => stage switch
    {
        DataStage.Staging => ProtoStage.Staging,
        DataStage.Challenger => ProtoStage.Challenger,
        DataStage.Champion => ProtoStage.Champion,
        DataStage.Retired => ProtoStage.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "ModelStage di dominio non mappato al contratto proto."),
    };

    public static DataStage FromProto(ProtoStage stage) => stage switch
    {
        ProtoStage.Staging => DataStage.Staging,
        ProtoStage.Challenger => DataStage.Challenger,
        ProtoStage.Champion => DataStage.Champion,
        ProtoStage.Retired => DataStage.Retired,
        // Unspecified non è mai un valore reale di SavedMlModel.Stage: chi lo passa ha un bug.
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "ModelStage proto non mappabile al dominio (Unspecified?)."),
    };
}
