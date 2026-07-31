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
    ILogger<LaneInvariantWatchdog> logger,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null) : BackgroundService
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
            // Il tracciamento del battito si azzera: da ferma, "nessuna candela" è la normalità.
            if (state is null || !state.IsRunning)
            {
                _lastSeenHeartbeat.Remove(laneId);
                _starvationAlerted.Remove(laneId);
                continue;
            }

            // Già in quarantena: la riga esistente conserva l'evidenza, non si accumulano duplicati.
            if (await db.LaneQuarantines.AsNoTracking().AnyAsync(q => q.LaneId == laneId, ct)) continue;

            // Stesse posizioni che vede il motore: solo quelle della modalità corrente (filtro M2).
            var positions = await db.OpenPositions.AsNoTracking()
                .Where(p => p.LaneId == laneId && p.OpenedInMode == state.Mode).ToListAsync(ct);

            var violations = LaneInvariantChecker.Check(state, positions, opts);
            if (violations.Count > 0)
            {
                await QuarantineLaneAsync(laneId, state, positions.Count, violations, ct);
                continue;
            }

            await CheckEvaluationHeartbeatAsync(laneId, state.Timeframe, ct);
        }

        await ReportOrphanPositionsAsync(db, ct);
    }

    // --- [E6] Inedia di valutazione ------------------------------------------------------------

    /// <summary>Ultimo battito visto per corsia al giro precedente: serve a distinguere «ferma» da «in rincorsa».</summary>
    private readonly Dictionary<int, DateTime?> _lastSeenHeartbeat = new();

    /// <summary>Corsie già allertate per inedia: una notifica per transizione, non una per tick.</summary>
    private readonly HashSet<int> _starvationAlerted = [];

    /// <summary>
    /// [E6] Una corsia <c>running</c> il cui motore non valuta candele è una corsia i cui stop e
    /// trailing non li guarda nessuno — e ogni superficie la mostra verde, perché <c>IsRunning</c>
    /// è un flag d'intento, non una prova di attività (è l'«OK: 1 candele» di B2.a, sul motore).
    ///
    /// Il verdetto usa la regola UNICA di <see cref="Ingestion.SeriesFreshness"/> (misura contro
    /// ADESSO) più un discriminatore che la freschezza da sola non ha: durante il replay di avvio
    /// il battito è legittimamente vecchio ma AVANZA a ogni giro, quindi si allerta solo quando è
    /// stantio E fermo rispetto al giro precedente. Allarme, non quarantena: la corsia non è
    /// corrotta, è a digiuno — e fermarla d'ufficio toglierebbe anche la valutazione che riprenderà
    /// da sola quando le candele tornano.
    /// </summary>
    private async Task CheckEvaluationHeartbeatAsync(int laneId, string timeframe, CancellationToken ct)
    {
        DateTime? heartbeat;
        try
        {
            var status = await serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId).GetStatusAsync(ct);
            heartbeat = status.LastProcessedCandleUtc;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Host senza motore keyed (composizione di test) o motore che non risponde: il battito
            // non è misurabile da qui, e inventare un verdetto sarebbe il difetto che stiamo togliendo.
            logger.LogDebug(ex, "Corsia {Lane}: battito di valutazione non leggibile in questo host.", laneId);
            return;
        }

        var hadPrevious = _lastSeenHeartbeat.TryGetValue(laneId, out var previous);
        _lastSeenHeartbeat[laneId] = heartbeat;

        if (!Ingestion.SeriesFreshness.IsStale(timeframe, heartbeat, DateTime.UtcNow))
        {
            _starvationAlerted.Remove(laneId);
            return;
        }
        if (hadPrevious && heartbeat != previous)
        {
            // Stantio ma in movimento: è la rincorsa del replay, non un digiuno. Si riarma.
            _starvationAlerted.Remove(laneId);
            return;
        }
        if (!hadPrevious) return;              // primo sguardo: il verdetto al prossimo giro
        if (!_starvationAlerted.Add(laneId)) return;

        var ultima = heartbeat is DateTime hb ? $"{hb:yyyy-MM-dd HH:mm} UTC" : "MAI (da questo avvio)";
        logger.LogCritical(
            "CORSIA {Lane} AFFAMATA: running ma il motore non valuta candele — ultima valutata: {Ultima} "
            + "(timeframe {Timeframe}). Stop e trailing non vengono valutati da nessuno. Cause tipiche: "
            + "sync dati fermo, database irraggiungibile, serie della corsia ferma.",
            laneId, ultima, timeframe);

        if (notifier is not null)
        {
            await notifier.NotifyAsync(Notifications.NotificationSeverity.Critical,
                $"Corsia {laneId} affamata (running senza candele)",
                $"Il motore risulta in esecuzione ma non valuta candele: ultima valutata {ultima}, "
                + $"timeframe {timeframe}. Stop e trailing non vengono valutati. Verifica sync dati e database.", ct);
        }
    }

    /// <summary>Corsie orfane già segnalate: si allerta una volta per corsia, non a ogni tick.</summary>
    private readonly HashSet<int> _orphanAlerted = [];

    /// <summary>
    /// Posizioni su corsie che NON ESISTONO PIÙ, cioè con <c>LaneId</c> oltre
    /// <see cref="TradingLanes.Count"/>. Il ciclo qui sopra non può vederle: itera sulle corsie
    /// configurate, e una corsia fuori range non è fra quelle.
    ///
    /// Trovato dal vivo il 2026-07-28: la corsia 3, rimasta da un assetto precedente a
    /// `LaneCount=3`, teneva una posizione Paper aperta su DOT/USDT con stop, take profit e
    /// trailing configurati e nessun motore che li valutasse — <c>CurrentPrice</c> fermo al prezzo
    /// d'ingresso da un giorno. Il commento del ciclo («uno stato corrotto a corsia ferma non può
    /// peggiorare, e verrà comunque azzerato dal prossimo StartAsync») non regge per una corsia che
    /// non può più essere avviata: quel prossimo StartAsync non arriverà mai.
    ///
    /// Nessuna azione automatica, stessa filosofia della quarantena: su una posizione che non
    /// capiamo, chiudere d'ufficio è il gesto irreversibile e quindi quello sbagliato. Si dice, e
    /// decide un umano.
    /// </summary>
    private async Task ReportOrphanPositionsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var laneCount = TradingLanes.Count;
        var orphans = await db.OpenPositions.AsNoTracking()
            .Where(p => p.LaneId >= laneCount)
            .ToListAsync(ct);

        foreach (var group in orphans.GroupBy(p => p.LaneId))
        {
            if (!_orphanAlerted.Add(group.Key)) continue;

            var detail = string.Join(", ", group.Select(p =>
                $"{p.Symbol} {p.Side} {p.Quantity:0.####} @ {p.EntryPrice:0.####} dal {p.OpenedAtUtc:u}"));

            logger.LogCritical(
                "POSIZIONI ORFANE sulla corsia {Lane}, che non esiste più (LaneCount={Count}): {Detail}. "
                + "Nessun motore ne valuta stop, target o trailing. Nessuna azione automatica: "
                + "chiudile o archiviale a mano.",
                group.Key, laneCount, detail);

            if (notifier is not null)
            {
                await notifier.NotifyAsync(Notifications.NotificationSeverity.Critical,
                    $"Posizioni orfane sulla corsia {group.Key}",
                    $"La corsia non esiste più (LaneCount={laneCount}) ma tiene {group.Count()} posizione/i aperte "
                    + $"che nessun motore sorveglia: {detail}.", ct);
            }
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

        if (notifier is not null)
        {
            await notifier.NotifyAsync(Notifications.NotificationSeverity.Critical,
                $"Corsia {laneId} in QUARANTENA ({state.Mode})",
                $"{reason}. Trading fermato, posizioni lasciate aperte: verifica e rimuovi la quarantena in /trading.", ct);
        }

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
