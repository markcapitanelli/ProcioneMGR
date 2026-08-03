using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// [AF5.4] Il digest giornaliero, sezione <c>Notifications:Digest</c>. Default SPENTO. L'ora è
/// quella LOCALE della macchina (il PC del proprietario): il digest serve a un umano che si
/// sveglia, non a un cron UTC.
/// </summary>
public sealed class DigestOptions
{
    public bool Enabled { get; set; }

    public int Hour { get; set; } = 7;

    public int Minute { get; set; } = 30;
}

/// <summary>
/// Decisione pura di scheduling: il digest parte quando l'orario del giorno è passato E oggi non
/// è ancora stato mandato. Separata dal worker per essere testabile con orologi finti.
/// </summary>
public static class DigestSchedule
{
    public static bool IsDue(DateTime nowLocal, int hour, int minute, DateOnly? lastSentDate)
    {
        var target = nowLocal.Date
            .AddHours(Math.Clamp(hour, 0, 23))
            .AddMinutes(Math.Clamp(minute, 0, 59));
        return nowLocal >= target && lastSentDate != DateOnly.FromDateTime(nowLocal.Date);
    }
}

/// <summary>Il materiale del digest, già raccolto: il compositore è puro e non tocca servizi.</summary>
public sealed record DigestData(
    IReadOnlyList<string> Lanes,
    IReadOnlyList<string> FleetDecisions,
    IReadOnlyList<string> Attention,
    string? AiUsage,
    string? Carry,
    IReadOnlyList<string> Heartbeats);

