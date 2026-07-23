using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2.S roadmap macchina-ricerca] Il segnale "Ora UTC" nel catalogo: la stagionalità oraria che
/// CyclicalAnalyzer misura da tempo diventa CACCIABILE dalla stessa combinatoria degli altri
/// segnali (Composite/StrategyComposer), senza sottosistemi nuovi. Appeso come id 9: gli id 0-8
/// delle strategie Composite già salvate restano validi.
/// </summary>
public class HourOfDaySignalTests
{
    private static List<OhlcvData> HourlyCandles(int n)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n).Select(i => new OhlcvData
        {
            Symbol = "HOD/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
            Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m,
        }).ToList();
    }

    [Fact]
    public async Task Catalog_SignalIdsAreAppendOnly_AndTheTenthIsUtcHour()
    {
        // Append-only: gli id storici restano validi. 9 = Ora UTC (2.S), 10-11 = MFI/OBV (3.8a),
        // 12-13 = Post-Crash/Post-Surge (F3, promossi dopo il run eventstudy sul campo).
        Assert.Equal(14, SignalCatalog.SignalCount);
        Assert.Equal("Ora UTC", SignalCatalog.SignalNames[9]);
        Assert.Equal("MFI", SignalCatalog.SignalNames[10]);
        Assert.Equal("OBV slope pct", SignalCatalog.SignalNames[11]);
        Assert.Equal("Post-Crash", SignalCatalog.SignalNames[12]);
        Assert.Equal("Post-Surge", SignalCatalog.SignalNames[13]);
        Assert.Equal(SignalCatalog.SignalCount, SignalCatalog.SignalNames.Count);

        var candles = HourlyCandles(48);
        var matrix = await SignalCatalog.GetMatrixAsync(candles, new TechnicalIndicatorsService(), CancellationToken.None);

        Assert.Equal(SignalCatalog.SignalCount, matrix.Length);
        for (var i = 0; i < candles.Count; i++)
        {
            // Scala 0-100: ora 0 → 0, ora 23 → 100. Nessun warm-up: il valore c'è dalla prima barra.
            var expected = candles[i].TimestampUtc.Hour * 100m / 23m;
            Assert.Equal(expected, matrix[9][i]);
        }
    }

    [Fact]
    public async Task HourSignal_DependsOnlyOnItsOwnTimestamp_NoLookAheadPossible()
    {
        // L'invariante di troncamento del catalogo, applicata al segnale ora: il valore alla barra i
        // è identico sulla serie piena e su una troncata subito dopo i.
        var full = HourlyCandles(48);
        var truncated = full.Take(20).ToList();

        var mFull = await SignalCatalog.GetMatrixAsync(full, new TechnicalIndicatorsService(), CancellationToken.None);
        var mTrunc = await SignalCatalog.GetMatrixAsync(truncated, new TechnicalIndicatorsService(), CancellationToken.None);

        for (var i = 0; i < truncated.Count; i++)
        {
            Assert.Equal(mFull[9][i], mTrunc[9][i]);
        }
    }
}
