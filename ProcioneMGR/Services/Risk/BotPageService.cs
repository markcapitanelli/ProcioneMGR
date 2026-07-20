using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// [R3] Orchestrazione della Modalità Semplice (<c>Components/Pages/Bot.razor</c>).
///
/// Stessa divisione di responsabilità di <see cref="TradingPageService"/>: qui vivono le chiamate
/// ai servizi e lo stato che ne deriva, così la logica è testabile senza Blazor; il componente
/// resta responsabile solo di rendering e ciclo di vita.
///
/// LA MODALITÀ SEMPLICE È UNA VISTA, NON UN MOTORE PARALLELO. Opera su una corsia esistente
/// attraverso gli stessi <see cref="IEnsembleManager"/> e <see cref="ITradingEngine"/> della pagina
/// /trading: nessun percorso alternativo verso l'exchange, nessun controllo di sicurezza scavalcato.
/// Ciò che semplifica è la SCELTA (capitale + profilo invece di dodici soglie), non l'esecuzione.
///
/// Registrato Scoped: in Blazor Server uno scope = un circuito.
/// </summary>
public sealed class BotPageService(
    IServiceProvider services,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineApplier applier,
    ILogger<BotPageService> logger)
{
    /// <summary>
    /// La Modalità Semplice governa la corsia 0. Le altre restano alla pagina /trading per l'uso
    /// esperto: una vista "un pulsante" che gestisse tre corsie non sarebbe più semplice.
    /// </summary>
    public const int BotLaneId = 0;

    /// <summary>Round-turn usato per la stima dei costi mostrata all'utente: fee 0,1%/lato + slippage 0,05%/fill, gli stessi di R2.</summary>
    public const decimal RoundTurnPercent = 0.30m;

    // --- scelte dell'utente (copia di lavoro del form) ---
    public decimal Capital { get; set; } = 10_000m;
    public string ProfileName { get; set; } = RiskProfiles.Default.Name;

    public RiskProfile Profile => RiskProfiles.Find(ProfileName) ?? RiskProfiles.Default;

    // --- stato osservato ---
    public TradingEngineStatus? Status { get; private set; }
    public List<OpenPosition> Positions { get; private set; } = [];
    public List<TradeRecord> RecentTrades { get; private set; } = [];

    /// <summary>Cosa la corsia è configurata per operare, in una riga leggibile. Null se non c'è nulla.</summary>
    public string? StrategySummary { get; private set; }

    /// <summary>Timeframe realmente configurato in corsia: può divergere da quelli preferiti dal profilo.</summary>
    public string? ConfiguredTimeframe { get; private set; }

    /// <summary>Run di ricerca più recente con un ensemble applicabile, se la corsia è vuota.</summary>
    public Guid? LatestApplicableRunId { get; private set; }

    public bool HasStrategies => StrategySummary is not null;
    public bool IsRunning => Status?.IsRunning == true;
    public bool Busy { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>
    /// Il profilo scelto preferisce timeframe diversi da quello effettivamente in corsia. Non è un
    /// errore — il tetto di operazioni del profilo protegge comunque — ma va detto: una strategia a
    /// 15m sotto un profilo Prudente verrà rallentata parecchio, e il silenzio farebbe sembrare il
    /// bot rotto invece che prudente.
    /// </summary>
    public bool TimeframeMismatch =>
        ConfiguredTimeframe is { Length: > 0 } tf
        && !Profile.PreferredTimeframes.Contains(tf, StringComparer.OrdinalIgnoreCase);

    private ITradingEngine Engine => services.GetRequiredKeyedService<ITradingEngine>(BotLaneId);
    private IEnsembleManager Ensemble => services.GetRequiredKeyedService<IEnsembleManager>(BotLaneId);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var cfg = await Ensemble.GetConfigurationAsync(ct);
            Capital = cfg.TotalCapital > 0m ? cfg.TotalCapital : 10_000m;
            ProfileName = RiskProfiles.Find(cfg.RiskProfileName)?.Name ?? RiskProfiles.Default.Name;
            ConfiguredTimeframe = cfg.Timeframe;

            var active = cfg.Strategies.Count(s => s.IsActive);
            StrategySummary = active == 0
                ? null
                : $"{cfg.Symbol} {cfg.Timeframe} — {active} strategi{(active == 1 ? "a" : "e")}";

            await RefreshAsync(ct);

            if (!HasStrategies)
            {
                LatestApplicableRunId = await FindLatestApplicableRunAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Fail(ex, "Caricamento della configurazione fallito");
        }
    }

    /// <summary>Aggiorna solo lo stato osservato: chiamato dal polling, non deve toccare il form.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var engine = Engine;
            Status = await engine.GetStatusAsync(ct);
            Positions = await engine.GetOpenPositionsAsync(ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            RecentTrades = await db.TradeRecords.AsNoTracking()
                .Where(t => t.LaneId == BotLaneId)
                .OrderByDescending(t => t.ClosedAtUtc)
                .Take(10)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Il polling non deve trasformare un errore transitorio in un banner permanente:
            // si logga e si tiene l'ultimo stato noto.
            logger.LogWarning(ex, "Refresh della Modalità Semplice fallito; mantengo l'ultimo stato noto.");
        }
    }

    /// <summary>Salva capitale e profilo sulla corsia. Non avvia nulla.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await GuardedAsync("Impostazioni salvate.", async () =>
        {
            var cfg = await Ensemble.GetConfigurationAsync(ct);
            cfg.TotalCapital = Capital;
            cfg.RiskProfileName = Profile.Name;
            await Ensemble.UpdateConfigurationAsync(cfg, ct);
        });
    }

    /// <summary>
    /// Salva e avvia in PAPER. Mai in Testnet o Live: il passaggio a denaro reale resta un'azione
    /// esplicita dalla pagina /trading, dietro i controlli che già esistono. Una vista "un pulsante"
    /// non deve poter avviare operatività con soldi veri.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await GuardedAsync("Bot avviato in simulazione (Paper).", async () =>
        {
            var cfg = await Ensemble.GetConfigurationAsync(ct);
            if (cfg.Strategies.Count(s => s.IsActive) == 0)
            {
                throw new InvalidOperationException(
                    "Non c'è ancora nessuna strategia validata su questa corsia: avviare il motore non produrrebbe alcuna operazione.");
            }
            cfg.TotalCapital = Capital;
            cfg.RiskProfileName = Profile.Name;
            await Ensemble.UpdateConfigurationAsync(cfg, ct);

            await Engine.StartAsync(TradingMode.Paper, ct);
        });
    }

    public async Task StopAsync(CancellationToken ct = default)
        => await GuardedAsync("Bot fermato.", async () => await Engine.StopAsync(ct));

    /// <summary>
    /// Schiera sulla corsia l'ensemble dell'ultima ricerca completata. Scrive SOLO configurazione:
    /// non avvia trading (stessa garanzia di <see cref="IPipelineApplier"/>).
    /// </summary>
    public async Task ApplyLatestResearchAsync(CancellationToken ct = default)
    {
        if (LatestApplicableRunId is not Guid runId)
        {
            Message = "Nessun risultato di ricerca disponibile da applicare.";
            IsError = true;
            return;
        }

        await GuardedAsync("Strategie schierate sulla corsia. Ora puoi avviare il bot.", async () =>
        {
            var result = await applier.ApplyRunAsync(runId, ct);
            logger.LogInformation("Modalità Semplice: applicato il run {RunId} — {Message}", runId, result.Message);
        });
    }

    private async Task<Guid?> FindLatestApplicableRunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed")
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.Id, r.RecommendationJson })
            .Take(10)
            .ToListAsync(ct);

        // "Ha una raccomandazione" non basta: dev'esserci almeno una gamba, altrimenti applicarlo
        // non cambierebbe nulla e l'utente resterebbe con un pulsante che sembra non fare niente.
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c.RecommendationJson)
                && c.RecommendationJson.Contains("\"ensembleLegs\"", StringComparison.OrdinalIgnoreCase)
                && !c.RecommendationJson.Contains("\"ensembleLegs\":[]", StringComparison.OrdinalIgnoreCase))
            {
                return c.Id;
            }
        }
        return null;
    }

    private async Task GuardedAsync(string successMessage, Func<Task> action)
    {
        Busy = true;
        Message = null;
        IsError = false;
        try
        {
            await action();
            Message = successMessage;
            await RefreshAsync();
            await LoadSummaryAsync();
        }
        catch (Exception ex)
        {
            Fail(ex, "Operazione non riuscita");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task LoadSummaryAsync()
    {
        var cfg = await Ensemble.GetConfigurationAsync();
        ConfiguredTimeframe = cfg.Timeframe;
        var active = cfg.Strategies.Count(s => s.IsActive);
        StrategySummary = active == 0 ? null : $"{cfg.Symbol} {cfg.Timeframe} — {active} strategi{(active == 1 ? "a" : "e")}";
        if (!HasStrategies) LatestApplicableRunId = await FindLatestApplicableRunAsync(CancellationToken.None);
    }

    private void Fail(Exception ex, string prefix)
    {
        logger.LogError(ex, "{Prefix} nella Modalità Semplice.", prefix);
        Message = $"{prefix}: {ex.Message}";
        IsError = true;
    }
}
