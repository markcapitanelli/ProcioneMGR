using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R1] Test della disciplina verso le API REST degli exchange: ritiro sui rate-limit e
/// allineamento dell'orologio per le richieste firmate.
///
/// Entrambi nascono da guasti che colpiscono le CHIUSURE tanto quanto le aperture: un ordine di
/// stop rifiutato perché l'IP è in ban da 429, o perché il timestamp è fuori dalla recvWindow, è
/// una perdita reale — non un fastidio operativo.
/// </summary>
public class ExchangeRateLimitAndClockTests
{
    /// <summary>Handler terminale pilotato dal test: risponde secondo un copione e conta le chiamate.</summary>
    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var response = index < responses.Length ? responses[index] : responses[^1];
            // Ogni tentativo deve ricevere un'istanza propria: il chiamante la dispone.
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Content?.ReadAsStringAsync(ct).Result ?? string.Empty),
                Headers = { RetryAfter = response.Headers.RetryAfter },
            });
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var msg = new HttpResponseMessage(status) { Content = new StringContent("{}") };
        if (retryAfter is { } delta)
        {
            msg.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
        }
        return msg;
    }

    private static (HttpClient Client, ScriptedHandler Inner) BuildClient(
        ScriptedHandler inner, int requestsPerSecond = 1000, int maxRetries = 3)
    {
        var limiter = new ExchangeRateLimitHandler(
            NullLogger<ExchangeRateLimitHandler>.Instance, requestsPerSecond, maxRetries)
        {
            InnerHandler = inner,
        };
        return (new HttpClient(limiter) { BaseAddress = new Uri("https://example.test") }, inner);
    }

    [Fact]
    public async Task RateLimited_429_IsRetried_AndEventuallySucceeds()
    {
        var (client, inner) = BuildClient(new ScriptedHandler(
            Response(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1)),
            Response(HttpStatusCode.OK)));

        using var response = await client.GetAsync("/api/v3/order");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task RateLimited_GivesUpAfterMaxRetries_ReturningLastResponse()
    {
        // Non si ritenta all'infinito: dopo il tetto si restituisce l'ultima risposta e decide il
        // chiamante, che ha più contesto per sapere se ha senso insistere.
        var (client, inner) = BuildClient(
            new ScriptedHandler(Response(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1))),
            maxRetries: 2);

        using var response = await client.GetAsync("/api/v3/order");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, inner.Calls); // tentativo iniziale + 2 ritentativi
    }

    [Fact]
    public async Task Http418_IpBanned_IsAlsoTreatedAsRateLimit()
    {
        // Binance risponde 418 quando ha già bannato l'IP dopo 429 ripetuti: insistere lì è il
        // modo migliore per allungare il ban.
        var (client, inner) = BuildClient(new ScriptedHandler(
            Response((HttpStatusCode)418, TimeSpan.FromMilliseconds(1)),
            Response(HttpStatusCode.OK)));

        using var response = await client.GetAsync("/api/v3/order");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task ServerError_IsNotRetried()
    {
        // SCELTA DI SICUREZZA, non pigrizia: un 5xx su un piazzamento d'ordine lascia lo stato
        // INCERTO. Un retry cieco potrebbe duplicare un ordine reale; la riconciliazione
        // (OrderReconciler) verifica prima se l'ordine è passato.
        var (client, inner) = BuildClient(new ScriptedHandler(Response(HttpStatusCode.InternalServerError)));

        using var response = await client.GetAsync("/api/v3/order");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Success_PassesThroughUntouched()
    {
        var (client, inner) = BuildClient(new ScriptedHandler(Response(HttpStatusCode.OK)));

        using var response = await client.GetAsync("/api/v3/time");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Throttle_SpacesOutBurstOfRequests()
    {
        // 5 richieste a 20/s non possono completarsi istantaneamente: il bucket le distribuisce.
        var (client, _) = BuildClient(new ScriptedHandler(Response(HttpStatusCode.OK)), requestsPerSecond: 20);

        var started = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/api/v3/time")).Dispose();
        }
        var elapsed = DateTime.UtcNow - started;

        // 4 intervalli da 50ms = 200ms minimi; si resta larghi per non essere fragili in CI.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(150), $"le richieste non sono state distanziate: {elapsed.TotalMilliseconds}ms");
    }

    // ------------------------------------------------------------------ orologio

    [Fact]
    public void Clock_WithoutMeasuredOffset_MatchesLocalTime()
    {
        // Finché la sonda non ha misurato nulla il comportamento deve essere ESATTAMENTE quello
        // storico: nessuna regressione se il worker non gira o fallisce.
        var clock = new ExchangeClock(NullLogger<ExchangeClock>.Instance);

        Assert.Equal(TimeSpan.Zero, clock.Offset(ExchangeName.Binance));
        var local = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        Assert.InRange(clock.TimestampMillis(ExchangeName.Binance), local - 2_000, local + 2_000);
    }

    [Fact]
    public void Clock_AppliesMeasuredOffset()
    {
        var clock = new ExchangeClock(NullLogger<ExchangeClock>.Instance);
        var local = ExchangeSigning.UnixMillis(DateTime.UtcNow);

        clock.SetOffset(ExchangeName.Binance, TimeSpan.FromSeconds(3));

        // Il timestamp firmato deve ora essere ~3s avanti rispetto all'orologio locale.
        Assert.InRange(clock.TimestampMillis(ExchangeName.Binance), local + 2_000, local + 4_000);
    }

    [Fact]
    public void Clock_OffsetIsPerExchange()
    {
        var clock = new ExchangeClock(NullLogger<ExchangeClock>.Instance);

        clock.SetOffset(ExchangeName.Binance, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), clock.Offset(ExchangeName.Binance));
        Assert.Equal(TimeSpan.Zero, clock.Offset(ExchangeName.Bitget));
    }

    [Fact]
    public void Clock_RejectsImplausibleOffset()
    {
        // Un offset enorme è quasi sempre una misura sbagliata (risposta lentissima, proxy che
        // bufferizza) piuttosto che un orologio davvero sballato: applicarlo peggiorerebbe le cose,
        // facendo rifiutare TUTTE le richieste firmate invece di alcune.
        var clock = new ExchangeClock(NullLogger<ExchangeClock>.Instance);
        clock.SetOffset(ExchangeName.Binance, TimeSpan.FromSeconds(2));

        clock.SetOffset(ExchangeName.Binance, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromSeconds(2), clock.Offset(ExchangeName.Binance));
    }

    [Fact]
    public void Clock_RejectsImplausibleNegativeOffset()
    {
        var clock = new ExchangeClock(NullLogger<ExchangeClock>.Instance);

        clock.SetOffset(ExchangeName.Bitget, TimeSpan.FromHours(-3));

        Assert.Equal(TimeSpan.Zero, clock.Offset(ExchangeName.Bitget));
    }
}
