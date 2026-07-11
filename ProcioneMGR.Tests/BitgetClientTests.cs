using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione per un bug reale trovato verificando dal vivo le credenziali Bitget appena
/// configurate dall'utente: <see cref="BitgetClient.GetFuturesBalanceAsync"/> usava l'endpoint
/// "singolo account" (.../account/account) senza il parametro "symbol" che Bitget richiede,
/// ottenendo sempre un errore applicativo (code 400172 "Parameter verification failed") — errore
/// che veniva ingoiato silenziosamente restituendo un FuturesBalance vuoto, indistinguibile da un
/// vero saldo zero. Fix: usa l'endpoint "lista account" (.../account/accounts), che con il solo
/// productType restituisce l'array di conti per moneta di margine.
/// </summary>
public class BitgetClientTests
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

    [Fact]
    public async Task GetFuturesBalanceAsync_ParsesUsdtAccount_FromAccountsListEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"code":"00000","msg":"success","requestTime":1,"data":[
                {"marginCoin":"USDT","available":"123.45","accountEquity":"200.00"},
                {"marginCoin":"USDC","available":"9.00","accountEquity":"9.00"}
            ]}
            """);
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var balance = await client.GetFuturesBalanceAsync(Creds);

        Assert.Equal(123.45m, balance.AvailableMargin);
        Assert.Equal(200.00m, balance.TotalEquity);
        Assert.Contains("/api/v2/mix/account/accounts", handler.LastRequest!.RequestUri!.ToString());
        Assert.DoesNotContain("symbol=", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetFuturesBalanceAsync_EmptyAccountsArray_ReturnsZeroWithoutThrowing()
    {
        // Lo scenario reale riscontrato: il conto Demo Trading esiste ma non ha ancora
        // un sotto-conto Futures/USDT valorizzato -> Bitget risponde OK con data=[].
        var handler = new FakeHandler(HttpStatusCode.OK, """{"code":"00000","msg":"success","requestTime":1,"data":[]}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var balance = await client.GetFuturesBalanceAsync(Creds);

        Assert.Equal(0m, balance.AvailableMargin);
        Assert.Equal(0m, balance.TotalEquity);
    }

    [Fact]
    public async Task GetFuturesBalanceAsync_ApplicationError_ReturnsZero_DoesNotThrow()
    {
        // Il bug originale: senza "symbol" l'endpoint SINGOLARE rispondeva con questo errore.
        // Qui verifichiamo solo che un errore applicativo Bitget (code != "00000") non esploda,
        // qualunque endpoint venga chiamato in futuro.
        var handler = new FakeHandler(HttpStatusCode.OK, """{"code":"400172","msg":"Parameter verification failed","requestTime":1,"data":null}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var balance = await client.GetFuturesBalanceAsync(Creds);

        Assert.Equal(0m, balance.AvailableMargin);
    }

    // --- Lookup stato ordine per clientOid (fix C2) ---------------------------------------------

    [Fact]
    public async Task GetOrderStatusAsync_SpotFilled_ParsesPriceAvgBaseVolumeAndOrderId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"code":"00000","msg":"success","requestTime":1,"data":[
                {"orderId":"btg-1","clientOid":"cid-1","status":"filled","priceAvg":"101.5","baseVolume":"80","symbol":"BTCUSDT"}
            ]}
            """);
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-1", Creds);

        Assert.True(result.Found);
        Assert.Equal("Filled", result.Status);
        Assert.Equal(101.5m, result.FilledPrice);
        Assert.Equal(80m, result.FilledQuantity);
        Assert.Equal("btg-1", result.ExchangeOrderId);

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("/api/v2/spot/trade/orderInfo", url);
        Assert.Contains("clientOid=cid-1", url);
    }

    [Fact]
    public async Task GetOrderStatusAsync_SpotLiveUnfilled_EmptyDecimalFieldsTolerated()
    {
        // Bitget usa stringhe vuote per i campi non ancora valorizzati: niente FormatException.
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"code":"00000","msg":"success","requestTime":1,"data":[
                {"orderId":"btg-2","clientOid":"cid-2","status":"live","priceAvg":"","baseVolume":""}
            ]}
            """);
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-2", Creds);

        Assert.True(result.Found);
        Assert.Equal("Open", result.Status);
        Assert.Null(result.FilledPrice);
        Assert.Null(result.FilledQuantity);
    }

    [Fact]
    public async Task GetOrderStatusAsync_EmptyDataArray_IsCertainNotFound()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"code":"00000","msg":"success","requestTime":1,"data":[]}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-x", Creds);

        Assert.False(result.Found);
        Assert.False(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_Error43001_IsCertainNotFound_NotUncertain()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"code":"43001","msg":"The order does not exist","requestTime":1,"data":null}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-x", Creds);

        Assert.False(result.Found);
        Assert.False(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_OtherApplicationError_IsUncertain_NeverNotFound()
    {
        // Dichiarare "non trovato" un errore generico riaprirebbe la finestra dell'ordine
        // duplicato che il fix C2 chiude: deve restare IGNOTO (il riconciliatore ritenta).
        var handler = new FakeHandler(HttpStatusCode.OK, """{"code":"40009","msg":"sign signature error","requestTime":1,"data":null}""");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-x", Creds);

        Assert.False(result.Found);
        Assert.True(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetOrderStatusAsync_Http500_IsUncertain()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "Bad Gateway");
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetOrderStatusAsync("BTC/USDT", "cid-x", Creds);

        Assert.False(result.Found);
        Assert.True(result.NetworkUncertain);
    }

    [Fact]
    public async Task GetFuturesOrderStatusAsync_MixDetail_ParsesStateFieldAndDemoProductType()
    {
        // L'endpoint mix restituisce un OGGETTO (non array) e chiama lo stato "state" (non "status").
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"code":"00000","msg":"success","requestTime":1,"data":
                {"orderId":"btg-f1","clientOid":"cid-f","state":"filled","priceAvg":"27100.5","baseVolume":"0.04"}
            }
            """);
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var result = await client.GetFuturesOrderStatusAsync("BTC/USDT", "cid-f", Creds);

        Assert.True(result.Found);
        Assert.Equal("Filled", result.Status);
        Assert.Equal(27_100.5m, result.FilledPrice);
        Assert.Equal(0.04m, result.FilledQuantity);
        Assert.Equal("btg-f1", result.ExchangeOrderId);

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("/api/v2/mix/order/detail", url);
        Assert.Contains("productType=SUSDT-FUTURES", url);   // credenziali demo → productType demo
        Assert.Contains("clientOid=cid-f", url);
        Assert.Contains("symbol=BTCUSDT", url);
    }

    [Theory]
    [InlineData("live", "Open")]
    [InlineData("new", "Open")]
    [InlineData("init", "Open")]
    [InlineData("not_trigger", "Open")]
    [InlineData("partially_filled", "PartiallyFilled")]
    [InlineData("partial-fill", "PartiallyFilled")]
    [InlineData("filled", "Filled")]
    [InlineData("full-fill", "Filled")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("canceled", "Cancelled")]
    [InlineData("rejected", "Rejected")]
    [InlineData("stato_nuovo_ignoto", "Open")]   // ignoto → vivo: il riconciliatore cancella e ricontrolla
    public void NormalizeBitgetOrderStatus_MapsToCommonSchema(string exchange, string expected)
        => Assert.Equal(expected, BitgetClient.NormalizeBitgetOrderStatus(exchange));

    // --- [M4] Recupero dei fill reali dopo il place --------------------------------------------

    /// <summary>Handler a CODA di risposte: ogni richiesta consuma la successiva (place → lookup → …).</summary>
    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _queue = new(responses);
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            var (status, body) = _queue.Count > 0 ? _queue.Dequeue()
                : throw new InvalidOperationException("Richiesta HTTP oltre lo script del test.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task PlaceOrderAsync_LookupFilled_PopulatesRealFill()
    {
        // Bitget NON restituisce i fill nel POST di place: senza il lookup M4 l'engine ripiegava
        // sempre su currentPrice e lo slippage reale restava invisibile.
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":{"orderId":"btg-9","clientOid":"cid-9"}}"""),
            (HttpStatusCode.OK, """
                {"code":"00000","msg":"success","requestTime":1,"data":[
                    {"orderId":"btg-9","clientOid":"cid-9","status":"filled","priceAvg":"100.35","baseVolume":"2"}
                ]}
                """));
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var res = await client.PlaceOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "BUY", Type = "MARKET", Quantity = 2m,
            ClientOrderId = "cid-9", Credentials = Creds,
        });

        Assert.True(res.Success);
        Assert.Equal(100.35m, res.FilledPrice);
        Assert.Equal(2m, res.FilledQuantity);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/api/v2/spot/trade/orderInfo") && u.Contains("clientOid=cid-9"));
    }

    [Fact]
    public async Task PlaceOrderAsync_LookupFails_PlaceStillSucceeds_FillNull()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":{"orderId":"btg-9"}}"""),
            (HttpStatusCode.InternalServerError, "Bad Gateway"));
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var res = await client.PlaceOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "BUY", Type = "MARKET", Quantity = 2m,
            ClientOrderId = "cid-9", Credentials = Creds,
        });

        // Il place È riuscito: l'arricchimento best-effort non deve mai trasformarlo in errore.
        Assert.True(res.Success);
        Assert.Null(res.FilledPrice);
        Assert.Null(res.FilledQuantity);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_StillOpenThenFilled_OneRetryRecoversFill()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":{"orderId":"btg-f9"}}"""),
            (HttpStatusCode.OK, """{"code":"00000","msg":"success","requestTime":1,"data":{"orderId":"btg-f9","state":"live","priceAvg":"","baseVolume":""}}"""),
            (HttpStatusCode.OK, """{"code":"00000","msg":"success","requestTime":1,"data":{"orderId":"btg-f9","state":"filled","priceAvg":"27100.5","baseVolume":"0.04"}}"""));
        var client = new BitgetClient(new HttpClient(handler), NullLogger<BitgetClient>.Instance);

        var res = await client.PlaceFuturesOrderAsync(new PlaceOrderRequest
        {
            Symbol = "BTC/USDT", Side = "BUY", Type = "MARKET", Quantity = 0.04m,
            ClientOrderId = "cid-f9", Credentials = Creds,
        }, reduceOnly: false);

        Assert.True(res.Success);
        Assert.Equal(27_100.5m, res.FilledPrice);     // recuperato al 2° lookup (dopo 500ms)
        Assert.Equal(0.04m, res.FilledQuantity);
        Assert.Equal(3, handler.RequestedUrls.Count); // place + 2 lookup, non di più
    }
}
