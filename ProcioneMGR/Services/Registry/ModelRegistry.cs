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
/// Esito di una transizione di stadio che può essere rifiutata con motivazione. Separato da
/// <see cref="PromotionOutcome"/> perché "promosso" non descrive un rientro da Retired: lì non si
/// sale di stadio, si torna in coda.
/// <para>
/// <see cref="Changed"/> significa <b>una scrittura è avvenuta</b>, non "lo stadio desiderato è
/// raggiunto" — diverso da <see cref="PromotionOutcome.Promoted"/>, che è un'asserzione di stato e
/// vale <c>true</c> anche per il no-op su un Champion già in carica. La differenza è voluta: qui
/// serve poter dire «non ho fatto niente, ed ecco perché», che è l'unico modo perché un pulsante
/// possa dire di no.
/// </para>
/// </summary>
public sealed record StageChangeOutcome(bool Changed, string Reason);

/// <summary>
/// Governo del ciclo di vita dei modelli ML (Fase 2, rif. docs/REPORT-ANALISI-RICOSTRUZIONE). Fa
/// rispettare due invarianti: (1) <b>un solo Champion per (Symbol, Timeframe)</b>; (2) un Challenger
/// può diventare Champion <b>solo se il suo Deflated Sharpe (Fase 1) è ≥ di quello del Champion in
/// carica</b> — un modello meno difendibile non sostituisce mai uno più difendibile. NON tocca mai il
/// trading Live: sposta solo di stadio i record. Additivo: lavora sui campi di ciclo di vita di
/// <see cref="SavedMlModel"/>, senza tabelle nuove.
/// <para>
/// [2026-08-19] <b>Retired non è terminale</b>, ed è una precisazione, non un allentamento. Lo era
/// diventato per omissione (nessuna transizione in uscita era mai stata scritta), non per una
/// decisione: nessun documento né commento ha mai dichiarato l'irreversibilità, mentre il messaggio
/// di rifiuto in <see cref="TryPromoteToChampionAsync"/> indicava da sempre un rientro. Il ritiro
/// non è d'altronde una quarantena: <c>MlModelLoader</c> non guarda lo stadio, quindi un modello
/// ritirato è già eseguibile in backtest, come gamba d'ensemble e via gRPC per id. L'unica cosa che
/// Retired toglie davvero è la sentinella Champion — cioè il motore — e quella si riguadagna solo
/// ri-passando dal gate DSR. Questo tipo resta comunque l'<b>unico scrittore</b> di
/// <see cref="SavedMlModel.Stage"/> in tutta la codebase: l'invariante di unicità del Champion non
/// ha alcun appoggio nel database (l'indice non è unico) e regge solo per questo.
/// </para>
/// </summary>
public interface IModelRegistry
{
    /// <summary>Il Champion attivo per (symbol, timeframe), o null se non esiste.</summary>
    Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default);

    /// <summary>Tutti i modelli di un gruppo (symbol, timeframe), per la UI del registry.</summary>
    Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default);

    /// <summary>
    /// Porta un modello Staging → Challenger (in valutazione). Rifiuta con motivazione se il modello
    /// non esiste o non è in Staging: prima era un <b>no-op silenzioso</b> su ogni altro stadio, e
    /// la pagina non poteva far altro che dichiarare successo comunque.
    /// </summary>
    Task<StageChangeOutcome> PromoteToChallengerAsync(int modelId, CancellationToken ct = default);

    /// <summary>
    /// Prova a promuovere il modello a Champion applicando il gate DSR e l'invariante di unicità.
    /// Se supera, l'eventuale Champion in carica viene ritirato. Idempotente: promuovere l'attuale
    /// Champion è un successo no-op.
    /// </summary>
    Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default);

    /// <summary>
    /// Ritira un modello con un motivo; opzionalmente marca "retrain accodato" (nessun retrain
    /// automatico). <b>Rifiuta se il modello è già ritirato</b> invece di sovrascrivere: il motivo
    /// di un ritiro da drift è una diagnosi, e una conferma rimasta aperta la cancellava
    /// sostituendola con «Ritirato manualmente dalla UI.».
    /// </summary>
    Task<StageChangeOutcome> RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default);

    /// <summary>
    /// [2026-08-19] Riporta un modello Retired a <see cref="ModelStage.Staging"/>. Restituisce
    /// l'<b>eleggibilità</b>, non lo stadio perduto: da Staging il modello deve ri-percorrere
    /// Challenger → Champion e quindi ri-superare il gate DSR e quello semantico. Rifiuta con
    /// motivazione se il modello non esiste o non è ritirato — mai un no-op silenzioso.
    /// </summary>
    Task<StageChangeOutcome> ReinstateToStagingAsync(int modelId, CancellationToken ct = default);
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

    public async Task<StageChangeOutcome> PromoteToChallengerAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return new StageChangeOutcome(false, "Modello inesistente.");

        // [2026-08-19] Qui si usciva in silenzio per ogni stadio diverso da Staging, e il chiamante
        // non aveva modo di saperlo: /registry dichiarava «Promosso a Challenger» anche quando non
        // era successo nulla. Ogni ramo ora ha la sua frase.
        if (model.Stage is not ModelStage.Staging)
        {
            return model.Stage switch
            {
                ModelStage.Challenger => new StageChangeOutcome(false, "Il modello è già Challenger: niente da fare."),
                ModelStage.Champion => new StageChangeOutcome(false, "Il modello è Champion in carica: non si retrocede a Challenger, semmai lo si ritira."),
                _ => new StageChangeOutcome(false, "Modello ritirato: riportalo prima in Staging (pulsante «Riporta in Staging»)."),
            };
        }

        model.Stage = ModelStage.Challenger;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Modello {Id} '{Name}' → Challenger.", model.Id, model.Name);
        return new StageChangeOutcome(true, "Promosso a Challenger.");
    }

    public async Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return new PromotionOutcome(false, "Modello inesistente.");
        if (model.Stage == ModelStage.Champion) return new PromotionOutcome(true, "Già Champion.");
        // [2026-08-19] Il messaggio indicava «va prima ri-portato a Challenger», cioè un percorso che
        // NON esisteva: PromoteToChallengerAsync accetta solo Staging e nessuna transizione usciva da
        // Retired. Ora il percorso c'è (ReinstateToStagingAsync) e la frase indica quello vero.
        if (model.Stage == ModelStage.Retired) return new PromotionOutcome(false, "Modello ritirato: riportalo prima in Staging (pulsante «Riporta in Staging» su /registry).");

        // [1.V fase 2] Gate 0: la semantica prima delle metriche — il Champion alimenta MlStrategy
        // (segnali long/short), quindi un modello che non predice rendimenti non è MAI promuovibile,
        // qualunque metrica abbia. (I modelli vol hanno comunque DSR null, ma il motivo giusto
        // merita il messaggio giusto.)
        if (!model.IsDirectional)
            return new PromotionOutcome(false, $"Il modello predice '{model.TargetKind}', non un rendimento: mai promuovibile a Champion.");

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
        // [2026-08-19] Qui si azzeravano RetiredAtUtc e RetiredReason. Era codice morto — un Retired
        // veniva respinto molto prima, alla guardia in cima — e con l'arrivo di ReinstateToStagingAsync
        // sarebbe diventato vivo E dannoso: avrebbe cancellato proprio la cicatrice che serve a chi
        // rivaluta un modello già ritirato per drift. Regola unica sui tre campi di ritiro:
        // RetiredAtUtc/RetiredReason/RetrainRequestedAtUtc descrivono l'ULTIMO ritiro, lo Stage dice
        // se è in corso. Nessuno li azzera; sono i lettori a essere consapevoli dello stadio.

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Modello {Id} '{Name}' → Champion ({Sym} {Tf}, DSR {Dsr:F3}); ritirati {N} precedenti.",
            model.Id, model.Name, model.Symbol, model.Timeframe, dsr, champions.Count);
        return new PromotionOutcome(true, "Promosso a Champion.", demotedId);
    }

    public async Task<StageChangeOutcome> RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return new StageChangeOutcome(false, "Modello inesistente.");

        // [2026-08-19] Ritirare un già-ritirato sovrascriveva RetiredAtUtc e RetiredReason senza dire
        // nulla. Il caso non è teorico: con la conferma a due passi aperta su una riga, il ciclo drift
        // può ritirare il modello nel frattempo, e il secondo clic avrebbe cancellato la diagnosi
        // («drift: 3 feature in alert (…)») sostituendola col motivo manuale. Il motivo di un ritiro
        // è una diagnosi, non un campo di servizio: non si riscrive per sbaglio.
        if (model.Stage == ModelStage.Retired)
            return new StageChangeOutcome(false,
                $"Il modello è già ritirato: il motivo precedente non è stato sovrascritto ({model.RetiredReason ?? "non registrato"}).");

        var now = DateTime.UtcNow;
        model.Stage = ModelStage.Retired;
        model.RetiredAtUtc = now;
        model.RetiredReason = reason;
        if (requestRetrain) model.RetrainRequestedAtUtc = now;

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Modello {Id} '{Name}' RITIRATO ({Reason}). Retrain accodato: {Retrain}.",
            model.Id, model.Name, reason, requestRetrain);
        return new StageChangeOutcome(true, $"Modello ritirato: {reason}");
    }

    public async Task<StageChangeOutcome> ReinstateToStagingAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return new StageChangeOutcome(false, "Modello inesistente.");

        // La guardia sullo stadio NON è ridondante con la UI: la conferma a due passi può restare
        // aperta mentre il ciclo drift o una seconda scheda cambiano lo stadio sotto. Un rifiuto
        // motivato è l'unico modo perché il secondo clic non menta.
        if (model.Stage != ModelStage.Retired)
            return new StageChangeOutcome(false, $"Il modello non è ritirato (stadio attuale: {model.Stage}): niente da riportare in Staging.");

        // Si atterra su Staging, MAI su Challenger o Champion: il rientro restituisce l'eleggibilità,
        // non il regno. Da qui il modello ripercorre gli stessi gate di chiunque altro, e il motore —
        // che risolve solo GetChampionAsync — resta fuori dal raggio di questa transizione.
        // I campi del ritiro restano scritti apposta: sono la cicatrice che l'operatore deve vedere
        // se un giorno riproverà a promuoverlo (il DeflatedSharpe, invece, nessuno l'ha ricalcolato).
        model.Stage = ModelStage.Staging;
        await db.SaveChangesAsync(ct);

        var previousReason = model.RetiredReason ?? "non registrato";
        logger.LogWarning("Modello {Id} '{Name}' RIPORTATO IN STAGING ({Sym} {Tf}). Motivo del ritiro scavalcato: {Reason}.",
            model.Id, model.Name, model.Symbol, model.Timeframe, previousReason);
        return new StageChangeOutcome(true,
            $"Riportato in Staging. Motivo del ritiro precedente: {previousReason}. Per tornare Champion deve ri-superare il gate DSR.");
    }
}
