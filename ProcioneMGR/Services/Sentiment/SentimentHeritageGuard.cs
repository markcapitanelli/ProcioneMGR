using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// La profondità misurata di UNA serie-patrimonio, con la soglia dichiarata accanto: la UI deve
/// poter mostrare atteso e trovato fianco a fianco, non un semaforo senza numeri.
/// </summary>
/// <param name="Key">Chiave stabile per il riarmo delle notifiche (es. "Funding:BTC").</param>
/// <param name="DisplayName">Nome leggibile (es. "Funding BTC").</param>
/// <param name="OldestUtc">Punto più vecchio trovato (null = serie assente).</param>
/// <param name="Count">Punti trovati.</param>
/// <param name="Expected">La soglia dichiarata, in parole (es. "storia da ≤ 2020-10-01, ≥ 5.000 punti").</param>
/// <param name="Problem">Perché la soglia è violata; null = serie sana (o non sorvegliata).</param>
/// <param name="Enforced">False = misurata ma NON giudicata (interruttore spento con cognizione,
/// es. liquidazioni da postazione EEA bloccata): la UI la mostra come «non sorvegliata», mai OK.</param>
public sealed record HeritageSeriesDepth(
    string Key,
    string DisplayName,
    DateTime? OldestUtc,
    long Count,
    string Expected,
    string? Problem,
    bool Enforced = true)
{
    public bool Violated => Problem is not null;
}

/// <summary>
/// Ultima fotografia nota della profondità delle serie-patrimonio. Singleton: scritta dal worker,
/// letta da Home e /sentiment senza I/O al rendering (pattern <c>FactorDriftSnapshot</c>).
/// Thread-safe per sostituzione atomica della lista. <see cref="LastRunUtc"/> è la regola
/// «degradare dicendolo»: la UI dichiara sempre QUANDO il verdetto è stato calcolato.
/// </summary>
public sealed class SentimentHeritageSnapshot
{
    private volatile IReadOnlyList<HeritageSeriesDepth> _all = [];

    /// <summary>Ultimo giro completato del guardiano (null = mai da questo avvio).</summary>
    public DateTime? LastRunUtc { get; private set; }

    public IReadOnlyList<HeritageSeriesDepth> All => _all;

    /// <summary>Le serie in violazione (le assenti per prime: sono la perdita più grave).</summary>
    public IReadOnlyList<HeritageSeriesDepth> Violations =>
        _all.Where(d => d.Violated).OrderBy(d => d.OldestUtc is null ? 0 : 1).ToList();

    public void Replace(IReadOnlyList<HeritageSeriesDepth> depths, DateTime computedAtUtc)
    {
        _all = depths;
        LastRunUtc = computedAtUtc;
    }
}

