using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// [I14] UNA riga per coppia esaminata dallo screening — l'indice a righe dei blob
/// <c>PipelineArtifacts</c> Kind="PairScreen".
///
/// <para><b>Perché esiste.</b> Ogni run della pipeline con lo stage «Screening coppie» scrive un
/// artefatto con tutte le coppie testate: al 2026-08-06 erano 86 artefatti, e <b>nessuno li aveva
/// mai riletti</b> — non esisteva una sola query nel repo che filtrasse per quel Kind. Un test di
/// cointegrazione costa, viene rifatto a ogni run, e il suo risultato finiva in un blob che nessuna
/// superficie apriva.</para>
///
/// <para>Come <see cref="Research.ResearchCandidate"/>: tabella <b>derivata</b> e ricostruibile in
/// ogni momento dagli artefatti — non è una seconda verità, è una vista materializzata a mano.
/// Nessun dato nuovo viene raccolto, nessun calcolo nuovo viene fatto.</para>
/// </summary>
public class PairCandidate
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>Denormalizzato da PipelineRuns.CompletedAt: il filtro temporale del pannello non deve fare join.</summary>
    public DateTime RunCompletedUtc { get; set; }

    public string SymbolY { get; set; } = string.Empty;
    public string SymbolX { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>L'identità della coppia secondo <see cref="PairKey.Build"/> — mai ricostruirla inline.</summary>
    public string PairKeyValue { get; set; } = string.Empty;

    /// <summary>Statistica ADF sui residui della regressione di cointegrazione (più negativa = più stazionaria).</summary>
    public double AdfStatistic { get; set; }

    /// <summary>Verdetto di cointegrazione al 5% (ADF sotto il valore critico MacKinnon).</summary>
    public bool IsCointegrated { get; set; }

    /// <summary>
    /// Beta della regressione sui LOG dei prezzi: un'elasticità, adimensionale. <b>Non è il beta del
    /// backtest</b>, che è walk-forward e per default stimato col filtro di Kalman: questo è
    /// full-sample e serve solo a giudicare la plausibilità economica della relazione.
    /// </summary>
    public double HedgeRatio { get; set; }

    /// <summary>Il beta sta nella banda di sanità economica [0,5 – 2,0].</summary>
    public bool IsHedgeRatioPlausible { get; set; }

    public int AlignedCandles { get; set; }

    /// <summary>
    /// Cache del verdetto composto, calcolata <b>all'indicizzazione</b> e mai letta dal JSON.
    ///
    /// <para>Il campo omonimo di <c>PairScreenResult</c> è una property <i>get-only</i>:
    /// System.Text.Json la scrive nel payload ma la <b>ignora</b> in deserializzazione, quindi
    /// leggerla dal blob darebbe <c>false</c> su ogni riga — e il filtro «operabili» del pannello
    /// sarebbe sempre vuoto, cioè un pannello muto dall'aria funzionante. Stessa scelta di
    /// <c>ResearchCandidate.IsGrey</c>: si ricalcola dalla stessa regola della sorgente.</para>
    /// </summary>
    public bool IsTradeable { get; set; }
}

/// <summary>
/// [I14] L'identità di una coppia, in un posto solo. <c>"ETH/USDT|BTC/USDT 1h"</c>.
///
/// <para><b>Normalizza l'ordine delle due gambe.</b> Oggi lo stage genera solo <c>i&lt;j</c> sui
/// simboli distinti, quindi la coppia arriva sempre nello stesso verso — ma è una proprietà di
/// <i>come</i> il ciclo è scritto, non del dominio: un universo ordinato diversamente produrrebbe
/// due chiavi per la stessa coppia, e l'indice unico non le vedrebbe come duplicati. Normalizzare
/// qui costa una riga e toglie la dipendenza da quel dettaglio.</para>
///
/// <para>Attenzione: la chiave identifica la <b>coppia non orientata</b>, mentre
/// <see cref="PairCandidate.SymbolY"/>/<see cref="PairCandidate.SymbolX"/> conservano il verso
/// della regressione — che conta, perché <c>HedgeRatio</c> è l'elasticità di Y rispetto a X.</para>
/// </summary>
public static class PairKey
{
    public static string Build(string symbolY, string symbolX, string timeframe)
    {
        var a = (symbolY ?? string.Empty).Trim().ToUpperInvariant();
        var b = (symbolX ?? string.Empty).Trim().ToUpperInvariant();
        var (first, second) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        return $"{first}|{second} {(timeframe ?? string.Empty).Trim()}";
    }
}

