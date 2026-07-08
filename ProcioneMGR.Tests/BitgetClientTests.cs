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
}
