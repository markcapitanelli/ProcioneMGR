using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Llm;

/// <summary>Kind dell'artifact che memorizza l'advisory AI di un run.</summary>
public static class LlmArtifactKinds
{
    public const string Advisory = "LlmAdvisory";
}

public interface IPipelineSupervisor
{
    /// <summary>
    /// Analizza un run completato: legge la sua <c>PipelineRecommendation</c>, chiede un parere
    /// all'LLM e persiste un <see cref="SupervisorAdvisory"/> come PipelineArtifact. Idempotente per
    /// run (non riscrive se un advisory esiste già). Un errore TRANSITORIO (credito, rate-limit,
    /// rete, breaker aperto) non persiste nulla: il run resta pendente e verrà ritentato da solo.
    /// Restituisce false se saltato o rinviato.
    /// </summary>
    Task<bool> SuperviseRunAsync(Guid runId, CancellationToken ct, bool forceProbe = false);

    /// <summary>
    /// Elimina gli advisory in errore dei run completati da <paramref name="since"/> in poi, così
    /// il worker li rianalizza (l'idempotenza per-run altrimenti li blocca per sempre). Azione
    /// manuale-only dalla UI: gli errori più vecchi della finestra restano come record storico.
    /// </summary>
    Task<int> DeleteErrorAdvisoriesAsync(DateTime since, CancellationToken ct);
}