/// <param name="RunsIndexed">Run che hanno prodotto righe nuove.</param>
/// <param name="PairsIndexed">Righe scritte.</param>
/// <param name="RunsSkipped">Run esclusi: payload illeggibile, o gara persa con un altro processo.</param>
/// <param name="RunsEmpty">
/// [I14] Run il cui screening non ha prodotto NESSUNA coppia — succede quando nessuna serie ha
/// abbastanza candele allineate. Contati a parte, e per una ragione precisa: non producendo righe
/// non lasciano traccia nella tabella, quindi <b>l'incrementale li rilegge a ogni giro</b>. Il costo
/// è una deserializzazione di un payload minuscolo; il danno sarebbe dichiararli «indicizzati» e
/// far credere che il pulsante stia facendo lavoro nuovo per sempre.
/// </param>
public sealed record PairIndexResult(int RunsIndexed, int PairsIndexed, int RunsSkipped, int RunsEmpty = 0);

/// <summary>
/// [I14] Costruisce e mantiene l'indice delle coppie: incrementale (solo i run non ancora
/// indicizzati) e ricostruzione totale. Difensivo per run — un payload illeggibile esce con un log
/// e non ferma gli altri — sullo stesso progetto di <see cref="Research.IResearchCandidateIndexer"/>.
/// </summary>
public interface IPairCandidateIndexer
{
    /// <summary>Indicizza i soli run non ancora presenti.</summary>
    Task<PairIndexResult> IndexNewAsync(CancellationToken ct = default);

    /// <summary>Svuota e ricostruisce da zero. Idempotente: due ricostruzioni danno le stesse righe.</summary>
    Task<PairIndexResult> RebuildAsync(CancellationToken ct = default);
}

