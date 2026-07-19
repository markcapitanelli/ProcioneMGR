using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Worker che collega il layer AI al ciclo di ricerca SENZA accoppiarlo al motore: sonda
/// periodicamente i <see cref="PipelineRun"/> completati privi di advisory AI e li fa supervisionare
/// da <see cref="IPipelineSupervisor"/>. Decoupling deliberato — il <c>PipelineEngine</c> non conosce
/// il layer AI, e questo worker non conosce trading/esecuzione: legge run e scrive artifact advisory,
/// nient'altro (confine di sicurezza research→esecuzione, come per <see cref="PipelineSchedulerWorker"/>).
///
/// Inattivo per default: sia <c>Llm:Enabled</c> sia la presenza di <c>ANTHROPIC_API_KEY</c> sono
/// valutati a OGNI tick (modello ExecutionWorker) — il worker NON muore mai: se la chiave manca
/// logga una volta e resta in attesa, così toggle e chiave prendono effetto senza riavvio.
/// </summary>
public sealed class LlmSupervisorWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILlmClient llm,
    Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions> options,
    IPipelineSupervisor supervisor,
    ILogger<LlmSupervisorWorker> logger) : BackgroundService
{
    private bool _warnedUnconfigured;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // L'intervallo si legge una volta sola (cambiarlo richiede riavvio); Enabled e chiave sono per-tick.
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.PollIntervalMinutes));
        logger.LogInformation("LlmSupervisorWorker avviato (modello {Model}, check ogni {Interval}, Enabled={Enabled}, chiave presente={Configured}).",
            llm.Model, interval, options.CurrentValue.Enabled, llm.IsConfigured);

        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    if (!llm.IsConfigured)
                    {
                        if (!_warnedUnconfigured)
                        {
                            logger.LogWarning("LlmSupervisorWorker: ANTHROPIC_API_KEY non impostata — supervisione in attesa (ricontrollo a ogni tick, nessun riavvio necessario).");
                            _warnedUnconfigured = true;
                        }
                    }
                    else
                    {
                        if (_warnedUnconfigured)
                        {
                            logger.LogInformation("LlmSupervisorWorker: ANTHROPIC_API_KEY ora presente — supervisione attiva.");
                            _warnedUnconfigured = false;
                        }
                        await TickAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo LlmSupervisorWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("LlmSupervisorWorker fermato.");
    }

    /// <summary>
    /// Un tick: trova i run completati di recente senza advisory e li supervisiona. Pubblico per
    /// test e per la UI; <paramref name="forceProbe"/> ignora il cooldown del breaker ("Riprova adesso").
    /// </summary>
    public async Task TickAsync(CancellationToken ct, bool forceProbe = false)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        List<Guid> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Run completati recenti che NON hanno ancora un advisory AI (left-anti-join).
            pending = await db.PipelineRuns
                .Where(r => r.Status == "Completed" && r.CompletedAt != null && r.CompletedAt >= since)
                .Where(r => !db.PipelineArtifacts.Any(a => a.RunId == r.Id && a.Kind == LlmArtifactKinds.Advisory))
                .OrderBy(r => r.CompletedAt)
                .Select(r => r.Id)
                .Take(5) // limitiamo il fan-out per tick (costo/latenza)
                .ToListAsync(ct);
        }

        foreach (var runId in pending)
        {
            ct.ThrowIfCancellationRequested();
            await supervisor.SuperviseRunAsync(runId, ct, forceProbe);
            forceProbe = false; // il bypass del cooldown vale per UN probe, non per tutto il batch
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
