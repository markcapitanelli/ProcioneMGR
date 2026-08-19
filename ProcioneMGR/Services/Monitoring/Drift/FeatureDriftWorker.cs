using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Opzioni del <see cref="FeatureDriftWorker"/> (sezione config "Drift"). Default safe-off.</summary>
public sealed class DriftMonitorOptions
{
    /// <summary>Master switch. Default false: il worker si spegne subito, il drift resta valutabile on-demand dalla UI.</summary>
    public bool Enabled { get; set; }

    /// <summary>Cadenza di valutazione automatica (ore).</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Quante candele recenti usare come campione "corrente".</summary>
    public int RecentCandles { get; set; } = 200;

    /// <summary>
    /// Ciclo chiuso (Fase 2): quando un modello <b>Champion</b> va in drift, ritiralo dal registry e
    /// accoda un retrain. Default true (il worker è comunque opt-in). Il retrain NON è automatico —
    /// si marca soltanto la richiesta per l'operatore. Nessun impatto sul trading Live.
    /// </summary>
    public bool RetireChampionOnAlert { get; set; } = true;

    /// <summary>Numero minimo di feature in <c>Alert</c> per far scattare il ritiro del Champion.</summary>
    public int MinAlertsToRetire { get; set; } = 1;

    /// <summary>
    /// [I6] Soglie dei tre rilevatori (PSI, KS, Page-Hinkley). Fino al 2026-08-18 vivevano SOLO nei
    /// default di <see cref="DriftThresholds"/> e si cambiavano <b>ricompilando</b>: nessuna chiave,
    /// nessun pannello, in violazione diretta del mandato «tutto amministrabile da UI» del
    /// 2026-08-09. Sono la prassi generica del settore, non una misura fatta su serie finanziarie:
    /// poterle muovere è il presupposto per tararle.
    ///
    /// <para>I default restano quelli di prima, quindi una configurazione che non nomina questa
    /// sottosezione si comporta esattamente come prima — verificato al livello 2, non promesso.</para>
    /// </summary>
    public DriftThresholds Thresholds { get; set; } = new();

    /// <summary>
    /// [I6c] Quali <b>stage</b> del registry sorvegliare. Lista vuota = <see cref="DefaultStages"/>
    /// (Champion e Challenger), cioè i soli modelli che possono alimentare o contendere una corsia.
    ///
    /// <para><b>Perché esiste, e perché è il modo giusto di accendere questo monitor.</b> Al
    /// 2026-08-19 il registry conteneva <b>158 modelli, tutti in Staging</b>, e nessuna delle otto
    /// corsie aveva un riferimento ML: accendere il worker così avrebbe significato leggere 31.000
    /// candele e i blob di 158 modelli ogni sei ore — 39 secondi misurati sul database condiviso con
    /// motore e ingestion — per sorvegliare cose che nessuno usa, producendo 151 allarmi su 153.</para>
    ///
    /// <para>Quei 151 allarmi erano probabilmente <i>corretti</i>: modelli vecchi di mesi hanno
    /// feature davvero derivate. Il difetto non era la soglia, era il <b>soggetto</b>: ricalibrare le
    /// soglie su quella popolazione avrebbe adattato il metro a un campione irrilevante. Filtrare per
    /// stage trasforma un «gate senza soggetto» in un gate che <b>si accende insieme al soggetto</b>:
    /// oggi zero modelli sorvegliati e tick a costo trascurabile, e il giorno che un modello viene
    /// promosso parte da solo su quello.</para>
    ///
    /// <para>Default VUOTO per la stessa lezione di <c>Committee.Providers</c>: il binder di
    /// configurazione APPENDE gli elementi di un array alla lista già inizializzata, quindi un
    /// default popolato qui raddoppierebbe a ogni salvataggio dal pannello.</para>
    /// </summary>
    public List<string> MonitorStages { get; set; } = [];

    /// <summary>Gli stage sorvegliati quando <see cref="MonitorStages"/> è vuoto.</summary>
    public static readonly IReadOnlyList<string> DefaultStages = ["Champion", "Challenger"];

    /// <summary>Gli stage effettivamente sorvegliati, deduplicati e senza distinzione di maiuscole.</summary>
    public IReadOnlyList<string> EffectiveStages()
    {
        var source = MonitorStages.Count > 0 ? (IReadOnlyList<string>)MonitorStages : DefaultStages;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return source.Where(s => !string.IsNullOrWhiteSpace(s) && seen.Add(s.Trim())).Select(s => s.Trim()).ToList();
    }

