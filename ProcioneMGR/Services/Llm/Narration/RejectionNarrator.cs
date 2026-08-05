using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProcioneMGR.Services.Llm.Narration;

/// <summary>Una nota in prosa riferita a UN candidato bocciato, identificato dalla sua chiave.</summary>
public sealed record RejectionNote(string Key, string Text);

/// <summary>
/// [G6] La spiegazione in prosa delle bocciature di un run. Additiva per costruzione: il
/// <see cref="RunRejectionDigest"/> resta la fonte dei numeri, questa aggiunge solo parole.
/// </summary>
public sealed class RejectionNarration
{
    public string Summary { get; set; } = string.Empty;
    public List<RejectionNote> Notes { get; set; } = [];

    /// <summary>Il modello che ha DAVVERO risposto (col failover può non essere quello attivo).</summary>
    public string ModelUsed { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Quante note l'AI ha prodotto riferite a candidati INESISTENTI (scartate). Vedi <see cref="RejectionNarrator.Parse"/>.</summary>
    public int DiscardedNotes { get; set; }
}

public interface IRejectionNarrator
{
    /// <summary>
    /// Chiede al provider attivo di raccontare le bocciature del digest. <c>null</c> = nessuna
    /// narrazione (AI spenta, senza chiave, breaker aperto, budget esaurito, errore, o digest
    /// vuoto): il chiamante mostra comunque il digest deterministico.
    /// </summary>
    Task<RejectionNarration?> NarrateAsync(RunRejectionDigest digest, CancellationToken ct = default);
}

/// <summary>
/// [G6] Trasforma i numeri di un <see cref="RunRejectionDigest"/> in poche righe di italiano
/// piano.
///
/// <para><b>Perché è sicuro per costruzione</b>: i candidati di cui parla sono GIÀ stati respinti
/// dai gate e non sono mai stati schierati; non esiste percorso di codice per cui questo testo
/// torni a toccare una decisione. La sicurezza sta QUI — nell'assenza di un percorso di ritorno —
/// non nella pulizia dell'input.</para>
///
/// <para><b>Sull'input, per onestà</b>: il prompt è quasi tutto numeri calcolati dal motore, ma
/// non è testo "sterile": i nomi dei simboli vengono in ultima analisi dagli exchange, e
/// <c>RejectReason</c> può contenere il messaggio di un'eccezione
/// (<c>"Backtest fallito: {ex.Message}"</c>). Dare per scontato che nulla di ostile possa entrare
/// sarebbe una rassicurazione, non una garanzia.</para>
///
/// <para><b>La difesa vera è sull'OUTPUT</b>, e regge anche se l'input fosse avvelenato: le note
/// tornano indicizzate per CHIAVE e ogni chiave che non è fra quelle inviate viene scartata
/// (contata in <see cref="RejectionNarration.DiscardedNotes"/>, mai nascosta); il testo prodotto
/// non è mai eseguito né interpretato come istruzione, e finisce solo in una pagina accanto ai
/// numeri veri — così anche una prosa sbagliata si smaschera a occhio.</para>
/// </summary>
public sealed class RejectionNarrator(
    ILlmClient llm,
    ILlmCallGuard guard,
    ILogger<RejectionNarrator> logger) : IRejectionNarrator
{
    /// <summary>Etichetta del path per metriche, breaker e budget (AF1).</summary>
    public const string GuardPath = "explain";

    private const string SystemPrompt = """
        Sei un analista quantitativo che spiega a un collega, in ITALIANO piano e senza gergo
        inutile, perché dei candidati di una ricerca di strategie sono stati SCARTATI.

        I candidati di cui parli sono già stati respinti: il tuo testo è solo una spiegazione a
        posteriori, non una raccomandazione. Non proporre di riabilitarli, non suggerire di
        abbassare soglie, non inventare numeri che non ti sono stati dati.

        Rispondi SOLO con un oggetto JSON di questa forma, senza testo attorno:
        {"summary": "2-4 frasi sul quadro d'insieme delle bocciature",
         "notes": [{"key": "<la chiave esatta ricevuta>", "text": "1-2 frasi su questo candidato"}]}

        Regole:
        - usa esclusivamente le chiavi che ti sono state fornite, copiate esattamente;
        - riporta i numeri come te li ho dati, senza arrotondarli diversamente né inventarne altri;
        - per ogni candidato dì QUALE giudice l'ha respinto e di quanto è mancato;
        - niente saluti, niente preamboli, niente markdown.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<RejectionNarration?> NarrateAsync(RunRejectionDigest digest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (!digest.HasContent)
        {
            return null; // niente bocciati: non c'è nulla da raccontare, e una chiamata a vuoto si paga
        }

        var userPrompt = BuildPrompt(digest);
        var allowedKeys = digest.TopRejected.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var result = await guard.ExecuteAsync(GuardPath, token => llm.CompleteAsync(SystemPrompt, userPrompt, token), ct: ct);
        if (result.Outcome != LlmCallOutcome.Ok)
        {
            logger.LogDebug("Spiegazione delle bocciature non prodotta: {Cause}.", result.Cause);
            return null; // il digest deterministico resta, ed è la parte che conta
        }

        try
        {
            var narration = Parse(result.Text!, allowedKeys);
            narration.ModelUsed = llm is ILlmCompletionInfo { LastCompletionModel: { Length: > 0 } served } ? served : llm.Model;
            narration.CreatedAtUtc = DateTime.UtcNow;
            if (narration.DiscardedNotes > 0)
            {
                logger.LogWarning("Spiegazione bocciature: {Count} note scartate perché riferite a candidati non presenti nel run.",
                    narration.DiscardedNotes);
            }
            return narration;
        }
        catch (Exception ex)
        {
            // Risposta arrivata ma illeggibile: si perde la prosa, non il digest.
            logger.LogWarning(ex, "Spiegazione delle bocciature non interpretabile; resta il riassunto deterministico.");
            return null;
        }
    }

    /// <summary>
    /// Costruisce il prompt dai SOLI numeri del digest. Puro e deterministico: è la parte che i
    /// test possono ispezionare senza rete.
    /// </summary>
    public static string BuildPrompt(RunRejectionDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        sb.AppendLine(ci, $"Run di ricerca: {digest.Evaluated} candidati valutati, {digest.Survived} sopravvissuti, {digest.Rejected} scartati.");
        if (digest.GreyCount > 0)
        {
            sb.AppendLine(ci, $"Di quelli scartati, {digest.GreyCount} sono in \"fascia grigia\" (bocciati per finestra troppo corta, non nel merito).");
        }

        sb.AppendLine();
        sb.AppendLine("Bocciature per causa:");
        foreach (var g in digest.Groups)
        {
            sb.AppendLine(ci, $"- {g.Count} × {g.Label}");
        }

        sb.AppendLine();
        sb.AppendLine("I migliori fra gli scartati (ordinati per Sharpe holdout decrescente). Usa QUESTE chiavi esatte nelle note:");
        foreach (var c in digest.TopRejected)
        {
            sb.AppendLine(ci, $"- chiave: {c.Key}");
            sb.AppendLine(ci, $"  strategia {c.StrategyName} su {c.Symbol} {c.Timeframe}");
            sb.AppendLine(ci, $"  Sharpe holdout {c.HoldoutSharpe.ToString("F2", ci)}, {c.HoldoutTrades} trade, rendimento holdout {c.HoldoutReturn.ToString("F2", ci)}%");
            if (c.DeflatedSharpe is { } dsr) sb.AppendLine(ci, $"  Deflated Sharpe {dsr.ToString("F3", ci)}");
            if (c.PanelPbo is { } pbo) sb.AppendLine(ci, $"  PBO di pannello {pbo.ToString("F2", ci)}");
            if (c.NullTwinPercentile is { } pct) sb.AppendLine(ci, $"  percentile nel gemello sintetico {pct.ToString("F0", ci)}");
            sb.AppendLine(ci, $"  verdetto del motore: {c.RejectReason}");
            if (c.IsGrey) sb.AppendLine("  (fascia grigia: bocciato per finestra corta, con Sharpe holdout positivo)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Interpreta la risposta e SCARTA ogni nota la cui chiave non sia fra quelle inviate: un
    /// candidato inventato non deve poter comparire in pagina accanto a quelli veri. Pubblico per
    /// i test.
    /// </summary>
    public static RejectionNarration Parse(string raw, IReadOnlySet<string> allowedKeys)
    {
        ArgumentNullException.ThrowIfNull(allowedKeys);
        var json = ExtractJsonObject(raw ?? string.Empty);
        var dto = JsonSerializer.Deserialize<NarrationDto>(json, JsonOpts)
                  ?? throw new InvalidOperationException("Risposta JSON vuota.");

        var notes = new List<RejectionNote>();
        var discarded = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in dto.Notes ?? [])
        {
            var key = n.Key?.Trim();
            var text = n.Text?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text)) { discarded++; continue; }
            // Chiave inventata, o ripetuta due volte: fuori. Nessun tentativo di "avvicinarla" a una
            // vera — indovinare a quale candidato si riferisse è esattamente il modo di attribuire
            // una spiegazione sbagliata al candidato sbagliato.
            if (!allowedKeys.Contains(key) || !seen.Add(key)) { discarded++; continue; }
            notes.Add(new RejectionNote(key, text));
        }

        return new RejectionNarration
        {
            Summary = dto.Summary?.Trim() ?? string.Empty,
            Notes = notes,
            DiscardedNotes = discarded,
        };
    }

    /// <summary>Isola il primo oggetto JSON: i modelli amano avvolgerlo in fences markdown o preamboli.</summary>
    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Nessun oggetto JSON nella risposta.");
        }
        return raw[start..(end + 1)];
    }

    private sealed class NarrationDto
    {
        public string? Summary { get; set; }
        public List<NoteDto>? Notes { get; set; }
    }

    private sealed class NoteDto
    {
        public string? Key { get; set; }
        public string? Text { get; set; }
    }
}
