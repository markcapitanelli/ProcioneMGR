using System.Net;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Disciplina di rate-limit verso le API REST degli exchange, applicata come
/// <see cref="DelegatingHandler"/> sul typed HttpClient: vale per OGNI chiamata (pubblica o
/// firmata, spot o futures) senza toccare le decine di punti che le compongono.
///
/// Due meccanismi distinti, entrambi necessari:
///
///  1. LIMITE PROATTIVO (token bucket): non si supera un tetto di richieste al secondo. È ciò che
///     evita di finire in ban, invece di reagirvi. Serve perché il feed real-time e le corsie
///     multiple possono generare raffiche che il ciclo REST da solo non produceva.
///
///  2. RITIRO REATTIVO su 429 (rate limit) e 418 (IP bannato da Binance dopo 429 ripetuti): si
///     rispetta <c>Retry-After</c> quando c'è, altrimenti backoff esponenziale con jitter.
///     Continuare a martellare dopo un 429 è esattamente il comportamento che trasforma un limite
///     temporaneo in un ban dell'IP.
///
/// NB: non si ritenta MAI più di <see cref="MaxRetries"/> volte, e i 5xx NON vengono ritentati qui.
/// La ragione è di sicurezza, non di pigrizia: un 5xx su un piazzamento d'ordine lascia lo stato
/// INCERTO, e i chiamanti hanno già una macchina apposita per quel caso (<c>OrderReconciler</c>,
/// che verifica se l'ordine è passato prima di riprovare). Un retry cieco qui potrebbe duplicare
/// un ordine reale.
/// </summary>
public sealed class ExchangeRateLimitHandler(
    ILogger<ExchangeRateLimitHandler> logger,
    int requestsPerSecond = 10,
    int maxRetries = 3,
    TimeProvider? timeProvider = null) : DelegatingHandler
{
    private const int MaxRetries = 3;

    /// <summary>Tetto di attesa per un singolo ritiro: oltre, tanto vale fallire e far decidere il chiamante.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _retries = Math.Clamp(maxRetries, 0, MaxRetries);
    private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, requestsPerSecond));

    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);

            var response = await base.SendAsync(request, ct);
            if (!IsRateLimited(response.StatusCode) || attempt >= _retries)
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            logger.LogWarning(
                "Rate limit dall'exchange ({Status}) su {Uri}: attendo {Delay:F1}s e ritento (tentativo {Attempt}/{Max}).",
                (int)response.StatusCode, request.RequestUri?.AbsolutePath, delay.TotalSeconds, attempt + 1, _retries);

            response.Dispose();
            await Task.Delay(delay, _time, ct);
        }
    }

    /// <summary>429 = troppe richieste; 418 = Binance ha già bannato l'IP (si smette subito di insistere).</summary>
    private static bool IsRateLimited(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status == 418;

    /// <summary>
    /// L'exchange sa meglio di noi quanto aspettare: <c>Retry-After</c> vince sempre sul backoff
    /// calcolato. Si applica comunque il tetto, per non restare appesi a un valore assurdo.
    /// </summary>
    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            var hinted = retryAfter.Delta
                ?? (retryAfter.Date is { } date ? date - _time.GetUtcNow() : null);
            if (hinted is { } d && d > TimeSpan.Zero)
            {
                return d < MaxBackoff ? d : MaxBackoff;
            }
        }

        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var jittered = backoff * (0.5 + Random.Shared.NextDouble() * 0.5);
        return jittered < MaxBackoff ? jittered : MaxBackoff;
    }

    /// <summary>
    /// Token bucket serializzato: ogni richiesta prenota lo slot successivo, così N chiamanti
    /// concorrenti si distribuiscono nel tempo invece di partire tutti insieme.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        TimeSpan wait;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            var slot = _nextSlot > now ? _nextSlot : now;
            wait = slot - now;
            _nextSlot = slot + _minInterval;
        }
        finally { _gate.Release(); }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, ct);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}
