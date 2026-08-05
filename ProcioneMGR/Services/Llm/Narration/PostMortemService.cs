using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Llm.Narration;

/// <summary>[G4] Opzioni del post-mortem, sezione <c>PostMortem</c>. Default SPENTO.</summary>
public sealed class PostMortemOptions
{
    /// <summary>Accende la scrittura dei post-mortem. Spento: nessuna riga, nessuna chiamata AI.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Perdita percentuale oltre la quale un trade merita un post-mortem (valore POSITIVO: 1.0 =
    /// perdite oltre l'1%). Sotto soglia si tace: non ogni perdita è una lezione.
    ///
    /// <para><b>Interazione da conoscere</b>: la causa deterministica «costi che hanno mangiato il
    /// lordo» può scattare solo quando la perdita netta è più piccola del costo di andata e
    /// ritorno (~0,2% con fee 0,1%). Con questa soglia a 1,0% quei trade non vengono nemmeno
    /// esaminati, quindi quella causa resta di fatto dormiente: per vederla all'opera serve
    /// abbassare la soglia sotto il costo di round-turn. Non è un difetto — è il prezzo di non
    /// analizzare ogni singola perdita minuscola — ma è meglio saperlo che scoprirlo.</para>
    /// </summary>
    public decimal LossThresholdPercent { get; set; } = 1.0m;

    /// <summary>Chiede anche la prosa e la classificazione all'AI. Spento = solo le cause calcolabili dal codice.</summary>
    public bool UseAi { get; set; }

    /// <summary>Quanti post-mortem al massimo per giro, per non trasformare un arretrato in una bolletta.</summary>
    public int MaxPerRun { get; set; } = 5;

    /// <summary>Quanti post-mortem recenti passare al comitato come contesto (0 = non passarne).</summary>
    public int CommitteeContextCount { get; set; } = 5;
}

public interface IPostMortemService
{
    /// <summary>
    /// Analizza i trade in perdita non ancora analizzati. Restituisce quanti post-mortem ha
    /// scritto. Idempotente per trade (indice unico su <c>TradeRecordId</c>).
    /// </summary>
    Task<int> AnalyzeRecentAsync(CancellationToken ct = default);

    /// <summary>
    /// Il contesto per il comitato su una corsia: il conteggio delle cause recenti, in una riga.
    /// Stringa VUOTA se non c'è nulla — mai una frase che finge di sapere.
    /// </summary>
    Task<string> BuildCommitteeContextAsync(int laneId, CancellationToken ct = default);

