using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Applies a lane mode change (Paper↔Testnet) as a stop→restart of the lane's keyed trading engine,
/// and records a user-visible audit entry. This is the "action" half of the promotion feature
/// (decisions live in <see cref="IPromotionEvaluator"/>).
///
/// SAFETY (defense in depth): this method THROWS if asked to switch a lane to
/// <see cref="TradingMode.Live"/> — no automated path may ever put a lane into Live. Switching to
/// Testnet uses the already-configured Testnet credentials; if they are missing the engine's
/// StartAsync throws a clear error (not silent), the lane is left stopped, and the failure is logged.
/// </summary>
public interface ILanePromoter
{
    Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default);
}

/// <inheritdoc cref="ILanePromoter"/>
public sealed class LanePromoter(
    IServiceProvider serviceProvider,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<PromotionEvaluatorOptions> options,
    ILogger<LanePromoter> logger) : ILanePromoter
{
    public async Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
    {
        // Confine non negoziabile: nessun automatismo può portare una corsia in Live.
        if (newMode == TradingMode.Live)
        {
            throw new InvalidOperationException(
                "Passaggio automatico a Live non consentito: Testnet→Live richiede sempre conferma manuale da /trading.");
        }

        var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId);
        var before = await engine.GetStatusAsync(ct);

        // [M2] Flatten PRIMA del cambio modalità, in entrambe le direzioni:
        // - Paper→Testnet: le posizioni simulate non devono "sembrare" reali nella nuova sessione;
        // - Testnet→Paper: le posizioni REALI vanno chiuse reduce-only sull'exchange ORA — dopo
        //   StartAsync(Paper) le righe verrebbero cancellate e l'esposizione resterebbe orfana.
        // Niente emergency stop: la promozione non è un'emergenza (il flag bloccherebbe la corsia).
        await engine.CloseAllPositionsAsync($"LaneModeChange:{before.Mode}->{newMode}", ct);

        await engine.StopAsync(ct);
        // StartAsync(Testnet) carica le credenziali Testnet; se mancano lancia un errore chiaro e la
        // corsia resta ferma (nessun cambio silenzioso). Lo propaghiamo al chiamante (il worker lo logga).
        await engine.StartAsync(newMode, ct);

        var action = newMode == TradingMode.Testnet && before.Mode == TradingMode.Paper ? "LanePromoted" : "LaneDemoted";
        logger.LogWarning("Corsia {Lane} ({Symbol}) {Action}: {Before} → {After}. {Reason}",
            laneId, before.Symbol, action, before.Mode, newMode, reason);

        if (options.CurrentValue.NotifyOnPromotion)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.TradingAuditLogs.Add(new TradingAuditLog
            {
                LaneId = laneId,
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                Details = JsonSerializer.Serialize(new { from = before.Mode.ToString(), to = newMode.ToString(), symbol = before.Symbol, reason }),
                Mode = newMode,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
