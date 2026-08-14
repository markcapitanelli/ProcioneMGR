using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>Un candidato grigio schierabile, come lo mostra il form.</summary>
public sealed record GreyChoice(
    string StrategyName, string Symbol, string Timeframe,
    decimal HoldoutSharpe, int HoldoutTrades, string? RejectReason);

/// <summary>Esito dello schieramento, scritto per un umano.</summary>
public sealed record GreyDeployResult(bool Success, string Message);

/// <summary>
/// [F5] IL CLICK UMANO della fascia grigia: prende un candidato grigio da un run (identità +
/// parametri ESATTI validati), gli monta il bracket SL/TP data-driven (stesso <see cref="AutoBracket"/>
/// dell'applica) e lo scrive su una corsia di FLOTTA libera, avviandola in Paper se richiesto.
///
/// Confini (gli stessi della Queen Bee, qui applicati a una azione UMANA):
/// - solo corsie oltre l'impronta auto-apply, mai quarantene, mai corsie che girano;
/// - solo Paper: la modalità non è nemmeno un parametro;
/// - solo candidati che passano il filtro grigio del lettore (Sharpe holdout positivo, bocciati
///   per sola finestra corta) — questo servizio non è una porta di servizio per schierare
///   qualunque cosa, è il braccio della proposta F5.
/// Ogni schieramento finisce nel journal della flotta con Source="human".
/// </summary>
public interface IGreyDeployer
{
    Task<IReadOnlyList<GreyChoice>> ListGreyAsync(Guid runId, CancellationToken ct = default);

    Task<GreyDeployResult> DeployAsync(
        Guid runId, string strategyName, string symbol, string timeframe,
        int laneId, bool startPaper, CancellationToken ct = default);
}

