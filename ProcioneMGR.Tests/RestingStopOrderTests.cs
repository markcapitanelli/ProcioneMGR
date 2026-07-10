using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>
/// [P0-5] Costruzione delle richieste per gli ordini TRIGGER reduce-only "resting" (stop-market /
/// take-profit-market) su Bitget e Binance. Verifica i parametri inviati SENZA rete (fake handler):
/// è ciò che si può controllare in modo deterministico prima della verifica dal vivo su Demo/Testnet.
/// </summary>
public class RestingStopOrderTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static readonly TradingCredentials Creds = new("key", "secret", "pass", IsTestnet: true);

    [Fact]
    public async Task Bitget_TriggerOrder_BuildsReduceOnlyPlanOrder()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"code":"00000","msg":"success","data":{"orderId":"plan-1"}}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var res = await client.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "SELL", Quantity = 0.5m, TriggerPrice = 25000m,
            ClientOrderId = "cid-1", Credentials = Creds,
        }, isStopLoss: true);

        Assert.True(res.Success);
        Assert.Equal("plan-1", res.ExchangeOrderId);
        Assert.Contains("/api/v2/mix/order/place-plan-order", handler.LastRequest!.RequestUri!.ToString());
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"planType\":\"normal_plan\"", handler.LastBody);
        Assert.Contains("\"reduceOnly\":\"YES\"", handler.LastBody);
        Assert.Contains("\"triggerType\":\"mark_price\"", handler.LastBody);
        Assert.Contains("\"triggerPrice\":\"25000\"", handler.LastBody);
        Assert.Contains("\"side\":\"sell\"", handler.LastBody);
    }

    [Fact]
    public async Task Binance_StopLoss_UsesStopMarketReduceOnlyMarkPrice()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"orderId":123,"status":"NEW"}""");
        var client = new BinanceClient(new HttpClient(handler), NullLogger<BinanceClient>.Instance);

        var res = await client.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "SELL", Quantity = 0.5m, TriggerPrice = 25000m,
            ClientOrderId = "cid-1", Credentials = Creds,
        }, isStopLoss: true);

        Assert.True(res.Success);
        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("/fapi/v1/order", url);
        Assert.Contains("type=STOP_MARKET", url);
        Assert.Contains("reduceOnly=true", url);
        Assert.Contains("workingType=MARK_PRICE", url);
        Assert.Contains("stopPrice=25000", url);
        Assert.Contains("side=SELL", url);
    }

    [Fact]
    public async Task Binance_TakeProfit_UsesTakeProfitMarket()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"orderId":124,"status":"NEW"}""");
        var client = new BinanceClient(new HttpClient(handler), NullLogger<BinanceClient>.Instance);

        await client.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "BUY", Quantity = 1m, TriggerPrice = 30000m,
            ClientOrderId = "cid-2", Credentials = Creds,
        }, isStopLoss: false);

        Assert.Contains("type=TAKE_PROFIT_MARKET", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task TriggerOrder_MissingTriggerPrice_FailsWithoutCallingExchange()
    {
        var bitgetHandler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var binanceHandler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var bitget = new BitgetClient(new HttpClient(bitgetHandler), NullLogger<BitgetClient>.Instance);
        var binance = new BinanceClient(new HttpClient(binanceHandler), NullLogger<BinanceClient>.Instance);

        var req = new PlaceOrderRequest { Symbol = "BTC/USDT", Side = "SELL", Quantity = 1m, Credentials = Creds }; // TriggerPrice null

        var b1 = await bitget.PlaceFuturesTriggerOrderAsync(req, isStopLoss: true);
        var b2 = await binance.PlaceFuturesTriggerOrderAsync(req, isStopLoss: true);

        Assert.False(b1.Success);
        Assert.False(b2.Success);
        Assert.Null(bitgetHandler.LastRequest);   // nessuna chiamata di rete
        Assert.Null(binanceHandler.LastRequest);
    }
}
