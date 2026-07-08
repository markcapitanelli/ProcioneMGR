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
/// Inattivo per default: parte solo se <c>Llm:Enabled=true</c> E la env <c>ANTHROPIC_API_KEY</c> è
/// presente; altrimenti si spegne subito (l'app funziona normalmente senza layer AI).
/// </summary>
public sealed class LlmSupervisorWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILlmClient llm,
    LlmOptions options,
    IPipelineSupervisor supervisor,
    ILogger<LlmSupervisorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("LlmSupervisorWorker: layer AI disabilitato (Llm:Enabled=false). Non parte.");
            return;
        }
        if (!llm.IsConfigured)
        {
            logger.LogWarning("LlmSupervisorWorker: ANTHROPIC_API_KEY non impostata. Layer AI inattivo.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.PollIntervalMinutes));
        logger.LogInformation("LlmSupervisorWorker avviato (modello {Model}, check ogni {Interval}).", llm.Model, interval);

        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo LlmSupervisorWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("LlmSupervisorWorker fermato.");
    }

    /// <summary>Un tick: trova i run completati di recente senza advisory e li supervisiona. Pubblico per test.</summary>
    public async Task TickAsync(CancellationToken ct)
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
            await supervisor.SuperviseRunAsync(runId, ct);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
