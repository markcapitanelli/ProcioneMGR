using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Services.Alpha;

// =============================================================================================
//  [D2, completamento] Dal pannello su richiesta alla SEGNALAZIONE che ti viene incontro.
//
//  Il PRD §5e chiedeva due cose che mancavano: l'alert accanto al widget di decadimento-strategia
//  in Home, e un job periodico che lo alimentasse. Il pannello in /feature-selection risponde solo
//  a chi va a cercarlo — ma il senso del monitor è accorgersi di un fattore che si spegne SENZA
//  doverci pensare, come già fa StrategyDecayMonitor per le gambe dell'ensemble.
//
//  MEMORIA **E** TABELLA (secondo giro, 2026-07-28). Il primo giro teneva solo una fotografia in
//  memoria, con l'argomento che l'IC storico è deterministico dalle candele. La fotografia resta —
//  è ciò che la Home legge senza toccare il DB — ma ora è ALIMENTATA anche dalla tabella
//  FactorIcWindows: il worker scrive le finestre che calcola e, all'avvio, ricostruisce la
//  fotografia da quanto già registrato. Le due ragioni per cui la sola memoria non bastava stanno
//  in testa a FactorIcHistory.cs: il guscio si riavvia di continuo (e l'alert in Home resterebbe
//  vuoto proprio quando serve) e le candele non sono eterne (quando la finestra fine verrà ruotata,
//  la storia dell'IC non sarà più ricalcolabile — allora è un'osservazione, non una cache).
//
//  NESSUNA AZIONE AUTOMATICA: si segnala e basta, come il fratello maggiore.
// =============================================================================================

/// <summary>Fotografia dell'ultimo calcolo di deriva, per serie.</summary>
public sealed record FactorDriftSeriesSnapshot(
    string Symbol,
    string Timeframe,
    DateTime ComputedAtUtc,
    IReadOnlyList<FactorDriftReport> Reports)
{
    public IReadOnlyList<FactorDriftReport> Alerts => Reports.Where(r => r.IsAlert).ToList();
}

/// <summary>
/// Ultima fotografia nota della deriva dei fattori, per tutte le serie monitorate. Singleton:
/// scritto dal worker, letto dalla UI. Thread-safe per sostituzione atomica del dizionario.
/// </summary>
public sealed class FactorDriftSnapshot
{
    private volatile IReadOnlyDictionary<string, FactorDriftSeriesSnapshot> _bySeries =
        new Dictionary<string, FactorDriftSeriesSnapshot>();

    /// <summary>Ultimo istante in cui il worker ha completato un giro (null se non ancora girato).</summary>
    public DateTime? LastRunUtc { get; private set; }

    /// <summary>
    /// Quante serie sono abilitate in watchlist quando la fotografia è stata composta. Serve alla UI
    /// per dichiarare la COPERTURA: "1 fattore in deriva" su 5 serie di 228 monitorate è vero e
    /// fuorviante, perché si legge come un verdetto sulla piattaforma invece che su un campione.
    /// Zero = non dichiarata (fotografia composta senza conoscere la watchlist).
    /// </summary>
    public int TrackedSeriesCount { get; private set; }

    public IReadOnlyCollection<FactorDriftSeriesSnapshot> All => _bySeries.Values.ToList();

    /// <summary>Tutti i fattori in allarme, su tutte le serie, i più gravi per primi.</summary>
    public IReadOnlyList<(FactorDriftSeriesSnapshot Series, FactorDriftReport Report)> Alerts =>
        _bySeries.Values
            .SelectMany(s => s.Alerts.Select(r => (Series: s, Report: r)))
            .OrderByDescending(x => (int)x.Report.Status)
            .ThenByDescending(x => Math.Abs(x.Report.ReferenceIc - x.Report.RecentIc))
            .ToList();