    /// <summary>Gli ultimi post-mortem, per la pagina.</summary>
    Task<IReadOnlyList<TradePostMortem>> GetRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// [G4] Scrive il post-mortem delle operazioni chiuse in perdita.
///
/// <para><b>L'ordine dei fattori è il punto</b>: prima i fatti (da <c>TradeRecord</c>), poi la
/// causa che il CODICE sa calcolare da solo; solo se resta un dubbio si interpella l'AI, e anche
/// allora sceglie dentro un menù chiuso. Se l'AI non c'è, non risponde o esce dal menù, la causa è
/// <c>Inconcludente</c> — un default deterministico, mai un'invenzione.</para>
///
/// <para><b>Confine</b>: questo servizio scrive righe di testo e una classificazione. Non ha fra le
/// dipendenze nulla che possa aprire, chiudere o dimensionare una posizione.</para>
/// </summary>
public sealed class PostMortemService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<PostMortemOptions> options,
    IOptionsMonitor<SafetyConfiguration> safety,
    ILogger<PostMortemService> logger,
    ILlmClient? llm = null,
    ILlmCallGuard? guard = null) : IPostMortemService
{
    /// <summary>Etichetta del path per metriche, breaker e budget (AF1).</summary>
    public const string GuardPath = "postmortem";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt = """
        Sei un analista quantitativo. Ti do i fatti di UN'operazione di trading chiusa in perdita.
        Devi dire, in ITALIANO, qual è la causa più probabile SCEGLIENDOLA da un elenco chiuso, e
        aggiungere due righe di spiegazione.

        Rispondi SOLO con questo JSON, senza testo attorno:
        {"cause": "<una voce esatta dell'elenco>", "text": "1-2 frasi"}

        Regole:
        - la causa deve essere copiata ESATTAMENTE da quelle ammesse: qualunque altra cosa è nulla;
        - se i fatti non bastano a distinguere, usa "Inconcludente": è una risposta legittima;
        - non proporre modifiche a parametri, soglie o strategie: qui si spiega, non si decide;
        - usa solo i numeri che ti do.
        """;

    public async Task<int> AnalyzeRecentAsync(CancellationToken ct = default)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var threshold = -Math.Abs(opt.LossThresholdPercent);
        var limit = Math.Clamp(opt.MaxPerRun, 1, 50);

        // I trade in perdita oltre soglia che non hanno ancora un post-mortem (anti-join).
        var pending = await db.TradeRecords.AsNoTracking()
            .Where(t => t.PnlPercent <= threshold)
            .Where(t => !db.Set<TradePostMortem>().Any(p => p.TradeRecordId == t.Id))
            .OrderByDescending(t => t.ClosedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var feePercent = safety.CurrentValue.FeePercent;
        var written = 0;

        foreach (var trade in pending)
        {
            ct.ThrowIfCancellationRequested();

            var facts = PostMortemAnalyzer.Extract(trade, feePercent);
            var cause = PostMortemAnalyzer.DeterministicCause(facts);
            var source = "rules";
            var narrative = string.Empty;
            var model = string.Empty;

            // L'AI si interpella SOLO se il codice non sa già rispondere.
            if (cause is null)
            {
                var (aiCause, aiText, aiModel) = await AskAiAsync(facts, opt, ct);
                cause = aiCause ?? PostMortemCauses.Inconclusive;
                source = aiCause is null ? "default" : "ai";
                narrative = aiText;
                model = aiModel;
            }

            db.Add(new TradePostMortem
            {
                CreatedAtUtc = DateTime.UtcNow,
                LaneId = trade.LaneId,
                TradeRecordId = trade.Id,
                Symbol = trade.Symbol,
                StrategyId = trade.StrategyId,
                PnlPercent = trade.PnlPercent,
                Cause = cause,
                Source = source,
                FactsJson = JsonSerializer.Serialize(facts),
                Narrative = narrative,
                ModelUsed = model,
            });
            written++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Post-mortem scritti: {Count}.", written);
        return written;
    }

    private async Task<(string? Cause, string Text, string Model)> AskAiAsync(
        TradeFacts facts, PostMortemOptions opt, CancellationToken ct)
    {
        if (!opt.UseAi || llm is null || guard is null) return (null, string.Empty, string.Empty);

        var result = await guard.ExecuteAsync(GuardPath,
            token => llm.CompleteAsync(SystemPrompt, BuildPrompt(facts), token), ct: ct);
        if (result.Outcome != LlmCallOutcome.Ok)
        {
            logger.LogDebug("Post-mortem senza parere AI ({Cause}): resta la causa deterministica.", result.Cause);
            return (null, string.Empty, string.Empty);
        }

        try
        {
            var (cause, text) = ParseVerdict(result.Text!);
            var model = llm is ILlmCompletionInfo { LastCompletionModel: { Length: > 0 } served } ? served : llm.Model;
            return (cause, text, model);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Verdetto del post-mortem illeggibile: causa Inconcludente.");
            return (null, string.Empty, string.Empty);
        }
    }

    /// <summary>Il prompt: solo fatti, e il menù delle risposte ammesse. Puro, ispezionabile dai test.</summary>
    public static string BuildPrompt(TradeFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ci, $"Operazione su {f.Symbol}, lato {f.Side}, corsia {f.LaneId}, modalità {f.Mode}.");
        sb.AppendLine(ci, $"Ingresso {f.EntryPrice.ToString("G", ci)}, uscita {f.ExitPrice.ToString("G", ci)}.");
        sb.AppendLine(ci, $"Risultato netto {f.PnlPercent.ToString("F2", ci)}%, lordo stimato {f.GrossPnlPercent.ToString("F2", ci)}% (costi andata e ritorno {f.FeePercentEstimate.ToString("F2", ci)}%).");
        sb.AppendLine(ci, $"Durata {f.Duration.TotalHours.ToString("F1", ci)} ore. Motivo di uscita: {f.ExitReason}.");
        if (f.WasLiquidated) sb.AppendLine("Chiusura per liquidazione forzata.");
        sb.AppendLine();
        sb.AppendLine("Cause ammesse (copiane UNA esattamente):");
        foreach (var c in PostMortemCauses.AiSelectable)
        {
            sb.AppendLine(ci, $"- {c} = {PostMortemCauses.Label(c)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Interpreta il verdetto e RIVALIDA la causa contro il menù: una voce inventata vale come
    /// nessuna risposta (stessa disciplina del comitato AF3). Pubblico per i test.
    /// </summary>
    public static (string? Cause, string Text) ParseVerdict(string raw)
    {
        var start = (raw ?? string.Empty).IndexOf('{');
        var end = (raw ?? string.Empty).LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Nessun oggetto JSON nella risposta.");

        var dto = JsonSerializer.Deserialize<VerdictDto>(raw![start..(end + 1)], JsonOpts)
                  ?? throw new InvalidOperationException("Risposta JSON vuota.");

        var cause = dto.Cause?.Trim();
        // Fuori menù ⇒ nulla. Nessun tentativo di "avvicinarla" a una voce vera: indovinare qui
        // significa attribuire una causa che nessuno ha scelto.
        if (!PostMortemCauses.AiSelectable.Contains(cause, StringComparer.Ordinal)) return (null, string.Empty);

        return (cause, dto.Text?.Trim() ?? string.Empty);
    }

    public async Task<string> BuildCommitteeContextAsync(int laneId, CancellationToken ct = default)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled || opt.CommitteeContextCount <= 0) return string.Empty;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.Set<TradePostMortem>().AsNoTracking()
            .Where(p => p.LaneId == laneId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(Math.Clamp(opt.CommitteeContextCount, 1, 50))
            .Select(p => p.Cause)
            .ToListAsync(ct);

        return Summarize(recent);
    }

    /// <summary>
    /// Il conteggio delle cause in una riga. Puro e testabile: è il TESTO che finisce nel prompt
    /// del comitato, e va guardato senza database.
    /// </summary>
    public static string Summarize(IReadOnlyList<string> causes)
    {
        ArgumentNullException.ThrowIfNull(causes);
        if (causes.Count == 0) return string.Empty;

        var groups = causes
            .GroupBy(c => c, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()}× {PostMortemCauses.Label(g.Key)}");

        return $"Analisi delle ultime {causes.Count} operazioni in perdita di questa corsia: {string.Join(", ", groups)}.";
    }

    public async Task<IReadOnlyList<TradePostMortem>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<TradePostMortem>().AsNoTracking()
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
    }

    private sealed class VerdictDto
    {
        public string? Cause { get; set; }
        public string? Text { get; set; }
    }
}
