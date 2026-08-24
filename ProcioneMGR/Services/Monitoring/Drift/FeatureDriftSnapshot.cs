using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Riga della fotografia: l'esito dell'ultimo check su UN modello.</summary>
public sealed record FeatureDriftModelSnapshot(
    int ModelId,
    string ModelName,
    string Symbol,
    string Timeframe,
    DriftSeverity Overall,
    int DriftingFeatures,
    int TotalFeatures,
    int AlertFeatures,
    string? SkipReason,
    DateTime CheckedAtUtc)
{
    /// <summary>Il check ha prodotto un giudizio, non un rinvio.</summary>
    public bool IsVerdict => string.IsNullOrEmpty(SkipReason);

    /// <summary>
    /// [2026-08-24] La riga SENTINELLA di un tick senza soggetto (<c>ModelId = 0</c>: nessun
    /// <c>SavedMlModel</c> ha quell'id). Non è un modello saltato — è la prova che il worker ha
    /// girato e non aveva niente da guardare, che è un'informazione diversa sia da «tutto pulito»
    /// sia da «il worker non sta girando».
    /// </summary>
    public bool IsSentinel => ModelId == 0;
}

/// <summary>
/// [I6] Ultima fotografia nota della deriva delle <b>feature ML</b>, per la Home.
///
/// <para><b>Perché esiste.</b> Il monitor persisteva su <c>DriftCheckResults</c> e mostrava tutto in
/// <c>/admin/autonomy</c>, cioè rispondeva solo a chi andava a cercarlo — mentre il senso di un
/// monitor di deriva è accorgersene <i>senza doverci pensare</i>. È la stessa lacuna che D2.a chiuse
/// per la deriva dei FATTORI, e questa classe ne è deliberatamente il gemello: stesso impianto,
/// stessa idratazione all'avvio, stessa dichiarazione di copertura.</para>
///
/// <para><b>L'omonimia va tenuta a mente.</b> Questa è la deriva delle <i>feature dei modelli ML</i>
/// (PSI/KS/Page-Hinkley sulle distribuzioni); l'altra, già in Home dal 2026-07-27, è la deriva
/// dell'<i>IC dei fattori</i> (<c>Services/Alpha/FactorDriftMonitor</c>, filone D2). Sono due cose
/// diverse con nomi quasi identici, e la Home deve etichettarle in modo che non si confondano —
/// due verdetti indistinguibili sullo stesso schermo sono peggio di un verdetto solo.</para>
///
/// Singleton: scritto dal worker, letto dalla UI. Thread-safe per sostituzione atomica.
/// </summary>
public sealed class FeatureDriftSnapshot
{
    private volatile IReadOnlyList<FeatureDriftModelSnapshot> _models = [];

    /// <summary>Quando è stata composta la fotografia (null = mai, né da tick né da idratazione).</summary>
    public DateTime? LastRunUtc { get; private set; }

    /// <summary>Vero se la fotografia viene dalla tabella e non da un tick di questo processo.</summary>
    public bool FromStoredHistory { get; private set; }

    /// <summary>
    /// [2026-08-24] Quanti modelli ci sono nel REGISTRO adesso. È il denominatore vero, e mancava:
    /// «Copertura: 158 modelli, tutti giudicati» contava le righe del tick, non il registro — al
    /// 2026-08-24 i modelli salvati erano 176, quindi «tutti» copriva 158/176 e lo scarto cresceva
    /// a ogni modello nuovo. Zero = non dichiarato.
    /// </summary>
    public int RegistrySize { get; private set; }

    /// <summary>
    /// [2026-08-24] Righe del tick SCARTATE all'idratazione perché il modello non esiste più o non
    /// ricade più negli stage sorvegliati. Va dichiarato: una fotografia che si assottiglia in
    /// silenzio è un allarme che sparisce senza che nessuno abbia deciso nulla.
    /// </summary>
    public int DroppedNotMonitored { get; private set; }

    public IReadOnlyList<FeatureDriftModelSnapshot> All => _models;

    /// <summary>Modelli su cui il check ha prodotto un giudizio.</summary>
    public int ModelsWithVerdict => _models.Count(m => m.IsVerdict);

    /// <summary>
    /// Modelli su cui il check è stato SALTATO. È il numero che rende onesto un «nessun allarme»:
    /// senza, «0 allarmi su 53 modelli» si legge come un via libera anche quando 50 di quei 53 non
    /// sono stati guardati affatto.
    /// </summary>
    public int ModelsSkipped => _models.Count(m => !m.IsVerdict);

    /// <summary>I modelli in Alert, i più gravi per primi. Solo fra quelli con un verdetto vero.</summary>
    public IReadOnlyList<FeatureDriftModelSnapshot> Alerts =>
        _models.Where(m => m.IsVerdict && m.Overall == DriftSeverity.Alert)
            .OrderByDescending(m => m.AlertFeatures)
            .ThenByDescending(m => m.DriftingFeatures)
            .ToList();

