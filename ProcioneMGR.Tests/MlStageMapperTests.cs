using ProcioneMGR.Services.ML;
using DataStage = ProcioneMGR.Data.ModelStage;
using ProtoStage = ProcioneMGR.Contracts.Ml.V1.ModelStage;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il proto ml.proto e l'enum di dominio ModelStage hanno numerazioni diverse (proto3 impone lo
/// zero-value UNSPECIFIED): la mappatura deve reggere sui NOMI, non sugli ordinali. Questi test
/// impediscono che un futuro riordino di uno dei due enum introduca un disallineamento silenzioso.
/// </summary>
public class MlStageMapperTests
{
    [Theory]
    [InlineData(DataStage.Staging)]
    [InlineData(DataStage.Challenger)]
    [InlineData(DataStage.Champion)]
    [InlineData(DataStage.Retired)]
    public void ToProto_ThenFromProto_RoundTrips(DataStage stage)
    {
        Assert.Equal(stage, MlStageMapper.FromProto(MlStageMapper.ToProto(stage)));
    }

    [Fact]
    public void FromProto_Unspecified_Throws()
    {
        // Unspecified non è mai un valore reale di SavedMlModel.Stage: deve fallire rumorosamente.
        Assert.Throws<ArgumentOutOfRangeException>(() => MlStageMapper.FromProto(ProtoStage.Unspecified));
    }

    [Fact]
    public void ToProto_MapsChampion_ToChampion()
    {
        Assert.Equal(ProtoStage.Champion, MlStageMapper.ToProto(DataStage.Champion));
    }
}
