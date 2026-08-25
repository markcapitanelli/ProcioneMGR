using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Research;

/// <summary>Filtri della pagina /research. "all" | "survived" | "grey" | "rejected" per il verdetto.</summary>
public sealed record ResearchFilter(
    string Symbol = "",
    string Timeframe = "",
    string Verdict = "all",
    string Family = "",
    string Search = "");

public sealed record ResearchSummary(
    int TotalCandidates,
    int TotalRuns,
    int Survivors,
    int Grey,
    int GreyShortWindow,
    int RejectedOnMerit,
    DateTime? OldestRunUtc,
    DateTime? NewestRunUtc);

public sealed record FamilyStat(string Family, int Tested, int Survived, int Grey);

public sealed record RejectReasonStat(string Category, int Count, decimal AvgHoldoutSharpe);

/// <summary>
/// [R1+R5, PRD memoria-caccia 2026-08-14] Orchestrazione di Research.razor (pattern P1-5):
/// aggregati trasversali sui candidati archiviati + elenco filtrabile per coppia con la fascia
/// grigia in evidenza. Sola lettura sull'indice; le uniche scritture passano dall'indexer, che
/// costruisce una tabella DERIVATA. La corsia dell'utente non c'entra: qui si guarda la caccia,
/// non il trading.
/// </summary>
public sealed class ResearchPageService(
    IResearchCandidateIndexer indexer,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>Tetto righe della tabella: la pagina è una lente, non un export.</summary>
    public const int MaxRows = 200;

    private static readonly string[] TimeframeOrder = ["1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w"];

    public ResearchSummary? Summary { get; private set; }

    /// <summary>[J4] Righe escluse perché il run era a universo misto (verdetti non confrontabili). Da dichiarare in UI.</summary>
    public int MixedExcludedRows { get; private set; }

    /// <summary>[J4] Le stesse esclusioni contate per CandidateKey distinti — il numero che conta (le righe sono ~19×).</summary>
    public int MixedExcludedKeys { get; private set; }

    public IReadOnlyList<FamilyStat> FamilyStats { get; private set; } = [];
    public IReadOnlyList<RejectReasonStat> RejectReasons { get; private set; } = [];
    public IReadOnlyList<string> Symbols { get; private set; } = [];
    public IReadOnlyList<string> Timeframes { get; private set; } = [];
    public IReadOnlyList<string> Families { get; private set; } = [];
    public IReadOnlyList<ResearchCandidate> Candidates { get; private set; } = [];
    public int FilteredCount { get; private set; }
    public ResearchIndexResult? LastIndex { get; private set; }

    /// <summary>Valorizzato quando l'ultima indicizzazione è fallita: la pagina lo dichiara e mostra comunque l'archivio esistente.</summary>
    public string? IndexError { get; private set; }

    /// <summary>
    /// Indicizzazione incrementale a ogni apertura della pagina: a regime è una no-op (nessun run
    /// nuovo), dopo una caccia porta dentro i run freschi senza che nessuno debba ricordarsene.
    /// [Review 2026-08-14] Un'indicizzazione fallita NON svuota la pagina: l'archivio già
    /// indicizzato si mostra comunque, col fallimento dichiarato — degradare dicendolo.
    /// </summary>
    public async Task InitializeAsync(ResearchFilter filter, CancellationToken ct = default)
    {
        IndexError = null;
        try
        {
            LastIndex = await indexer.IndexNewRunsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IndexError = $"Indicizzazione dei run nuovi fallita ({ex.Message}): mostro l'archivio già indicizzato.";
        }
        await LoadAsync(filter, ct);
    }

    public async Task RebuildIndexAsync(ResearchFilter filter, CancellationToken ct = default)
    {
        LastIndex = await indexer.RebuildAsync(ct);
        await LoadAsync(filter, ct);
    }

    public async Task LoadAsync(ResearchFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // [J4] I candidati dei run a universo MISTO escono da OGNI lettura della pagina — aggregati
        // compresi — perché PBO e DSR di quei run mescolavano due ppy e i loro verdetti non sono
        // confrontabili con gli altri (dal 2026-08-20 lo stage rifiuta quegli universi; questo
        // chiude il buco sui run già archiviati). L'esclusione è DICHIARATA col conteggio in UI:
        // uno scarto silenzioso si leggerebbe come «non c'era nulla».
        var mixedRunIds = db.PipelineRuns.AsNoTracking()
            .Where(r => r.MixedTimeframeUniverse)
            .Select(r => r.Id);
        MixedExcludedRows = await db.ResearchCandidates.AsNoTracking()
            .CountAsync(c => mixedRunIds.Contains(c.RunId), ct);
        MixedExcludedKeys = await db.ResearchCandidates.AsNoTracking()
            .Where(c => mixedRunIds.Contains(c.RunId))
            .Select(c => c.CandidateKey)
            .Distinct()
            .CountAsync(ct);

        var all = db.ResearchCandidates.AsNoTracking()
            .Where(c => !mixedRunIds.Contains(c.RunId));

        // --- Aggregati globali (mai filtrati: il quadro d'insieme resta fermo mentre si filtra) --
        var totals = await all
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Survivors = g.Count(x => x.Survived),
                Grey = g.Count(x => x.IsGrey),
                GreyShortWindow = g.Count(x => x.IsGrey && x.RejectReason != null && x.RejectReason.StartsWith(GreyZone.ShortWindowRejectPrefix)),
                Oldest = g.Min(x => (DateTime?)x.RunCompletedUtc),
                Newest = g.Max(x => (DateTime?)x.RunCompletedUtc),
            })
            .FirstOrDefaultAsync(ct);
        var runCount = await all.Select(x => x.RunId).Distinct().CountAsync(ct);
        Summary = totals is null
            ? new ResearchSummary(0, 0, 0, 0, 0, 0, null, null)
            : new ResearchSummary(totals.Total, runCount, totals.Survivors, totals.Grey, totals.GreyShortWindow,
                totals.Total - totals.Survivors - totals.Grey, totals.Oldest, totals.Newest);

        // Proiezione su tipo anonimo: EF non traduce un costruttore di record posizionale.
        FamilyStats = (await all
            .GroupBy(x => x.StrategyName)
            .Select(g => new { Family = g.Key, Tested = g.Count(), Survived = g.Count(x => x.Survived), Grey = g.Count(x => x.IsGrey) })
            .OrderByDescending(f => f.Tested)
            .ToListAsync(ct))
            .Select(f => new FamilyStat(f.Family, f.Tested, f.Survived, f.Grey))
            .ToList();

        // Motivi di scarto: classificazione client-side (2 colonne per riga, l'archivio attuale
        // sta in memoria senza fatica; se un giorno non bastasse, l'indice è già a righe).
        var rejects = await all.Where(x => !x.Survived)
            .Select(x => new { x.RejectReason, x.HoldoutSharpe })
            .ToListAsync(ct);
        RejectReasons = rejects
            .GroupBy(r => RejectCategory(r.RejectReason))
            .Select(g => new RejectReasonStat(g.Key, g.Count(),
                Math.Round(g.Average(r => r.HoldoutSharpe), 2)))
            .OrderByDescending(r => r.Count)
            .ToList();

        Symbols = await all.Select(x => x.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
        var tfs = await all.Select(x => x.Timeframe).Distinct().ToListAsync(ct);
        Timeframes = tfs
            .OrderBy(t => { var i = Array.IndexOf(TimeframeOrder, t); return i < 0 ? int.MaxValue : i; })
            .ThenBy(t => t)
            .ToList();
        Families = FamilyStats.Select(f => f.Family).ToList();

        // --- Elenco filtrato ---------------------------------------------------------------------
        var query = all;
        if (!string.IsNullOrWhiteSpace(filter.Symbol)) query = query.Where(x => x.Symbol == filter.Symbol);
        if (!string.IsNullOrWhiteSpace(filter.Timeframe)) query = query.Where(x => x.Timeframe == filter.Timeframe);
        if (!string.IsNullOrWhiteSpace(filter.Family)) query = query.Where(x => x.StrategyName == filter.Family);
        query = filter.Verdict switch
        {
            "survived" => query.Where(x => x.Survived),
            "grey" => query.Where(x => x.IsGrey),
            "rejected" => query.Where(x => !x.Survived && !x.IsGrey),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search.Trim().ToLower();
            query = query.Where(x => x.CandidateKey.ToLower().Contains(needle));
        }

        FilteredCount = await query.CountAsync(ct);
        Candidates = await query
            .OrderByDescending(x => x.RunCompletedUtc)
            .ThenByDescending(x => x.HoldoutSharpe)
            .Take(MaxRows)
            .ToListAsync(ct);
    }

    /// <summary>
    /// «Rianalizza»: Optimization precompilata con strategia, coppia e parametri del candidato —
    /// stesso handoff in query string di Backtest e ML Lab, nessuna scrittura. Exchange e periodo
    /// restano quelli correnti della pagina di destinazione (ApplyHandoff fa fallback campo per campo).
    /// </summary>
    public static string OptimizationHandoffUrl(ResearchCandidate c) =>
        "optimization"
        + $"?symbol={Uri.EscapeDataString(c.Symbol)}"
        + $"&timeframe={Uri.EscapeDataString(c.Timeframe)}"
        + $"&strategy={Uri.EscapeDataString(c.StrategyName)}"
        + $"&parameters={Uri.EscapeDataString(c.ParametersJson)}";

    /// <summary>
    /// [Difetto B, 2026-08-22] Come si legge il confronto col passivo. Il numero da solo non dice
    /// contro cosa: il tooltip porta direzione prevalente, esposizione netta, tempo a mercato e
    /// Sharpe del passivo — e, quando manca, dichiara <b>perché</b> manca invece di lasciare un
    /// trattino muto.
    /// </summary>
    public static string SpiegaConfrontoPassivo(ResearchCandidate c)
    {
        if (c.ExcessHoldoutSharpe is not decimal eccesso)
        {
            return c.DominantDirection switch
            {
                null => "Non misurato: riga precedente al 2026-08-22. Non e' ricavabile a posteriori — "
                        + "i blob degli artifact non contengono i trade.",
                "Unknown" => "Direzione non determinabile (nessun trade, o trade tutti istantanei): "
                             + "non esiste un passivo con cui confrontarsi.",
                "Mixed" => $"Direzione mista (esposizione netta {c.NetExposure:+0.00;-0.00}): nessun lato domina "
                           + "abbastanza da rendere ovvio quale passivo sia il confronto giusto.",
                _ => "Benchmark non calcolabile su questo candidato; il suo verdetto e' intatto.",
            };
        }

        var tempo = c.TimeInMarketFraction is decimal f ? $"{f:P0} del tempo a mercato" : "tempo a mercato non misurabile";
        var verso = eccesso > 0m ? "BATTE" : "NON batte";
        return $"{verso} il passivo di {eccesso:+0.00;-0.00} Sharpe. "
             + $"Direzione prevalente {c.DominantDirection} (esposizione netta {c.NetExposure:+0.00;-0.00}), {tempo}. "
             + $"Passivo {c.PassiveHoldoutSharpe:F2} contro candidato {(c.PassiveHoldoutSharpe + eccesso):F2}. "
             + "Entrambi a risk-free ZERO, e il passivo senza funding: il funding e' una costante inventata "
             + "che il long paga e lo short incassa, e il passivo sta a mercato il 100% della finestra. "
             + "Residuo dichiarato: l'eccesso resta favorevole al candidato, che la sua carry ce l'ha dentro.";
    }

    /// <summary>Parametri leggibili per la cella della tabella ("k=v, k=v"); JSON illeggibile ⇒ testo grezzo.</summary>
    public static string FormatParams(string parametersJson)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, decimal>>(parametersJson);
            if (dict is null || dict.Count == 0) return "—";
            return string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value:0.####}"));
        }
        catch (JsonException)
        {
            return parametersJson;
        }
    }

    /// <summary>
    /// Categoria leggibile di un motivo di scarto. I testi sono prodotti dai nostri gate, quindi i
    /// prefissi sono stabili; ciò che non si riconosce resta com'è (troncato), mai "altro" muto.
    /// </summary>
    internal static string RejectCategory(string? reason) => reason switch
    {
        null or "" => "(nessun motivo registrato)",
        var r when r.StartsWith(GreyZone.ShortWindowRejectPrefix, StringComparison.Ordinal) => "Finestra corta (pochi trade)",
        var r when r.Contains("DSR", StringComparison.OrdinalIgnoreCase) => "DSR sotto soglia",
        var r when r.Contains("PBO", StringComparison.OrdinalIgnoreCase) => "PBO (overfitting di pannello)",
        var r when r.Contains("gemell", StringComparison.OrdinalIgnoreCase) => "Gemello nullo non battuto",
        // Il gate scrive "permutation p 0,123 ≥ … (Sharpe holdout compatibile col rumore)":
        // inglese, e con la parola "Sharpe" dentro — quindi va riconosciuto PRIMA del ramo
        // Sharpe, e con la grafia vera (review 2026-08-14: "permutazion" non è un prefisso di
        // "permutation", il caso finiva nella classe sbagliata).
        var r when r.Contains("permutation", StringComparison.OrdinalIgnoreCase)
            || r.Contains("permutazion", StringComparison.OrdinalIgnoreCase) => "Permutation test",
        var r when r.Contains("Sharpe", StringComparison.OrdinalIgnoreCase) => "Sharpe holdout sotto soglia",
        var r => r.Length <= 60 ? r : r[..60] + "…",
    };
}
