using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Health;

/// <summary>
/// [AF5.1] Configurazione dell'heartbeat incrociato. Default SPENTO: a config vuota nessun host
/// scrive né sorveglia, comportamento identico a prima della fase (invariante di piattaforma).
/// Sezione <c>Heartbeat</c>, hot-reload via IOptionsMonitor.
/// </summary>
public sealed class HeartbeatOptions
{
    public bool Enabled { get; set; }

    /// <summary>Cadenza di scrittura del proprio battito.</summary>
    public int WriteSeconds { get; set; } = 60;

    /// <summary>
    /// Dopo quanti minuti senza battito l'ALTRO host è dichiarato muto. Molto maggiore del periodo
    /// di scrittura (10× di default): un tick perso per rumore di rete non deve allarmare nessuno.
    /// </summary>
    public int StaleMinutes { get; set; } = 10;
}

/// <summary>Stato di salute dell'altro host, come lo vede il monitor.</summary>
public enum HeartbeatHealth
{
    /// <summary>Nessuna riga: l'altro host non ha mai battuto (feature spenta là, o mai partito).
    /// È ignoranza, non guasto — non si allarma su ciò che non si sa.</summary>
    Unknown,
    Healthy,
    Stale,
}

/// <summary>
/// Decisione pura del monitor, separata dal worker per essere testabile con un orologio finto.
/// </summary>
public static class HeartbeatMonitorLogic
{
    public static HeartbeatHealth Evaluate(DateTime? lastSeenUtc, DateTime nowUtc, TimeSpan staleAfter)
    {
        if (lastSeenUtc is not DateTime last) return HeartbeatHealth.Unknown;
        return nowUtc - last > staleAfter ? HeartbeatHealth.Stale : HeartbeatHealth.Healthy;
    }
}

/// <summary>Cosa notificare a fronte di un'osservazione (null = niente).</summary>
public sealed record HeartbeatNotice(NotificationSeverity Severity, string Title);

/// <summary>
/// Traduce la sequenza di osservazioni in notifiche UNA-PER-TRANSIZIONE, mai a raffica: Warning
/// quando l'altro host diventa muto (anche se lo è già alla prima osservazione), Info quando
/// torna. Unknown non produce mai nulla, in nessuna direzione: prima di dichiarare un guasto
/// bisogna aver visto — ora o in passato — un battito, oppure la sua assenza prolungata su una
/// riga che esiste.
/// </summary>
public sealed class HeartbeatTransitionTracker(string otherRole)
{
    private HeartbeatHealth _last = HeartbeatHealth.Unknown;

    public HeartbeatNotice? Observe(HeartbeatHealth current)
    {
        var previous = _last;
        _last = current;

        return (previous, current) switch
        {
            (HeartbeatHealth.Stale, HeartbeatHealth.Stale) => null,       // già detto
            (_, HeartbeatHealth.Stale) => new HeartbeatNotice(
                NotificationSeverity.Warning,
                $"Heartbeat: l'host '{otherRole}' è muto"),
            (HeartbeatHealth.Stale, HeartbeatHealth.Healthy) => new HeartbeatNotice(
                NotificationSeverity.Info,
                $"Heartbeat: l'host '{otherRole}' batte di nuovo"),
            _ => null,
        };
    }
}

