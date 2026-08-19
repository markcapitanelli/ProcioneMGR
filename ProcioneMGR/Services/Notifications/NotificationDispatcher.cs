using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Notifications;

/// <summary>Esito di un tentativo di recapito. Serve alla diagnostica, non ai producer.</summary>
public enum NotificationOutcome
{
    /// <summary>Il provider ha accettato il messaggio.</summary>
    Delivered,

    /// <summary>Gate globale spento (<c>Notifications:Enabled=false</c>): scartata prima di tutto.</summary>
    Disabled,

    /// <summary>Soppressa dal rate-limit; verrà conteggiata nel primo messaggio successivo.</summary>
    RateLimited,

    /// <summary>Nome di provider non riconosciuto in configurazione.</summary>
    UnknownProvider,

    /// <summary>Il provider ha rifiutato o è esploso (token assente, rete, API).</summary>
    Failed,
}

/// <param name="Detail">Motivo leggibile quando l'esito non è <see cref="NotificationOutcome.Delivered"/>.</param>
public sealed record NotificationResult(NotificationOutcome Outcome, string? Detail = null)
{
    public bool IsDelivered => Outcome == NotificationOutcome.Delivered;
}

/// <summary>
/// [E5] La spia di guasto del canale: ultimo recapito riuscito, ultimo fallimento col motivo, e
/// quanti fallimenti si sono accumulati dall'ultimo recapito. Esiste perché <c>NotifyAsync</c>
/// assorbe l'esito per contratto (giusto per i producer) e quindi un canale rotto falliva SOLO nei
/// log — è ciò che ha tenuto Telegram muto per due giorni senza che nessuno potesse accorgersene.
/// Un canale rotto non può auto-denunciarsi via notifica per definizione: serve una superficie che
/// si legge, non un messaggio che non partirà.
/// </summary>
/// <param name="LastDeliveredUtc">Ultimo recapito accettato dal provider (null = mai da questo avvio).</param>
/// <param name="LastFailureUtc">Ultimo fallimento di recapito o provider sconosciuto (null = mai da questo avvio).</param>
/// <param name="LastFailureDetail">Motivo leggibile dell'ultimo fallimento.</param>
/// <param name="FailuresSinceLastDelivery">Fallimenti consecutivi dall'ultimo recapito riuscito: &gt; 0 = il canale sta perdendo messaggi ADESSO.</param>
public sealed record NotificationChannelStatus(
    DateTime? LastDeliveredUtc,
    DateTime? LastFailureUtc,
    string? LastFailureDetail,
    int FailuresSinceLastDelivery);

/// <summary>
/// [I4] Pressione sul budget CONDIVISO delle notifiche.
///
/// <para><c>Notifications:MaxPerHour</c> è uno solo per processo, e nel guscio ci confluiscono
/// <b>otto sorveglianti</b>: deriva delle feature e dei fattori, orchestratore di flotta, campagne,
/// comitato, freschezza delle serie, guardiano del patrimonio, digest. Venti messaggi/ora divisi
/// fra otto significa che <b>il primo che sbaglia soglia zittisce gli altri sette</b> — è già
/// successo, con la staleness su una serie illiquida che ha inondato il canale e saturato il
/// budget degli allarmi veri.</para>
///
/// <para>Finora la soppressione viveva in un <c>LogWarning</c> e nel conteggio accodato al
/// messaggio successivo: nessuna superficie diceva <i>quanto è pieno il secchio adesso</i>, cioè
/// quanto manca al silenzio. Questa è quella superficie.</para>
/// </summary>
/// <param name="SentInWindow">Messaggi recapitati nell'ultima ora scorrevole.</param>
/// <param name="MaxPerHour">Il tetto in vigore adesso (hot-reload).</param>
/// <param name="SuppressedPending">
/// Soppressi non ancora dichiarati: verranno conteggiati in coda al primo messaggio che passa, e
/// questo contatore torna a zero. Un valore &gt; 0 significa che il canale <b>sta perdendo</b>
/// messaggi in questo momento.
/// </param>
/// <param name="SuppressedTotal">
/// Soppressi da questo avvio, e <b>non si azzera mai</b>: è la differenza fra «adesso va bene» e
/// «oggi è andata male sette volte». Senza, un'occhiata al pannello un minuto dopo la tempesta
/// direbbe che non è successo niente.
/// </param>
/// <param name="LastSuppressedUtc">Quando è stato soppresso l'ultimo messaggio (null = mai).</param>
public sealed record NotificationRateLimitPressure(
    int SentInWindow,
    int MaxPerHour,
    int SuppressedPending,
    long SuppressedTotal,
    DateTime? LastSuppressedUtc)
{
    /// <summary>Il canale sta perdendo messaggi adesso.</summary>
    public bool IsLosingNow => SuppressedPending > 0;

    /// <summary>Quanti messaggi restano prima del silenzio. Mai negativo.</summary>
    public int Remaining => Math.Max(0, MaxPerHour - SentInWindow);
}

