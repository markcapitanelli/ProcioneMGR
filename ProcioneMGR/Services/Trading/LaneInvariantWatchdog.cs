using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Watchdog degli invarianti contabili per corsia (Fase 0-A3, PRD Autonomia Operativa §3).
/// Motivazione empirica: nella sessione di esercizio 2026-07-18 la corsia 2 Testnet è rimasta a
/// PnL -1.817.925 su capitale 10.000 per ORE senza che nessun automatismo se ne accorgesse —
/// il fill sanity check (A1) impedisce che si ripeta per QUELLA via, questo watchdog è la rete
/// di sicurezza per qualunque via futura verso uno stato contabile assurdo.
///
/// Politica su violazione: QUARANTENA — stop del trading, riga persistita che blocca il
/// riavvio (vedi <see cref="LaneQuarantine"/>), audit + LogCritical. NESSUNA chiusura forzata
/// delle posizioni: stessa filosofia della "difesa inversa" del FuturesPositionReconciler —
/// su uno stato che non capiamo, l'azione automatica peggiore è proprio quella irreversibile.
///
/// Registrato SOLO accanto al motore locale (vedi TradingServiceCollectionExtensions): in
/// modalità remota il watchdog vive nel servizio di trading, mai in due host insieme
/// (regola Fase 2b: ogni scrittore ha esattamente un host).
/// </summary>
public sealed class LaneInvariantWatchdog(
    IServiceProvider serviceProvider,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILaneQuarantineStore quarantine,
    IOptionsMonitor<LaneInvariantOptions> options,
    ILogger<LaneInvariantWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cadenza fissa all'avvio (pattern PromotionWorker); soglie e Enabled letti a ogni tick (hot).
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.CheckIntervalSeconds));
        logger.LogInformation("LaneInvariantWatchdog avviato (check ogni {Interval}, enabled={Enabled}).",
            interval, options.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo LaneInvariantWatchdog fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("LaneInvariantWatchdog fermato.");
    }

    /// <summary>Un tick: controlla tutte le corsie in esecuzione. Pubblico per test.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        for (var laneId = 0; laneId < TradingLanes.Count; laneId++)
        {
            ct.ThrowIfCancellationRequested();

            var state = await db.TradingEngineStates.AsNoTracking()
                .Where(s => s.LaneId == laneId).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

            // Corsia mai avviata o ferma: niente da sorvegliare (uno stato corrotto a corsia
            // ferma non può peggiorare, e verrà comunque azzerato dal prossimo StartAsync).
            if (state is null || !state.IsRunning) continue;

            // Già in quarantena: la riga esistente conserva l'evidenza, non si accumulano duplicati.
            if (await db.LaneQuarantines.AsNoTracking().AnyAsync(q => q.LaneId == laneId, ct)) continue;

            // Stesse posizioni che vede il motore: solo quelle della modalità corrente (filtro M2).
            var positions = await db.OpenPositions.AsNoTracking()
                .Where(p => p.LaneId == laneId && p.OpenedInMode == state.Mode).ToListAsync(ct);

            var violations = LaneInvariantChecker.Check(state, positions, opts);
            if (violations.Count == 0) continue;

            await QuarantineLaneAsync(laneId, state, positions.Count, violations, ct);
        }
    }

    private async Task QuarantineLaneAsync(
        int laneId, TradingEngineState state, int openPositions, IReadOnlyList<string> violations, CancellationToken ct)
    {
        var reason = string.Join(" | ", violations);
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            violations,
            mode = state.Mode.ToString(),
            state.TotalCapital,
            state.AvailableCapital,
            state.RealizedPnl,
            state.Leverage,
            openPositions,
        });

        // PRIMA la riga di quarantena (che blocca ogni futuro StartAsync), POI lo stop: se lo
        // stop fallisse a metà, la corsia resta comunque non riavviabile finché un umano non guarda.
        var created = await quarantine.TryQuarantineAsync(laneId, reason, detailsJson, ct);
        if (!created) return; // race con un altro tick: la prima quarantena vince

        logger.LogCritical(
            "CORSIA {Lane} IN QUARANTENA ({Mode}): {Reason}. Trading fermato, posizioni LASCIATE APERTE. " +
            "Verifica e rimuovi la quarantena in /trading (solo Admin).",
            laneId, state.Mode, reason);

        try
        {
            await serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId).StopAsync(ct);
        }
        catch (Exception ex)
        {
            // La quarantena è già persistita (il riavvio resta bloccato): lo stop fallito non è
            // silenzioso ma nemmeno fatale — al prossimo tick la corsia risulta già quarantenata.
            logger.LogError(ex, "Stop della corsia {Lane} in quarantena fallito (la quarantena resta attiva).", laneId);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
