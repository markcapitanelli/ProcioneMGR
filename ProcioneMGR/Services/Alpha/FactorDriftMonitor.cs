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
//  PERCHÉ IN MEMORIA E NON SU TABELLA. Il PRD ipotizzava una tabella o il riuso di ExperimentRuns.
//  Qui si tiene una sola fotografia in memoria, aggiornata dal worker, per una ragione precisa:
//  l'IC storico è una funzione DETERMINISTICA delle candele, quindi persisterlo sarebbe una cache,
//  non un'osservazione — e costerebbe una migrazione sul DB reale. Ciò che serviva davvero non era
//  la storia, ma che il calcolo avvenisse SENZA che l'utente lo chieda: quello lo dà il job. Al
//  riavvio la fotografia riparte vuota e si ricostruisce al primo giro; è un costo accettabile per
//  non aggiungere schema.
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
    FactorDriftSnapshot snapshot,
    IConfiguration configuration,
    ILogger<FactorDriftWorker> logger) : BackgroundService
{
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
                ForwardHorizon = 1,
                // Stessa regola della UI: ~10 finestre sui dati disponibili, così il pavimento di
                // rumore resta basso abbastanza da rendere il verdetto utile.
                WindowSize = Math.Clamp(candles.Count / 10, 250, 3000),
            };

            var reports = analyzer.AnalyzeMany(specs, candles, config);
            results.Add(new FactorDriftSeriesSnapshot(s.Symbol, s.Timeframe, DateTime.UtcNow, reports));
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
