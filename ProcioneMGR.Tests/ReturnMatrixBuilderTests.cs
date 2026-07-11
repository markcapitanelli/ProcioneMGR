using ProcioneMGR.Data;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// <see cref="ReturnMatrixBuilder"/> è il punto in cui storici DISALLINEATI (simbolo quotato dopo,
/// buchi di ingestione, candele sporche) diventano la matrice allineata che gli allocatori e la PCA
/// richiedono: un errore qui sfalsa TUTTE le covarianze a valle senza sollevare eccezioni.
/// </summary>
public class ReturnMatrixBuilderTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Candle(string symbol, int hour, decimal close) => new()
    {
        Symbol = symbol, Timeframe = "1h", TimestampUtc = T0.AddHours(hour),
        Open = close, High = close, Low = close, Close = close, Volume = 10m,
    };

    [Fact]
    public void InnerJoin_UsesOnlyCommonTimestamps_AndComputesReturnsFromClose()
    {
        // A ha le ore 0-4; B parte DOPO (ore 1-4) e gli manca l'ora 3: comune = {1, 2, 4}.
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 100m), Candle("A", 2, 110m), Candle("A", 3, 999m), Candle("A", 4, 121m)],
            ["B"] = [Candle("B", 1, 50m), Candle("B", 2, 50m), Candle("B", 4, 60m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        // 3 timestamp comuni -> 2 rendimenti, timestamp = barre di ARRIVO (ore 2 e 4).
        Assert.Equal(2, result.ReturnCount);
        Assert.Equal([T0.AddHours(2), T0.AddHours(4)], result.Timestamps);

        // A: 100->110 (+10%), poi 110->121 (+10%) — l'ora 3 (999) NON entra perché manca a B.
        Assert.Equal([0.10m, 0.10m], result.ReturnsBySymbol["A"].Select(r => Math.Round(r, 10)));
        // B: 50->50 (0%), poi 50->60 (+20%).
        Assert.Equal([0m, 0.20m], result.ReturnsBySymbol["B"].Select(r => Math.Round(r, 10)));
    }

    [Fact]
    public void InputOrder_DoesNotMatter()
    {
        var shuffled = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 2, 110m), Candle("A", 0, 100m), Candle("A", 1, 100m)],
            ["B"] = [Candle("B", 1, 50m), Candle("B", 2, 55m), Candle("B", 0, 50m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(shuffled);

        Assert.Equal(2, result.ReturnCount);
        Assert.Equal([0m, 0.10m], result.ReturnsBySymbol["A"].Select(r => Math.Round(r, 10)));
        Assert.Equal([0m, 0.10m], result.ReturnsBySymbol["B"].Select(r => Math.Round(r, 10)));
    }

    [Fact]
    public void NonPositiveCloses_AreDiscardedBeforeTheJoin()
    {
        // La candela sporca (Close=0) dell'ora 1 di A viene scartata: l'ora 1 esce dal join per
        // TUTTI (come se mancasse), niente divisione per zero né rendimenti infiniti.
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 0m), Candle("A", 2, 110m)],
            ["B"] = [Candle("B", 0, 50m), Candle("B", 1, 51m), Candle("B", 2, 55m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        Assert.Equal(1, result.ReturnCount);
        Assert.Equal([T0.AddHours(2)], result.Timestamps);
        Assert.Equal(0.10m, Math.Round(result.ReturnsBySymbol["A"][0], 10));
        Assert.Equal(0.10m, Math.Round(result.ReturnsBySymbol["B"][0], 10));
    }

    [Fact]
    public void NoOverlap_ReturnsEmptyMatrix_NotException()
    {
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 101m)],
            ["B"] = [Candle("B", 10, 50m), Candle("B", 11, 51m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        Assert.Equal(0, result.ReturnCount);
        Assert.Empty(result.Timestamps);
        Assert.All(result.ReturnsBySymbol.Values, r => Assert.Empty(r));
    }

    [Fact]
    public void SingleCommonBar_YieldsNoReturns()
    {
        // 1 solo timestamp comune: nessuna coppia consecutiva, quindi zero rendimenti (degenere ok).
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 101m)],
            ["B"] = [Candle("B", 1, 50m), Candle("B", 2, 51m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        Assert.Equal(0, result.ReturnCount);
    }

    [Fact]
    public void EmptySeries_ReturnsEmptyMatrix()
    {
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 101m)],
            ["B"] = [],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        Assert.Equal(0, result.ReturnCount);
    }

    [Fact]
    public void DuplicateTimestamps_LastOneWins()
    {
        // Stessa ora ingerita due volte (re-ingestione): vince l'ultima, nessuna riga doppia.
        var input = new Dictionary<string, IReadOnlyList<OhlcvData>>
        {
            ["A"] = [Candle("A", 0, 100m), Candle("A", 1, 999m), Candle("A", 1, 110m)],
            ["B"] = [Candle("B", 0, 50m), Candle("B", 1, 55m)],
        };

        var result = ReturnMatrixBuilder.BuildAlignedReturns(input);

        Assert.Equal(1, result.ReturnCount);
        Assert.Equal(0.10m, Math.Round(result.ReturnsBySymbol["A"][0], 10));
    }
}