    /// <summary>Questo modello va sorvegliato? Regola UNA, condivisa da worker, sonda e pannello.</summary>
    public bool Monitors(ModelStage stage) =>
        EffectiveStages().Any(s => string.Equals(s, stage.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Valuta periodicamente (opt-in) il drift delle feature di ogni <see cref="SavedMlModel"/> e logga
/// warning/alert. AFFIANCA il <c>StrategyDecayMonitor</c>: è un segnale anticipatore sugli input,
/// non una decisione di trading — non apre/chiude nulla, scrive solo log (rif. ROADMAP-QLIB §1.5).
/// Default spento (<see cref="DriftMonitorOptions.Enabled"/>=false), come le altre automazioni.
/// </summary>
public sealed class FeatureDriftWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IFeatureDriftMonitor monitor,
    ProcioneMGR.Services.Registry.IModelRegistry registry,
    Microsoft.Extensions.Options.IOptionsMonitor<DriftMonitorOptions> options,
    ILogger<FeatureDriftWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    FeatureDriftSnapshot? snapshot = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Enabled è valutato a OGNI tick (modello ExecutionWorker), non all'avvio: il toggle da
        // /admin/autonomy prende effetto a caldo. L'intervallo invece è fisso al primo avvio
        // (PeriodicTimer): cambiarlo richiede riavvio — un timer spento costa nulla.
        var interval = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.IntervalHours));
        logger.LogInformation("FeatureDriftWorker avviato (check ogni {Interval}, Enabled={Enabled}).",
            interval, options.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo FeatureDriftWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("FeatureDriftWorker fermato.");
    }

    /// <summary>Righe più vecchie di così vengono eliminate a ogni tick (lo storico utile è "di recente").</summary>
    internal const int ResultRetentionDays = 90;

