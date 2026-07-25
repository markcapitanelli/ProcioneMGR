namespace ProcioneMGR.Services.Optimization;

public interface IOptimizationEngine
{
    Task<OptimizationResult> OptimizeAsync(
        OptimizationConfiguration config,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// [T1.6] Validazione CPCV del percorso strategie: C(gruppi, gruppiTest) percorsi → una
    /// DISTRIBUZIONE di Sharpe out-of-sample per candidato invece del singolo percorso walk-forward.
    /// Solo GridSearch: i backtest per (combinazione × gruppo) sono pre-calcolati sull'intera griglia.
    /// </summary>
    Task<CpcvResult> OptimizeCpcvAsync(
        OptimizationConfiguration config,
        CpcvConfiguration cpcv,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct);
}
