namespace ProcioneMGR.Services.Optimization;

public interface IOptimizationEngine
{
    Task<OptimizationResult> OptimizeAsync(
        OptimizationConfiguration config,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct);
}
