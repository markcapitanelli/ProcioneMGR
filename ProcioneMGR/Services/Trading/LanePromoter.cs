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

        // [2026-09-05] PRE-FLIGHT: LE CREDENZIALI SI CONTROLLANO PRIMA DI TOCCARE LA CORSIA.
        //
        // La sequenza qui sotto ferma la corsia e poi la riavvia nella nuova modalità; è lo
        // StartAsync a caricare le credenziali, e se mancano fallisce DOPO aver sostituito lo stato
        // della sessione (PnL, picco, data di avvio azzerati) e lasciato la corsia ferma in Testnet.
        // Sul database vivo esiste una sola credenziale testnet (Bitget) e sei corsie su Binance:
        // con la promozione automatica accesa ogni corsia Binance matura sarebbe stata fermata e
        // svuotata per un errore prevedibile. Un rifiuto che non tocca nulla è la forma giusta.
        if (newMode == TradingMode.Testnet && serviceProvider.GetService<Security.IExchangeCredentialReader>() is { } reader)
        {
            if (!Enum.TryParse<ExchangeName>(before.ExchangeName, out var exchange))
            {
                throw new InvalidOperationException(
                    $"Promozione a Testnet rifiutata PRIMA di toccare la corsia {laneId}: exchange «{before.ExchangeName}» non riconosciuto.");
            }
            var credential = await reader.FindForTradingAsync(exchange, testnet: true, ct);
            if (credential is null)
            {
                throw new InvalidOperationException(
                    $"Promozione a Testnet rifiutata PRIMA di toccare la corsia {laneId} ({before.Symbol}): nessuna credenziale " +
                    $"testnet per {exchange} in /settings/exchanges. La corsia resta in {before.Mode}, in corsa, con le sue posizioni.");
            }
            if (!credential.IsDecryptable)
            {
                throw new InvalidOperationException(
                    $"Promozione a Testnet rifiutata PRIMA di toccare la corsia {laneId} ({before.Symbol}): la credenziale testnet " +
                    $"«{credential.Label}» di {exchange} non si decifra con la master key corrente. La corsia resta in {before.Mode}.");
            }
        }

        // [M2] Flatten PRIMA del cambio modalità, in entrambe le direzioni:
        // - Paper→Testnet: le posizioni simulate non devono "sembrare" reali nella nuova sessione;
        // - Testnet→Paper: le posizioni REALI vanno chiuse reduce-only sull'exchange ORA — dopo
        //   StartAsync(Paper) le righe verrebbero cancellate e l'esposizione resterebbe orfana.
        // Niente emergency stop: la promozione non è un'emergenza (il flag bloccherebbe la corsia).
        await engine.CloseAllPositionsAsync($"LaneModeChange:{before.Mode}->{newMode}", ct);

        // [2026-09-05] IL FLATTEN SI VERIFICA, NON SI PRESUME. CloseAllPositionsAsync è best-effort:
        // una chiusura rifiutata o incerta dall'exchange non lancia. Cambiare modalità con una
        // posizione ancora aperta significa, in Testnet→Paper, cancellarne la riga senza alcun
        // ordine (esposizione reale orfana), e in Paper→Testnet farla contare in una sessione che
        // non l'ha aperta. Se resta qualcosa, la corsia non si tocca: continua nella modalità di
        // prima, con le sue protezioni, e il worker riproverà al ciclo successivo.
        var residue = await engine.GetOpenPositionsAsync(ct);
        if (residue.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cambio di modalità {before.Mode}→{newMode} sulla corsia {laneId} ({before.Symbol}) interrotto: {residue.Count} posizioni " +
                "ancora aperte dopo il flatten (chiusura rifiutata o incerta dall'exchange). La corsia resta in corsa nella modalità di prima.");
        }

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
