using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del lookup di stato ordine per clientOrderId (fix C2): endpoint corretti, media di fill
/// ricavata da <c>cummulativeQuoteQty/executedQty</c> (l'endpoint di QUERY non restituisce
/// <c>fills[]</c> come il place), e soprattutto la distinzione che chiude la finestra dell'ordine
/// duplicato: -2013 ("Order does not exist") = NON TROVATO certo; qualunque altro errore =
/// IGNOTO (<see cref="OrderStatusResult.NetworkUncertain"/>), mai "non trovato".
/// </summary>
public class BinanceClientOrderStatusTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static readonly TradingCredentials Creds = new("key", "secret", null, IsTestnet: true);

    private static BinanceClient Client(FakeHandler handler) => new(new HttpClient(handler), NullLogger<BinanceClient>.Instance);

    // --- Spot ----------------------------------------------------------------------------------

    [Fact]
    public async Task GetOrderStatusAsync_Filled_AvgPriceFromCumulativeQuoteOverExecuted()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"symbol":"BTCUSDT","orderId":12345,"clientOrderId":"cid-1","status":"FILLED",
             "executedQty":"0.50000000","cummulativeQuoteQty":"50750.00000000","origQty":"0.5"}
            """);
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.True(result.Found);
        Assert.False(result.NetworkUncertain);
        Assert.Equal("Filled", result.Status);
        Assert.Equal(101_500m, result.FilledPrice);      // 50'750 / 0.5
        Assert.Equal(0.5m, result.FilledQuantity);
        Assert.Equal("12345", result.ExchangeOrderId);

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.StartsWith("https://testnet.binance.vision/api/v3/order", url);   // GET di query, non place
        Assert.Contains("origClientOrderId=cid-1", url);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GetOrderStatusAsync_OpenNotExecuted_NoFillFields()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"symbol":"BTCUSDT","orderId":7,"status":"NEW","executedQty":"0.00000000","cummulativeQuoteQty":"0.00000000"}
            """);
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.True(result.Found);
        Assert.Equal("Open", result.Status);
        Assert.Null(result.FilledPrice);
        Assert.Null(result.FilledQuantity);
        Assert.False(result.IsTerminalUnfilled);
    }

    [Fact]
    public async Task GetOrderStatusAsync_Error2013_IsCertainNotFound_NotUncertain()
    {
        // "Order does not exist": l'unico esito che autorizza il retry sicuro alla candela dopo.
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"code":-2013,"msg":"Order does not exist."}""");
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.False(result.Found);
        Assert.False(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_OtherApiError_IsUncertain_NeverNotFound()
    {
        // Qualunque altro errore (-1021 timestamp, ban, ecc.): dichiararlo "non trovato"
        // riaprirebbe la finestra dell'ordine duplicato. Deve restare IGNOTO.
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"code":-1021,"msg":"Timestamp out of recv window."}""");
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.False(result.Found);
        Assert.True(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_Http500_IsUncertain()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "Internal Server Error");
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.False(result.Found);
        Assert.True(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_CancelledUnfilled_IsTerminalUnfilled()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"symbol":"BTCUSDT","orderId":9,"status":"CANCELED","executedQty":"0.00000000","cummulativeQuoteQty":"0"}
            """);
        var result = await Client(handler).GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.True(result.Found);
        Assert.Equal("Cancelled", result.Status);
        Assert.True(result.IsTerminalUnfilled);
    }

    // --- Futures -------------------------------------------------------------------------------

    [Fact]
    public async Task GetFuturesOrderStatusAsync_Filled_UsesFapiEndpointAndAvgPrice()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"symbol":"BTCUSDT","orderId":555,"status":"FILLED","executedQty":"0.040","avgPrice":"101250.5"}
            """);
        var result = await Client(handler).GetFuturesOrderStatusAsync("BTC/USDT", "cid-f", Creds);

        Assert.True(result.Found);
        Assert.Equal("Filled", result.Status);
        Assert.Equal(101_250.5m, result.FilledPrice);    // i futures riportano avgPrice direttamente
        Assert.Equal(0.040m, result.FilledQuantity);
        Assert.Equal("555", result.ExchangeOrderId);

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.StartsWith("https://testnet.binancefuture.com/fapi/v1/order", url);
        Assert.Contains("origClientOrderId=cid-f", url);
    }

    [Fact]
    public async Task GetFuturesOrderStatusAsync_NewOrder_AvgPriceZeroTreatedAsNoFill()
    {
        // Binance futures usa avgPrice="0" finché non c'è esecuzione: non è un prezzo valido.
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"symbol":"BTCUSDT","orderId":556,"status":"NEW","executedQty":"0","avgPrice":"0"}
            """);
        var result = await Client(handler).GetFuturesOrderStatusAsync("BTC/USDT", "cid-f", Creds);

        Assert.True(result.Found);
        Assert.Equal("Open", result.Status);
        Assert.Null(result.FilledPrice);
        Assert.Null(result.FilledQuantity);
    }

    // --- Normalizzazione -----------------------------------------------------------------------

    [Theory]
    [InlineData("NEW", "Open")]
    [InlineData("PENDING_NEW", "Open")]
    [InlineData("PENDING_CANCEL", "Open")]
    [InlineData("PARTIALLY_FILLED", "PartiallyFilled")]
    [InlineData("FILLED", "Filled")]
    [InlineData("CANCELED", "Cancelled")]
    [InlineData("REJECTED", "Rejected")]
    [InlineData("EXPIRED", "Expired")]
    [InlineData("EXPIRED_IN_MATCH", "Expired")]
    [InlineData("SOME_FUTURE_STATE", "Open")]   // ignoto → vivo: il riconciliatore cancella e ricontrolla
    public void NormalizeBinanceOrderStatus_MapsToCommonSchema(string exchange, string expected)
        => Assert.Equal(expected, BinanceClient.NormalizeBinanceOrderStatus(exchange));
}