/// <summary>
/// Layer AI di supervisione del ciclo di ricerca. CONFINE DI SICUREZZA NON NEGOZIABILE: questo
/// servizio è <b>solo advisory</b>. Legge i risultati di un run e produce un parere testuale +
/// suggerimenti sui parametri di caccia; NON avvia trading, NON passa mai in Live, NON tocca
/// <c>SafetyChecker</c> né l'apertura di posizioni. Per costruzione non riceve in DI alcun servizio
/// di esecuzione/trading: può solo leggere PipelineRun e scrivere un artifact di tipo advisory.
/// </summary>
public sealed class PipelineSupervisor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILlmClient llm,
    ILlmCallGuard guard,
    IOptionsMonitor<LlmOptions> options,
    ILogger<PipelineSupervisor> logger,
    ProcioneMetrics? metrics = null,
    INotifier? notifier = null,
    ProcioneMGR.Services.Sentiment.SentimentSnapshotCache? sentimentCache = null) : IPipelineSupervisor
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt = """
        Sei un supervisore AI del ciclo di ricerca di una piattaforma di trading algoritmico crypto.
        Ricevi il riepilogo (PipelineRecommendation) di UN run di ricerca già concluso: regime di
        mercato, volatilità, sentiment, quanti candidati sono stati valutati e quanti sono
        sopravvissuti alla validazione, il migliore, gli alert e le azioni suggerite dal motore, e i
        limiti di rischio proposti. Ricevi anche il CONTESTO OPERATIVO attuale della piattaforma
        (stato delle corsie di trading, eventuali quarantene, regime di mercato del run): usalo per
        ancorare il parere alla situazione reale, non solo ai numeri del run.

        Il tuo compito:
        1. Scrivere un riepilogo esecutivo BREVE e LEGGIBILE in italiano per l'utente.
        2. Proporre eventuali aggiustamenti ai PARAMETRI DI CACCIA (es. soglie di sopravvivenza,
           finestre temporali, numero di candidati, limiti di rischio) — come PROPOSTE motivate, non
           come modifiche da applicare in automatico.
        3. Elencare le decisioni che richiedono CONFERMA ESPLICITA dell'utente.

        VINCOLI DI SICUREZZA (assoluti): sei solo un consulente. NON puoi avviare trading live, NON
        puoi bypassare i controlli di sicurezza, NON puoi aprire posizioni. Non proporre di farlo:
        l'esecuzione reale resta sempre dietro conferma manuale dell'utente e dei controlli di
        sicurezza della piattaforma. Le tue proposte di parametri sono spunti, mai comandi.

        Rispondi ESCLUSIVAMENTE con un oggetto JSON valido (nessun testo prima o dopo) con questa forma:
        {
          "summary": "string (italiano, 2-5 frasi)",
          "parameterSuggestions": [
            {"parameter":"string","currentOrObserved":"string","suggested":"string","rationale":"string"}
          ],
          "decisionsForUser": ["string", "..."],
          "confidence": "bassa|media|alta"
        }
        """;

    public async Task<bool> SuperviseRunAsync(Guid runId, CancellationToken ct, bool forceProbe = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            logger.LogWarning("Supervisione saltata: run {RunId} non trovato.", runId);
            return false;
        }

        var already = await db.PipelineArtifacts
            .AnyAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory, ct);
        if (already)
        {
            return false; // idempotente
        }

        var userPrompt = await BuildUserPromptAsync(db, run, ct);

        var result = await guard.ExecuteAsync("advisory",
            token => llm.CompleteAsync(SystemPrompt, userPrompt, token), forceProbe: forceProbe, ct: ct);

        SupervisorAdvisory advisory;
        switch (result.Outcome)
        {
            case LlmCallOutcome.Ok:
                try
                {
                    advisory = ParseAdvisory(result.Text!);
                }
                catch (Exception ex)
                {
                    // Risposta arrivata ma non interpretabile: errore permanente, l'utente deve vederlo.
                    logger.LogError(ex, "Advisory AI non interpretabile per il run {RunId}.", runId);
                    advisory = ErrorAdvisory(ex.Message);
                }
                break;

            case LlmCallOutcome.SkippedNotConfigured:
            case LlmCallOutcome.SkippedBreakerOpen:
                logger.LogDebug("Supervisione rinviata per il run {RunId}: {Cause}.", runId, result.Cause);
                return false; // nessun artifact: il run resta pendente

            case LlmCallOutcome.FailedRetryable:
                logger.LogWarning("Supervisione rinviata per il run {RunId} (errore transitorio: {Cause}); verrà ritentata da sola.",
                    runId, result.Cause);
                return false; // nessun artifact: il run resta pendente

            default: // FailedPermanent
                logger.LogError(result.Error, "Supervisione AI fallita per il run {RunId}.", runId);
                advisory = ErrorAdvisory(result.Error?.Message ?? result.Cause);
                break;
        }

        advisory.ModelUsed = llm.Model;
        advisory.CreatedAtUtc = DateTime.UtcNow;

        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "LlmSupervisor",
            Kind = LlmArtifactKinds.Advisory,
            PayloadJson = JsonSerializer.Serialize(advisory),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        metrics?.RecordLlmAdvisory(advisory.IsError);
        logger.LogInformation("Advisory AI scritto per il run {RunId} (errore={IsError}).", runId, advisory.IsError);

        if (!advisory.IsError && advisory.DecisionsForUser.Count > 0 && options.CurrentValue.NotifyDecisions && notifier is not null)
        {
            await notifier.NotifyAsync(NotificationSeverity.Info, "Advisory AI: decisioni in attesa",
                $"Run {runId.ToString("N")[..8]}: {advisory.Summary}\n" +
                $"{advisory.DecisionsForUser.Count} decisioni da confermare in /admin/ai-supervisor.", ct);
        }

        return true;
    }

    public async Task<int> DeleteErrorAdvisoriesAsync(DateTime since, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Solo advisory di run ancora nella finestra del worker: cancellare i più vecchi non serve
        // (non verrebbero comunque ripresi) e distruggerebbe l'unico record di cos'è successo.
        var candidates = await db.PipelineArtifacts
            .Where(a => a.Kind == LlmArtifactKinds.Advisory)
            .Where(a => db.PipelineRuns.Any(r => r.Id == a.RunId && r.CompletedAt != null && r.CompletedAt >= since))
            .ToListAsync(ct);

        var toDelete = candidates.Where(a =>
        {
            try { return JsonSerializer.Deserialize<SupervisorAdvisory>(a.PayloadJson, JsonOpts)?.IsError == true; }
            catch { return false; }
        }).ToList();

        if (toDelete.Count == 0) return 0;

        db.PipelineArtifacts.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Eliminati {Count} advisory in errore: i run verranno rianalizzati dal worker.", toDelete.Count);
        return toDelete.Count;
    }

    private static SupervisorAdvisory ErrorAdvisory(string message) => new()
    {
        IsError = true,
        Summary = $"Supervisione AI non riuscita: {message}",
        Confidence = "bassa",
    };

    private const string RegimeProfileKind = "RegimeProfile";

    /// <summary>
    /// Prompt del run + contesto operativo compatto (corsie, quarantene, regime del run). Ogni
    /// sezione di contesto è DIFENSIVA: se una lettura fallisce, la sezione si salta e l'advisory
    /// si fa comunque — meglio un parere con meno contesto che nessun parere.
    /// </summary>
    private async Task<string> BuildUserPromptAsync(ApplicationDbContext db, PipelineRun run, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run: {run.Id}  Stato: {run.Status}  Trigger: {run.Trigger}");
        sb.AppendLine($"Conclusione del motore: {run.Conclusion}");

        try
        {
            var lanes = await db.TradingEngineStates.AsNoTracking().OrderBy(s => s.LaneId).ToListAsync(ct);
            if (lanes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CORSIE DI TRADING (stato attuale):");
                foreach (var l in lanes)
                {
                    sb.AppendLine($"  - Corsia {l.LaneId}: {l.Mode}{(l.IsRunning ? ", in esecuzione" : ", ferma")}, " +
                                  $"{(string.IsNullOrWhiteSpace(l.Symbol) ? "nessuna serie" : $"{l.Symbol} {l.Timeframe}")}, " +
                                  $"capitale {l.TotalCapital:F0}, PnL realizzato {l.RealizedPnl:F2}, max drawdown {l.MaxDrawdownPercent:F1}%");
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Sezione corsie saltata nel prompt advisory."); }

        try
        {
            var quarantines = await db.LaneQuarantines.AsNoTracking().OrderBy(q => q.LaneId).ToListAsync(ct);
            sb.AppendLine();
            if (quarantines.Count == 0)
            {
                sb.AppendLine("QUARANTENE: nessuna corsia in quarantena.");
            }
            else
            {
                sb.AppendLine("CORSIE IN QUARANTENA (bloccate finché un umano non verifica):");
                foreach (var q in quarantines)
                {
                    sb.AppendLine($"  - Corsia {q.LaneId} dal {q.CreatedAtUtc:yyyy-MM-dd}: {Truncate(q.Reason, 200)}");
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Sezione quarantene saltata nel prompt advisory."); }

        try
        {
            var regimeJson = await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.RunId == run.Id && a.Kind == RegimeProfileKind)
                .Select(a => a.PayloadJson)
                .FirstOrDefaultAsync(ct);
            if (regimeJson is not null &&
                JsonSerializer.Deserialize<RegimeOutput>(regimeJson, JsonOpts) is { } regime)
            {
                sb.AppendLine();
                sb.AppendLine($"REGIME DI MERCATO del run: {regime.CurrentRegimeLabel} (id {regime.CurrentRegimeId}), " +
                              $"silhouette {regime.SilhouetteScore:F2}, {regime.Profiles.Count} profili.");
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Sezione regime saltata nel prompt advisory."); }

        try
        {
            if (sentimentCache?.Current is { } mood)
            {
                sb.AppendLine();
                sb.AppendLine($"SENTIMENT DI MERCATO (mood della folla, lettura contrarian agli estremi): " +
                              $"composite {mood.CompositeScore:+0.00;-0.00}" +
                              (mood.FearGreedValue is null ? "" : $", Fear&Greed {mood.FearGreedValue:F0} ({mood.FearGreedLabel})"));
                foreach (var s in mood.Symbols)
                {
                    sb.AppendLine($"  - {s.Symbol}: mood {s.Composite:+0.00;-0.00}" +
                                  (s.FundingZ is null ? "" : $", funding z {s.FundingZ:+0.0;-0.0}") +
                                  (s.GlobalLongShortZ is null ? "" : $", long/short z {s.GlobalLongShortZ:+0.0;-0.0}"));
                }
                foreach (var extreme in mood.Extremes.Take(6))
                {
                    sb.AppendLine($"  ! {extreme}");
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Sezione sentiment saltata nel prompt advisory."); }

        sb.AppendLine();
        sb.AppendLine("PipelineRecommendation (JSON grezzo prodotto dal motore):");
        sb.AppendLine(Truncate(string.IsNullOrWhiteSpace(run.RecommendationJson) ? "{}" : run.RecommendationJson, 8000));
        return sb.ToString();
    }

    /// <summary>Il prompt deve restare bounded: il RecommendationJson può crescere coi run grossi.</summary>
    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "\n... [troncato]";

    /// <summary>Estrae e deserializza l'oggetto JSON dalla risposta del modello, con tolleranza a testo attorno.</summary>
    public static SupervisorAdvisory ParseAdvisory(string raw)
    {
        var json = ExtractJsonObject(raw);
        var dto = JsonSerializer.Deserialize<AdvisoryDto>(json, JsonOpts)
                  ?? throw new InvalidOperationException("Risposta LLM non deserializzabile.");
        return new SupervisorAdvisory
        {
            Summary = dto.Summary?.Trim() ?? string.Empty,
            Confidence = NormalizeConfidence(dto.Confidence),
            DecisionsForUser = dto.DecisionsForUser?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(),
            ParameterSuggestions = (dto.ParameterSuggestions ?? new()).Select(p => new ParameterSuggestion
            {
                Parameter = p.Parameter ?? string.Empty,
                CurrentOrObserved = p.CurrentOrObserved ?? string.Empty,
                Suggested = p.Suggested ?? string.Empty,
                Rationale = p.Rationale ?? string.Empty,
            }).ToList(),
        };
    }

    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Risposta LLM vuota.");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Nessun oggetto JSON nella risposta LLM.");
        return raw.Substring(start, end - start + 1);
    }

    private static string NormalizeConfidence(string? c)
    {
        var v = (c ?? "").Trim().ToLowerInvariant();
        return v is "bassa" or "media" or "alta" ? v : "media";
    }

    private sealed class AdvisoryDto
    {
        public string? Summary { get; set; }
        public string? Confidence { get; set; }
        public List<string>? DecisionsForUser { get; set; }
        public List<SuggestionDto>? ParameterSuggestions { get; set; }
    }

    private sealed class SuggestionDto
    {
        public string? Parameter { get; set; }
        public string? CurrentOrObserved { get; set; }
        public string? Suggested { get; set; }
        public string? Rationale { get; set; }
    }
}
