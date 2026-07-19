using Anthropic.Exceptions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Services.Llm;

/// <summary>Esito di una chiamata Claude passata dal guard.</summary>
public enum LlmCallOutcome
{
    Ok,
    /// <summary>ANTHROPIC_API_KEY assente: nessuna chiamata, il breaker non si muove.</summary>
    SkippedNotConfigured,
    /// <summary>Breaker aperto e cooldown non scaduto: nessuna chiamata.</summary>
    SkippedBreakerOpen,
    /// <summary>Errore transitorio (credito, credenziali, rate-limit, server, rete, timeout): ritentabile.</summary>
    FailedRetryable,
    /// <summary>Errore permanente (richiesta non valida, refusal, parse): ritentarlo non serve.</summary>
    FailedPermanent,
}

/// <summary>Risultato di <see cref="ILlmCallGuard.ExecuteAsync"/>.</summary>
public sealed class LlmCallResult
{
    public LlmCallOutcome Outcome { get; init; }

    /// <summary>Testo della risposta, solo per <see cref="LlmCallOutcome.Ok"/>.</summary>
    public string? Text { get; init; }

    public Exception? Error { get; init; }

    /// <summary>Causa leggibile ("credito API", "rate-limit", "server", "rete", "timeout", ...).</summary>
    public string Cause { get; init; } = string.Empty;
}

/// <summary>Fotografia dello stato del breaker per la UI (/admin/ai-supervisor).</summary>
public sealed record LlmGuardStatus(
    bool BreakerOpen,
    int ConsecutiveFailures,
    string? LastFailureCause,
    string? LastFailureMessage,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextProbeUtc);

/// <summary>
/// Chokepoint di OGNI chiamata Claude (path advisory e path veto). Un problema dell'API — credito
/// esaurito, chiave revocata, rate-limit, guasto — non deve né bloccare la piattaforma né bruciare
/// chiamate a vuoto né passare sotto silenzio: il guard classifica l'errore con le eccezioni
/// tipizzate del SDK, apre un circuit breaker dopo N errori transitori consecutivi, riprova da solo
/// con un probe periodico (half-open) e avvisa l'operatore UNA volta per transizione (Warning
/// all'apertura con la causa, Info al ripristino). Stato solo in-memory: dopo un riavvio il breaker
/// si riapre da sé dopo pochi errori a buon mercato.
/// </summary>
public interface ILlmCallGuard
{
    /// <param name="path">Etichetta della metrica: "advisory" | "veto".</param>
    /// <param name="call">La chiamata vera (riceve il token con timeout già collegato).</param>
    /// <param name="timeout">Override del timeout; default <see cref="LlmOptions.RequestTimeoutSeconds"/>.</param>
    /// <param name="forceProbe">Ignora il cooldown del breaker aperto (bottone "Riprova adesso").</param>
    Task<LlmCallResult> ExecuteAsync(
        string path,
        Func<CancellationToken, Task<string>> call,
        TimeSpan? timeout = null,
        bool forceProbe = false,
        CancellationToken ct = default);

    LlmGuardStatus GetStatus();
}

