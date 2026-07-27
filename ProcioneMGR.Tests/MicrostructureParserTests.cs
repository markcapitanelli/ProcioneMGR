using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D3] Lettura dei dump Binance. Le righe usate qui sono COPIATE dai file veri (spot e futures del
/// 2026-07-25), non inventate: i due formati differiscono in tre punti — header, unità del timestamp,
/// maiuscole del booleano — e un parser tarato su uno solo leggerebbe l'altro producendo barre tutte
/// vuote tranne una. Che poi darebbe IC zero, indistinguibile dal verdetto "nessuna informazione".
/// </summary>
public class MicrostructureParserTests
{
    // Righe reali: spot (nessun header, timestamp in microsecondi, "False"/"True").
    private const string SpotAggTrades =
        """
        4022022976,64140.00000000,0.00007000,6532815037,6532815037,1784937600171020,False,True
        4022022977,64140.00000000,0.00020000,6532815038,6532815038,1784937600263133,True,True
        """;

    // Righe reali: futures USD-M (header, timestamp in millisecondi, "false"/"true").
    private const string FuturesAggTrades =
        """
        agg_trade_id,price,quantity,first_trade_id,last_trade_id,transact_time,is_buyer_maker
        3392954217,64116.5,0.002,7926941418,7926941418,1784937600157,false
        3392954218,64116.5,0.252,7926941419,7926941424,1784937600545,true
        """;

    [Fact]
    public void SpotAggTrades_AreReadWithoutHeaderAndWithMicrosecondTimestamps()
    {
        var parser = new BinanceDumpParser();

        var trades = parser.ReadAggTrades(new StringReader(SpotAggTrades)).ToList();

        Assert.Equal(2, trades.Count);
        Assert.Equal(0, parser.MalformedLines);
        // 1784937600171020 µs = mezzanotte del 25 luglio 2026 + 171020 µs (= 1.710.200 tick).
        var midnight = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(midnight.AddTicks(1_710_200), trades[0].TimestampUtc);
        Assert.Equal(64140.00000000m, trades[0].Price);
        Assert.Equal(0.00007000m, trades[0].Quantity);
    }

    [Fact]
    public void FuturesAggTrades_SkipTheHeaderAndUseMillisecondTimestamps()
    {
        var parser = new BinanceDumpParser();

        var trades = parser.ReadAggTrades(new StringReader(FuturesAggTrades)).ToList();

        Assert.Equal(2, trades.Count);
        Assert.Equal(0, parser.MalformedLines);
        Assert.Equal(new DateTime(2026, 7, 25, 0, 0, 0, 157, DateTimeKind.Utc), trades[0].TimestampUtc);
        Assert.Equal(0.002m, trades[0].Quantity);
    }

    [Fact]
    public void IsBuyerMaker_MeansTheAggressorWasTheSeller()
    {
        // È LA convenzione da non sbagliare: se il compratore era il maker, chi ha attraversato lo
        // spread era il venditore. Invertirla capovolgerebbe l'intero order flow e il segno di ogni
        // misura successiva, restando plausibile a occhio.
        var parser = new BinanceDumpParser();

        var trades = parser.ReadAggTrades(new StringReader(SpotAggTrades)).ToList();

        Assert.False(trades[0].IsBuyerMaker);
        Assert.True(trades[0].IsTakerBuy);   // riga con False → aggressore in acquisto
        Assert.True(trades[1].IsBuyerMaker);
        Assert.False(trades[1].IsTakerBuy);  // riga con True → aggressore in vendita
    }

    [Theory]
    [InlineData(1784937600157L, "2026-07-25T00:00:00.1570000")]      // millisecondi
    [InlineData(1784937600171020L, "2026-07-25T00:00:00.1710200")]   // microsecondi
    public void Epoch_UnitIsDeducedFromTheOrderOfMagnitude(long value, string expected)
    {
        var parsed = BinanceDumpParser.FromEpoch(value);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void MalformedLines_AreCountedNotSilentlyDropped()
    {
        // Un file troncato a metà download deve potersi accorgere di esserlo: contare le righe rotte
        // è la differenza fra "quel giorno il mercato era fermo" e "quel giorno il file era rotto".
        var parser = new BinanceDumpParser();
        var csv = "1,100,1,1,1,1784937600157,false\nrotta,rotta\n3,100,1,1,1,ora-sbagliata,false\n";

        var trades = parser.ReadAggTrades(new StringReader(csv)).ToList();

        Assert.Single(trades);
        Assert.Equal(2, parser.MalformedLines);
        Assert.Equal(1, parser.ParsedLines);
    }

    [Fact]
    public void BookDepth_GroupsTheRowsOfEachSnapshotAndExposesTheBands()
    {
        var parser = new BinanceDumpParser();
        var csv =
            """
            timestamp,percentage,depth,notional
            2026-07-25 00:00:01,-1.00,10.0,1000.0
            2026-07-25 00:00:01,1.00,5.0,600.0
            2026-07-25 00:00:31,-1.00,8.0,800.0
            2026-07-25 00:00:31,1.00,9.0,1200.0
            """;

        var snapshots = parser.ReadBookDepth(new StringReader(csv)).ToList();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(new DateTime(2026, 7, 25, 0, 0, 1, DateTimeKind.Utc), snapshots[0].TimestampUtc);
        Assert.Equal(1000.0m, snapshots[0].BidNotional(1m));
        Assert.Equal(600.0m, snapshots[0].AskNotional(1m));
        // (1000 − 600)/1600 = 0,25 → book sbilanciato in acquisto.
        Assert.Equal(0.25m, snapshots[0].Imbalance(1m));
        // L'ultimo snapshot del file deve essere emesso: senza il flush finale si perderebbe.
        Assert.Equal(1200.0m, snapshots[1].AskNotional(1m));
    }

    [Fact]
    public void BookDepth_AMissingBand_IsNullNotZero()
    {
        var parser = new BinanceDumpParser();
        var csv = "timestamp,percentage,depth,notional\n2026-07-25 00:00:01,-1.00,10.0,1000.0\n";

        var snapshot = parser.ReadBookDepth(new StringReader(csv)).Single();

        Assert.Null(snapshot.AskNotional(1m));
        Assert.Null(snapshot.Imbalance(1m)); // metà book = nessun verdetto, non "equilibrio"
    }

    [Fact]
    public void Klines_AreReadIntoTheSameOhlcvEntityAsThePlatformCandles()
    {
        // Non è comodità: il proxy del gate si calcola col TakerImbalanceFactor VERO, quello che gira
        // in produzione, e non con una riscrittura per l'occasione che potrebbe differire proprio nel
        // punto in discussione.
        var parser = new BinanceDumpParser();
        var csv =
            """
            open_time,open,high,low,close,volume,close_time,quote_volume,count,taker_buy_volume,taker_buy_quote_volume,ignore
            1784937600000,64116.50,64125.40,64105.60,64105.70,68.743,1784937659999,4407801.56290,1280,31.313,2007822.61190,0
            """;

        var candle = parser.ReadKlines(new StringReader(csv), "BTC/USDT", "1m").Single();

        Assert.Equal(new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc), candle.TimestampUtc);
        Assert.Equal(64105.70m, candle.Close);
        Assert.Equal(68.743m, candle.Volume);
        Assert.Equal(31.313m, candle.TakerBuyVolume);
        Assert.Equal(1280L, candle.TradeCount);
        Assert.Equal("BTC/USDT", candle.Symbol);
    }
}
