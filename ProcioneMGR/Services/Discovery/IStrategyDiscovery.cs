namespace ProcioneMGR.Services.Discovery;

public interface IStrategyDiscovery
{
    Task<StrategyDiscoveryResult> DiscoverAsync(
        StrategyDiscoveryConfiguration config,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken ct);
}
