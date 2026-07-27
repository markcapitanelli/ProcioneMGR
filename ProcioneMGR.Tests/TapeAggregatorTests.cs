using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D3 / C5 §9.2] Aggregazione del tape in barre da N secondi. I test guardano i BORDI, che è dove
/// un aggregatore sbaglia in silenzio: un trade esattamente sul confine, una barra senza scambi, un
/// trade del giorno dopo in coda al file (i dump giornalieri di Binance ne contengono).
/// </summary>
public class TapeAggregatorTests
{
    private static readonly DateTime Day = new(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

    private static AggTrade Trade(int secondsFromMidnight, decimal qty, bool takerBuy, int id = 1) =>
        new(id, 100m, qty, Day.AddSeconds(secondsFromMidnight), IsBuyerMaker: !takerBuy);

    [Fact]
    public void ATradeExactlyOnTheBoundary_BelongsToTheNewBar()
    {
        // Convenzione [inizio, fine): il trade a 10,000 s appartiene alla seconda barra da 10s. Se
        // finisse nella prima, ogni misura "ultimi 10 secondi" sarebbe spostata di una barra.
        var trades = new[] { Trade(9, 1m, true), Trade(10, 2m, true) };

        var bars = TapeAggregator.Aggregate(trades, TimeSpan.FromSeconds(10), Day, Day.AddSeconds(20));

        Assert.Equal(2, bars.Count);
        Assert.Equal(1m, bars[0].BuyVolume);
        Assert.Equal(2m, bars[1].BuyVolume);
    }

    [Fact]
    public void EmptyBarsAreKept_SoTheGridStaysRegular()
    {
        // Omettere le barre vuote farebbe scivolare indietro nel tempo ogni finestra "ultimi N
        // secondi" di quanto è durato il silenzio.
        var trades = new[] { Trade(1, 1m, true), Trade(35, 1m, false) };

        var bars = TapeAggregator.Aggregate(trades, TimeSpan.FromSeconds(10), Day, Day.AddSeconds(40));

        Assert.Equal(4, bars.Count);
        Assert.True(bars[1].IsEmpty);
        Assert.True(bars[2].IsEmpty);
        Assert.Null(bars[1].Imbalance); // vuota = nessun verdetto, non "equilibrio perfetto"
        Assert.Equal(0, bars[1].TradeCount);
    }

    [Fact]
    public void TradesOutsideTheWindowAreIgnored()
    {
        var trades = new[] { Trade(-5, 5m, true), Trade(5, 1m, true), Trade(3600 * 24, 7m, true) };

        var bars = TapeAggregator.Aggregate(trades, TimeSpan.FromSeconds(10), Day, Day.AddSeconds(20));

        Assert.Equal(1m, bars.Sum(b => b.BuyVolume));
    }

    [Fact]
    public void TheVolumeIsConserved_NothingIsLostOrCountedTwice()
    {
        // Riferimento indipendente: la somma dei volumi delle barre deve fare la somma dei volumi dei
        // trade. È il controllo che un bucketing sbagliato non può superare.
        var rnd = new Random(7);
        var trades = Enumerable.Range(0, 5000)
            .Select(i => Trade(rnd.Next(0, 3600), (decimal)Math.Round(rnd.NextDouble() * 3, 6), rnd.Next(2) == 0, i))
            .OrderBy(t => t.TimestampUtc)
            .ToList();

        var bars = TapeAggregator.Aggregate(trades, TimeSpan.FromSeconds(10), Day, Day.AddHours(1));

        Assert.Equal(trades.Where(t => t.IsTakerBuy).Sum(t => t.Quantity), bars.Sum(b => b.BuyVolume));
        Assert.Equal(trades.Where(t => !t.IsTakerBuy).Sum(t => t.Quantity), bars.Sum(b => b.SellVolume));
        Assert.Equal(trades.Count, bars.Sum(b => b.TradeCount));
        Assert.Equal(360, bars.Count);
    }

    [Fact]
    public void TheCloseOfABar_IsThePriceOfItsLastTrade()
    {
        var trades = new[]
        {
            new AggTrade(1, 100m, 1m, Day.AddSeconds(1), false),
            new AggTrade(2, 105m, 1m, Day.AddSeconds(9), false),
        };

        var bars = TapeAggregator.Aggregate(trades, TimeSpan.FromSeconds(10), Day, Day.AddSeconds(10));

        Assert.Equal(105m, bars[0].Close);
    }

    [Fact]
    public void GroupBy_RefusesADurationThatIsNotAnIntegerMultiple()
    {
        var bars = TapeAggregator.Aggregate([Trade(1, 1m, true)], TimeSpan.FromSeconds(10), Day, Day.AddSeconds(60));

        // 25 s non è multiplo di 10 s: raggruppare taglierebbe a metà una barra fine e nessuno se ne
        // accorgerebbe. Meglio un'eccezione di un raggruppamento silenziosamente storto.
        Assert.Throws<ArgumentException>(() => TapeAggregator.GroupBy(bars, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void GroupBy_DropsTheTruncatedFirstGroupInsteadOfMixingIt()
    {
        // Si parte a 00:00:20 con barre da 10s: il primo minuto completo comincia a 00:01:00, e le
        // quattro barre iniziali NON devono finire nel primo gruppo buono.
        var start = Day.AddSeconds(20);
        var bars = TapeAggregator.Aggregate(
            [new AggTrade(1, 100m, 1m, start.AddSeconds(1), false)],
            TimeSpan.FromSeconds(10), start, start.AddSeconds(160));

        var groups = TapeAggregator.GroupBy(bars, TimeSpan.FromMinutes(1));

        // Due minuti completi (00:01 e 00:02); le quattro barre iniziali di 00:00:20-00:00:50
        // appartengono a un minuto troncato e restano fuori.
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(6, g.Count));
        Assert.Equal(Day.AddMinutes(1), groups[0][0].StartUtc);
        Assert.Equal(Day.AddMinutes(2), groups[1][0].StartUtc);
    }

    [Fact]
    public void GroupImbalance_AggregatesVolumesNotAverages()
    {
        // Media delle imbalance ≠ imbalance del gruppo: una barra da 1 unità e una da 99 non pesano
        // uguale. Il gruppo va ricostruito dai volumi.
        var bars = new List<TapeBar>
        {
            new(Day, TimeSpan.FromSeconds(10), 1m, 0m, 1, 100m),      // imbalance +1
            new(Day.AddSeconds(10), TimeSpan.FromSeconds(10), 0m, 99m, 1, 100m), // imbalance −1
        };

        var imbalance = TapeAggregator.Imbalance(bars);

        Assert.Equal((1m - 99m) / 100m, imbalance);
        Assert.NotEqual(0m, imbalance); // la media delle due imbalance sarebbe esattamente zero
    }

    [Fact]
    public void AnEmptyGroup_HasNoImbalance()
    {
        var bars = new List<TapeBar> { new(Day, TimeSpan.FromSeconds(10), 0m, 0m, 0, null) };

        Assert.Null(TapeAggregator.Imbalance(bars));
    }
}