/// <summary>
/// Compone il testo. La chiusura è la parte più importante: dichiara che l'ASSENZA del digest è
/// essa stessa l'allarme — il dead-man's-switch percepibile da un umano senza infrastruttura.
/// </summary>
public static class DailyDigestComposer
{
    public static string Compose(DigestData data, DateTime nowLocal)
    {
        var lines = new List<string> { $"ProcioneMGR — digest del {nowLocal:dd/MM/yyyy HH:mm}", "" };

        lines.Add("CORSIE");
        if (data.Lanes.Count == 0) lines.Add("  (nessuna corsia leggibile)");
        lines.AddRange(data.Lanes.Select(l => "  " + l));

        if (data.Attention.Count > 0)
        {
            lines.Add("");
            lines.Add("DA GUARDARE");
            lines.AddRange(data.Attention.Select(a => "  " + a));
        }

        lines.Add("");
        lines.Add("FLOTTA (ultime 24h)");
        if (data.FleetDecisions.Count == 0) lines.Add("  nessuna decisione: coda vuota o flotta in quiete");
        lines.AddRange(data.FleetDecisions.Select(d => "  " + d));

        if (data.AiUsage is not null)
        {
            lines.Add("");
            lines.Add("CONSUMO AI");
            lines.Add("  " + data.AiUsage);
        }

        if (data.Carry is not null)
        {
            lines.Add("");
            lines.Add("CARRY");
            lines.Add("  " + data.Carry);
        }

        if (data.Heartbeats.Count > 0)
        {
            lines.Add("");
            lines.Add("HEARTBEAT");
            lines.AddRange(data.Heartbeats.Select(h => "  " + h));
        }

        lines.Add("");
        lines.Add("Se domani questo messaggio non arriva alla stessa ora, la piattaforma è muta: guarda il watchdog e scripts/bringup.ps1.");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// [AF5.4] Il worker: ogni minuto controlla se il digest è dovuto; quando lo è, raccoglie ogni
/// sezione IN PROPRIO try/catch (meglio un digest con meno sezioni che nessun digest) e lo manda
/// dal canale normale. Vive nel SOLO monolite. L'anti-doppione è in memoria: dopo un riavvio a
/// cavallo dell'ora configurata un secondo invio è possibile e accettato (il rate-limit del
/// dispatcher lo assorbe; un doppione all'anno batte una tabella in più).
/// </summary>
public sealed class DailyDigestWorker(
    IOptionsMonitor<DigestOptions> options,
    IPromotionEvaluator promotionEvaluator,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILlmUsageSink usageSink,
    IServiceProvider serviceProvider,
    ILogger<DailyDigestWorker> logger,
    INotifier? notifier = null) : BackgroundService
{
    private DateOnly? _lastSentDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled && notifier is not null
                && DigestSchedule.IsDue(DateTime.Now, opt.Hour, opt.Minute, _lastSentDate))
            {
                try
                {
                    var body = DailyDigestComposer.Compose(await GatherAsync(stoppingToken), DateTime.Now);
                    await notifier.NotifyAsync(NotificationSeverity.Info, "Digest giornaliero", body, stoppingToken);
                    _lastSentDate = DateOnly.FromDateTime(DateTime.Now.Date);
                    logger.LogInformation("Digest giornaliero inviato.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // NON si marca come inviato: al prossimo giro si riprova (l'assenza del digest
                    // è l'allarme, ma solo quando la piattaforma è morta — non quando può riprovare).
                    logger.LogWarning(ex, "Invio digest fallito: riprovo al prossimo giro.");
                }
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<DigestData> GatherAsync(CancellationToken ct)
    {
        var lanes = new List<string>();
        var attention = new List<string>();
        try
        {
            var decisions = await promotionEvaluator.EvaluateAllLanesAsync(ct);
            foreach (var d in decisions)
            {
                var m = d.Metrics;
                lanes.Add(d.IsRunning
                    ? $"corsia {d.LaneId} {d.Symbol} [{d.CurrentMode}]: Sharpe {m.RealizedSharpe:F2}, {m.TradeCount} trade, DD {m.MaxDrawdown:F1}%, {m.ObservationPeriod.TotalDays:F0}gg"
                    : $"corsia {d.LaneId} {(string.IsNullOrEmpty(d.Symbol) ? "—" : d.Symbol)}: ferma");
                if (d.ReadyForTestnet && !d.ShouldPromote) attention.Add($"corsia {d.LaneId}: PRONTA per Testnet (auto-promozione spenta)");
                if (d.WouldDemoteLive) attention.Add($"corsia {d.LaneId}: retrocederei da Live (dry-run) — {d.Reason}");
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Digest: sezione corsie non leggibile."); }

        var fleet = new List<string>();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var since = DateTime.UtcNow.AddHours(-24);
            fleet = await db.OrchestratorDecisions.AsNoTracking()
                .Where(d => d.AtUtc >= since)
                .OrderBy(d => d.AtUtc)
                .Take(20)
                .Select(d => d.Kind + (d.LaneId != null ? $" corsia {d.LaneId}" : "") + (d.DryRun ? " [dry-run]" : "") + ": " + d.Reason)
                .ToListAsync(ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Digest: sezione flotta non leggibile."); }

        string? aiUsage = null;
        try
        {
            var snap = usageSink.GetSnapshot();
            aiUsage = snap.TrackingEnabled
                ? $"oggi {snap.TodayCalls} chiamate / {snap.TodayTokens} token · mese {snap.MonthTokens} token"
                : "tracking spento";
        }
        catch (Exception ex) { logger.LogWarning(ex, "Digest: sezione consumo AI non leggibile."); }

        string? carry = null;
        try
        {
            // Lo stato vivo del carry è in-process: in topologia remota da questo host non si
            // vede, e si dichiara il limite invece di fingere un "tutto bene".
            var worker = serviceProvider.GetService<Carry.CarryWorker>();
            carry = worker is null
                ? "vive nell'host del motore (stato non visibile da qui)"
                : $"ultima valutazione {worker.LastEvaluationUtc?.ToString("u") ?? "mai"}, {worker.States.Count(s => s.Value.InPosition)} posizioni";
        }
        catch (Exception ex) { logger.LogWarning(ex, "Digest: sezione carry non leggibile."); }

        var heartbeats = new List<string>();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            heartbeats = (await db.HostHeartbeats.AsNoTracking().OrderBy(h => h.Host).ToListAsync(ct))
                .Select(h => $"{h.Host}: ultimo battito {(DateTime.UtcNow - h.LastUtc).TotalMinutes:F0} min fa")
                .ToList();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Digest: sezione heartbeat non leggibile."); }

        return new DigestData(lanes, fleet, attention, aiUsage, carry, heartbeats);
    }
}
