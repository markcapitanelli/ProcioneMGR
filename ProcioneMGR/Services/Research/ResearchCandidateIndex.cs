using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Research;

/// <summary>
/// [R2, PRD memoria-caccia 2026-08-14] UNA riga per candidato della caccia — l'indice a righe dei
/// blob <c>PipelineArtifacts</c> Kind="ValidatedCandidates". I dati esistono da luglio (6.554
/// candidati misurati il 2026-08-06) ma ogni domanda trasversale costava una scansione
/// <c>jsonb_array_elements</c> sull'intero archivio: questa tabella è DERIVATA e ricostruibile
/// dagli artifact in ogni momento (<see cref="IResearchCandidateIndexer.RebuildAsync"/>) — non è
/// una seconda verità, è una vista materializzata a mano. Nessun dato nuovo viene raccolto.
/// </summary>
public class ResearchCandidate
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>Denormalizzato da PipelineRuns.CompletedAt: il filtro temporale della pagina non deve fare join.</summary>
    public DateTime RunCompletedUtc { get; set; }

    public string StrategyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>La chiave identità di <see cref="PipelineCandidateKey"/> — mai ricostruirla inline.</summary>
    public string CandidateKey { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "{}";

    // Metriche di selezione (walk-forward)
    /// <summary>
    /// Sharpe della fase di selezione. <b>null = mai misurato</b>, e la pagina lo mostra come «—».
    /// Vedi <see cref="WalkForwardSource"/>: le provenienze NON sono sulla stessa scala.
    /// </summary>
    public decimal? WalkForwardOosSharpe { get; set; }

    /// <summary>Provenienza del numero qui sopra (costanti <c>DiscoveryCandidate.Source*</c> + le due storiche di bonifica).</summary>
    public string? WalkForwardSource { get; set; }
    public decimal SelectionSharpe { get; set; }
    public decimal SelectionReturn { get; set; }
    public decimal SelectionMaxDrawdown { get; set; }
    public int SelectionTrades { get; set; }

    // Verdetto holdout (mai usato per la selezione)
    public decimal HoldoutSharpe { get; set; }
    public decimal HoldoutReturn { get; set; }
    public decimal HoldoutMaxDrawdown { get; set; }
    public decimal HoldoutProfitFactor { get; set; }
    public int HoldoutTrades { get; set; }

    public bool Survived { get; set; }
    public string? RejectReason { get; set; }

    public double? DeflatedSharpe { get; set; }
    public double? PanelPbo { get; set; }
    public double? PermutationPValue { get; set; }
    public double? NullTwinPercentile { get; set; }

    public string BestStopVariant { get; set; } = "base";

    /// <summary>
    /// Cache del giudice unico <see cref="GreyZone.IsGrey"/>, calcolata all'indicizzazione:
    /// serve al filtro SQL della pagina. La tabella è derivata, quindi il valore si riallinea a
    /// ogni rebuild — mai una seconda definizione di "grigio".
    /// </summary>
    public bool IsGrey { get; set; }
}

public sealed record ResearchIndexResult(int RunsIndexed, int CandidatesIndexed, int RunsSkipped);

/// <summary>
/// Costruisce e mantiene l'indice: incrementale (solo i run non ancora indicizzati) su richiesta
/// della pagina, ricostruzione totale col bottone dedicato. Difensivo per run, come il lettore di
/// flotta: un payload illeggibile esclude QUEL run con un log, mai l'intera indicizzazione.
/// </summary>
public interface IResearchCandidateIndexer
{
    Task<ResearchIndexResult> IndexNewRunsAsync(CancellationToken ct = default);
    Task<ResearchIndexResult> RebuildAsync(CancellationToken ct = default);
}

public sealed class ResearchCandidateIndexer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ResearchCandidateIndexer> logger) : IResearchCandidateIndexer
{
    /// <summary>Stesso KindTag scritto da PipelineEngine.SaveArtifacts e letto da FleetStateReader.</summary>
    internal const string ArtifactKind = "ValidatedCandidates";

