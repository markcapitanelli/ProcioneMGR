using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Health;

/// <summary>Se il guscio si può riavviare adesso, e — quando no — perché no.</summary>
/// <param name="Quiet">Vero se un riavvio non interrompe nulla di irripetibile.</param>
/// <param name="Reason">Sempre valorizzato: anche il sì dice cosa ha guardato.</param>
public sealed record ShellQuietVerdict(bool Quiet, string Reason);

/// <summary>
/// [K3, PRD autonomia-piena 2026-08-31] <b>Il guscio dichiara se è un buon momento per fermarlo.</b>
///
/// <para>Serve perché l'aggiornamento automatico del guscio (lavoro <c>guscio</c> della plancia) non
/// può decidere da solo: la plancia non ha, di proposito, alcun riferimento ai progetti
/// dell'applicazione — deve poter dire «il guscio non compila» anche quando il guscio non compila —
/// e quindi non sa nulla di run di pipeline o di campagne. Chi sa è il guscio: gli si chiede.</para>
///
/// <para><b>Cosa costa davvero un riavvio</b>, misurato il 2026-08-31: ~3m36s di indisponibilità,
/// build inclusa. NON si perdono posizioni, corsie né lease — con
/// <c>Trading:UseRemoteTrading=true</c> vivono nel pod, e il lease è un advisory lock tenuto dalla
/// connessione del motore. Si perdono i 22 hosted service del guscio per quei minuti, e un run di
/// pipeline in volo resta affidato all'auto-resume, che ha un budget finito — in tabella ci sono
/// tre run <c>Paused</c> di luglio che quel budget lo hanno esaurito. Quindi il run in volo è la
/// condizione che decide.</para>
///
/// <para><b>Perché un endpoint separato da <c>/health</c>.</b> La liveness non deve MAI dipendere
/// dal database: se Postgres cade, <c>/health</c> deve continuare a rispondere 200 — altrimenti
/// Kubernetes riavvia un processo sano per un guasto che sta altrove, e il watchdog dichiara giù un
/// guscio che sta benissimo. Questa domanda invece il database lo interroga per forza, e quando non
/// ci riesce risponde <b>NON quieto</b>: non sapere non è permesso di riavviare.</para>
/// </summary>
public sealed class ShellQuietProbe(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ShellQuietProbe> logger)
{
    /// <summary>Stati di un run che non è finito: un riavvio qui dentro lo lascia a metà.</summary>
    private static readonly string[] NonTerminali = ["Running", "Pending", "Queued"];

    public async Task<ShellQuietVerdict> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var inVolo = await db.PipelineRuns.AsNoTracking()
                .CountAsync(r => NonTerminali.Contains(r.Status), ct);
            if (inVolo > 0)
            {
                return new ShellQuietVerdict(false,
                    $"{inVolo} run di pipeline in volo: un riavvio adesso li affida all'auto-resume, "
                    + "che ha un budget finito (in tabella ci sono tre run Paused di luglio che lo hanno esaurito)");
            }

            // Una campagna con un run appeso sta aspettando l'esito di QUEL run: fermarla nel
            // mezzo non perde dati, ma la lascia in uno stato che solo il giro successivo ripulisce.
            var campagnaAppesa = await db.VettingCampaigns.AsNoTracking()
                .CountAsync(c => c.Enabled && c.PendingRunId != null, ct);
            if (campagnaAppesa > 0)
            {
                return new ShellQuietVerdict(false,
                    $"{campagnaAppesa} campagna in attesa dell'esito di un run: si riprova al prossimo giro");
            }

            // Le posizioni aperte NON impediscono il riavvio — vivono nel pod e il guscio non le
            // tocca — ma vanno DETTE: chi legge il verdetto deve sapere cosa non è stato guardato.
            var posizioni = await db.OpenPositions.AsNoTracking().CountAsync(ct);
            return new ShellQuietVerdict(true,
                $"nessun run in volo, nessuna campagna appesa; {posizioni} posizioni aperte "
                + "(vivono nel pod: il riavvio del guscio non le tocca)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fail-CLOSED, ed è la differenza con la diagnostica: qui la risposta autorizza
            // un'AZIONE che ferma un processo. Non sapere non è permesso.
            logger.LogWarning(ex, "Sonda di quiete: lettura fallita — rispondo NON quieto.");
            return new ShellQuietVerdict(false, $"stato non leggibile ({ex.GetType().Name}): non autorizzo un riavvio al buio");
        }
    }
}