    public void Replace(IEnumerable<FactorDriftSeriesSnapshot> snapshots, DateTime computedAtUtc, int trackedSeriesCount = 0)
    {
        _bySeries = snapshots.ToDictionary(s => $"{s.Symbol}|{s.Timeframe}");
        LastRunUtc = computedAtUtc;
        TrackedSeriesCount = trackedSeriesCount;
    }
}

/// <summary>
/// Calcola periodicamente la deriva dei fattori sulle serie della watchlist e aggiorna
/// <see cref="FactorDriftSnapshot"/>. Configurazione: <see cref="FactorDriftOptions"/>
/// (sezione <c>FactorDrift</c>, pannello in /admin/autonomy).
///
/// [M1, 2026-08-20] Prima leggeva le quattro chiavi da <c>IConfiguration</c> con l'overload a tipo
/// inferito: nessuno dei due guardiani di copertura UI riusciva a vederle, e la sezione governava un
/// worker vivo senza avere un pannello. Ora passa da un POCO registrato, che è ciò che entrambi i
/// guardiani sanno guardare.
/// </summary>
public sealed class FactorDriftWorker(
    IServiceScopeFactory scopeFactory,
    IFactorDriftAnalyzer analyzer,
    IAlphaFactorFactory factorFactory,
    IFactorIcHistoryStore history,
    FactorDriftSnapshot snapshot,
    IOptionsMonitor<FactorDriftOptions> options,
    ILogger<FactorDriftWorker> logger) : BackgroundService
{
    /// <summary>Orizzonte forward del monitor periodico: una barra, come il pannello.</summary>
    private const int ForwardHorizon = 1;

    /// <summary>
    /// Ultimo TENTATIVO per serie, in memoria. Serve alla rotazione, e serve per un motivo preciso:
    /// una serie con troppe poche candele viene saltata e quindi **non scrive nulla** in tabella —
    /// ordinando per solo "ultimo calcolo" resterebbe per sempre in cima alla coda e occuperebbe un
    /// posto a ogni giro, affamando le serie buone. Contando il tentativo, una serie che non si può
    /// calcolare cede il posto e verrà ririprovata al proprio turno. In memoria e non su tabella
    /// perché al riavvio il costo è al massimo un giro sprecato.
    /// </summary>
    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new(StringComparer.Ordinal);

    /// <summary>
    /// Solo i fattori scritti a mano, non il catalogo Alpha158: 158 fattori × N serie × finestre
    /// rolling trasformerebbero un monitor in un consumo di CPU permanente. Chi vuole guardare
    /// l'intero catalogo lo fa su richiesta da /feature-selection.
    ///
    /// [2.9 PRD-RISANAMENTO] A questa base si AGGIUNGONO, per serie, i fattori del modello Champion
    /// di quella coppia (vedi <see cref="ChampionSpecsAsync"/>): l'esclusione in blocco del catalogo
    /// lasciava scoperti proprio i fattori che un modello in carica sta usando — quelli per cui
    /// "si sta spegnendo?" non è curiosità ma manutenzione. Il costo resta limitato: non 158 × serie,
    /// ma i pochi (top-K) che un Champion dichiara nel proprio FactorsJson.
    /// </summary>
    private static readonly string[] MonitoredFactors =
    [
        "Momentum", "MeanReversion", "RealizedVol", "ParkinsonVol", "RelativeVolume",
        "RsiFactor", "MacdFactor", "DistanceFromMa",
    ];

    /// <summary>
    /// [2.9] I fattori dichiarati dai modelli <see cref="ModelStage.Champion"/>, per serie.
    /// Champion e non Staging/Challenger: il monitor sorveglia ciò che è IN CARICA, non ogni
    /// esperimento salvato. FeatureName come chiave (due varianti parametriche dello stesso
    /// fattore sono feature diverse); un FactorName che la factory non conosce più si salta con
    /// un log invece di far cadere il giro — il monitor è advisory, fail-open per regola 4.
    /// </summary>
    private async Task<Dictionary<string, List<FactorSpec>>> ChampionSpecsAsync(
        ApplicationDbContext db, CancellationToken ct)
    {
        var bySeries = new Dictionary<string, List<FactorSpec>>(StringComparer.Ordinal);
        var champions = await db.SavedMlModels
            .Where(m => m.Stage == ModelStage.Champion)
            .Select(m => new { m.Name, m.Symbol, m.Timeframe, m.FactorsJson })
            .ToListAsync(ct);

        foreach (var model in champions)
        {
            List<SavedFactorSpecDto> dtos;
            try
            {
                dtos = JsonSerializer.Deserialize<List<SavedFactorSpecDto>>(model.FactorsJson) ?? [];
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "FactorsJson illeggibile per il Champion '{Name}' ({Symbol} {Tf}): fattori non sorvegliati.",
                    model.Name, model.Symbol, model.Timeframe);
                continue;
            }

            var key = $"{model.Symbol}|{model.Timeframe}";
            foreach (var dto in dtos)
            {
                try
                {
                    var factor = factorFactory.Create(dto.FactorName);
                    if (!bySeries.TryGetValue(key, out var list)) bySeries[key] = list = [];
                    list.Add(new FactorSpec(dto.FeatureName, factor, dto.Parameters));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fattore '{Factor}' del Champion '{Name}' non ricostruibile: non sorvegliato.",
                        dto.FactorName, model.Name);
                }
            }
        }
        return bySeries;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = options.CurrentValue.EffectiveIntervalHours();
        logger.LogInformation("FactorDriftWorker avviato (ogni {Hours}h, Enabled={Enabled}, {MaxSeries} serie/giro).",
            hours, options.CurrentValue.Enabled, options.CurrentValue.EffectiveMaxSeries());

        // IDRATAZIONE: prima di qualunque calcolo, la fotografia si ricostruisce da ciò che è già
        // registrato. È il pezzo che rende l'alert in Home presente SUBITO dopo un riavvio del
        // guscio, invece di comparire soltanto dopo il primo giro (cioè dopo i 2 minuti di ritardo
        // qui sotto più il tempo di macinare gli IC).
        await HydrateAsync(stoppingToken);

        // Ritardo iniziale: all'avvio l'app ha di meglio da fare che macinare IC.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        do
        {
            try
            {
                // Riletto a ogni tick: il toggle prende effetto a caldo, come negli altri worker.
                if (options.CurrentValue.Enabled)
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Calcolo della deriva dei fattori fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Ricostruisce la fotografia dalla storia registrata. Non fallisce mai in modo rumoroso: se il
    /// DB non è raggiungibile si riparte a vuoto, esattamente come prima che la tabella esistesse —
    /// un monitor advisory non deve poter impedire l'avvio dell'app.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default) => await TryHydrateAsync(ct);

    /// <summary>Come <see cref="HydrateAsync"/>, ma dice se la fotografia è stata effettivamente rimpiazzata.</summary>
    private async Task<bool> TryHydrateAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await history.LoadSnapshotsAsync(new FactorDriftConfig { ForwardHorizon = ForwardHorizon }, ct);
            if (all.Count == 0) return false;

            // Solo le serie ANCORA in watchlist. La storia di una serie rimossa resta in tabella (è
            // un'osservazione vera, e cancellarla sarebbe perdere il passato), ma non deve tornare a
            // farsi vedere in Home dopo un riavvio: sarebbe un allarme su qualcosa che non si segue
            // più, cioè rumore in un pannello che deve restare leggibile.
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var tracked = (await db.TrackedSeries
                    .Where(s => s.Enabled)
                    .Select(s => new { s.Symbol, s.Timeframe })
                    .ToListAsync(ct))
                .Select(s => $"{s.Symbol}|{s.Timeframe}")
                .ToHashSet(StringComparer.Ordinal);

            var snapshots = all.Where(s => tracked.Contains($"{s.Symbol}|{s.Timeframe}")).ToList();
            if (snapshots.Count == 0) return false;

            snapshot.Replace(snapshots, snapshots.Max(s => s.ComputedAtUtc), tracked.Count);
            logger.LogInformation(
                "Deriva fattori: fotografia ricostruita dalla storia registrata ({Series} serie su {Tracked} in watchlist, {Alerts} in allarme, ultimo calcolo {At:u}).",
                snapshots.Count, tracked.Count, snapshots.Sum(s => s.Alerts.Count), snapshots.Max(s => s.ComputedAtUtc));
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storia della deriva non leggibile all'avvio: si riparte dalla fotografia vuota.");
        }
        return false;
    }

    /// <summary>
    /// Ampiezza della finestra per una serie. **Delega alla regola condivisa**
    /// <see cref="FactorDriftAnalyzer.SuggestWindowSize"/>, che è anche quella del pannello di
    /// <c>/feature-selection</c>: due regole diverse producevano soglie diverse e quindi verdetti
    /// diversi sulla stessa serie (vedi il commento su quella funzione).
    /// </summary>
    internal static int WindowSizeFor(int candleCount) => FactorDriftAnalyzer.SuggestWindowSize(candleCount);

    /// <summary>
    /// Quando la serie è stata "toccata" l'ultima volta: il più recente fra l'ultimo calcolo riuscito
    /// (dalla tabella) e l'ultimo tentativo di questo processo (in memoria).
    /// </summary>
    private DateTime LastTouched(IReadOnlyDictionary<string, DateTime> lastComputed, string symbol, string timeframe)
    {
        var key = $"{symbol}|{timeframe}";
        var computed = lastComputed.TryGetValue(key, out var c) ? c : DateTime.MinValue;
        var attempted = _lastAttemptUtc.TryGetValue(key, out var a) ? a : DateTime.MinValue;
        return computed > attempted ? computed : attempted;
    }

    /// <summary>Un giro completo. Pubblico per poterlo esercitare nei test senza aspettare il timer.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var maxSeries = options.CurrentValue.EffectiveMaxSeries();
        var maxCandles = options.CurrentValue.EffectiveMaxCandles();

        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var enabled = await db.TrackedSeries
            .Where(s => s.Enabled)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Symbol, s.Timeframe })
            .ToListAsync(ct);

        if (enabled.Count == 0)
        {
            snapshot.Replace([], DateTime.UtcNow);
            return;
        }

        // ROTAZIONE. Prima si prendevano sempre le prime N per Id: con una watchlist da centinaia di
        // serie il monitor guardava per sempre lo stesso 2% e le altre non venivano viste MAI — un
        // "nessun allarme" che in realtà voleva dire "non ho guardato". Ora l'ordine è per ultimo
        // calcolo (le più vecchie prima, quelle mai viste per prime in assoluto): a regime la
        // copertura è completa in ⌈serie / MaxSeries⌉ giri, a costo costante per giro.
        var lastComputed = await history.LoadLastComputedAsync(ct);
        var series = enabled
            .OrderBy(s => LastTouched(lastComputed, s.Symbol, s.Timeframe))
            .ThenBy(s => s.Symbol, StringComparer.Ordinal)
            .ThenBy(s => s.Timeframe, StringComparer.Ordinal)
            .Take(maxSeries)
            .ToList();

        var baseSpecs = MonitoredFactors.Select(name =>
        {
            var factor = factorFactory.Create(name);
            return new FactorSpec(name, factor, factor.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default));
        }).ToList();

        // [2.9] I fattori dei Champion, per serie: si sommano alla base scritta a mano.
        var championSpecs = await ChampionSpecsAsync(db, ct);

        var results = new List<FactorDriftSeriesSnapshot>(series.Count);
        foreach (var s in series)
        {
            ct.ThrowIfCancellationRequested();

            // Unione per FeatureName: se il Champion usa un fattore già nella base (stesso nome e
            // parametri di default) non lo si calcola due volte.
            var specs = baseSpecs;
            if (championSpecs.TryGetValue($"{s.Symbol}|{s.Timeframe}", out var extra))
            {
                var known = baseSpecs.Select(sp => sp.FeatureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = extra.Where(sp => known.Add(sp.FeatureName)).ToList();
                if (added.Count > 0)
                {
                    specs = [.. baseSpecs, .. added];
                    logger.LogInformation("Deriva fattori: {Symbol} {Tf} sorveglia anche {Count} fattori del Champion ({Names}).",
                        s.Symbol, s.Timeframe, added.Count, string.Join(", ", added.Select(a => a.FeatureName)));
                }
            }

            // Il tentativo conta comunque per la rotazione, anche se la serie verrà saltata subito
            // dopo per mancanza di candele: altrimenti resterebbe in cima alla coda per sempre.
            _lastAttemptUtc[$"{s.Symbol}|{s.Timeframe}"] = DateTime.UtcNow;

            // Le candele più RECENTI: la deriva è una domanda sul presente, e caricare tutto lo
            // storico di ogni serie a ogni giro sarebbe sproporzionato.
            var candles = await db.OhlcvData
                .Where(c => c.Symbol == s.Symbol && c.Timeframe == s.Timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(maxCandles)
                .ToListAsync(ct);
            if (candles.Count < 500) continue;
            candles.Reverse(); // l'analizzatore pretende ordine cronologico

            var config = new FactorDriftConfig
            {
                ForwardHorizon = ForwardHorizon,
                WindowSize = WindowSizeFor(candles.Count),
            };

            var reports = analyzer.AnalyzeMany(specs, candles, config);
            var computedAt = DateTime.UtcNow;
            results.Add(new FactorDriftSeriesSnapshot(s.Symbol, s.Timeframe, computedAt, reports));

            // La storia si registra qui, serie per serie: se il giro viene interrotto a metà, ciò che
            // è stato calcolato resta. La scrittura è un upsert sulle finestre, quindi rilanciare il
            // worker non duplica nulla.
            try
            {
                var inserted = await history.SaveAsync(s.Symbol, s.Timeframe, ForwardHorizon, reports, computedAt, ct);
                if (inserted > 0)
                {
                    logger.LogInformation("Deriva fattori: {Rows} finestre nuove registrate per {Symbol} {Tf}.",
                        inserted, s.Symbol, s.Timeframe);
                }
            }
            catch (Exception ex)
            {
                // La fotografia in memoria vale anche senza scrittura: un errore di DB degrada la
                // storia, non il monitor.
                logger.LogWarning(ex, "Storia della deriva non scritta per {Symbol} {Tf}.", s.Symbol, s.Timeframe);
            }
        }

        // La fotografia si ricostruisce dalla TABELLA, non dai soli risultati di questo giro: con la
        // rotazione ogni giro tocca serie diverse, e sostituire la fotografia con l'ultimo gruppo
        // farebbe sparire dalla Home gli allarmi delle serie viste il giro prima. Se l'idratazione
        // non è possibile (DB irraggiungibile) si ripiega su ciò che si è appena calcolato.
        var hydrated = await TryHydrateAsync(ct);
        if (!hydrated) snapshot.Replace(results, DateTime.UtcNow, enabled.Count);

        var visible = snapshot.All.Count;
        var alerts = snapshot.Alerts.Count;
        if (alerts > 0)
        {
            logger.LogWarning(
                "Deriva fattori: {Alerts} fattori in allarme su {Visible} serie note ({Round} calcolate in questo giro, {Enabled} in watchlist).",
                alerts, visible, results.Count, enabled.Count);
        }
        else
        {
            logger.LogInformation(
                "Deriva fattori: nessun allarme su {Visible} serie note ({Round} calcolate in questo giro, {Enabled} in watchlist).",
                visible, results.Count, enabled.Count);
        }
    }
}