/// <summary>
/// Scrive il battito del PROPRIO host (upsert sulla riga col proprio ruolo, mai su quella altrui).
/// Registrato in entrambi gli host da AddTradingLanes; a feature spenta dorme e basta.
/// </summary>
public sealed class HostHeartbeatWorker(
    string role,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<HeartbeatOptions> options,
    ILogger<HostHeartbeatWorker> logger) : BackgroundService
{
    /// <summary>
    /// [K7, 2026-08-31] La REVISIONE, non «1.0.0.0».
    ///
    /// <para>Fino a oggi questa colonna portava <c>Assembly.GetName().Version</c>, che su questo
    /// repository vale costantemente <c>1.0.0.0</c>: un campo chiamato «versione» che non distingue
    /// nessuna versione da nessun'altra. Chi lo avesse confrontato con qualcosa avrebbe ottenuto un
    /// controllo che dice sempre la stessa cosa — e infatti l'audit lo ha classificato come canale
    /// occupato e inutilizzabile.</para>
    ///
    /// <para>Ora porta lo sha di build (K1). Il valore: il battito del MOTORE diventa la sua
    /// dichiarazione di revisione letta dal database, cioè una seconda sorgente indipendente dal
    /// tag dell'immagine — utile esattamente quando le due divergono (immagine ri-taggata a mano,
    /// rollout a metà). Ripiego dichiarato quando il timbro manca.</para>
    /// </summary>
    private static readonly string AssemblyVersion =
        BuildRevision.Short ?? $"senza timbro ({typeof(HostHeartbeatWorker).Assembly.GetName().Version})";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled)
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    var row = await db.HostHeartbeats.FirstOrDefaultAsync(h => h.Host == role, stoppingToken);
                    if (row is null)
                    {
                        db.HostHeartbeats.Add(new HostHeartbeat { Host = role, LastUtc = DateTime.UtcNow, Version = AssemblyVersion });
                    }
                    else
                    {
                        row.LastUtc = DateTime.UtcNow;
                        row.Version = AssemblyVersion;
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Il battito che fallisce (Postgres giù?) non deve uccidere il worker: al
                    // prossimo giro si riprova, e la stantiezza vista DALL'ALTRO host è proprio
                    // il segnale che questo canale esiste per produrre.
                    logger.LogWarning(ex, "Scrittura heartbeat '{Role}' fallita: riprovo al prossimo giro.", role);
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(opt.WriteSeconds, 10, 3600));
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>
/// Sorveglia la riga dell'ALTRO host. Vive: nel motore sempre (il guscio è riavviabile per
/// definizione, ma un guscio muto da ore significa niente advisory, niente pipeline, niente
/// occhi); nel guscio solo col trading remoto (in-process non esiste un "engine" separato da
/// sorvegliare). Le notifiche passano dal dispatcher normale — rate-limit e coalescing compresi.
/// </summary>
public sealed class HeartbeatMonitorWorker(
    string otherRole,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<HeartbeatOptions> options,
    ILogger<HeartbeatMonitorWorker> logger,
    INotifier? notifier = null) : BackgroundService
{
    private readonly HeartbeatTransitionTracker _tracker = new(otherRole);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled)
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    var row = await db.HostHeartbeats.AsNoTracking()
                        .FirstOrDefaultAsync(h => h.Host == otherRole, stoppingToken);

                    var health = HeartbeatMonitorLogic.Evaluate(
                        row?.LastUtc, DateTime.UtcNow, TimeSpan.FromMinutes(Math.Max(1, opt.StaleMinutes)));

                    var notice = _tracker.Observe(health);
                    if (notice is not null)
                    {
                        var body = health == HeartbeatHealth.Stale
                            ? $"Nessun battito dall'host '{otherRole}' da oltre {opt.StaleMinutes} minuti " +
                              $"(ultimo: {row!.LastUtc:yyyy-MM-dd HH:mm:ss} UTC, versione {row.Version}). " +
                              "Se è il motore: le corsie non elaborano candele. Se è il guscio: nessuna " +
                              "pipeline, advisory o UI. Vedi scripts/bringup.ps1."
                            : $"L'host '{otherRole}' ha ripreso a battere.";
                        logger.Log(
                            health == HeartbeatHealth.Stale ? LogLevel.Warning : LogLevel.Information,
                            "{Title}. {Body}", notice.Title, body);
                        if (notifier is not null)
                        {
                            await notifier.NotifyAsync(notice.Severity, notice.Title, body, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Lettura heartbeat di '{Other}' fallita: riprovo al prossimo giro.", otherRole);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