    /// <summary>Un tick: valuta il drift di ogni modello salvato e logga gli scostamenti. Pubblico per test e per "Esegui ora" da /admin/autonomy.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue; // snapshot coerente per l'intero tick
        var checkedAt = DateTime.UtcNow;
        // [I6] Lo strumento del costo viene PRIMA dell'accensione: senza, «quanto costa un tick»
        // resta un'opinione, e questo worker legge candele e blob di modelli contro lo stesso
        // database che serve il motore e l'ingestion.
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var recentCandlesRead = 0;
        var featuresEvaluated = 0;
        List<SavedMlModel> models;
        int salvati;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            salvati = await db.SavedMlModels.AsNoTracking().CountAsync(ct);
            // [I6c] Si sorvegliano solo gli stage dichiarati: leggere i blob di tutti i modelli
            // salvati per giudicare cose che nessuna corsia usa è costo senza soggetto.
            var stages = opt.EffectiveStages();
            models = await db.SavedMlModels.AsNoTracking()
                .Where(m => stages.Contains(m.Stage.ToString()))
                .ToListAsync(ct);
        }

        if (models.Count == 0)
        {
            // Non è un guasto ed è l'esito normale finché nessun modello è schierato: si dichiara,
            // invece di lasciare una tabella vuota che si legge come «non ha girato».
            logger.LogInformation(
                "Tick drift: nessun modello negli stage sorvegliati ({Stages}) su {Salvati} salvati — niente da confrontare.",
                string.Join(", ", opt.EffectiveStages()), salvati);
            snapshot?.Replace([], checkedAt);
            metrics?.RecordDriftTick(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 0, 0, 0);
            return;
        }

        // [U4] Ogni check produce UNA riga per modello — anche quando è tutto pulito: così
        // l'assenza di righe si distingue da "il worker non sta girando" e la UI ha uno storico.
        var rows = new List<DriftCheckResult>(models.Count);

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            List<OhlcvData> recent;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                recent = await db.OhlcvData.AsNoTracking()
                    .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe)
                    .OrderByDescending(c => c.TimestampUtc)
                    .Take(Math.Max(20, opt.RecentCandles))
                    .ToListAsync(ct);
            }
            recent.Reverse(); // rimetti in ordine cronologico
            recentCandlesRead += recent.Count;

            // [I6] Prima di valutare: questo check PUÒ produrre un verdetto? I tre casi qui sotto
            // producevano tutti Overall=None, cioè il verde — indistinguibile da «ho guardato e va
            // tutto bene». Dichiararli SALTATI è ciò che rende il monitor capace di dire di no.
            var skip = DescribeSkip(model, recent, opt);
            if (skip is not null)
            {
                logger.LogInformation(
                    "Drift SALTATO sul modello '{Model}' ({Symbol} {Tf}): {Reason}",
                    model.Name, model.Symbol, model.Timeframe, skip);
                rows.Add(new DriftCheckResult
                {
                    CheckedAtUtc = checkedAt,
                    ModelId = model.Id,
                    ModelName = model.Name,
                    Symbol = model.Symbol,
                    Timeframe = model.Timeframe,
                    Overall = DriftSeverity.None,
                    SkipReason = skip,
                });
                continue;
            }

            // [I6] Le soglie arrivano dalla configurazione, non dai default incorporati: sono lo
            // snapshot preso a inizio tick, così un salvataggio a metà giro non produce due modelli
            // giudicati con due metri diversi nella stessa fotografia.
            var reports = await monitor.EvaluateAsync(model, recent, opt.Thresholds, ct);
            featuresEvaluated += reports.Count;

            // [I6] Il monitor non ha trovato NULLA DA MISURARE. Due casi, e il secondo è il quarto
            // modo di dire verde a prescindere — trovato dalla revisione avversaria DENTRO la
            // correzione che ne eliminava tre.
            //
            // Il primo è ovvio: zero report (nessuna feature costruibile).
            // Il secondo no: i tre detector restituiscono `None` con «Dati insufficienti» anche
            // quando NON hanno potuto guardare, perché le osservazioni valide (dopo il warm-up del
            // fattore e dopo lo scarto dei null) sono sotto MinObservations. In quel caso
            // reports.Count > 0 e drifting.Count == 0, quindi la riga finiva persistita come
            // GIUDIZIO verde: «pulito» costruito su rilevatori che avevano dichiarato di non aver
            // potuto misurare. Il dato per distinguerlo c'era già ed era inutilizzato —
            // ReferenceCount e CurrentCount sul report.
            var misurabili = reports.Count(r => IsMeasured(r, opt.Thresholds));
            if (misurabili == 0)
            {
                var reason = reports.Count == 0
                    ? "il monitor non ha prodotto alcuna feature valutabile per questo modello"
                    : $"nessuna delle {reports.Count} feature aveva abbastanza osservazioni valide "
                      + $"(servono {opt.Thresholds.MinObservations} per lato): i rilevatori hanno risposto «dati insufficienti», non «nessuna deriva»";
                logger.LogInformation(
                    "Drift SALTATO sul modello '{Model}' ({Symbol} {Tf}): {Reason}",
                    model.Name, model.Symbol, model.Timeframe, reason);
                rows.Add(new DriftCheckResult
                {
                    CheckedAtUtc = checkedAt,
                    ModelId = model.Id,
                    ModelName = model.Name,
                    Symbol = model.Symbol,
                    Timeframe = model.Timeframe,
                    Overall = DriftSeverity.None,
                    SkipReason = reason,
                });
                continue;
            }
            // [I6] Il denominatore del verdetto sono le feature DAVVERO misurate, non quante ne
            // esistono: «0 su 12» quando 9 non avevano osservazioni sufficienti è un rapporto che
            // rassicura contando anche ciò che nessuno ha guardato.
            if (misurabili < reports.Count)
            {
                logger.LogInformation(
                    "Drift sul modello '{Model}' ({Symbol} {Tf}): {NonMisurate} feature su {Totali} senza osservazioni sufficienti, escluse dal verdetto.",
                    model.Name, model.Symbol, model.Timeframe, reports.Count - misurabili, reports.Count);
            }

            var drifting = reports.Where(r => r.Overall != DriftSeverity.None).ToList();
            var alerts = drifting.Count(r => r.Overall == DriftSeverity.Alert);
            var championRetired = false;

            if (drifting.Count > 0)
            {
                logger.Log(alerts > 0 ? LogLevel.Warning : LogLevel.Information,
                    "Drift feature sul modello '{Model}' ({Symbol} {Tf}): {Drift}/{Total} feature in drift ({Alerts} alert). Es.: {Examples}",
                    model.Name, model.Symbol, model.Timeframe, drifting.Count, reports.Count, alerts,
                    string.Join(", ", drifting.Take(5).Select(r => $"{r.FeatureName}[{r.Overall}]")));
                if (alerts > 0) metrics?.RecordDriftAlerts(model.Symbol, model.Timeframe, alerts);

                // Ciclo chiuso (Fase 2): un Champion in drift Alert va ritirato e il retrain accodato.
                // Solo governance dei record: nessun retrain automatico, nessun impatto sul Live.
                if (opt.RetireChampionOnAlert
                    && model.Stage == ModelStage.Champion
                    && alerts >= Math.Max(1, opt.MinAlertsToRetire))
                {
                    var reason = $"drift: {alerts} feature in alert ({string.Join(", ", drifting.Where(r => r.Overall == DriftSeverity.Alert).Take(5).Select(r => r.FeatureName))})";
                    // [2026-08-19] L'esito del ritiro si legge, non si presume: fra la lettura dei
                    // modelli in cima al tick e questa riga l'operatore può aver ritirato o
                    // riportato in Staging il Champion da /registry. Segnare ChampionRetired=true
                    // su un ritiro che non è avvenuto metterebbe una bugia nella tabella d'esito.
                    var retired = await registry.RetireAsync(model.Id, reason, requestRetrain: true, ct);
                    if (retired.Changed)
                    {
                        metrics?.RecordModelRetired(model.Symbol, model.Timeframe);
                        championRetired = true;
                    }
                    else
                    {
                        logger.LogInformation("Ritiro per drift del modello {Id} '{Name}' non applicato: {Reason}",
                            model.Id, model.Name, retired.Reason);
                    }
                }
            }

            rows.Add(new DriftCheckResult
            {
                CheckedAtUtc = checkedAt,
                ModelId = model.Id,
                ModelName = model.Name,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                TotalFeatures = misurabili, // le feature DAVVERO misurate: vedi la nota sul denominatore
                DriftingFeatures = drifting.Count,
                AlertFeatures = alerts,
                Overall = drifting.Count == 0 ? DriftSeverity.None : drifting.Max(r => r.Overall),
                TopFeaturesJson = BuildTopFeaturesJson(drifting),
                ChampionRetired = championRetired,
            });
        }

        if (rows.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.DriftCheckResults.AddRange(rows);
            await db.SaveChangesAsync(ct);
            // Prune nello stesso giro: lo storico oltre la retention non serve a nessuno e la
            // tabella cresce di N modelli per tick, per sempre.
            var cutoff = checkedAt.AddDays(-ResultRetentionDays);
            await db.DriftCheckResults.Where(r => r.CheckedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        }

        // [I6] La fotografia per la Home si aggiorna SEMPRE a fine tick, anche quando non c'è nessun
        // allarme: «zero allarmi su 53 modelli di cui 50 saltati» è un'informazione, e senza la
        // fotografia la Home non potrebbe distinguerla da «non ho ancora guardato».
        snapshot?.Replace(rows.Select(FeatureDriftSnapshot.FromRow), checkedAt);

        // [I6] Il costo del tick, misurato e dichiarato. La riga di riepilogo esiste perché il
        // numero deve poter essere letto anche da chi guarda i log e non la pagina — e perché i
        // contatori del processo si azzerano a ogni riavvio del guscio, che è frequente.
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var skipped = rows.Count(r => !r.IsVerdict);
        metrics?.RecordDriftTick(elapsedMs, models.Count, recentCandlesRead, featuresEvaluated);
        logger.LogInformation(
            "Tick drift: {Models} modelli ({Verdicts} con verdetto, {Skipped} saltati), {Candles} candele recenti lette, {Features} feature valutate in {Elapsed:F0} ms.",
            models.Count, rows.Count - skipped, skipped, recentCandlesRead, featuresEvaluated, elapsedMs);
    }

    /// <summary>
    /// [I6] Questa feature è stata <b>davvero misurata</b>, o i rilevatori hanno risposto «dati
    /// insufficienti»?
    ///
    /// <para>È la distinzione che mancava e che la revisione avversaria del 2026-08-18 ha trovato:
    /// i tre detector restituiscono <see cref="DriftSeverity.None"/> <i>anche</i> quando non hanno
    /// potuto guardare, perché le osservazioni valide — dopo il warm-up del fattore e dopo lo scarto
    /// dei null — sono sotto <see cref="DriftThresholds.MinObservations"/>. Contare quel «None» come
    /// «nessuna deriva» è il quarto modo di dire verde a prescindere, ed è dormiente solo finché
    /// nessuno tara le soglie: bastano <c>MinObservations</c> alzato sopra le osservazioni
    /// disponibili, o <c>RecentCandles</c> al minimo con un fattore a warm-up lungo, perché ogni
    /// riga diventi un falso «pulito».</para>
    ///
    /// <para>La soglia è la stessa che usano i detector, presa dalle stesse opzioni: due pavimenti
    /// per la stessa domanda darebbero due verdetti sulla stessa feature.</para>
    /// </summary>
    internal static bool IsMeasured(FactorDriftReport report, DriftThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(thresholds);
        // Un report senza alcun risultato non è una misura: è il caso «fattore non più costruibile»,
        // che il monitor impacchetta comunque in un report per non perdere la traccia del fattore.
        if (report.Results.Count == 0) return false;

        // Un verdetto DIVERSO da None è una misura per definizione: i rilevatori rispondono None
        // quando non hanno potuto guardare, quindi un Warning o un Alert può venire solo da un
        // confronto realmente eseguito. Chiederlo qui rende la regola robusta anche se i conteggi
        // non fossero valorizzati, e non allarga mai il silenzio — aggiunge solo casi misurati.
        if (report.Overall != DriftSeverity.None) return true;

        // Resta il caso ambiguo, ed è quello che conta: `None` può significare «nessuna deriva»
        // oppure «dati insufficienti». Li distinguono solo i conteggi.
        var floor = Math.Max(1, thresholds.MinObservations);
        return report.ReferenceCount >= floor && report.CurrentCount >= floor;
    }

    /// <summary>
    /// [I6] Il check può produrre un verdetto? <c>null</c> = sì. Ogni ramo qui dentro produceva
    /// prima <c>Overall=None</c>, cioè il colore verde: sono i tre modi in cui questo monitor
    /// diceva la cosa rassicurante indipendentemente dalla realtà.
    /// Puro e statico apposta: si prova senza database, senza orologio e senza worker.
    /// </summary>
    internal static string? DescribeSkip(SavedMlModel model, IReadOnlyList<OhlcvData> recent, DriftMonitorOptions opt)
    {
        var required = Math.Max(20, opt.RecentCandles);
        if (recent.Count < required)
        {
            return $"candele recenti insufficienti: {recent.Count} su {required} richieste";
        }

        // NOTA DI DISEGNO: «il modello ha feature valutabili?» NON si decide qui. La risposta la dà
        // il monitor, che è l'unico a sapere quali specifiche sa costruire, e leggere FactorsJson
        // per conto suo sarebbe una SECONDA regola sulla stessa domanda — due regole che possono
        // divergere sullo stesso modello, il difetto già pagato in D2 e con SeriesFreshness. Il caso
        // si dichiara DOPO la valutazione, da un report vuoto (vedi TickAsync).

        // Il caso insidioso: la finestra corrente è dentro il periodo di training. Confrontarla con
        // la distribuzione di training significa confrontare un campione con la popolazione che lo
        // contiene — non può quasi mai allarmare, e quel silenzio si legge come stabilità.
        var windowStart = recent[0].TimestampUtc;
        if (model.TrainingDataTo > DateTime.MinValue && windowStart < model.TrainingDataTo)
        {
            var overlap = recent.Count(c => c.TimestampUtc <= model.TrainingDataTo);
            var pct = 100.0 * overlap / recent.Count;
            return $"la finestra corrente si sovrappone al periodo di training per il {pct:F0}% delle candele "
                   + $"(training fino al {model.TrainingDataTo:yyyy-MM-dd}): il confronto sarebbe con la popolazione che contiene il campione";
        }

        return null;
    }

    /// <summary>Top-5 feature in drift come JSON compatto per la UI: [{"name","severity","detector","score"}].</summary>
    internal static string? BuildTopFeaturesJson(IReadOnlyList<FactorDriftReport> drifting)
    {
        if (drifting.Count == 0) return null;
        var top = drifting
            .OrderByDescending(r => r.Overall)
            .ThenByDescending(r => r.Results.Count == 0 ? 0.0 : r.Results.Max(x => x.Score))
            .Take(5)
            .Select(r =>
            {
                var worst = r.Results.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Score).FirstOrDefault();
                return new
                {
                    name = r.FeatureName,
                    severity = r.Overall.ToString(),
                    detector = worst?.Detector ?? "",
                    score = Math.Round(worst?.Score ?? 0.0, 4),
                };
            });
        return JsonSerializer.Serialize(top);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
