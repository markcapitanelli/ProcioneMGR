using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// [I14c] Opzioni della sorveglianza dello spread (sezione <c>PairsWatch</c>).
/// </summary>
public sealed class PairsWatchOptions
{
    /// <summary>
    /// <b>Default SPENTO, e qui non è la solita prudenza.</b> Questo è l'unico worker introdotto
    /// dall'ondata di integrazione che <b>scrive in permanenza</b> sul Postgres condiviso con motore
    /// e ingestion: gli altri leggono, indicizzano su richiesta, o journalizzano decisioni rare.
    /// Accenderlo è una decisione sul carico, non solo sulla funzione.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Le coppie sorvegliate, <b>scelte a mano</b>: <c>"ETH/USDT|BTC/USDT 1h"</c>, la forma di
    /// <see cref="PairKey.Build"/>. Vuota = nessuna sorveglianza, anche da acceso.
    ///
    /// <para><b>Perché a mano e non alimentate da ciò che lo screening marca operabile.</b>
    /// L'alimentazione automatica sceglierebbe fra centinaia di test ADF per timeframe <b>senza
    /// correzione per test multipli</b>: al 5%, su 190 coppie ne «trova» una decina per puro rumore,
    /// e le sorveglierebbe come se fossero relazioni. Fabbricherebbe candidati per costruzione — il
    /// primo cugino dell'errore già pagato randomizzando su asset correlati, che fabbricava falsa
    /// significatività.</para>
    ///
    /// <para>[2026-08-25, decisione del proprietario] L'alimentazione automatica ESISTE, dietro
    /// <see cref="AutoWatch"/> — ma non è il top-N su un test singolo che questo commento temeva:
    /// il criterio è la <b>replicazione</b> (vedi lì). Le coppie manuali restano e hanno la
    /// precedenza sul tetto.</para>
    /// </summary>
    public List<string> Pairs { get; set; } = [];

    /// <summary>
    /// [2026-08-25, decisione del proprietario: «impostiamo anche le coppie in automatico»]
    /// Alimenta la sorveglianza dai candidati dell'indice (<c>PairCandidates</c>) che
    /// <b>REPLICANO</b>: una coppia entra solo se risulta operabile in almeno
    /// <see cref="AutoWatchMinScreens"/> screen DISTINTI che coprono almeno
    /// <see cref="AutoWatchMinSpanDays"/> giorni.
    ///
    /// <para><b>Perché la replicazione e non il top-N.</b> Un singolo passaggio ADF al 5% su ~190
    /// coppie ne «trova» una decina per puro rumore: sorvegliarle sarebbe fabbricare candidati (il
    /// commento su <see cref="Pairs"/> esiste per questo). Pretendere che la stessa coppia risulti
    /// operabile in K screen indipendenti distanti nel tempo è un'altra domanda: il rumore non
    /// replica su richiesta. È la stessa epistemologia del forward test come giudice — la
    /// ripetizione batte la significatività di un colpo solo.</para>
    /// </summary>
    public bool AutoWatch { get; set; }

    /// <summary>Screen distinti minimi in cui la coppia dev'essere risultata operabile. Sotto 2 il criterio degenererebbe nel test singolo.</summary>
    public int AutoWatchMinScreens { get; set; } = 3;

    /// <summary>Arco minimo (giorni) fra il primo e l'ultimo screen operabile: la replica a distanza di ore non è indipendenza.</summary>
    public int AutoWatchMinSpanDays { get; set; } = 14;

    /// <summary>Tetto sul TOTALE sorvegliato (manuali + automatiche). Le manuali non vengono mai sfrattate dal tetto.</summary>
    public int AutoWatchMaxPairs { get; set; } = 5;

    /// <summary>Cadenza in ore. 12 come il gemello sull'IC: lo spread di una coppia non cambia natura in un'ora.</summary>
    public int IntervalHours { get; set; } = 12;

    /// <summary>
    /// Ampiezza della finestra in candele. 250 è un compromesso dichiarato: sotto, l'ADF perde
    /// potenza e il pavimento di rumore sale; sopra, servono anni di storia per avere le cinque
    /// finestre non sovrapposte che il giudice pretende.
    /// </summary>
    public int WindowSize { get; set; } = 250;

