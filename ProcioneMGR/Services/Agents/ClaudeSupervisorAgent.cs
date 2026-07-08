using System.Text;
using System.Text.Json;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Agents;

/// <summary>
/// Optional Claude-backed supervisor. It reuses the existing <see cref="ILlmClient"/> (Anthropic SDK,
/// key from <c>ANTHROPIC_API_KEY</c> only) to produce a qualitative judgment on a proposed ensemble
/// swap. It has a hard timeout and degrades gracefully: if the key is missing, the call times out, or
/// anything throws/parses wrong, it returns <c>ApproveReplacement = true</c> (defer to the metrics) so
/// an AI problem never blocks a metrically-justified replacement. It can only VETO, never force a swap,
/// never trade, never touch SafetyChecker (no execution service is injected).
/// </summary>
public sealed class ClaudeSupervisorAgent(
    ILlmClient llm,
    SupervisorAgentOptions options,
    ILogger<ClaudeSupervisorAgent> logger) : IPipelineSupervisorAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string Provider => "Claude";

    private const string SystemPrompt = """
        Sei un supervisore AI del ciclo di ricerca di una piattaforma di trading algoritmico crypto.
        Un run di ricerca è appena terminato e ha prodotto un ENSEMBLE CANDIDATO. La piattaforma ha
        già deciso, con metriche oggettive, se sostituire l'ensemble CORRENTE con il candidato. Il tuo
        compito è dare un parere QUALITATIVO e, se vedi un rischio serio che le metriche non colgono,
        porre un VETO alla sostituzione.

        Regole del tuo verdetto (approveReplacement):
        - true  = non ho obiezioni, la sostituzione può procedere se le metriche la approvano.
        - false = VETO: sconsiglio la sostituzione anche se le metriche la approvano (spiega perché).
        Puoi solo porre un veto a una sostituzione; NON puoi forzare una sostituzione, NON puoi avviare
        trading, NON puoi passare in Live, NON puoi toccare i controlli di sicurezza. Il trading reale
        resta sempre dietro conferma manuale e SafetyChecker.

        Rispondi ESCLUSIVAMENTE con un oggetto JSON valido (nessun testo prima o dopo):
        {
          "approveReplacement": true|false,
          "summary": "string (italiano, 2-4 frasi, leggibile per l'utente)",
          "suggestions": ["string", "..."],
          "concerns": ["string", "..."],
          "reasoning": "string (ragionamento sintetico)"
        }
        """;

    public async Task<SupervisorJudgment> AnalyzeRunAsync(
        PipelineRun run,
        EnsembleSummary? currentEnsemble,
        EnsembleSummary? candidateEnsemble,
        CancellationToken ct = default)
    {
        if (!llm.IsConfigured)
        {
            logger.LogInformation("Supervisore (Claude): ANTHROPIC_API_KEY assente — approvo di default (decisione alle metriche).");
            return Fallback("Supervisore Claude selezionato ma ANTHROPIC_API_KEY non impostata: decisione basata solo su metriche.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var userPrompt = BuildUserPrompt(run, currentEnsemble, candidateEnsemble);
            var raw = await llm.CompleteAsync(SystemPrompt, userPrompt, linked.Token);
            var judgment = Parse(raw);
            judgment.Provider = Provider;
            judgment.AnalyzedAt = DateTime.UtcNow;
            logger.LogInformation("Supervisore (Claude): run {RunId} → approveReplacement={Approve}.", run.Id, judgment.ApproveReplacement);
            return judgment;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("Supervisore (Claude): timeout dopo {S}s per il run {RunId} — approvo di default (decisione alle metriche).", options.TimeoutSeconds, run.Id);
            return Fallback($"Supervisore AI in timeout ({options.TimeoutSeconds}s): decisione basata solo su metriche.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Supervisore (Claude): errore per il run {RunId} — approvo di default (decisione alle metriche).", run.Id);
            return Fallback($"Supervisore AI non riuscito ({ex.Message}): decisione basata solo su metriche.");
        }
    }

    private SupervisorJudgment Fallback(string summary) => new()
    {
        ApproveReplacement = true, // safety: an AI problem must never block a metrically-justified swap
        Provider = Provider,
        Summary = summary,
        AnalyzedAt = DateTime.UtcNow,
    };

    private static string BuildUserPrompt(PipelineRun run, EnsembleSummary? current, EnsembleSummary? candidate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run: {run.Id}  Stato: {run.Status}  Trigger: {run.Trigger}");
        sb.AppendLine($"Conclusione del motore: {run.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("ENSEMBLE CORRENTE (già schierato sulle corsie):");
        sb.AppendLine(Describe(current));
        sb.AppendLine();
        sb.AppendLine("ENSEMBLE CANDIDATO (proposto da questo run):");
        sb.AppendLine(Describe(candidate));
        sb.AppendLine();
        sb.AppendLine("PipelineRecommendation (JSON grezzo del motore):");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.RecommendationJson) ? "{}" : run.RecommendationJson);
        return sb.ToString();
    }

    private static string Describe(EnsembleSummary? s)
    {
        if (s is null || s.IsEmpty) return "  (nessuno)";
        var sb = new StringBuilder();
        sb.AppendLine($"  Sharpe medio pesato: {s.WeightedAverageSharpe:F2}; RF95 medio: {s.WeightedAverageRiskFactor95:F2}; gambe: {s.SurvivingLegs}; simboli distinti: {s.DistinctSymbols}");
        foreach (var l in s.Legs)
        {
            sb.AppendLine($"    - {l.StrategyName} {l.Symbol} {l.Timeframe}: peso {l.WeightPercent:F1}%, Sharpe {l.Sharpe:F2}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Parses the model's JSON judgment, tolerant of surrounding text. Public for unit testing.</summary>
    public static SupervisorJudgment Parse(string raw)
    {
        var json = ExtractJsonObject(raw);
        var dto = JsonSerializer.Deserialize<JudgmentDto>(json, JsonOpts)
                  ?? throw new InvalidOperationException("Risposta LLM non deserializzabile.");
        return new SupervisorJudgment
        {
            // Default to true when the model omits the field: absence of an explicit veto = no veto.
            ApproveReplacement = dto.ApproveReplacement ?? true,
            Summary = dto.Summary?.Trim() ?? string.Empty,
            Suggestions = dto.Suggestions?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new(),
            Concerns = dto.Concerns?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new(),
            Reasoning = dto.Reasoning?.Trim() ?? string.Empty,
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

    private sealed class JudgmentDto
    {
        public bool? ApproveReplacement { get; set; }
        public string? Summary { get; set; }
        public List<string>? Suggestions { get; set; }
        public List<string>? Concerns { get; set; }
        public string? Reasoning { get; set; }
    }
}
