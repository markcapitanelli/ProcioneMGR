namespace ProcioneMGR.Services.Security;

/// <summary>Esito dell'ultimo probe della master key (Fase 3-C2). Null in <see cref="IMasterKeyProbe.Result"/> = probe non ancora eseguito.</summary>
public sealed record MasterKeyProbeResult(int Total, int Unreadable, DateTime CheckedAtUtc)
{
    /// <summary>Vero quando esistono credenziali cifrate con una master key DIVERSA da quella del processo.</summary>
    public bool HasUnreadable => Unreadable > 0;
}

/// <summary>
/// Probe di avvio della master key (Fase 3-C2, PRD Autonomia §6): l'app avviata con la chiave
/// sbagliata oggi "muore in silenzio" sul percorso credenziali — lo scopri quando una pagina va
/// in 500 o un avvio Testnet fallisce. Qui il fallimento diventa RUMOROSO alla partenza:
/// LogCritical + notifica (Fase 4) + stato esposto alla UI (banner persistente in /trading e
/// /settings/exchanges). Il probe LEGGE soltanto (nessuna scrittura, può vivere in ogni host).
/// </summary>
public interface IMasterKeyProbe
{
    MasterKeyProbeResult? Result { get; }

    /// <summary>Esegue il probe e aggiorna <see cref="Result"/>. Usato dal worker di avvio (e dai test).</summary>
    Task<MasterKeyProbeResult> ProbeAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IMasterKeyProbe"/>
public sealed class MasterKeyProbe(
    IExchangeCredentialReader reader,
    ILogger<MasterKeyProbe> logger,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null) : IMasterKeyProbe
{
    public MasterKeyProbeResult? Result { get; private set; }

    public async Task<MasterKeyProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var (total, unreadable) = await reader.CountUnreadableAsync(ct);
        var result = new MasterKeyProbeResult(total, unreadable, DateTime.UtcNow);
        Result = result;

        if (result.HasUnreadable)
        {
            logger.LogCritical(
                "MASTER KEY INCOERENTE: {Unreadable} credenziali exchange su {Total} NON si decifrano con la " +
                "Security:MasterKey corrente. Testnet/Live falliranno finché non reinserisci le credenziali in " +
                "/settings/exchanges o ripristini la chiave giusta (env PROCIONE_MGR_MASTER_KEY).",
                result.Unreadable, result.Total);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(Notifications.NotificationSeverity.Critical,
                    "Master key incoerente",
                    $"{result.Unreadable} credenziali su {result.Total} non decifrabili con la chiave corrente: " +
                    "Testnet/Live falliranno. Reinserisci le credenziali o ripristina la chiave giusta.", ct);
            }
        }
        else
        {
            logger.LogInformation("Probe master key: {Total} credenziali, tutte decifrabili.", result.Total);
        }
        return result;
    }
}

/// <summary>
/// Esegue il probe UNA volta all'avvio, con retry se il DB non è ancora raggiungibile (l'ordine
/// di avvio dei pod in K8s non è garantito — stessa cura di PipelineSchedulerWorker per la
/// bonifica orfani).
/// </summary>
public sealed class MasterKeyProbeWorker(
    IMasterKeyProbe probe,
    ILogger<MasterKeyProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await probe.ProbeAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Probe master key fallito (tentativo {Attempt}/5): riprovo tra 30s.", attempt);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