    /// <summary>
    /// [Review 2026-08-14] Serializza le chiamate DENTRO il processo: due circuiti Blazor che
    /// aprono /research insieme non devono correre sullo stesso read-then-insert. Fra processi
    /// diversi (guscio + pod ui) la gara resta possibile: lì decide l'indice unico, e il
    /// perdente tratta il run come "già indicizzato da un altro" (vedi catch in IndexAsync).
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ResearchIndexResult> IndexNewRunsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var alreadyIndexed = await db.ResearchCandidates.AsNoTracking()
                .Select(r => r.RunId).Distinct().ToListAsync(ct);
            return await IndexAsync(db, alreadyIndexed, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResearchIndexResult> RebuildAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // Tabella derivata: cancellare e rifare È il contratto di coerenza col giudice corrente
            // (se la definizione di grigio cambia, il rebuild riallinea il flag su tutto lo storico).
            await db.ResearchCandidates.ExecuteDeleteAsync(ct);
            return await IndexAsync(db, [], ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// [Review 2026-08-14] Tronca ai limiti di colonna PRIMA dell'insert: il RejectReason può
    /// contenere un messaggio d'eccezione ILLIMITATO ("Backtest fallito: {ex.Message}",
    /// ModelStages) contro varchar(256) — senza troncamento un solo candidato storico fuori
    /// misura farebbe fallire l'intero giro di indicizzazione e svuoterebbe la pagina.
    /// </summary>
    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    // --------------------------------------------------- bonifica dello storico [2026-08-22]
    //
    // STA QUI, e non in una UPDATE una-tantum, perché la tabella è DERIVATA: RebuildAsync la
    // cancella e la rifà dagli artifact, e gli artifact contengono lo stesso numero falso. Una
    // UPDATE verrebbe annullata al primo click sul bottone di ricostruzione, e nessuno se ne
    // accorgerebbe.
    //
    // L'impronta è verificata sull'INTERO archivio, senza eccezioni in nessuna delle due direzioni:
    // famiglia della scoperta creativa + due decimali esatti + uguale a round(SelectionSharpe, 2)
    // seleziona 9.665 righe su 9.665 prodotte da CreativeDiscovery e ZERO delle 3.833 della
    // discovery classica. Nessuna informazione si perde: quel valore È round(SelectionSharpe, 2), e
    // SelectionSharpe resta nella stessa riga a piena precisione.

    /// <summary>Le tre meta-strategie della scoperta creativa, cioè del percorso che copiava.</summary>
    private static readonly HashSet<string> FamiglieComposer =
        new(StringComparer.Ordinal) { "Composite", "EventTrigger", "RegimeConditional" };

    /// <summary>Storico: il numero era una copia arrotondata dello Sharpe di selezione.</summary>
    private const string SorgenteCopiaPreFix = "CopiaSelezionePreFix";

    /// <summary>Storico: c'è un numero, ma da quale percorso venga non è più ricostruibile.</summary>
    private const string SorgenteIgnota = "SconosciutaPreFix";

    private static bool CopiaDaSelezione(ValidatedCandidate v)
        => v.WalkForwardSource is null
        && v.WalkForwardOosSharpe is decimal wf
        && FamiglieComposer.Contains(v.StrategyName)
        && wf == decimal.Round(wf, 2)
        && wf == decimal.Round(v.SelectionSharpe, 2);

    /// <summary>Candidato "Ml" storico: nessuna discovery lo ha mai misurato, lo 0 è il default del POCO.</summary>
    private static bool ZeroInventato(ValidatedCandidate v)
        => v.WalkForwardSource is null && v.StrategyName == "Ml" && v.WalkForwardOosSharpe == 0m;

    internal static decimal? WalkForwardBonificato(ValidatedCandidate v)
        => CopiaDaSelezione(v) || ZeroInventato(v) ? null : v.WalkForwardOosSharpe;

    internal static string? SorgenteStorica(ValidatedCandidate v)
        => CopiaDaSelezione(v) ? SorgenteCopiaPreFix
         : ZeroInventato(v) ? Discovery.DiscoveryCandidate.SourceNone
         : v.WalkForwardOosSharpe is null ? null
         : SorgenteIgnota;

    /// <summary>
    /// [2026-08-22] Come si legge la provenienza dello Sharpe di selezione. Un trattino senza
    /// spiegazione sarebbe metà del difetto che stiamo correggendo.
    /// </summary>
    public static string SpiegaSorgenteWalkForward(string? source) => source switch
    {
        Discovery.DiscoveryCandidate.SourceWalkForward =>
            "Walk-forward vero: parametri scelti sull'in-sample, giudicati sull'out-of-sample che segue. "
            + "È il MASSIMO su centinaia di combinazioni provate, quindi ottimistico per selection bias.",
        Discovery.DiscoveryCandidate.SourceSelectionSubPeriods =>
            "MEDIA su sottoperiodi contigui dentro il range di selezione, parametri congelati. "
            + "È una misura di coerenza, non un fuori campione — e non è sulla stessa scala del walk-forward vero.",
        Discovery.DiscoveryCandidate.SourceNone =>
            "Mai misurato: questo candidato non viene da una discovery (modello ML). Il vuoto è la verità.",
        Discovery.DiscoveryCandidate.SourceUndeclared =>
            "Il produttore non ha dichiarato la provenienza: il numero è stato scartato per non farlo passare per una misura.",
        SorgenteCopiaPreFix =>
            "Bonificato: era una COPIA arrotondata dello Sharpe di selezione, non un walk-forward. La scoperta "
            + "creativa rieseguiva N volte lo stesso backtest sul range intero (corretto il 2026-08-22).",
        SorgenteIgnota =>
            "Numero precedente alla tracciatura della provenienza (2026-08-22): c'è, ma da quale percorso venga non è ricostruibile.",
        _ => "Provenienza non registrata.",
    };

    // Internal e non private: il test della gara fra processi deve poter passare una lista
    // "stantia" di run già indicizzati — dall'API pubblica la finestra non è riproducibile.
    internal async Task<ResearchIndexResult> IndexAsync(ApplicationDbContext db, List<Guid> alreadyIndexed, CancellationToken ct)
    {
        // Gli artifact esistono solo per run "Completed" (PipelineEngine.SaveArtifacts), quindi il
        // join filtra da solo; CompletedAt si denormalizza qui, una volta. Il fallback per un
        // CompletedAt assente è il CreatedAt dell'artifact (stessa transazione di fine run):
        // STABILE fra rebuild — DateTime.UtcNow fabbricherebbe una recency diversa a ogni giro.
        var sources = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.Kind == ArtifactKind && !alreadyIndexed.Contains(a.RunId))
            .Join(db.PipelineRuns.AsNoTracking(),
                a => a.RunId, r => r.Id,
                (a, r) => new { a.RunId, a.PayloadJson, r.CompletedAt, a.CreatedAt })
            .ToListAsync(ct);

        var runsIndexed = 0;
        var candidatesIndexed = 0;
        var runsSkipped = 0;
        // [2026-08-22] Quante righe la bonifica ha svuotato. Si conta per DIRLO: se i due
        // arrotondamenti (decimal.Round e' banker's, la verifica in SQL era half-up) selezionassero
        // insiemi diversi, senza questo numero la divergenza resterebbe implicita.
        var bonificate = 0;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            List<ValidatedCandidate> validated;
            try
            {
                validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(source.PayloadJson) ?? [];
            }
            catch (JsonException ex)
            {
                runsSkipped++;
                logger.LogWarning(ex, "Run {Run} escluso dall'indice candidati (payload illeggibile).", source.RunId);
                continue;
            }

            // Dentro un run le chiavi sono uniche per costruzione (fingerprint dei parametri);
            // il GroupBy è la cintura per i payload storici precedenti a quel fix — un duplicato
            // violerebbe l'indice unico e farebbe fallire l'intero giro.
            var rows = validated
                .GroupBy(v => v.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(v => new ResearchCandidate
                {
                    RunId = source.RunId,
                    RunCompletedUtc = source.CompletedAt ?? source.CreatedAt,
                    StrategyName = Truncate(v.StrategyName, 64)!,
                    Symbol = Truncate(v.Symbol, 32)!,
                    Timeframe = Truncate(v.Timeframe, 8)!,
                    CandidateKey = Truncate(v.Key, 160)!,
                    ParametersJson = JsonSerializer.Serialize(v.Parameters),
                    WalkForwardOosSharpe = WalkForwardBonificato(v),
                    WalkForwardSource = Truncate(v.WalkForwardSource ?? SorgenteStorica(v), 32),
                    SelectionSharpe = v.SelectionSharpe,
                    SelectionReturn = v.SelectionReturn,
                    SelectionMaxDrawdown = v.SelectionMaxDrawdown,
                    SelectionTrades = v.SelectionTrades,
                    HoldoutSharpe = v.HoldoutSharpe,
                    HoldoutReturn = v.HoldoutReturn,
                    HoldoutMaxDrawdown = v.HoldoutMaxDrawdown,
                    HoldoutProfitFactor = v.HoldoutProfitFactor,
                    HoldoutTrades = v.HoldoutTrades,
                    Survived = v.Survived,
                    RejectReason = Truncate(v.RejectReason, 256),
                    DeflatedSharpe = v.DeflatedSharpe,
                    PanelPbo = v.PanelPbo,
                    PermutationPValue = v.PermutationPValue,
                    NullTwinPercentile = v.NullTwinPercentile,
                    BestStopVariant = Truncate(v.BestStopVariant, 32)!,
                    IsGrey = GreyZone.IsGrey(v),
                })
                .ToList();
            if (rows.Count < validated.Count)
            {
                logger.LogWarning("Run {Run}: {Dupes} candidati con chiave duplicata nel payload storico — tenuto il primo di ciascuna.",
                    source.RunId, validated.Count - rows.Count);
            }

            db.ResearchCandidates.AddRange(rows);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // [Review 2026-08-14] "Difensivo per run" vale anche qui: la violazione tipica è
                // l'indice unico quando un ALTRO processo (guscio + pod ui sullo stesso Postgres)
                // ha già indicizzato questo run nella finestra fra la nostra lettura e questo
                // insert. Il run è a posto — lo ha scritto qualcun altro — quindi si sgancia
                // tutto dal change tracker (altrimenti il prossimo SaveChanges rifallirebbe con
                // le stesse righe) e si prosegue col run successivo.
                db.ChangeTracker.Clear();
                runsSkipped++;
                logger.LogWarning(ex, "Run {Run} saltato dall'indice candidati (probabile indicizzazione concorrente da un altro processo).",
                    source.RunId);
                continue;
            }
            runsIndexed++;
            candidatesIndexed += rows.Count;
            bonificate += rows.Count(r => r.WalkForwardOosSharpe is null && r.WalkForwardSource is not null);
        }

        if (runsIndexed > 0)
        {
            logger.LogInformation("Indice candidati: {Runs} run, {Candidates} candidati aggiunti ({Skipped} run illeggibili).",
                runsIndexed, candidatesIndexed, runsSkipped);
        }
        if (bonificate > 0)
        {
            logger.LogInformation(
                "Indice candidati: {N} righe bonificate — il loro «Sharpe di selezione» era una copia arrotondata "
                + "dello Sharpe di selezione (o lo 0 di default dei modelli Ml). Ora e' vuoto e dichiarato.",
                bonificate);
        }
        return new ResearchIndexResult(runsIndexed, candidatesIndexed, runsSkipped);
    }
}
