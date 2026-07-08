namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Crea istanze di strategia per nome (switch case, niente reflection) ed espone
/// i "prototipi" per popolare la UI (dropdown + definizioni parametri).
/// Aggiungere una strategia = nuova classe + un case qui.
/// </summary>
public interface IStrategyFactory
{
    /// <summary>Istanze "vuote" per leggere DisplayName/ParameterDefinitions nella UI.</summary>
    IReadOnlyList<IStrategy> Prototypes { get; }

    IStrategy Create(string strategyName);
}

public sealed class StrategyFactory : IStrategyFactory
{
    public IReadOnlyList<IStrategy> Prototypes { get; } =
    [
        new EmaCrossStrategy(),
        new RsiOversoldStrategy(),
        new MacdTrendStrategy(),
        new BollingerMeanReversionStrategy(),
        new MomentumStrategy(),
        new DonchianBreakoutStrategy(),
        new PriceSmaCrossStrategy(),
        new SupertrendStrategy(),
        new StochasticStrategy(),
        new VwapReversionStrategy(),
        new CompositeSignalStrategy(),
        new EventTriggerStrategy(),
        new RegimeConditionalStrategy(),
    ];

    public IStrategy Create(string strategyName) => strategyName switch
    {
        "EmaCross" => new EmaCrossStrategy(),
        "RsiOversold" => new RsiOversoldStrategy(),
        "MacdTrend" => new MacdTrendStrategy(),
        "BollingerMeanReversion" => new BollingerMeanReversionStrategy(),
        "Momentum" => new MomentumStrategy(),
        "DonchianBreakout" => new DonchianBreakoutStrategy(),
        "PriceSmaCross" => new PriceSmaCrossStrategy(),
        "Supertrend" => new SupertrendStrategy(),
        "Stochastic" => new StochasticStrategy(),
        "VwapReversion" => new VwapReversionStrategy(),
        "Composite" => new CompositeSignalStrategy(),
        "EventTrigger" => new EventTriggerStrategy(),
        "RegimeConditional" => new RegimeConditionalStrategy(),
        _ => throw new NotSupportedException($"Strategia non supportata: '{strategyName}'."),
    };
}
