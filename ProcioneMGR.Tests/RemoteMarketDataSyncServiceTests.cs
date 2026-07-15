using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RemoteMarketDataSyncService"/> (Fase 1 microservizi): il client HTTP verso
/// il servizio Ingestion remoto, esercitato con un handler mock (nessuna rete reale).
/// </summary>
public class RemoteMarketDataSyncServiceTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task SyncSeriesAsync_PostsToSyncEndpoint_AndReturnsCandlesProcessed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"candlesProcessed":7}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ingestion.local") };
        var sut = new RemoteMarketDataSyncService(http, NullLogger<RemoteMarketDataSyncService>.Instance);

        var n = await sut.SyncSeriesAsync(42);

        Assert.Equal(7, n);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/sync/42", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SyncSeriesAsync_Throws_OnNonSuccessStatus()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ingestion.local") };
        var sut = new RemoteMarketDataSyncService(http, NullLogger<RemoteMarketDataSyncService>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.SyncSeriesAsync(1));
    }

    [Fact]
    public async Task SyncAllEnabledAsync_Throws_BecauseSchedulingLivesInRemoteWorker()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://ingestion.local") };
        var sut = new RemoteMarketDataSyncService(http, NullLogger<RemoteMarketDataSyncService>.Instance);

        // In modalità remota il ciclo periodico è del worker nel servizio Ingestion, non del monolite:
        // il metodo non deve avere chiamanti, e lancia se invocato per errore.
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.SyncAllEnabledAsync());
    }
}
