using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Registry;

/// <summary>Opzioni del registry (sezione config "Registry").</summary>
public sealed class ModelRegistryOptions
{
    /// <summary>
    /// Deflated Sharpe minimo perché un modello possa diventare Champion, anche se non c'è un
    /// Champion in carica da battere. Default 0: non blocca il primo Champion, ma il gate "batti
    /// l'incumbent" resta sempre attivo. Alzabile (es. 0.95) per pretendere significatività assoluta.
    /// </summary>
    public double MinChampionDeflatedSharpe { get; set; }
}

/// <summary>Esito di un tentativo di promozione a Champion.</summary>
public sealed record PromotionOutcome(bool Promoted, string Reason, int? DemotedChampionId = null);

/// <summary>
/// Governo del ciclo di vita dei modelli ML (Fase 2, rif. docs/REPORT-ANALISI-RICOSTRUZIONE). Fa
/// rispettare due invarianti: (1) <b>un solo Champion per (Symbol, Timeframe)</b>; (2) un Challenger
/// può diventare Champion <b>solo se il suo Deflated Sharpe (Fase 1) è ≥ di quello del Champion in
/// carica</b> — un modello meno difendibile non sostituisce mai uno più difendibile. NON tocca mai il
/// trading Live: sposta solo di stadio i record. Additivo: lavora sui campi di ciclo di vita di
/// <see cref="SavedMlModel"/>, senza tabelle nuove.
/// </summary>
public interface IModelRegistry
{
    /// <summary>Il Champion attivo per (symbol, timeframe), o null se non esiste.</summary>
    Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default);

    /// <summary>Tutti i modelli di un gruppo (symbol, timeframe), per la UI del registry.</summary>
    Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default);

    /// <summary>Porta un modello Staging → Challenger (in valutazione). No-op se già oltre.</summary>
    Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default);

    /// <summary>
    /// Prova a promuovere il modello a Champion applicando il gate DSR e l'invariante di unicità.
    /// Se supera, l'eventuale Champion in carica viene ritirato. Idempotente: promuovere l'attuale
    /// Champion è un successo no-op.
    /// </summary>
    Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default);

    /// <summary>Ritira un modello con un motivo; opzionalmente marca "retrain accodato" (nessun retrain automatico).</summary>
    Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default);
}

/// <inheritdoc cref="IModelRegistry"/>
public sealed class ModelRegistry(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ModelRegistryOptions options,
    ILogger<ModelRegistry> logger) : IModelRegistry
{
    public async Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SavedMlModels.AsNoTracking()
            .Where(m => m.Symbol == symbol && m.Timeframe == timeframe && m.Stage == ModelStage.Champion)
            .OrderByDescending(m => m.PromotedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SavedMlModels.AsNoTracking()
            .Where(m => m.Symbol == symbol && m.Timeframe == timeframe)
            .OrderByDescending(m => m.Stage == ModelStage.Champion)
            .ThenByDescending(m => m.DeflatedSharpe)
            .ToListAsync(ct);
    }

    public async Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new InvalidOperationException($"Modello {modelId} inesistente.");

        if (model.Stage is ModelStage.Staging)
        {
            model.Stage = ModelStage.Challenger;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Modello {Id} '{Name}' → Challenger.", model.Id, model.Name);
        }
    }

    public async Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return new PromotionOutcome(false, "Modello inesistente.");
        if (model.Stage == ModelStage.Champion) return new PromotionOutcome(true, "Già Champion.");
        if (model.Stage == ModelStage.Retired) return new PromotionOutcome(false, "Modello ritirato: va prima ri-portato a Challenger.");

        // Gate 1: nessuna promozione alla cieca — serve un DSR misurato.
        if (model.DeflatedSharpe is not { } dsr)
            return new PromotionOutcome(false, "Nessun Deflated Sharpe misurato: non promuovibile a Champion.");
        if (dsr < options.MinChampionDeflatedSharpe)
            return new PromotionOutcome(false, $"DSR {dsr:F3} sotto la soglia minima {options.MinChampionDeflatedSharpe:F3}.");

        // Gate 2: batti l'incumbent. Gestisce anche l'anomalia di più Champion (li ritira tutti).
        var champions = await db.SavedMlModels
            .Where(m => m.Symbol == model.Symbol && m.Timeframe == model.Timeframe
                        && m.Stage == ModelStage.Champion && m.Id != model.Id)
            .ToListAsync(ct);

        var incumbent = champions.OrderByDescending(c => c.DeflatedSharpe).FirstOrDefault();
        if (incumbent?.DeflatedSharpe is { } champDsr && dsr < champDsr)
            return new PromotionOutcome(false, $"DSR {dsr:F3} < Champion in carica {champDsr:F3}: promozione rifiutata.");

        var now = DateTime.UtcNow;
        int? demotedId = incumbent?.Id;
        foreach (var c in champions)
        {
            c.Stage = ModelStage.Retired;
            c.RetiredAtUtc = now;
            c.RetiredReason = "Superato da una versione con Deflated Sharpe ≥.";
        }

        var maxVersion = await db.SavedMlModels
            .Where(m => m.Symbol == model.Symbol && m.Timeframe == model.Timeframe)
            .MaxAsync(m => (int?)m.Version, ct) ?? 0;

        model.Stage = ModelStage.Champion;
        model.PromotedAtUtc = now;
        model.Version = maxVersion + 1;
        model.RetiredAtUtc = null;
        model.RetiredReason = null;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Modello {Id} '{Name}' → Champion ({Sym} {Tf}, DSR {Dsr:F3}); ritirati {N} precedenti.",
            model.Id, model.Name, model.Symbol, model.Timeframe, dsr, champions.Count);
        return new PromotionOutcome(true, "Promosso a Champion.", demotedId);
    }

    public async Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new InvalidOperationException($"Modello {modelId} inesistente.");

        var now = DateTime.UtcNow;
        model.Stage = ModelStage.Retired;
        model.RetiredAtUtc = now;
        model.RetiredReason = reason;
        if (requestRetrain) model.RetrainRequestedAtUtc = now;

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Modello {Id} '{Name}' RITIRATO ({Reason}). Retrain accodato: {Retrain}.",
            model.Id, model.Name, reason, requestRetrain);
    }
}