/// <summary>
/// L'<see cref="INotifier"/> registrato in DI: gate (<c>Notifications:Enabled</c>, default OFF,
/// hot-reload), rate-limit a finestra scorrevole con coalescing (i messaggi soppressi vengono
/// conteggiati e riportati nel primo messaggio successivo, mai persi in silenzio) e selezione
/// del provider per nome. NON propaga MAI eccezioni al producer: una notifica fallita non deve
/// far fallire un watchdog o un planner (si degrada a log d'errore).
///
/// <para>Quel «non propaga mai» è giusto per i producer e sbagliato per chi vuole SAPERE se il
/// canale funziona: il 2026-07-29 il pulsante «Invia notifica di prova» di /admin/autonomy
/// dichiarava successo mentre il recapito falliva per <c>TELEGRAM_BOT_TOKEN</c> assente — una
/// verifica che dice la cosa rassicurante indipendentemente dalla realtà è peggio di nessuna
/// verifica. Da qui <see cref="SendDiagnosticAsync"/>: stesso identico percorso (gate, rate-limit,
/// provider), ma l'esito torna al chiamante invece di finire solo nel log.</para>
/// </summary>
public sealed class NotificationDispatcher(
    IOptionsMonitor<NotificationOptions> options,
    IEnumerable<INotificationProvider> providers,
    ILogger<NotificationDispatcher> logger,
    TimeProvider? timeProvider = null) : INotifier
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _sentInWindow = new();
    private int _suppressed;

    // [I4] Il totale NON si azzera col messaggio successivo, a differenza di _suppressed: quello
    // dice «sto perdendo messaggi adesso», questo dice «oggi è già successo». Guardando il pannello
    // un minuto dopo la tempesta, senza il totale sembrerebbe che non sia successo niente.
    private long _suppressedTotal;
    private DateTime? _lastSuppressedUtc;

    // [E5] Spia di guasto del canale (vedi NotificationChannelStatus). Aggiornata sotto _gate.
    private DateTime? _lastDeliveredUtc;
    private DateTime? _lastFailureUtc;
    private string? _lastFailureDetail;
    private int _failuresSinceLastDelivery;

    /// <summary>[E5] Stato corrente del canale, per la UI: si legge senza inviare nulla.</summary>
    public NotificationChannelStatus ChannelStatus
    {
        get
        {
            lock (_gate)
            {
                return new NotificationChannelStatus(
                    _lastDeliveredUtc, _lastFailureUtc, _lastFailureDetail, _failuresSinceLastDelivery);
            }
        }
    }

    /// <summary>
    /// [I4] Pressione sul budget condiviso, letta senza inviare nulla e <b>senza effetti</b>: la
    /// finestra viene ripulita qui come nell'invio, altrimenti il pannello mostrerebbe messaggi
    /// vecchi di ore come se occupassero ancora uno slot.
    /// </summary>
    public NotificationRateLimitPressure RateLimitPressure
    {
        get
        {
            lock (_gate)
            {
                TrimWindow(_time.GetUtcNow());
                return new NotificationRateLimitPressure(
                    _sentInWindow.Count,
                    Math.Max(1, options.CurrentValue.MaxPerHour),
                    _suppressed,
                    _suppressedTotal,
                    _lastSuppressedUtc);
            }
        }
    }

    /// <summary>Scarta dalla finestra scorrevole i recapiti più vecchi di un'ora. Va chiamato sotto <c>_gate</c>.</summary>
    private void TrimWindow(DateTimeOffset now)
    {
        while (_sentInWindow.Count > 0 && now - _sentInWindow.Peek() > TimeSpan.FromHours(1))
        {
            _sentInWindow.Dequeue();
        }
    }

    private void RecordDelivered()
    {
        lock (_gate)
        {
            _lastDeliveredUtc = _time.GetUtcNow().UtcDateTime;
            _failuresSinceLastDelivery = 0;
        }
    }

    private void RecordFailure(string detail)
    {
        lock (_gate)
        {
            _lastFailureUtc = _time.GetUtcNow().UtcDateTime;
            _lastFailureDetail = detail;
            _failuresSinceLastDelivery++;
        }
    }

    /// <summary>
    /// Contratto invariato verso i producer: nessuna eccezione, nessun esito da controllare.
    /// Il risultato lo assorbe il log, esattamente come prima.
    /// </summary>
    public async Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        => await SendAsync(severity, title, body, ct);

    /// <summary>
    /// Percorso di VERIFICA: stessa catena di <see cref="NotifyAsync"/> — quindi una prova che passa
    /// dimostra davvero che il canale configurato recapita, gate e rate-limit compresi — ma con
    /// l'esito restituito, così la UI distingue «consegnato» da «gate spento», «rate-limit»,
    /// «provider sconosciuto» e «provider fallito».
    /// </summary>
    public Task<NotificationResult> SendDiagnosticAsync(
        NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        => SendAsync(severity, title, body, ct);

    private async Task<NotificationResult> SendAsync(
        NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled)
        {
            return new NotificationResult(NotificationOutcome.Disabled,
                "Notifications:Enabled = false: il messaggio è stato scartato prima di scegliere un provider.");
        }

        int suppressedToReport;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            TrimWindow(now);
            if (_sentInWindow.Count >= Math.Max(1, opt.MaxPerHour))
            {
                _suppressed++;
                _suppressedTotal++;
                _lastSuppressedUtc = now.UtcDateTime;
                logger.LogWarning("Notifica SOPPRESSA dal rate-limit ({Max}/h): [{Severity}] {Title}", opt.MaxPerHour, severity, title);
                return new NotificationResult(NotificationOutcome.RateLimited,
                    $"Raggiunto il tetto di {opt.MaxPerHour} messaggi/ora: questa verrà conteggiata nel primo messaggio successivo.");
            }
            _sentInWindow.Enqueue(now);
            suppressedToReport = _suppressed;
            _suppressed = 0;
        }

        if (suppressedToReport > 0)
        {
            body += $"\n(+{suppressedToReport} notifiche soppresse dal rate-limit nell'ultima ora)";
        }

        var provider = providers.FirstOrDefault(p => string.Equals(p.Name, opt.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogError("Provider di notifica '{Provider}' sconosciuto: notifica [{Severity}] {Title} solo nel log. {Body}",
                opt.Provider, severity, title, body);
            var detail = $"Provider '{opt.Provider}' sconosciuto. Disponibili: {string.Join(", ", providers.Select(p => p.Name))}.";
            RecordFailure(detail);
            return new NotificationResult(NotificationOutcome.UnknownProvider, detail);
        }

        try
        {
            await provider.SendAsync(severity, title, body, ct);
            RecordDelivered();
            return new NotificationResult(NotificationOutcome.Delivered);
        }
        catch (Exception ex)
        {
            // Mai propagare al producer: il canale di ritorno è "best effort ma rumoroso nel log".
            logger.LogError(ex, "Recapito notifica fallito su {Provider}: [{Severity}] {Title} — {Body}",
                provider.Name, severity, title, body);
            RecordFailure($"{provider.Name}: {ex.Message}");
            return new NotificationResult(NotificationOutcome.Failed, $"{provider.Name}: {ex.Message}");
        }
    }
}
