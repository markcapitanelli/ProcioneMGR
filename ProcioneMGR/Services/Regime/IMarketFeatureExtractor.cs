namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Estrae le <see cref="MarketFeatures"/> dalle candele OHLCV. Tutte le feature sono
/// calcolate usando esclusivamente dati fino alla candela corrente (no look-ahead).
/// </summary>
public interface IMarketFeatureExtractor
{
    Task<List<MarketFeatures>> ExtractFeaturesAsync(
        string exchangeName,
        string symbol,
        string timeframe,
        DateTime from,
        DateTime to,
        CancellationToken ct = default);
}
