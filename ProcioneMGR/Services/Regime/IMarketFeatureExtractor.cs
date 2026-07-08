using ProcioneMGR.Data;

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

    /// <summary>
    /// Calcolo PURO delle feature su una lista di candele già in memoria e ordinata
    /// cronologicamente (nessun accesso al DB) — stessa matematica anti-look-ahead di
    /// <see cref="ExtractFeaturesAsync"/>. Usato per il regime one-hot con parità train/serve:
    /// dataset (train) e <c>MlStrategy</c> (serve) etichettano la STESSA serie con lo stesso
    /// percorso. Default: non supportato (le sorgenti di sole feature grezze possono ometterlo).
    /// </summary>
    List<MarketFeatures> ComputeFeatures(IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
        => throw new NotSupportedException("Questa sorgente di feature non implementa il calcolo puro da candele in memoria.");
}
