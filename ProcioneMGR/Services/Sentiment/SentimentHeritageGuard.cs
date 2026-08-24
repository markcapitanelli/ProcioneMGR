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
/// <param name="NewestUtc">
/// Punto più recente trovato (null = serie assente). Serve alle serie che si possono solo
/// ACCUMULARE: per loro la domanda utile non è «quanto indietro arriva» ma «sta ancora arrivando».
/// </param>
/// <param name="Accumulating">
/// [2026-08-24] La serie esiste solo al PRESENTE: non c'è backfill possibile, quindi giudicarla
/// contro una data assoluta già passata la condanna per aritmetica. Vedi <c>EvaluateAccumulating</c>.
/// </param>
public sealed record HeritageSeriesDepth(
    string Key,
    string DisplayName,
    DateTime? OldestUtc,
    long Count,
    string Expected,
    string? Problem,
    bool Enforced = true,
    DateTime? NewestUtc = null,
    bool Accumulating = false)
{
    public bool Violated => Problem is not null;

    /// <summary>
    /// La fonte non ha MAI consegnato un punto. È un fatto diverso da «si è accorciata», e va
    /// detto diverso: la seconda è una perdita di patrimonio (una storia che c'era e non c'è più),
    /// la prima è una fonte che non ha mai funzionato. Fino al 2026-08-24 la Home le impastava
    /// nella stessa frase — «serie ASSENTE» — e allegava la conseguenza della prima («carry e
    /// backtest a leva leggono queste serie») anche quando valeva solo per la seconda.
    ///
    /// <para><b>Serve <see cref="Accumulating"/>, non basta il conteggio a zero.</b> Il funding a
    /// zero righe NON è «mai partito»: è la perdita più grave che questo guardiano esista per
    /// vedere — sette anni di storia spariti due volte, e <i>ricostruibili</i> con
    /// <c>fundingbackfill</c>. Solo per una fonte che si può unicamente accumulare lo zero
    /// significa davvero «non ha mai consegnato nulla».</para>
    /// </summary>
    public bool NeverStarted => Accumulating && Count == 0;
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

    /// <summary>
    /// [2026-08-24] Le serie che AVEVANO una storia e l'hanno persa. Sono queste — e solo queste —
    /// quelle di cui è vero dire che «carry e backtest a leva stanno leggendo una serie corta».
    /// </summary>
    public IReadOnlyList<HeritageSeriesDepth> Shortened =>
        _all.Where(d => d.Violated && !d.NeverStarted).ToList();

    /// <summary>
    /// Le fonti che non hanno MAI consegnato un punto. Non è una perdita di patrimonio: è una fonte
    /// che non ha mai funzionato, e la sua riga deve nominare la CAUSA invece di mandare a cercare
    /// un incidente che non c'è.
    /// </summary>
    public IReadOnlyList<HeritageSeriesDepth> NeverStarted =>
        _all.Where(d => d.Violated && d.NeverStarted).ToList();

    /// <summary>Righe MISURATE ma non giudicate (interruttore di sorveglianza spento).</summary>
    public int NotEnforcedCount => _all.Count(d => !d.Enforced);

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
    INotifier? notifier = null,
    // [2026-08-24] La causa del vuoto si LEGGE dal feed, non si asserisce a commento. Opzionale:
    // senza (vecchi harness di test) resta il messaggio generico, mai un OK finto.
    ProcioneMGR.Services.MarketData.ILiquidationFeedDiagnostics? liquidations = null) : BackgroundService
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
        var liquidationsNewest = await liquidationsSet.MaxAsync(p => (DateTime?)p.TimestampUtc, ct);
        var liquidationsCount = await liquidationsSet.LongCountAsync(ct);
        depths.Add(EvaluateAccumulating(
            "Liquidations", "Liquidazioni Binance",
            liquidationsOldest, liquidationsNewest, liquidationsCount,
            opt.LiquidationsMinPoints, opt.LiquidationsStaleAfterHours,
            DescribeSilence(liquidations), opt.LiquidationsEnforced));

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
            //
            // [2026-08-24] Ma DUE sezioni, non una. Il corpo affermava «Carry e backtest a leva
            // stanno leggendo una serie corta» per qualunque riga violata: sulle liquidazioni è
            // falso — nessun componente le legge, il composite di sentiment somma solo metriche
            // nominate e le liquidazioni non ci sono. Una notifica Critical che dichiara un danno
            // inesistente è la forma peggiore del controllo che parla a prescindere dalla realtà,
            // perché arriva sul telefono e pretende attenzione.
            var accorciate = newlyViolated.Where(v => !v.NeverStarted).ToList();
            var maiNate = newlyViolated.Where(v => v.NeverStarted).ToList();

            var corpo = new System.Text.StringBuilder();
            if (accorciate.Count > 0)
            {
                corpo.AppendLine("STORIA PERSA (è già successo due volte col funding):");
                foreach (var v in accorciate) corpo.AppendLine($"• {v.DisplayName}: {v.Problem}");
                corpo.AppendLine("Carry e backtest a leva stanno leggendo una serie corta.");
                corpo.AppendLine("Ripristino funding: dotnet run --project tools/PlatformExpand -- fundingbackfill");
            }
            if (maiNate.Count > 0)
            {
                if (accorciate.Count > 0) corpo.AppendLine();
                corpo.AppendLine("FONTI CHE NON HANNO MAI CONSEGNATO NULLA:");
                foreach (var v in maiNate) corpo.AppendLine($"• {v.DisplayName}: {v.Problem}");
                corpo.AppendLine("Non è una perdita di patrimonio e nessun calcolo in corso ne è alterato: è una fonte da collegare.");
            }
            corpo.Append("Dettaglio e soglie in /sentiment.");

            var titolo = accorciate.Count > 0
                ? $"{accorciate.Count} serie-patrimonio si sono ACCORCIATE"
                : $"{maiNate.Count} fonti-patrimonio non hanno mai consegnato dati";
            await notifier.NotifyAsync(
                accorciate.Count > 0 ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                titolo, corpo.ToString(), ct);
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
    /// [2026-08-24] Il giudizio per una serie che si può solo ACCUMULARE: liquidazioni oggi, e
    /// qualunque altro feed di eventi senza backfill domani.
    ///
    /// <para><b>Perché non è <see cref="Evaluate"/> con un'altra soglia.</b> Quella confronta il
    /// punto più vecchio con una data assoluta, ed è la regola giusta per il funding — che una
    /// storia ce l'ha e la si può riscaricare (<c>fundingbackfill</c>). Su un feed che esiste solo
    /// al presente la stessa regola è <b>inesigibile</b>: qualunque data già passata condanna la
    /// serie per sempre, perché il primo punto porterà sempre la data del giorno in cui il feed è
    /// ripartito. Un allarme che non può rientrare smette di essere letto, e si porta dietro anche
    /// quelli veri.</para>
    ///
    /// <para>Qui si giudicano le due cose che una serie di accumulo può davvero rispettare — <b>ha
    /// abbastanza punti</b> e <b>ne sta ancora ricevendo</b> — e ognuna delle tre uscite è
    /// rientrabile: il vuoto rientra quando arriva il primo punto, il «appena partito» quando i
    /// punti bastano, il «fermo» quando il feed riprende.</para>
    /// </summary>
    /// <param name="silenceCause">
    /// Perché la serie è vuota, letto dallo stato reale del feed. È ciò che distingue «non ha mai
    /// consegnato niente perché lo stream è muto da questa postazione» da «qualcuno ha cancellato
    /// la tabella»: due frasi che mandano a fare cose opposte.
    /// </param>
    internal static HeritageSeriesDepth EvaluateAccumulating(
        string key, string displayName,
        DateTime? oldestUtc, DateTime? newestUtc, long count,
        int minCount, int staleAfterHours, string silenceCause, bool enforced)
    {
        var ore = Math.Max(1, staleAfterHours);
        var expected = $"accumulo vivo: ≥ {minCount:N0} punti e un punto nuovo entro {ore} ore";

        string? problem = null;
        if (count == 0)
        {
            problem = silenceCause;
        }
        else if (count < minCount)
        {
            // Transitorio e DICHIARATO tale: senza, i primi minuti di un accumulo sano
            // sembrerebbero un guasto identico al vuoto perpetuo.
            problem = $"accumulo appena partito: {count:N0} punti sui {minCount:N0} attesi (dal {oldestUtc:yyyy-MM-dd HH:mm})";
        }
        else if (newestUtc is DateTime newest && (DateTime.UtcNow - newest).TotalHours > ore)
        {
            var eta = (DateTime.UtcNow - newest).TotalHours;
            problem = $"accumulo FERMO da {eta:0} ore (ultimo punto {newest:yyyy-MM-dd HH:mm} UTC, soglia {ore} ore)";
        }

        return new HeritageSeriesDepth(
            key, displayName, oldestUtc, count, expected,
            enforced ? problem : null,
            Enforced: enforced,
            NewestUtc: newestUtc,
            Accumulating: true);
    }

    /// <summary>
    /// Perché l'accumulo non ha prodotto punti, LETTO dallo stato del feed invece che asserito.
    ///
    /// <para>Quattro stati che mandano a fare quattro cose diverse — accendere l'interruttore,
    /// rassegnarsi al blocco (o cambiare venue), aspettare il primo flush, guardare la rete — e
    /// prima erano una frase sola, per giunta identica a quella della perdita di patrimonio del
    /// funding: «serie ASSENTE: nessun punto in SentimentMetricPoints». Chi la leggeva andava a
    /// cercare un incidente che non c'era.</para>
    ///
    /// <para>Statica e pura: si prova senza database, senza rete e senza worker.</para>
    /// </summary>
    internal static string DescribeSilence(ProcioneMGR.Services.MarketData.ILiquidationFeedDiagnostics? feed)
    {
        if (feed is null)
        {
            return "accumulo mai partito: nessun punto in SentimentMetricPoints (stato del feed non interrogabile da qui)";
        }
        if (!feed.Enabled)
        {
            return "accumulo mai partito: l'interruttore Liquidations:Enabled è SPENTO — nessuna connessione viene aperta";
        }
        if (feed.EndpointLikelyBlocked)
        {
            return "accumulo mai partito: lo stream futures Binance si connette ma non consegna alcun frame da questa "
                 + "postazione (blocco EEA sulla famiglia WebSocket dei derivati; il REST futures invece risponde, "
                 + "ed è la ragione per cui il funding continua ad arrivare). Il dato non è recuperabile a posteriori: "
                 + "i due endpoint REST di liquidazione sono stati ritirati e il dump storico USDS-M non è mai esistito";
        }
        if (feed.IsConnected)
        {
            return $"accumulo connesso ma ancora senza punti ({feed.TotalMessages:N0} messaggi ricevuti): "
                 + "il primo flush arriva entro pochi minuti";
        }
        return "accumulo mai partito: il feed non è connesso in questo momento";
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