    /// <summary>Candele lette per coppia a ogni giro. 5.000 a 250 di finestra = 20 finestre non sovrapposte.</summary>
    public int MaxCandles { get; set; } = 5000;

    /// <summary>Estimatore dell'hedge ratio: "Kalman" (default della piattaforma dal gate C2) o "RollingOls".</summary>
    public string Estimator { get; set; } = "Kalman";

    /// <summary>Frazione di finestre stazionarie sopra cui si parla di relazione persistente. Vedi <see cref="PairSpreadJudge"/>.</summary>
    public double PersistenceThreshold { get; set; } = 0.6;

    /// <summary>Quante finestre finali contano come «adesso» nel giudizio di rottura.</summary>
    public int RecentWindows { get; set; } = 3;
}

/// <summary>
/// [I14c] Calcola periodicamente lo spread delle coppie SORVEGLIATE e ne registra le finestre.
///
/// <para><b>Non decide nulla.</b> Non apre, non chiude, non tocca una corsia, non propone: scrive
/// righe che un pannello legge. È una scelta, non un incremento rimandato — l'esecuzione a due
/// gambe è un non-obiettivo dichiarato (le corsie sono mono-simbolo), e la classe pairs è misurata
/// a zero sopravvissuti su cinque.</para>
///
/// <para><b>Il carico, in numeri.</b> Per coppia e per giro legge <c>MaxCandles</c> candele (5.000)
/// e calcola <c>MaxCandles / WindowSize</c> finestre (20). Il PRIMO giro scrive quelle 20 righe; dal
/// secondo in poi l'upsert idempotente ne scrive <b>una sola</b> — quella nuova — e aggiorna le
/// altre. Con cinque coppie e un giro ogni 12 ore: 100 righe al primo giro, poi ~10 righe al giorno.
/// Su un anno, ~3.700 righe. È poco, ed è poco <i>perché</i> le coppie le sceglie una persona.</para>
///
/// <para><b>Difensivo per coppia</b>: una coppia senza candele, con chiave illeggibile o che fa
/// esplodere il calcolo esce con un log e non ferma le altre — un guasto su una serie non deve
/// spegnere la sorveglianza di tutte.</para>
/// </summary>
public sealed class PairSpreadWatchWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPairSpreadHistoryStore store,
    ICointegrationTest cointegration,
    IOptionsMonitor<PairsWatchOptions> options,
    ILogger<PairSpreadWatchWorker> logger) : BackgroundService
{
    public DateTime? LastRunUtc { get; private set; }

    /// <summary>Righe nuove scritte nell'ultimo giro: il numero con cui si giudica il carico vero.</summary>
    public int LastRowsWritten { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ritardo d'avvio: il guscio parte con migrazioni, idratazioni e la prima resa delle pagine.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled && (opt.Pairs.Count > 0 || opt.AutoWatch))
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "Giro della sorveglianza spread fallito; ritento al prossimo."); }
            }

            var delay = TimeSpan.FromHours(Math.Clamp(opt.IntervalHours, 1, 168));
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Le coppie AUTO scelte nell'ultimo giro, per il pannello: la selezione va vista, non dedotta.</summary>
    public IReadOnlyList<string> LastAutoSelected { get; private set; } = [];

    /// <summary>
    /// [2026-08-25] La lista sorvegliata del giro: le MANUALI sempre e per prime, poi le AUTO che
    /// replicano (vedi <see cref="SelectAutoPairs"/>), tutto sotto il tetto — che non sfratta mai
    /// una manuale. Un guasto nella lettura dell'indice non spegne la sorveglianza manuale:
    /// degrada alle sole manuali, dichiarandolo.
    /// </summary>
    internal async Task<List<string>> ResolveWatchlistAsync(PairsWatchOptions opt, CancellationToken ct)
    {
        var manuali = opt.Pairs.Distinct(StringComparer.Ordinal).ToList();
        if (!opt.AutoWatch)
        {
            LastAutoSelected = [];
            return manuali;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var righe = await db.PairCandidates.AsNoTracking()
                .Where(c => c.IsTradeable)
                .Select(c => new AutoWatchRow(c.PairKeyValue, c.RunId, c.RunCompletedUtc, c.AdfStatistic))
                .ToListAsync(ct);

            var (auto, tagliateDalTetto) = SelectAutoPairs(
                righe, manuali,
                Math.Max(2, opt.AutoWatchMinScreens),
                Math.Max(1, opt.AutoWatchMinSpanDays),
                Math.Max(1, opt.AutoWatchMaxPairs));
            LastAutoSelected = auto;
            if (auto.Count > 0 || tagliateDalTetto > 0)
            {
                logger.LogInformation(
                    "Sorveglianza spread AUTO: {N} coppie per replicazione (≥{Screens} screen su ≥{Giorni}gg){Tagliate}: {Coppie}",
                    auto.Count, Math.Max(2, opt.AutoWatchMinScreens), Math.Max(1, opt.AutoWatchMinSpanDays),
                    tagliateDalTetto > 0 ? $", {tagliateDalTetto} qualificate ESCLUSE dal tetto" : "",
                    string.Join("; ", auto));
            }
            return manuali.Concat(auto).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Regola 4: fail-open dichiarato — la sorveglianza manuale non muore per un guasto dell'indice.
            logger.LogWarning(ex, "Selezione automatica delle coppie fallita: questo giro sorveglia le sole manuali ({N}).", manuali.Count);
            LastAutoSelected = [];
            return manuali;
        }
    }

    /// <summary>Una riga dell'indice, ridotta a ciò che serve alla selezione. Record per i test.</summary>
    internal sealed record AutoWatchRow(string PairKey, Guid RunId, DateTime RunCompletedUtc, double AdfStatistic);

    /// <summary>
    /// [2026-08-25] La selezione AUTO, pura: qualificano le coppie operabili in almeno
    /// <paramref name="minScreens"/> run DISTINTI il cui primo e ultimo screen distano almeno
    /// <paramref name="minSpanDays"/> giorni — la replicazione, non il top-N di un test singolo
    /// (che al 5% su ~190 coppie fabbrica una decina di falsi per costruzione). Ordinate per ADF
    /// dell'ultimo screen (più forte prima); il tetto vale sul TOTALE e le manuali non si
    /// sfrattano: si riempie solo lo spazio che lasciano. Ritorna anche quante qualificate sono
    /// rimaste fuori dal tetto — un taglio silenzioso si leggerebbe come «non c'era altro».
    /// </summary>
    internal static (List<string> Selected, int CutByCap) SelectAutoPairs(
        IReadOnlyList<AutoWatchRow> rows, IReadOnlyList<string> manual,
        int minScreens, int minSpanDays, int maxTotal)
    {
        var manualSet = manual.ToHashSet(StringComparer.Ordinal);
        var qualificate = rows
            .GroupBy(r => r.PairKey, StringComparer.Ordinal)
            .Where(g => !manualSet.Contains(g.Key))
            .Select(g => new
            {
                Pair = g.Key,
                Screens = g.Select(r => r.RunId).Distinct().Count(),
                SpanDays = (g.Max(r => r.RunCompletedUtc) - g.Min(r => r.RunCompletedUtc)).TotalDays,
                LatestAdf = g.OrderByDescending(r => r.RunCompletedUtc).First().AdfStatistic,
            })
            .Where(x => x.Screens >= minScreens && x.SpanDays >= minSpanDays)
            .OrderBy(x => x.LatestAdf) // ADF più negativo = evidenza più forte
            .Select(x => x.Pair)
            .ToList();

        var slots = Math.Max(0, maxTotal - manual.Count);
        return (qualificate.Take(slots).ToList(), Math.Max(0, qualificate.Count - slots));
    }

    /// <summary>Un giro completo. Pubblico per i test e per un futuro «Calcola ora» dalla pagina.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var scritte = 0;

        var sorvegliate = await ResolveWatchlistAsync(opt, ct);
        foreach (var chiave in sorvegliate)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                scritte += await WatchPairAsync(chiave, opt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Difensivo per coppia: un guasto su una serie non spegne la sorveglianza di tutte.
                logger.LogWarning(ex, "Coppia {Coppia} saltata in questo giro della sorveglianza spread.", chiave);
            }
        }

        LastRunUtc = DateTime.UtcNow;
        LastRowsWritten = scritte;
        if (scritte > 0)
        {
            logger.LogInformation("Sorveglianza spread: {Righe} finestre nuove su {Coppie} coppie ({Auto} automatiche).",
                scritte, sorvegliate.Count, LastAutoSelected.Count);
        }
        return scritte;
    }

    private async Task<int> WatchPairAsync(string chiave, PairsWatchOptions opt, CancellationToken ct)
    {
        var (simboloA, simboloB, timeframe) = ParsePair(chiave);
        if (simboloA is null || simboloB is null || timeframe is null)
        {
            logger.LogWarning("Coppia sorvegliata «{Coppia}» non interpretabile: attesa la forma «SYMY|SYMX TF».", chiave);
            return 0;
        }

        var finestra = Math.Max(60, opt.WindowSize);
        var tetto = Math.Max(finestra * PairSpreadJudge.MinWindows, opt.MaxCandles);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candeleY = await UltimeCandeleAsync(db, simboloA, timeframe, tetto, ct);
        var candeleX = await UltimeCandeleAsync(db, simboloB, timeframe, tetto, ct);
        if (candeleY.Count < finestra || candeleX.Count < finestra)
        {
            logger.LogInformation("Coppia {Coppia}: candele insufficienti ({Y}/{X} contro {Serve} di finestra).",
                chiave, candeleY.Count, candeleX.Count, finestra);
            return 0;
        }

        // Lo STESSO allineamento dello stage di screening: due strade per allineare due serie
        // darebbero due spread diversi per la stessa coppia.
        var (allineateY, allineateX) = PairsCandleAligner.Align(candeleY, candeleX);
        if (allineateY.Count < finestra) return 0;

        var serieY = allineateY.Select(c => c.Close).ToList();
        var serieX = allineateX.Select(c => c.Close).ToList();

        // Le finestre NON SOVRAPPOSTE, tagliate dalla più recente all'indietro: il presente è la
        // parte che interessa, e il giudizio di rottura guarda le ultime.
        // L'estimatore dichiarato in configurazione: normalizzato una volta, non per finestra.
        // Sconosciuto ⇒ rolling OLS, che è il ripiego più prudente (nessuna adattività), e la riga
        // porterà comunque l'etichetta di ciò che è stato DAVVERO usato.
        var usaKalman = string.Equals(opt.Estimator, "Kalman", StringComparison.OrdinalIgnoreCase);
        var estimatoreUsato = usaKalman ? "Kalman" : "RollingOls";

        var righe = new List<PairSpreadWindow>();
        var ora = DateTime.UtcNow;
        var chiaveNormalizzata = PairKey.Build(simboloA, simboloB, timeframe);

        for (var fine = allineateY.Count; fine - finestra >= 0; fine -= finestra)
        {
            var inizio = fine - finestra;
            var fettaY = serieY.GetRange(inizio, finestra);
            var fettaX = serieX.GetRange(inizio, finestra);

            var esito = cointegration.Test(fettaY, fettaX);

            // Lo z-score passa dall'UNICA implementazione in repo: istanziarne una propria qui
            // sarebbe il difetto già chiuso da I10 (due verità sullo stesso numero).
            //
            // [revisione algoritmi 2026-08-20] E l'ESTIMATORE ora decide davvero il calcolo. Nella
            // prima versione chiamavo sempre la rolling OLS e poi etichettavo la riga con
            // `opt.Estimator`: scegliere «Kalman» dal pannello dava OLS con scritto Kalman sopra.
            // Un'etichetta che non descrive il numero che accompagna è la classe «controllo che
            // rassicura a prescindere dalla realtà» — scritta da me, nello stesso giorno in cui
            // l'ondata la bonificava altrove. La differenza non è cosmetica: i due estimatori danno
            // due spread diversi per costruzione (l'uno adatta β a ogni barra, l'altro a intervalli),
            // ed è la ragione per cui l'estimatore sta nella CHIAVE della tabella.
            var lookback = Math.Max(10, finestra / 4);
            var zLookback = Math.Max(3, finestra / 5);
            var analisi = usaKalman
                ? // δ: la costante dichiarata dall'analizzatore stesso, la stessa che usa il backtest —
                  // un valore diverso qui darebbe due spread per la stessa coppia.
                  new KalmanPairsSpreadAnalyzer().Analyze(fettaY, fettaX, lookback, KalmanPairsSpreadAnalyzer.DefaultDelta, zLookback)
                : new RollingPairsSpreadAnalyzer().Analyze(
                    fettaY, fettaX, lookback,
                    recalibrationInterval: Math.Max(1, finestra / 10),
                    zScoreLookback: zLookback);

            var spread = analisi.Spread.Where(v => v is not null).Select(v => v!.Value).ToList();
            var ultimoZ = analisi.ZScore.LastOrDefault(v => v is not null) ?? 0d;
            var beta = analisi.HedgeRatio.LastOrDefault(v => v is not null) ?? esito.HedgeRatio;

            righe.Add(new PairSpreadWindow
            {
                PairKeyValue = chiaveNormalizzata,
                SymbolY = simboloA, SymbolX = simboloB, Timeframe = timeframe,
                Estimator = estimatoreUsato,   // ciò che è stato usato, non ciò che è scritto in config
                WindowSize = finestra,
                WindowStartUtc = allineateY[inizio].TimestampUtc,
                WindowEndUtc = allineateY[fine - 1].TimestampUtc,
                AdfStatistic = esito.AdfStatistic,
                CriticalValue = esito.CriticalValue,
                IsStationaryWindow = esito.IsCointegrated,
                HedgeRatio = beta,
                SpreadMean = spread.Count > 0 ? spread.Average() : 0d,
                SpreadStdDev = DeviazioneStandard(spread),
                LastZScore = ultimoZ,
                ComputedAtUtc = ora,
            });
        }

        return await store.SaveAsync(righe, ct);
    }

    private static async Task<List<OhlcvData>> UltimeCandeleAsync(
        ApplicationDbContext db, string symbol, string timeframe, int quante, CancellationToken ct)
    {
        var righe = await db.OhlcvData.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
            .OrderByDescending(c => c.TimestampUtc)
            .Take(quante)
            .ToListAsync(ct);
        righe.Reverse();   // cronologico: l'allineatore e le finestre lo pretendono
        return righe;
    }

    private static double DeviazioneStandard(IReadOnlyList<double> valori)
    {
        if (valori.Count < 2) return 0d;
        var media = valori.Average();
        return Math.Sqrt(valori.Sum(v => (v - media) * (v - media)) / valori.Count);
    }

    /// <summary>
    /// Scompone <c>"ETH/USDT|BTC/USDT 1h"</c>. Tollerante sugli spazi, severa sulla forma: una
    /// chiave malformata produce un log e la coppia si salta, mai un'eccezione che fermi il giro.
    /// </summary>
    internal static (string? Y, string? X, string? Timeframe) ParsePair(string? chiave)
    {
        if (string.IsNullOrWhiteSpace(chiave)) return (null, null, null);

        // Gli spazi ai bordi si tolgono PRIMA di cercare il separatore: una chiave scritta a mano in
        // configurazione ne porta quasi sempre, e senza questo la coppia sarebbe stata scartata in
        // silenzio con un warning che nessuno legge. Trovato dal test, non dall'analisi.
        chiave = chiave.Trim();
        var spazio = chiave.LastIndexOf(' ');
        if (spazio <= 0 || spazio == chiave.Length - 1) return (null, null, null);

        var simboli = chiave[..spazio].Split('|', StringSplitOptions.TrimEntries);
        if (simboli.Length != 2 || simboli[0].Length == 0 || simboli[1].Length == 0) return (null, null, null);

        return (simboli[0], simboli[1], chiave[(spazio + 1)..].Trim());
    }
}
