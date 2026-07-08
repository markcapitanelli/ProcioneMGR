using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
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
    /// run (non riscrive se un advisory esiste già). Restituisce false se saltato.
    /// </summary>
    Task<bool> SuperviseRunAsync(Guid runId, CancellationToken ct);
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
    ILogger<PipelineSupervisor> logger) : IPipelineSupervisor
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt = """
        Sei un supervisore AI del ciclo di ricerca di una piattaforma di trading algoritmico crypto.
        Ricevi il riepilogo (PipelineRecommendation) di UN run di ricerca già concluso: regime di
        mercato, volatilità, sentiment, quanti candidati sono stati valutati e quanti sono
        sopravvissuti alla validazione, il migliore, gli alert e le azioni suggerite dal motore, e i
        limiti di rischio proposti.

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

    public async Task<bool> SuperviseRunAsync(Guid runId, CancellationToken ct)
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

        var userPrompt = BuildUserPrompt(run);

        SupervisorAdvisory advisory;
        try
        {
            var raw = await llm.CompleteAsync(SystemPrompt, userPrompt, ct);
            advisory = ParseAdvisory(raw);
        }
        catch (Exception ex)
        {
            // Persistiamo comunque un advisory "di errore" così il run non viene riprocessato
            // all'infinito (un tentativo per run) e l'utente vede cos'è andato storto.
            logger.LogError(ex, "Supervisione AI fallita per il run {RunId}.", runId);
            advisory = new SupervisorAdvisory
            {
                IsError = true,
                Summary = $"Supervisione AI non riuscita: {ex.Message}",
                Confidence = "bassa",
            };
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

        logger.LogInformation("Advisory AI scritto per il run {RunId} (errore={IsError}).", runId, advisory.IsError);
        return true;
    }

    private static string BuildUserPrompt(PipelineRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run: {run.Id}  Stato: {run.Status}  Trigger: {run.Trigger}");
        sb.AppendLine($"Conclusione del motore: {run.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("PipelineRecommendation (JSON grezzo prodotto dal motore):");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.RecommendationJson) ? "{}" : run.RecommendationJson);
        return sb.ToString();
    }

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