public sealed class LlmCallGuard(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> options,
    ILogger<LlmCallGuard> logger,
    ProcioneMetrics? metrics = null,
    INotifier? notifier = null,
    TimeProvider? timeProvider = null) : ILlmCallGuard
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    private bool _breakerOpen;
    private int _consecutiveFailures;
    private bool _probeInFlight;
    private string? _lastFailureCause;
    private string? _lastFailureMessage;
    private DateTimeOffset? _lastAttemptUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _nextProbeUtc;

    public async Task<LlmCallResult> ExecuteAsync(
        string path,
        Func<CancellationToken, Task<string>> call,
        TimeSpan? timeout = null,
        bool forceProbe = false,
        CancellationToken ct = default)
    {
        if (!llm.IsConfigured)
        {
            metrics?.RecordLlmCall(path, "skipped_unconfigured");
            return new LlmCallResult { Outcome = LlmCallOutcome.SkippedNotConfigured, Cause = "non configurato" };
        }

        lock (_gate)
        {
            if (_breakerOpen)
            {
                var canProbe = (forceProbe || _nextProbeUtc is null || _time.GetUtcNow() >= _nextProbeUtc) && !_probeInFlight;
                if (!canProbe)
                {
                    metrics?.RecordLlmCall(path, "skipped_breaker");
                    return new LlmCallResult { Outcome = LlmCallOutcome.SkippedBreakerOpen, Cause = _lastFailureCause ?? "errori ripetuti" };
                }
                _probeInFlight = true;
            }
        }

        var opt = options.CurrentValue;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(Math.Max(5, opt.RequestTimeoutSeconds));
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var text = await call(linked.Token);
            OnSuccess();
            metrics?.RecordLlmCall(path, "ok");
            return new LlmCallResult { Outcome = LlmCallOutcome.Ok, Text = text };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown/cancellazione esterna: non è un fallimento dell'API, non muove il breaker.
            lock (_gate) { _probeInFlight = false; }
            throw;
        }
        catch (Exception ex)
        {
            var timedOut = ex is OperationCanceledException && timeoutCts.IsCancellationRequested;
            var (retryable, cause) = timedOut ? (true, "timeout") : Classify(ex);
            var message = FirstLine(ex.Message);

            if (!retryable)
            {
                // L'API ha risposto (nel merito, anche se male): la condizione transitoria — se
                // c'era — è rientrata. Un probe con errore permanente CHIUDE quindi il breaker.
                OnSuccess(recoveredViaPermanentError: true);
                metrics?.RecordLlmCall(path, "error");
                logger.LogError(ex, "Chiamata Claude ({Path}) fallita in modo permanente ({Cause}).", path, cause);
                return new LlmCallResult { Outcome = LlmCallOutcome.FailedPermanent, Error = ex, Cause = cause };
            }

            var justOpened = false;
            lock (_gate)
            {
                _probeInFlight = false;
                _consecutiveFailures++;
                _lastFailureCause = cause;
                _lastFailureMessage = message;
                _lastAttemptUtc = _time.GetUtcNow();
                var cooldown = TimeSpan.FromMinutes(Math.Max(1, opt.BreakerCooldownMinutes));
                if (_breakerOpen)
                {
                    // Probe fallito: il breaker resta aperto in silenzio, prossimo probe dopo il cooldown.
                    _nextProbeUtc = _time.GetUtcNow() + cooldown;
                }
                else if (_consecutiveFailures >= Math.Max(1, opt.BreakerFailureThreshold))
                {
                    _breakerOpen = true;
                    _nextProbeUtc = _time.GetUtcNow() + cooldown;
                    justOpened = true;
                }
            }

            metrics?.RecordLlmCall(path, "error");
            logger.LogWarning(ex, "Chiamata Claude ({Path}) fallita ({Cause}); errori consecutivi: {Count}.", path, cause, _consecutiveFailures);

            if (justOpened)
            {
                logger.LogWarning("Supervisione AI SOSPESA dopo {Count} errori consecutivi (causa: {Cause}).", _consecutiveFailures, cause);
                if (notifier is not null)
                {
                    await notifier.NotifyAsync(NotificationSeverity.Warning, "Supervisione AI sospesa",
                        $"Chiamate Claude sospese dopo {_consecutiveFailures} errori consecutivi (causa: {cause} — {message}). " +
                        $"Nuovo tentativo automatico ogni {Math.Max(1, opt.BreakerCooldownMinutes)} min; riprende da sola quando il problema rientra.",
                        CancellationToken.None);
                }
            }

            return new LlmCallResult { Outcome = LlmCallOutcome.FailedRetryable, Error = ex, Cause = cause };
        }

        void OnSuccess(bool recoveredViaPermanentError = false)
        {
            bool wasOpen;
            lock (_gate)
            {
                wasOpen = _breakerOpen;
                _breakerOpen = false;
                _probeInFlight = false;
                _consecutiveFailures = 0;
                _nextProbeUtc = null;
                _lastAttemptUtc = _time.GetUtcNow();
                if (!recoveredViaPermanentError) _lastSuccessUtc = _lastAttemptUtc;
            }
            if (wasOpen)
            {
                logger.LogInformation("Supervisione AI RIPRISTINATA: le chiamate Claude tornano a funzionare.");
                // Fire-and-forget consapevole: NotifyAsync non lancia mai (dispatcher best-effort).
                _ = notifier?.NotifyAsync(NotificationSeverity.Info, "Supervisione AI ripristinata",
                    "Le chiamate Claude sono tornate operative: advisory e supervisione del ciclo di ri-applica riprendono da sole.",
                    CancellationToken.None);
            }
        }
    }

    public LlmGuardStatus GetStatus()
    {
        lock (_gate)
        {
            return new LlmGuardStatus(_breakerOpen, _consecutiveFailures, _lastFailureCause, _lastFailureMessage,
                _lastAttemptUtc, _lastSuccessUtc, _breakerOpen ? _nextProbeUtc : null);
        }
    }

    /// <summary>
    /// Classifica un'eccezione della chiamata Claude: transitoria (ritentabile, muove il breaker)
    /// o permanente. Pubblico e statico per i test. L'ordine conta: prima i tipi specifici.
    /// </summary>
    public static (bool Retryable, string Cause) Classify(Exception ex) => ex switch
    {
        AnthropicRateLimitException => (true, "rate-limit"),
        AnthropicUnauthorizedException or AnthropicForbiddenException => (true, "credenziali"),
        AnthropicBadRequestException bad when IsBilling(bad) => (true, "credito API"),
        Anthropic4xxException => (false, "richiesta non valida"),
        Anthropic5xxException or AnthropicUnexpectedStatusCodeException => (true, "server"),
        AnthropicIOException or AnthropicSseException => (true, "rete"),
        HttpRequestException or IOException => (true, "rete"),
        _ => (false, "inatteso"),
    };

    /// <summary>Il billing arriva come 400 generico: si riconosce solo dal testo dell'errore.</summary>
    private static bool IsBilling(AnthropicBadRequestException ex)
    {
        var haystack = $"{ex.Message} {ex.ResponseBody} {ex.InnerException?.Message}";
        return haystack.Contains("credit", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("billing", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string message)
    {
        var idx = message.IndexOfAny(['\r', '\n']);
        return idx < 0 ? message : message[..idx];
    }
}
