using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    public IReadOnlyCollection<FactorDriftSeriesSnapshot> All => _bySeries.Values.ToList();

    /// <summary>Tutti i fattori in allarme, su tutte le serie, i più gravi per primi.</summary>
    public IReadOnlyList<(FactorDriftSeriesSnapshot Series, FactorDriftReport Report)> Alerts =>
        _bySeries.Values
            .SelectMany(s => s.Alerts.Select(r => (Series: s, Report: r)))
            .OrderByDescending(x => (int)x.Report.Status)
            .ThenByDescending(x => Math.Abs(x.Report.ReferenceIc - x.Report.RecentIc))
            .ToList();

    public void Replace(IEnumerable<FactorDriftSeriesSnapshot> snapshots, DateTime computedAtUtc)
    {
        _bySeries = snapshots.ToDictionary(s => $"{s.Symbol}|{s.Timeframe}");
        LastRunUtc = computedAtUtc;
    }
}

/// <summary>
/// Calcola periodicamente la deriva dei fattori sulle serie della watchlist e aggiorna
/// <see cref="FactorDriftSnapshot"/>. Config: <c>FactorDrift:Enabled</c> (default true, è
/// sola lettura e advisory), <c>FactorDrift:IntervalHours</c> (default 12),
/// <c>FactorDrift:MaxSeries</c> (default 5), <c>FactorDrift:MaxCandles</c> (default 20000).
/// </summary>
public sealed class FactorDriftWorker(
    IServiceScopeFactory scopeFactory,
    IFactorDriftAnalyzer analyzer,
    IAlphaFactorFactory factorFactory,
    IFactorIcHistoryStore history,
    FactorDriftSnapshot snapshot,
    IConfiguration configuration,
    ILogger<FactorDriftWorker> logger) : BackgroundService
{
    /// <summary>Orizzonte forward del monitor periodico: una barra, come il pannello.</summary>
    private const int ForwardHorizon = 1;

    /// <summary>
    /// Solo i fattori scritti a mano, non il catalogo Alpha158: 158 fattori × N serie × finestre
    /// rolling trasformerebbero un monitor in un consumo di CPU permanente. Chi vuole guardare
    /// l'intero catalogo lo fa su richiesta da /feature-selection.
    /// </summary>
    private static readonly string[] MonitoredFactors =
    [
        "Momentum", "MeanReversion", "RealizedVol", "ParkinsonVol", "RelativeVolume",
        "RsiFactor", "MacdFactor", "DistanceFromMa",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Max(1, configuration.GetValue("FactorDrift:IntervalHours", 12));
        logger.LogInformation("FactorDriftWorker avviato (ogni {Hours}h, Enabled={Enabled}).",
            hours, configuration.GetValue("FactorDrift:Enabled", true));

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
                if (configuration.GetValue("FactorDrift:Enabled", true))
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
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await history.LoadSnapshotsAsync(new FactorDriftConfig { ForwardHorizon = ForwardHorizon }, ct);
            if (all.Count == 0) return;

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
            if (snapshots.Count == 0) return;

            snapshot.Replace(snapshots, snapshots.Max(s => s.ComputedAtUtc));
            logger.LogInformation(
                "Deriva fattori: fotografia ricostruita dalla storia registrata ({Series} serie, {Alerts} in allarme, ultimo calcolo {At:u}).",
                snapshots.Count, snapshots.Sum(s => s.Alerts.Count), snapshots.Max(s => s.ComputedAtUtc));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storia della deriva non leggibile all'avvio: si riparte dalla fotografia vuota.");
        }
    }

    /// <summary>
    /// Ampiezza della finestra per una serie, QUANTIZZATA a passi di 250 osservazioni.
    ///
    /// La regola di fondo è quella del pannello (~10 finestre sui dati disponibili, così il pavimento
    /// di rumore 1,96/√n resta basso abbastanza da rendere il verdetto utile). La quantizzazione è il
    /// pezzo che serve alla PERSISTENZA: senza, ogni candela nuova cambierebbe di poco l'ampiezza, e
    /// una serie storica la cui finestra si sposta a ogni giro non è una serie storica — sarebbe una
    /// collezione di misure incomparabili, ognuna con un pavimento di rumore diverso.
    /// </summary>
    internal static int WindowSizeFor(int candleCount)
    {
        var target = candleCount / 10;
        var quantized = (int)Math.Round(target / 250.0, MidpointRounding.AwayFromZero) * 250;
        return Math.Clamp(quantized, 250, 3000);
    }

    /// <summary>Un giro completo. Pubblico per poterlo esercitare nei test senza aspettare il timer.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var maxSeries = Math.Clamp(configuration.GetValue("FactorDrift:MaxSeries", 5), 1, 30);
        var maxCandles = Math.Clamp(configuration.GetValue("FactorDrift:MaxCandles", 20_000), 1_000, 200_000);

        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var series = await db.TrackedSeries
            .Where(s => s.Enabled)
            .OrderBy(s => s.Id)
            .Take(maxSeries)
            .Select(s => new { s.Symbol, s.Timeframe })
            .ToListAsync(ct);

        if (series.Count == 0)
        {
            snapshot.Replace([], DateTime.UtcNow);
            return;
        }

        var specs = MonitoredFactors.Select(name =>
        {
            var factor = factorFactory.Create(name);
            return new FactorSpec(name, factor, factor.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default));
        }).ToList();

        var results = new List<FactorDriftSeriesSnapshot>(series.Count);
        foreach (var s in series)
        {
            ct.ThrowIfCancellationRequested();

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

        snapshot.Replace(results, DateTime.UtcNow);

        var alerts = results.Sum(r => r.Alerts.Count);
        if (alerts > 0)
        {
            logger.LogWarning("Deriva fattori: {Alerts} fattori in allarme su {Series} serie monitorate.",
                alerts, results.Count);
        }
        else
        {
            logger.LogInformation("Deriva fattori: nessun allarme su {Series} serie monitorate.", results.Count);
        }
    }
}