public sealed class GreyDeployer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    IPipelineApplier applier,
    ILaneQuarantineStore quarantineStore,
    ExcursionAnalyzer excursion,
    ILogger<GreyDeployer> logger) : IGreyDeployer
{
    public async Task<IReadOnlyList<GreyChoice>> ListGreyAsync(Guid runId, CancellationToken ct = default)
    {
        var grey = await LoadGreyCandidatesAsync(runId, ct);
        return grey
            .OrderByDescending(c => c.HoldoutSharpe)
            .Select(c => new GreyChoice(c.StrategyName, c.Symbol, c.Timeframe, c.HoldoutSharpe, c.HoldoutTrades, c.RejectReason))
            .ToList();
    }

    public async Task<GreyDeployResult> DeployAsync(
        Guid runId, string strategyName, string symbol, string timeframe,
        int laneId, bool startPaper, CancellationToken ct = default)
    {
        // --- La corsia: di flotta, libera, senza vincoli. Si RILEGGE lo stato adesso, non ci si
        // fida della lista mostrata al momento del render (l'operatore può aver cliccato tardi).
        if (laneId < applier.LaneCount || laneId >= TradingLanes.Count)
        {
            return new(false, $"La corsia {laneId} non è una corsia di flotta (valide: {applier.LaneCount}..{TradingLanes.Count - 1} — le prime {applier.LaneCount} sono l'impronta dell'auto-apply).");
        }
        if (await quarantineStore.GetAsync(laneId, ct) is not null)
        {
            return new(false, $"La corsia {laneId} è in QUARANTENA: va prima esaminata e liberata da /trading.");
        }
        var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId);
        TradingEngineStatus status;
        try { status = await engine.GetStatusAsync(ct); }
        catch (Exception ex)
        {
            return new(false, $"Stato della corsia {laneId} non leggibile ({ex.Message}): non si schiera su una corsia di cui non si sa nulla.");
        }
        if (status.IsRunning)
        {
            return new(false, $"La corsia {laneId} sta GIRANDO ({status.Symbol}): fermala prima, o scegline una libera.");
        }

        // --- Il candidato: deve esistere nel run ED essere grigio per il filtro del lettore.
        var candidate = (await LoadGreyCandidatesAsync(runId, ct)).FirstOrDefault(c =>
            c.StrategyName.Equals(strategyName, StringComparison.Ordinal)
            && c.Symbol.Equals(symbol, StringComparison.Ordinal)
            && c.Timeframe.Equals(timeframe, StringComparison.Ordinal));
        if (candidate is null)
        {
            return new(false, "Candidato non trovato fra i GRIGI di quel run: questo pulsante schiera solo le proposte della fascia grigia, non qualunque cosa.");
        }

        // --- Il bracket: stesso calcolo dell'applica. Senza protezioni derivabili non si parte.
        var (sl, tp) = await AutoBracket.ComputeAsync(dbFactory, excursion, symbol, timeframe, ct);
        if (sl <= 0m && tp <= 0m)
        {
            return new(false, $"Bracket SL/TP non derivabile per {symbol} {timeframe} (dati insufficienti): un forward test senza protezioni non si schiera da un click.");
        }

        // --- Scrittura della configurazione (solo configurazione: l'avvio è il passo dopo).
        var manager = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(laneId);
        var cfg = await manager.GetConfigurationAsync(ct);
        cfg.Symbol = symbol;
        cfg.Timeframe = timeframe;
        cfg.Strategies =
        [
            new EnsembleStrategy
            {
                StrategyName = candidate.StrategyName,
                DisplayName = $"{candidate.StrategyName} (fascia grigia, run {runId.ToString()[..8]})",
                Parameters = new(candidate.Parameters),
                CurrentAllocation = 100m,
                IsActive = true,
                StopLossPercent = sl > 0m ? sl : null,
                TakeProfitPercent = tp > 0m ? tp : null,
                ExpectedSharpe = candidate.HoldoutSharpe != 0m ? candidate.HoldoutSharpe : null,
                ExpectedProfitFactor = candidate.HoldoutProfitFactor != 0m ? candidate.HoldoutProfitFactor : null,
                ExpectedMaxDrawdown = candidate.HoldoutMaxDrawdown != 0m ? candidate.HoldoutMaxDrawdown : null,
                SourceVerdict = "Grey", // [T1] stessa etichetta della pipeline: il badge non dipende dal percorso di schieramento
            },
        ];
        await manager.UpdateConfigurationAsync(cfg, ct);

        var startedText = "configurata, DA AVVIARE da /trading";
        string? error = null;
        if (startPaper)
        {
            try
            {
                await engine.StartAsync(TradingMode.Paper, ct);
                startedText = "avviata in Paper";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                startedText = $"configurata ma NON avviata ({ex.Message})";
            }
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.OrchestratorDecisions.Add(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow,
                Kind = "Assign",
                LaneId = laneId,
                RunId = runId,
                Source = "human",
                Applied = error is null,
                DryRun = false,
                Error = error,
                Reason = $"[F5, click umano] {candidate.StrategyName} {symbol} {timeframe} → corsia {laneId}, {startedText}. " +
                         $"Sharpe holdout {candidate.HoldoutSharpe:F2} su {candidate.HoldoutTrades} trade; SL {sl:F2}% / TP {tp:F2}%.",
            });
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Candidato grigio schierato: {Strategy} {Symbol} {Timeframe} → corsia {Lane} ({Stato}).",
            candidate.StrategyName, symbol, timeframe, laneId, startedText);
        return new(error is null, $"{candidate.StrategyName} {symbol} {timeframe} → corsia {laneId}: {startedText}. SL {sl:F2}% / TP {tp:F2}% (bracket automatico).");
    }

    /// <summary>I candidati GRIGI del run, con lo STESSO filtro del lettore della flotta (nessuna doppia verità).</summary>
    private async Task<List<ValidatedCandidate>> LoadGreyCandidatesAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var payload = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.RunId == runId && a.Kind == "ValidatedCandidates")
            .Select(a => a.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return [];

        List<ValidatedCandidate> validated;
        try { validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(payload) ?? []; }
        catch (JsonException) { return []; }

        return validated.Where(FleetStateReader.IsGrey).ToList();
    }
}
