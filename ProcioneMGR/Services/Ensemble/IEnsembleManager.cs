using ProcioneMGR.Services.Monitoring;

namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Gestione dell'ensemble multi-strategia con allocazione dinamica del capitale basata
/// su Sharpe rolling. La performance è una simulazione storica deterministica: ogni
/// strategia membro viene backtestata sulla finestra, e il capitale viene riallocato
/// periodicamente in base alla Sharpe degli ultimi N giorni.
/// </summary>
public interface IEnsembleManager
{
    /// <summary>Corsia di trading isolata a cui appartiene questa istanza (0 = corsia di default).</summary>
    int LaneId { get; }

    Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default);
    Task UpdateConfigurationAsync(EnsembleConfiguration config, CancellationToken ct = default);
    Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default);
    Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default);

    /// <summary>Confronta Sharpe realizzato (trade chiusi dal vivo) vs atteso (backtest/holdout) per ogni gamba attiva.</summary>
    Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default);
}
