using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R1] Test dei parser degli stream WebSocket. Nessuna rete: si passano al mapper esattamente i
/// payload che gli exchange pubblicano.
///
/// Il requisito trasversale più importante è la TOLLERANZA: un frame inatteso, malformato o di un
/// canale che non usiamo deve produrre "niente di utile", MAI un'eccezione — un parser che lancia
/// farebbe cadere la connessione (e quindi silenziare gli stop) per un messaggio irrilevante.
/// </summary>
public class RealtimeStreamMapperTests
{
    private static IReadOnlyDictionary<string, StreamSubscription> BinanceIndex() =>
        new Dictionary<string, StreamSubscription>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"] = new(ExchangeName.Binance, "BTC/USDT", "5m", MarketType.Spot),
        };

    private static IReadOnlyDictionary<string, StreamSubscription> BitgetIndex() =>
        new Dictionary<string, StreamSubscription>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"] = new(ExchangeName.Bitget, "BTC/USDT", "5m", MarketType.Futures),
        };

    // ------------------------------------------------------------------ Binance

    [Fact]
    public void Binance_BookTicker_ParsedAsTick()
    {
        const string raw = """
            {"stream":"btcusdt@bookTicker","data":{"u":400,"s":"BTCUSDT","b":"60000.10","B":"1.5","a":"60000.30","A":"2.0"}}
            """;

        var evt = new BinanceStreamMapper().Parse(raw, BinanceIndex());

        var tick = Assert.NotNull(evt.Tick);
        Assert.Equal("BTC/USDT", tick.Symbol);        // simbolo CANONICO, non quello dell'exchange
        Assert.Equal(60000.10m, tick.Bid);
        Assert.Equal(60000.30m, tick.Ask);
        Assert.Equal(60000.20m, tick.Mid);
    }

    [Fact]
    public void Binance_ClosedKline_ParsedAsBar()
    {
        const string raw = """
            {"stream":"btcusdt@kline_5m","data":{"e":"kline","s":"BTCUSDT","k":{
              "t":1767225600000,"i":"5m","o":"100.0","c":"104.0","h":"105.0","l":"99.0","v":"12.5","x":true}}}
            """;

        var evt = new BinanceStreamMapper().Parse(raw, BinanceIndex());

        var bar = Assert.NotNull(evt.Bar);
        Assert.Equal("BTC/USDT", bar.Symbol);
        Assert.Equal("5m", bar.Timeframe);
        Assert.Equal(100.0m, bar.Open);
        Assert.Equal(105.0m, bar.High);
        Assert.Equal(99.0m, bar.Low);
        Assert.Equal(104.0m, bar.Close);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000).UtcDateTime, bar.OpenTimeUtc);
    }

    [Fact]
    public void Binance_UnclosedKline_IsIgnored()
    {
        // Una candela IN FORMAZIONE ha High/Low provvisori: darla alle strategie significherebbe
        // valutare segnali su una barra che può ancora cambiare.
        const string raw = """
            {"stream":"btcusdt@kline_5m","data":{"e":"kline","s":"BTCUSDT","k":{
              "t":1767225600000,"i":"5m","o":"100.0","c":"104.0","h":"105.0","l":"99.0","v":"12.5","x":false}}}
            """;

        Assert.True(new BinanceStreamMapper().Parse(raw, BinanceIndex()).IsEmpty);
    }

    [Fact]
    public void Binance_UnknownSymbol_IsIgnored()
    {
        const string raw = """
            {"stream":"ethusdt@bookTicker","data":{"s":"ETHUSDT","b":"3000","B":"1","a":"3001","A":"1"}}
            """;

        Assert.True(new BinanceStreamMapper().Parse(raw, BinanceIndex()).IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non è json")]
    [InlineData("{ rotto")]
    [InlineData("[]")]                                          // regressione: vedi sotto
    [InlineData("""[{"s":"BTCUSDT"}]""")]
    [InlineData("""{"result":null,"id":1}""")]
    [InlineData("""{"stream":"x","data":{"s":"BTCUSDT"}}""")]
    [InlineData("""{"stream":"x","data":{"s":"BTCUSDT","k":[]}}""")]
    public void Binance_MalformedOrIrrelevant_NeverThrows(string raw)
    {
        // REGRESSIONE: su una radice (o un "k") che non è un oggetto, JsonElement.TryGetProperty
        // non ritorna false ma LANCIA InvalidOperationException — che non essendo una JsonException
        // sfuggiva al catch del parser e avrebbe abbattuto la connessione, silenziando gli stop,
        // per un frame del tutto irrilevante.
        Assert.True(new BinanceStreamMapper().Parse(raw, BinanceIndex()).IsEmpty);
    }

    [Fact]
    public void Binance_Endpoint_ContainsBothChannelsPerSymbol()
    {
        var subs = new[] { new StreamSubscription(ExchangeName.Binance, "BTC/USDT", "5m", MarketType.Spot) };

        var uri = new BinanceStreamMapper().BuildEndpoint(subs).ToString();

        Assert.Contains("btcusdt@bookTicker", uri, StringComparison.Ordinal);
        Assert.Contains("btcusdt@kline_5m", uri, StringComparison.Ordinal);
        Assert.StartsWith("wss://stream.binance.com", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Binance_Endpoint_SeparatesSpotFromFutures()
    {
        // Spot e futures vivono su domini diversi: mescolarli in una connessione sola darebbe
        // sottoscrizioni silenziosamente morte, quindi si fallisce forte invece.
        var mixed = new[]
        {
            new StreamSubscription(ExchangeName.Binance, "BTC/USDT", "5m", MarketType.Spot),
            new StreamSubscription(ExchangeName.Binance, "ETH/USDT", "5m", MarketType.Futures),
        };

        Assert.Throws<ArgumentException>(() => new BinanceStreamMapper().BuildEndpoint(mixed));
    }

    [Fact]
    public void Binance_FuturesEndpoint_UsesFuturesDomain()
    {
        var subs = new[] { new StreamSubscription(ExchangeName.Binance, "BTC/USDT", "1m", MarketType.Futures) };

        Assert.StartsWith("wss://fstream.binance.com", new BinanceStreamMapper().BuildEndpoint(subs).ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Bitget

    [Fact]
    public void Bitget_Ticker_ParsedAsTick()
    {
        const string raw = """
            {"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"ticker","instId":"BTCUSDT"},
             "data":[{"instId":"BTCUSDT","lastPr":"60000.2","bidPr":"60000.1","askPr":"60000.3","ts":"1767225600000"}]}
            """;

        var evt = new BitgetStreamMapper().Parse(raw, BitgetIndex());

        var tick = Assert.NotNull(evt.Tick);
        Assert.Equal("BTC/USDT", tick.Symbol);
        Assert.Equal(60000.1m, tick.Bid);
        Assert.Equal(60000.3m, tick.Ask);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000).UtcDateTime, tick.TimestampUtc);
    }

    [Theory]
    [InlineData("pong")]
    [InlineData("""{"event":"subscribe","arg":{"channel":"ticker"}}""")]
    [InlineData("""{"event":"error","code":"30001","msg":"channel not exist"}""")]
    [InlineData("{ rotto")]
    [InlineData("")]
    public void Bitget_ControlFramesAndGarbage_NeverThrow(string raw)
    {
        Assert.True(new BitgetStreamMapper().Parse(raw, BitgetIndex()).IsEmpty);
    }

    [Fact]
    public void Bitget_SubscribeFrame_UsesPublicProductType()
    {
        // Il productType demo ("SUSDT-FUTURES") sui canali PUBBLICI non restituisce nulla: la stessa
        // lezione già appresa nel client REST (BitgetClient.PublicMarketProductType).
        var subs = new[] { new StreamSubscription(ExchangeName.Bitget, "BTC/USDT", "5m", MarketType.Futures) };

        var frame = Assert.Single(new BitgetStreamMapper().BuildSubscribeFrames(subs));

        Assert.Contains("\"USDT-FUTURES\"", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("SUSDT", frame, StringComparison.Ordinal);
        Assert.Contains("\"BTCUSDT\"", frame, StringComparison.Ordinal);
        Assert.Contains("subscribe", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Bitget_RequiresApplicationHeartbeat_BinanceDoesNot()
    {
        // Bitget chiude il canale dopo ~30s di silenzio applicativo; Binance usa i ping di
        // protocollo, a cui ClientWebSocket risponde da solo.
        Assert.Equal("ping", new BitgetStreamMapper().HeartbeatFrame);
        Assert.Null(new BinanceStreamMapper().HeartbeatFrame);
    }

    // ------------------------------------------------------------------ plausibilità del tick

    [Theory]
    [InlineData(100, 101, true)]    // normale
    [InlineData(0, 101, false)]     // bid nullo
    [InlineData(100, 0, false)]     // ask nullo
    [InlineData(101, 100, false)]   // book incrociato
    [InlineData(100, 110, false)]   // spread ~9.5%: quotazione stantia o rotta
    public void PriceTick_Plausibility_RejectsGarbageQuotes(decimal bid, decimal ask, bool expected)
    {
        var tick = new PriceTick(ExchangeName.Binance, "BTC/USDT", bid, ask, DateTime.UtcNow);

        Assert.Equal(expected, tick.IsPlausible(maxSpreadPercent: 2m));
    }
}
