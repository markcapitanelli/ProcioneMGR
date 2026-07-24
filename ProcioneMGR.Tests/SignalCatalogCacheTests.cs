using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione del bug trovato DAL VIVO la prima notte di Composite su una corsia: la cache del
/// SignalCatalog era per ISTANZA della lista candele — corretta nei backtest (liste immutabili),
/// ma il TradingEngine live riusa UN buffer che cresce/scorre e ri-inizializza la strategia a
/// ogni candela. La matrice tornava stantia: più corta del buffer (IndexOutOfRange a ogni
/// candela) o, con finestra rotolante a lunghezza fissa, della STESSA lunghezza con contenuto
/// vecchio — segnali sbagliati in silenzio, il caso peggiore. La cache ora porta un'impronta
/// (Count, primo, ultimo timestamp) e si rinnova quando il contenuto cambia.
/// </summary>
public class SignalCatalogCacheTests
{
    private static OhlcvData Candle(DateTime ts, decimal price) => new()
    {
        Symbol = "CACHE/USDT", Timeframe = "1h", TimestampUtc = ts,
        Open = price, High = price * 1.001m, Low = price * 0.999m, Close = price, Volume = 100m,
    };

    private static List<OhlcvData> Series(int n)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rng = new Random(7);
        var price = 100m;
        var list = new List<OhlcvData>(n);
        for (var i = 0; i < n; i++)
        {
            price *= 1m + (decimal)((rng.NextDouble() - 0.5) * 0.01);
            list.Add(Candle(t0.AddHours(i), price));
        }
        return list;
    }

    [Fact]
    public async Task GrowingBuffer_SameListInstance_MatrixFollowsTheNewLength()
    {
        var svc = new TechnicalIndicatorsService();
        var buffer = Series(300);

        var m1 = await SignalCatalog.GetMatrixAsync(buffer, svc, CancellationToken.None);
        Assert.Equal(300, m1[0].Length);

        // Il pattern ESATTO del motore live: stessa istanza, una candela in più, nuova Initialize.
        buffer.Add(Candle(buffer[^1].TimestampUtc.AddHours(1), buffer[^1].Close * 1.001m));
        var m2 = await SignalCatalog.GetMatrixAsync(buffer, svc, CancellationToken.None);

        Assert.Equal(301, m2[0].Length);   // prima del fix: 300 → IndexOutOfRange su EvaluateSignal(300)
    }

    [Fact]
    public async Task RollingWindow_SameLengthDifferentContent_IsRecomputed_NotSilentlyStale()
    {
        var svc = new TechnicalIndicatorsService();
        var buffer = Series(300);

        var m1 = await SignalCatalog.GetMatrixAsync(buffer, svc, CancellationToken.None);
        var before = m1[9][^1];   // Ora UTC dell'ultima barra: dipende SOLO dal timestamp

        // Finestra rotolante: via la più vecchia, dentro una nuova — stessa lunghezza, contenuto diverso.
        buffer.RemoveAt(0);
        buffer.Add(Candle(buffer[^1].TimestampUtc.AddHours(1), buffer[^1].Close * 1.001m));
        var m2 = await SignalCatalog.GetMatrixAsync(buffer, svc, CancellationToken.None);
        var after = m2[9][^1];

        var expected = buffer[^1].TimestampUtc.Hour * 100m / 23m;
        Assert.Equal(expected, after);     // prima del fix: matrice VECCHIA ⇒ valore dell'ora sbagliata
        Assert.NotEqual(before, after);    // l'ora dell'ultima barra è cambiata davvero
    }

    [Fact]
    public async Task ImmutableList_BacktestPath_IsStillServedFromCache()
    {
        var svc = new TechnicalIndicatorsService();
        var candles = Series(300);

        var t1 = SignalCatalog.GetMatrixAsync(candles, svc, CancellationToken.None);
        var t2 = SignalCatalog.GetMatrixAsync(candles, svc, CancellationToken.None);

        Assert.Same(await t1, await t2);   // lista immutata ⇒ stesso task/matrice, zero ricalcoli
    }
}