    public void Replace(
        IEnumerable<FeatureDriftModelSnapshot> models,
        DateTime computedAtUtc,
        bool fromStoredHistory = false,
        int registrySize = 0,
        int droppedNotMonitored = 0)
    {
        _models = models.ToList();
        LastRunUtc = computedAtUtc;
        FromStoredHistory = fromStoredHistory;
        RegistrySize = registrySize;
        DroppedNotMonitored = droppedNotMonitored;
    }

    /// <summary>
    /// Ricostruisce la fotografia dall'ULTIMO tick registrato in <c>DriftCheckResults</c>.
    ///
    /// <para>Serve per la stessa ragione che vinse in D2.b: <b>il guscio si riavvia di continuo</b>
    /// (bring-up al logon, watchdog ogni 5 minuti), e senza idratazione l'allarme in Home sarebbe
    /// assente proprio nei minuti in cui uno guarda la Home — comparendo solo dopo il primo tick, che
    /// con cadenza di ore può essere lontanissimo. Non è una cache: è la differenza fra «non c'è
    /// deriva» e «non ho ancora guardato».</para>
    ///
    /// <para>Si prende UN solo tick — il più recente — e non «le ultime N righe»: righe di tick
    /// diversi mescolate darebbero una fotografia che non è mai esistita, con lo stesso modello
    /// contato due volte a stati diversi. È la lezione delle finestre sovrapposte di D2.b.</para>
    /// </summary>
    /// <param name="monitoredStages">
    /// [2026-08-24] Gli stage che il monitor sorveglia ADESSO. Le righe di modelli che non
    /// ricadono più lì — o che non esistono più — non tornano in Home: sarebbero un allarme su
    /// una popolazione che il monitor ha deliberatamente smesso di guardare. È la stessa regola,
    /// e la stessa motivazione, del gemello <c>FactorDriftMonitor.TryHydrateAsync</c>, che scarta
    /// le serie uscite dalla watchlist. Lista vuota = nessun filtro per stage.
    /// </param>
    public async Task HydrateAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IReadOnlyList<string>? monitoredStages = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var lastRun = await db.DriftCheckResults.AsNoTracking()
            .OrderByDescending(r => r.CheckedAtUtc)
            .Select(r => (DateTime?)r.CheckedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (lastRun is not DateTime at) return; // nessun tick mai registrato: si resta vuoti, dichiarandolo

        var rows = await db.DriftCheckResults.AsNoTracking()
            .Where(r => r.CheckedAtUtc == at)
            .ToListAsync(ct);

        // Il denominatore vero si legge ADESSO, non si eredita dal tick: è la dimensione del
        // registro oggi, che è ciò contro cui una copertura va letta.
        var registrySize = await db.SavedMlModels.AsNoTracking().CountAsync(ct);

        var ids = rows.Where(r => r.ModelId != 0).Select(r => r.ModelId).Distinct().ToList();
        var stageById = await db.SavedMlModels.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.Stage })
            .ToDictionaryAsync(x => x.Id, x => x.Stage.ToString(), ct);

        var kept = rows.Where(r => r.ModelId == 0 || IsStillMonitored(r.ModelId, stageById, monitoredStages)).ToList();

        Replace(kept.Select(FromRow), at,
            fromStoredHistory: true,
            registrySize: registrySize,
            droppedNotMonitored: rows.Count - kept.Count);
    }

    private static bool IsStillMonitored(
        int modelId,
        IReadOnlyDictionary<int, string> stageById,
        IReadOnlyList<string>? monitoredStages)
    {
        if (!stageById.TryGetValue(modelId, out var stage)) return false; // modello sparito
        if (monitoredStages is null || monitoredStages.Count == 0) return true;
        return monitoredStages.Any(s => string.Equals(s, stage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Proiezione riga → fotografia. Una sola, così tick e idratazione non possono divergere.</summary>
    public static FeatureDriftModelSnapshot FromRow(DriftCheckResult r) => new(
        r.ModelId, r.ModelName, r.Symbol, r.Timeframe,
        r.Overall, r.DriftingFeatures, r.TotalFeatures, r.AlertFeatures, r.SkipReason, r.CheckedAtUtc);
}

/// <summary>
/// Idrata <see cref="FeatureDriftSnapshot"/> all'avvio del guscio, prima che il worker abbia girato.
/// Fail-open: un guasto qui lascia la Home senza il blocco, non la fa fallire.
/// </summary>
public sealed class FeatureDriftHydrationWorker(
    FeatureDriftSnapshot snapshot,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<DriftMonitorOptions> options,
    ILogger<FeatureDriftHydrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await snapshot.HydrateAsync(dbFactory, options.CurrentValue.EffectiveStages(), stoppingToken);
            if (snapshot.LastRunUtc is DateTime at)
            {
                logger.LogInformation(
                    "Deriva feature: fotografia ricostruita dalla storia registrata ({Models} modelli, {Alerts} in allarme, {Skipped} saltati, {Dropped} scartati perché non più sorvegliati, registro {Registry}, tick del {At:yyyy-MM-dd HH:mm} UTC).",
                    snapshot.All.Count, snapshot.Alerts.Count, snapshot.ModelsSkipped,
                    snapshot.DroppedNotMonitored, snapshot.RegistrySize, at);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idratazione della fotografia di deriva feature fallita: la Home resterà senza il blocco fino al primo tick.");
        }
    }
}