public sealed class PairCandidateIndexer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<PairCandidateIndexer> logger) : IPairCandidateIndexer
{
    /// <summary>Il Kind scritto da <c>PipelineEngine</c> per l'output dello stage «Screening coppie».</summary>
    public const string ArtifactKind = "PairScreen";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PairIndexResult> IndexNewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var already = await db.PairCandidates.AsNoTracking()
                .Select(p => p.RunId).Distinct().ToListAsync(ct);
            return await IndexAsync(db, already, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<PairIndexResult> RebuildAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.PairCandidates.ExecuteDeleteAsync(ct);
            return await IndexAsync(db, [], ct);
        }
        finally { _gate.Release(); }
    }

    // Internal e non private per la stessa ragione dell'indice dei candidati: il test della gara fra
    // processi deve poter passare una lista "stantia" di run già indicizzati, e dall'API pubblica
    // quella finestra non è riproducibile.
    internal async Task<PairIndexResult> IndexAsync(ApplicationDbContext db, List<Guid> alreadyIndexed, CancellationToken ct)
    {
        // Gli artefatti esistono solo per run "Completed", quindi il join filtra da sé. CompletedAt
        // si denormalizza qui; il fallback per un CompletedAt assente è il CreatedAt dell'artefatto
        // — STABILE fra ricostruzioni, mentre DateTime.UtcNow fabbricherebbe una recency diversa a
        // ogni giro (stessa trappola già coperta da un test sull'indice dei candidati).
        var sources = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.Kind == ArtifactKind && !alreadyIndexed.Contains(a.RunId))
            .Join(db.PipelineRuns.AsNoTracking(),
                a => a.RunId, r => r.Id,
                (a, r) => new { a.RunId, a.PayloadJson, r.CompletedAt, a.CreatedAt })
            .ToListAsync(ct);

        var runsIndexed = 0;
        var pairsIndexed = 0;
        var runsSkipped = 0;
        var runsEmpty = 0;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            PairsOutput? payload;
            try
            {
                payload = JsonSerializer.Deserialize<PairsOutput>(source.PayloadJson);
            }
            catch (JsonException ex)
            {
                runsSkipped++;
                logger.LogWarning(ex, "Run {Run} escluso dall'indice coppie (payload illeggibile).", source.RunId);
                continue;
            }
            if (payload is null || payload.Pairs.Count == 0)
            {
                // Non è un guasto: uno screening che non ha trovato serie allineate a sufficienza
                // scrive un artefatto vuoto. Si conta A PARTE, e non fra gli indicizzati: senza
                // righe non lascia traccia nella tabella, quindi l'incrementale lo rileggerà al
                // prossimo giro — dichiararlo «indicizzato» farebbe credere al pulsante di aver
                // fatto lavoro nuovo, per sempre.
                runsEmpty++;
                continue;
            }

            var rows = payload.Pairs
                // Dentro un run la coppia è unica per costruzione (doppio ciclo i<j); il GroupBy è
                // la cintura per i payload storici, dove un duplicato violerebbe l'indice unico e
                // farebbe fallire l'intero giro invece della sola riga.
                .GroupBy(r => PairKey.Build(r.SymbolY, r.SymbolX, r.Timeframe), StringComparer.Ordinal)
                .Select(g => (Key: g.Key, Row: g.First()))
                .Select(x => new PairCandidate
                {
                    RunId = source.RunId,
                    RunCompletedUtc = source.CompletedAt ?? source.CreatedAt,
                    SymbolY = Truncate(x.Row.SymbolY, 32)!,
                    SymbolX = Truncate(x.Row.SymbolX, 32)!,
                    Timeframe = Truncate(x.Row.Timeframe, 8)!,
                    PairKeyValue = Truncate(x.Key, 96)!,
                    AdfStatistic = x.Row.AdfStatistic,
                    IsCointegrated = x.Row.IsCointegrated,
                    HedgeRatio = x.Row.HedgeRatio,
                    IsHedgeRatioPlausible = x.Row.IsHedgeRatioPlausible,
                    AlignedCandles = x.Row.AlignedCandles,
                    // RICALCOLATO, mai letto dal JSON: vedi il doc-comment del campo.
                    IsTradeable = x.Row.IsCointegrated && x.Row.IsHedgeRatioPlausible,
                })
                .ToList();

            if (rows.Count < payload.Pairs.Count)
            {
                logger.LogWarning("Run {Run}: {Dupes} coppie con chiave duplicata nel payload — tenuta la prima di ciascuna.",
                    source.RunId, payload.Pairs.Count - rows.Count);
            }

            db.PairCandidates.AddRange(rows);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // La violazione tipica è l'indice unico quando un ALTRO processo ha già indicizzato
                // questo run nella finestra fra la nostra lettura e questo insert. Il run è a posto
                // — lo ha scritto qualcun altro — quindi si sgancia tutto dal change tracker
                // (altrimenti il prossimo SaveChanges rifallirebbe con le stesse righe) e si va
                // avanti col run successivo.
                db.ChangeTracker.Clear();
                runsSkipped++;
                logger.LogWarning(ex, "Run {Run} saltato dall'indice coppie (probabile indicizzazione concorrente da un altro processo).",
                    source.RunId);
                continue;
            }
            runsIndexed++;
            pairsIndexed += rows.Count;
        }

        if (runsIndexed > 0)
        {
            logger.LogInformation("Indice coppie: {Runs} run, {Pairs} coppie ({Skipped} saltati, {Empty} senza coppie).",
                runsIndexed, pairsIndexed, runsSkipped, runsEmpty);
        }
        return new PairIndexResult(runsIndexed, pairsIndexed, runsSkipped, runsEmpty);
    }

    /// <summary>
    /// Tronca ai limiti di colonna PRIMA dell'insert. I simboli storici sono corti, ma un solo
    /// valore fuori misura farebbe fallire l'intero giro e svuoterebbe il pannello: la lezione è
    /// già stata pagata sull'indice dei candidati con un messaggio d'eccezione illimitato.
    /// </summary>
    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}