/// <summary>
/// Guardiano di PROFONDITÀ delle serie-patrimonio — le QUATTRO esenti dalla purge di
/// <see cref="SentimentSyncWorker"/>: funding (Source=BinanceFutures, Metric=FundingRate),
/// Fear &amp; Greed e liquidazioni in <c>SentimentMetricPoints</c>, e dal 2026-08-19 [I15] le
/// notizie CON PUNTEGGIO in <c>AltDataPoints</c> — che sono un'altra tabella, un altro criterio
/// (<see cref="NewsCorpus"/>) e quindi una query dedicata, non un altro giro sullo stesso DbSet.
///
/// <para>Perché esiste: la storia del funding dal 2019 è andata persa DUE volte in silenzio
/// (2026-07-24 costruendo F4, 2026-08-11 alla rimisura del carry) nonostante l'esenzione dalla
/// purge fosse al suo posto — carry e backtest a leva leggevano ~14 mesi credendoli 7 anni.
/// L'esenzione protegge dal worker; questo guardiano misura che la storia CI SIA, qualunque sia
/// stata la via della perdita (drop della tabella, re-backfill parziale, restore sbagliato).</para>
///
/// <para>Su violazione: log <b>Error</b> a ogni giro (finché dura, deve restare rumorosa nei log)
/// e UNA notifica aggregata per transizione (pattern <see cref="Ingestion.SeriesFreshnessWatchWorker"/>:
/// un guasto nuovo suona una volta, non una volta per giro). Il rientro riarma in silenzio (log
/// Information). Nessuna azione automatica: il ripristino — <c>fundingbackfill</c> — resta umano.</para>
///
/// <para>Vive nel guscio e legge solo aggregati indicizzati (min/count per Source+Metric+Symbol):
/// costo trascurabile a cadenza di ore. <c>Enabled</c> è per-tick (hot da /admin/autonomy);
/// l'intervallo si legge al boot. Fail-open sulla diagnostica: un giro fallito si logga e si
/// ritenta al successivo.</para>
/// </summary>
public sealed class SentimentHeritageGuardWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<SentimentOptions> options,
    SentimentHeritageSnapshot snapshot,
    ILogger<SentimentHeritageGuardWorker> logger,
    INotifier? notifier = null) : BackgroundService
{
    /// <summary>Serie già segnalate come violate (per Key): l'allarme è sulla transizione.</summary>
    private readonly HashSet<string> _alerted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.HeritageGuard.CheckIntervalHours));
        logger.LogInformation("SentimentHeritageGuardWorker avviato (controllo ogni {Interval}, Enabled={Enabled}).",
            interval, options.CurrentValue.HeritageGuard.Enabled);

        // Attesa iniziale breve: lascia finire migrate-on-startup, ma il primo verdetto deve
        // arrivare presto — dopo un riavvio la Home resterebbe muta proprio quando serve.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (options.CurrentValue.HeritageGuard.Enabled)
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Controllo profondità serie-patrimonio fallito; ritento al prossimo giro."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("SentimentHeritageGuardWorker fermato.");
    }

    /// <summary>
    /// Un giro di controllo: misura tutte le serie-patrimonio, aggiorna la fotografia, logga Error
    /// sulle violazioni correnti e notifica quelle NUOVE. Pubblico per i test e per «Controlla ora»
    /// da /sentiment (che di proposito NON passa dal gate Enabled: un'azione umana esplicita).
    /// Restituisce le serie appena DIVENTATE violate in questo giro.
    /// </summary>
    public async Task<IReadOnlyList<HeritageSeriesDepth>> RunOnceAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue.HeritageGuard;
        var depths = new List<HeritageSeriesDepth>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // --- Funding: profondità PER SIMBOLO (il backfill è per mercato, e una perdita parziale
        //     — un simbolo ripristinato e cinque no — deve nominare chi manca). ---
        var fundingSymbols = opt.EffectiveFundingSymbols;
        var fundingStats = await db.SentimentMetricPoints.AsNoTracking()
            .Where(p => p.Source == SentimentMetricSources.BinanceFutures
                        && p.Metric == SentimentMetrics.FundingRate
                        && fundingSymbols.Contains(p.Symbol))
            .GroupBy(p => p.Symbol)
            .Select(g => new { Symbol = g.Key, Oldest = g.Min(p => p.TimestampUtc), Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Symbol, ct);

        foreach (var symbol in fundingSymbols)
        {
            var found = fundingStats.GetValueOrDefault(symbol);
            depths.Add(Evaluate($"Funding:{symbol}", $"Funding {symbol}",
                found?.Oldest, found?.Count ?? 0, opt.FundingMinStartUtc, opt.FundingMinEventsPerSymbol));
        }

        // --- Fear & Greed: serie market-wide (Symbol = ""), si giudica la fonte intera. ---
        var fearGreedSet = db.SentimentMetricPoints.AsNoTracking()
            .Where(p => p.Source == SentimentMetricSources.FearGreed);
        depths.Add(Evaluate("FearGreed", "Fear & Greed",
            await fearGreedSet.MinAsync(p => (DateTime?)p.TimestampUtc, ct),
            await fearGreedSet.LongCountAsync(ct),
            opt.FearGreedMinStartUtc, opt.FearGreedMinPoints));

        // --- Liquidazioni: l'accumulo F4 non è ricostruibile — l'àncora è la data a cui deve
        //     arrivare, qualunque simbolo/metrica (la perdita tipica è dell'intera tabella).
        //     Con l'interruttore spento si MISURA comunque, ma senza giudizio: dalle postazioni
        //     EEA lo stream è muto (MiCA) e l'allarme sarebbe perpetuo. ---
        var liquidationsSet = db.SentimentMetricPoints.AsNoTracking()
            .Where(p => p.Source == SentimentMetricSources.BinanceLiquidations);
        var liquidationsOldest = await liquidationsSet.MinAsync(p => (DateTime?)p.TimestampUtc, ct);
        var liquidationsCount = await liquidationsSet.LongCountAsync(ct);
        depths.Add(opt.LiquidationsEnforced
            ? Evaluate("Liquidations", "Liquidazioni Binance",
                liquidationsOldest, liquidationsCount, opt.LiquidationsMinStartUtc, opt.LiquidationsMinPoints)
            : new HeritageSeriesDepth("Liquidations", "Liquidazioni Binance",
                liquidationsOldest, liquidationsCount,
                FormatExpected(opt.LiquidationsMinStartUtc, opt.LiquidationsMinPoints),
                null, Enforced: false));

        // --- [I15] Notizie con punteggio: patrimonio dal 2026-08-19 (decisione del proprietario).
        //     Altra TABELLA (AltDataPoints, non SentimentMetricPoints) e altro criterio: conta solo
        //     la notizia già valutata da uno scorer, con lo STESSO predicato che la purge usa per
        //     risparmiarla — sorvegliare un insieme diverso da quello protetto direbbe «tutto a
        //     posto» di righe che il worker sta cancellando.
        //     Come le liquidazioni, si MISURA anche da spenta: alla nascita l'interruttore è OFF
        //     perché la purge ha girato da sempre, quindi qualunque àncora plausibile scatterebbe
        //     al primo giro — e un allarme perpetuo smette di essere letto. Prima si misura il min
        //     vero, poi si sceglie l'àncora, poi si accende (è la storia dell'àncora del funding,
        //     spostata da gennaio a ottobre 2020 DOPO la misura sul database vero).
        var newsSet = db.AltDataPoints.AsNoTracking().Where(NewsCorpus.Scored);
        var newsOldest = await newsSet.MinAsync(a => (DateTime?)a.TimestampUtc, ct);
        var newsCount = await newsSet.LongCountAsync(ct);
        const string newsEmpty = "corpus ASSENTE: nessuna notizia con punteggio in AltDataPoints";
        depths.Add(opt.NewsEnforced
            ? Evaluate("News", "Notizie con punteggio",
                newsOldest, newsCount, opt.NewsMinStartUtc, opt.NewsMinPoints, newsEmpty)
            : new HeritageSeriesDepth("News", "Notizie con punteggio",
                newsOldest, newsCount,
                FormatExpected(opt.NewsMinStartUtc, opt.NewsMinPoints),
                null, Enforced: false));

        snapshot.Replace(depths, DateTime.UtcNow);

        var violated = depths.Where(d => d.Violated).ToList();
        var newlyViolated = new List<HeritageSeriesDepth>();
        foreach (var depth in depths)
        {
            if (!depth.Violated)
            {
                // Tornata sana (o sorveglianza spenta): riarmo silenzioso — il rientro di un
                // guasto già notificato è informazione, non allarme. Il riarmo vale anche per la
                // riga non sorvegliata: alla riaccensione dell'interruttore, se la violazione c'è
                // ancora, deve risuonare.
                if (_alerted.Remove(depth.Key) && depth.Enforced)
                {
                    logger.LogInformation("Serie-patrimonio {Series}: profondità RIENTRATA ({Count} punti da {Oldest:yyyy-MM-dd}).",
                        depth.DisplayName, depth.Count, depth.OldestUtc);
                }
                continue;
            }
            if (_alerted.Add(depth.Key)) newlyViolated.Add(depth);
        }

        if (violated.Count > 0)
        {
            // Error a OGNI giro finché la violazione dura: una serie-patrimonio corta è un guasto
            // attivo (carry e backtest leggono numeri sbagliati ADESSO), non un evento passato.
            logger.LogError("Serie-patrimonio sotto soglia ({Count}): {Details}",
                violated.Count, string.Join("; ", violated.Select(v => $"{v.DisplayName} — {v.Problem}")));
        }

        if (newlyViolated.Count > 0 && notifier is not null)
        {
            // UNA notifica aggregata per giro: una perdita della tabella colpisce 8 serie insieme
            // e deve produrre un messaggio, non otto.
            var elenco = string.Join("\n", newlyViolated.Select(v => $"• {v.DisplayName}: {v.Problem}"));
            await notifier.NotifyAsync(NotificationSeverity.Critical,
                $"{newlyViolated.Count} serie-patrimonio sotto la profondità attesa",
                "La storia esente dalla purge si è accorciata (è già successo due volte col funding):\n"
                + elenco
                + "\nCarry e backtest a leva stanno leggendo una serie corta. Dettaglio in /sentiment; "
                + "ripristino funding: dotnet run --project tools/PlatformExpand -- fundingbackfill", ct);
        }

        return newlyViolated;
    }

    /// <summary>
    /// Il confronto misurato-contro-dichiarato di una serie. Una serie ASSENTE è la violazione più
    /// grave, non un caso da saltare: null in un confronto di date si comporterebbe da «mai
    /// violato» (la trappola di B2.a sulla freschezza, stessa classe).
    /// </summary>
    private static HeritageSeriesDepth Evaluate(
        string key, string displayName, DateTime? oldestUtc, long count, DateTime minStartUtc, int minCount,
        string emptyProblem = "serie ASSENTE: nessun punto in SentimentMetricPoints")
    {
        var expected = FormatExpected(minStartUtc, minCount);

        string? problem = null;
        if (oldestUtc is null || count == 0)
        {
            // [I15] Il messaggio nomina la TABELLA, e le righe non stanno tutte nella stessa: il
            // corpus notizie vive in AltDataPoints. Un «nessun punto in SentimentMetricPoints» su
            // una serie che quella tabella non la tocca manderebbe a cercare nel posto sbagliato.
            problem = emptyProblem;
        }
        else
        {
            var problems = new List<string>(2);
            if (oldestUtc > minStartUtc)
            {
                problems.Add($"la storia parte dal {oldestUtc:yyyy-MM-dd} invece che da ≤ {minStartUtc:yyyy-MM-dd}: profondità persa");
            }
            if (count < minCount)
            {
                problems.Add($"solo {count:N0} punti (attesi ≥ {minCount:N0})");
            }
            if (problems.Count > 0) problem = string.Join("; ", problems);
        }

        return new HeritageSeriesDepth(key, displayName, oldestUtc, count, expected, problem);
    }

    /// <summary>
    /// [I15] La frase «che cosa ci si aspetta da questa serie», in UN SOLO posto.
    ///
    /// <para>Era scritta due volte: qui dentro <see cref="Evaluate"/> e a mano nel ramo delle serie
    /// NON sorvegliate. Con due righe spegnibili invece di una, al primo cambio di formato la
    /// pagina avrebbe mostrato due «attesi» diversi per due righe equivalenti — e nessun test
    /// l'avrebbe visto, perché entrambe le stringhe sarebbero state "giuste" ognuna per sé.</para>
    /// </summary>
    internal static string FormatExpected(DateTime minStartUtc, int minCount) =>
        $"storia da ≤ {minStartUtc:yyyy-MM-dd}, ≥ {minCount:N0} punti";

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
