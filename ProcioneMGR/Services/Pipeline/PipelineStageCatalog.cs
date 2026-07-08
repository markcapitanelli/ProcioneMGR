using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Catalog of the available pipeline stages. Stage classes are resolved via DI (they depend
/// on platform services); the catalog holds the TYPE list, materializes prototypes once for
/// metadata reads (Name/DisplayName/ParameterDefinitions/Dependencies are plain constants on
/// every stage, safe to read after the construction scope is gone), and creates fresh
/// per-run instances inside the engine's scope.
/// </summary>
public sealed class PipelineStageCatalog : IPipelineStageCatalog
{
    /// <summary>The full stage roster, in default execution order.</summary>
    internal static readonly IReadOnlyList<Type> StageTypes =
    [
        typeof(DataIngestionStage),
        typeof(AltDataSyncStage),
        typeof(FeatureEngineeringStage),
        typeof(RegimeAnalysisStage),
        typeof(VolatilityRegimeStage),
        typeof(PairsScreeningStage),
        typeof(MlModelTrainingStage),
        typeof(StrategyDiscoveryStage),
        typeof(CreativeDiscoveryStage),
        typeof(HoldoutValidationStage),
        typeof(RobustnessProbeStage),
        typeof(EnsembleAssemblyStage),
        typeof(RiskSizingStage),
        typeof(NewsImpactCheckStage),
        typeof(RecommendationStage),
        typeof(ExecutionPlanStage),
    ];

    private readonly IReadOnlyList<IPipelineStage> _prototypes;
    private readonly Dictionary<string, Type> _byName;

    public PipelineStageCatalog(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        _prototypes = StageTypes
            .Select(t => (IPipelineStage)ActivatorUtilities.CreateInstance(scope.ServiceProvider, t))
            .ToList();
        _byName = StageTypes.ToDictionary(
            t => _prototypes.First(p => p.GetType() == t).Name,
            t => t,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IPipelineStage> Prototypes => _prototypes;

    public IPipelineStage Create(IServiceProvider scopedProvider, string name)
    {
        if (!_byName.TryGetValue(name, out var type))
        {
            throw new ArgumentException($"Stage sconosciuto: '{name}'.", nameof(name));
        }
        return (IPipelineStage)ActivatorUtilities.CreateInstance(scopedProvider, type);
    }

    public List<StageConfig> DefaultStages()
        => _prototypes
            .OrderBy(p => p.DefaultOrder)
            .Select(p => new StageConfig
            {
                Type = p.Name,
                Order = p.DefaultOrder,
                Enabled = true,
                Parameters = p.ParameterDefinitions.ToDictionary(d => d.Key, d => d.DefaultValue),
            })
            .ToList();
}
