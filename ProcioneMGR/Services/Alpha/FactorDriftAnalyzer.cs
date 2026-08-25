using ProcioneMGR.Data;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Services.Alpha;

// =============================================================================================
//  [D2 roadmap scoperta-pattern] Monitor di decadimento dei FATTORI.
//
//  Esisteva già il monitor a livello di STRATEGIA (StrategyDecayMonitor: Sharpe realizzato vs
//  atteso). Mancava il gradino sotto: un fattore che smette di informare non si vede da nessuna
//  parte finché qualcuno non ripassa a mano da /feature-selection. Questo lo rende misurabile,
//  con lo stesso schema del fratello maggiore — un riferimento, un recente, una soglia, e nessuna
//  azione automatica.
//
//  DUE SCELTE DI METODO, entrambe per non fabbricare significatività:
//
//  1. FINESTRE NON SOVRAPPOSTE di default. Finestre scorrevoli darebbero una spezzata più fitta e
//     più bella, ma i punti consecutivi condividerebbero dati e quindi sarebbero correlati: la
//     serie SEMBREREBBE più stabile di quanto è. La piattaforma ha già pagato una volta il prezzo
//     di una randomizzazione che ripeteva lo stesso esperimento (t = 141 su asset correlati);
//     qui si preferiscono pochi punti indipendenti a molti punti che si assomigliano per
//     costruzione.
//
//  2. QUESTA CLASSE NON PERSISTE NULLA, ed è pura: nessun DB, nessun orologio. La storia dell'IC
//     viene registrata su tabella (vedi FactorIcHistory.cs, deciso il 2026-07-28) dal worker, non
//     da qui — così il calcolo resta verificabile senza database e il verdetto sulla storia
//     registrata passa dallo STESSO Judge del calcolo fresco: due strade che possono divergere
//     sarebbero due monitor diversi con lo stesso nome.
// =============================================================================================

/// <summary>Un punto della serie storica dell'IC: una finestra temporale e il suo IC.</summary>
public sealed record FactorIcPoint(DateTime WindowStartUtc, DateTime WindowEndUtc, double InformationCoefficient, int Observations);

/// <summary>Verdetto sul fattore. L'ordine riflette la gravità crescente.</summary>
public enum FactorDriftStatus
{
    /// <summary>Finestre insufficienti per dire alcunché.</summary>
    Insufficient,

    /// <summary>Il fattore informa oggi come informava all'inizio del periodo.</summary>
    Stable,

    /// <summary>Informava e ha smesso: |IC| recente sotto la soglia di sopravvivenza.</summary>
    Weakening,

    /// <summary>Informa al contrario di prima: il segno si è invertito su entrambi i lati significativi.</summary>
    SignFlip,
}

/// <summary>Report di deriva per un singolo fattore.</summary>
public sealed record FactorDriftReport(
    string FeatureName,
    string DisplayName,
    double FullSampleIc,
    double ReferenceIc,
    double RecentIc,
    double NoiseFloor,
    FactorDriftStatus Status,
    string StatusMessage,
    IReadOnlyList<FactorIcPoint> Series,
    // [2026-08-24] Di quanti errori standard è il calo, e la sua probabilità sotto l'ipotesi
    // «niente è cambiato». Senza questi due numeri il verdetto non è falsificabile e non si può
    // correggere per molteplicità: erano proprio le due cose che mancavano.
    double TStatistic = 0d,
    double PValue = double.NaN,
    // Soglia sotto la quale il RIFERIMENTO è considerato non informativo, calcolata sul suo
    // errore standard (non su quello di una finestra sola, che era l'asimmetria del difetto).
    // <see cref="NoiseFloor"/> resta il pavimento di rumore di UNA finestra: due domande diverse.
    double ReferenceGate = 0d)
{
    /// <summary>Vero quando il fattore merita attenzione (indebolito o invertito).</summary>
    public bool IsAlert => Status is FactorDriftStatus.Weakening or FactorDriftStatus.SignFlip;

    /// <summary>Fine della finestra RECENTE, cioè fin dove arriva davvero il verdetto.</summary>
    public DateTime? RecentWindowEndUtc => Series.Count > 0 ? Series[^1].WindowEndUtc : null;

    /// <summary>
    /// Per questo fattore il calo è stato CALCOLATO, quindi entra nel denominatore della correzione
    /// per molteplicità.
    ///
    /// <para>Ci entra <b>anche</b> se il cancello del riferimento lo ha poi escluso dal referto, e
    /// non è una svista: contare solo i fattori sopravvissuti al cancello significa correggere
    /// <i>condizionando sulla selezione</i>, cioè rifare in piccolo l'errore che tutta questa
    /// revisione corregge. Su otto fattori di puro rumore, con il denominatore ristretto a uno, un
    /// p di 0,007 passava indisturbato e il pannello dichiarava una deriva su una serie casuale;
    /// col denominatore giusto la soglia scende a 0,05/8 e il caso rientra nel rumore, che è quello
    /// che è.</para>
    /// </summary>
    public bool WasTestedForDrift => Status != FactorDriftStatus.Insufficient && !double.IsNaN(PValue);
}

/// <summary>Parametri del monitor. I default sono allineati a quelli già in uso in <c>/feature-selection</c>.</summary>
public sealed class FactorDriftConfig
{
    /// <summary>Orizzonte del rendimento forward su cui si misura l'IC.</summary>
    public int ForwardHorizon { get; set; } = 1;

    /// <summary>Ampiezza della finestra in osservazioni utilizzabili.</summary>
    public int WindowSize { get; set; } = 250;

    /// <summary>
    /// |IC| sotto il quale un fattore si considera non informativo per ragioni ECONOMICHE. 0,02 è
    /// la stessa soglia di sopravvivenza che <c>/feature-selection</c> usa da sempre: un criterio
    /// nuovo qui renderebbe i due pannelli incoerenti fra loro. Attenzione: non è la soglia
    /// operativa — quella è il massimo fra questa e il pavimento di rumore statistico, vedi
    /// <see cref="FactorDriftAnalyzer.NoiseFloorFor"/>.
    /// </summary>
    public double MinAbsIc { get; set; } = 0.02;

    /// <summary>
    /// Quanti errori standard servono perché un IC sia distinguibile da zero. 1,96 ≈ 95%.
    /// </summary>
    public double NoiseFloorZ { get; set; } = 1.96;

    /// <summary>Quante finestre finali compongono il "recente" da confrontare col riferimento.</summary>
    public int RecentWindows { get; set; } = 2;

    /// <summary>Minimo di finestre perché il verdetto abbia senso (riferimento + recente).</summary>
    public int MinWindows { get; set; } = 4;
}

/// <summary>
/// Calcola la serie storica dell'IC di un fattore e ne giudica la deriva. Puro e deterministico:
/// nessun DB, nessun orologio, nessuno stato — come <c>StrategyDecayMonitor</c>.
/// </summary>
public interface IFactorDriftAnalyzer
{
    FactorDriftReport Analyze(FactorSpec spec, IReadOnlyList<OhlcvData> candles, FactorDriftConfig config);

    /// <summary>Analizza più fattori, i più preoccupanti per primi.</summary>
    IReadOnlyList<FactorDriftReport> AnalyzeMany(
        IReadOnlyList<FactorSpec> specs, IReadOnlyList<OhlcvData> candles, FactorDriftConfig config);
}

/// <inheritdoc cref="IFactorDriftAnalyzer"/>
public sealed class FactorDriftAnalyzer : IFactorDriftAnalyzer
{
    private readonly IFactorEvaluator _evaluator = new FactorEvaluator();

    /// <summary>
    /// PAVIMENTO DI RUMORE dell'IC su una finestra di <paramref name="windowSize"/> osservazioni:
    /// <c>z / √n</c>, l'errore standard di una correlazione attorno a zero.
    ///
    /// Questo metodo esiste per un errore che i test hanno trovato nella prima versione di questa
    /// classe: giudicare l'IC contro la soglia fissa 0,02 senza guardare l'ampiezza della finestra.
    /// Su 300 osservazioni l'errore standard è ≈ 0,058, quindi un |IC| di 0,04 è **rumore puro** e
    /// una soglia a 0,02 lo avrebbe promosso a segnale — fabbricando allarmi (e "inversioni di
    /// segno") dal nulla ogni volta che il caso girava dalla parte sbagliata. La soglia operativa
    /// è quindi il MASSIMO fra il minimo economico e questo pavimento statistico: un fattore deve
    /// essere insieme abbastanza forte da valere qualcosa e abbastanza forte da essere visto.
    /// </summary>
    public static double NoiseFloorFor(int windowSize, double z = 1.96)
        => windowSize < 4 ? double.PositiveInfinity : z / Math.Sqrt(windowSize);

    /// <summary>
    /// AMPIEZZA DI FINESTRA CONSIGLIATA per <paramref name="observations"/> osservazioni utilizzabili:
    /// circa un decimo del campione (≈10 finestre non sovrapposte), **quantizzato a passi di 250** e
    /// tenuto fra 250 e 3000.
    ///
    /// Questa funzione esiste in UN SOLO posto per una ragione trovata guardando l'app dal vivo: il
    /// job periodico e il pannello di <c>/feature-selection</c> avevano due regole diverse (uno
    /// quantizzava, l'altro no) e sulla STESSA serie producevano finestre diverse — quindi soglie
    /// diverse (1,96/√n) e quindi **verdetti diversi**: su BTC/USDT 1h lo stesso fattore risultava
    /// "si è spento" per uno e "non ha mai informato" per l'altro. Entrambi corretti, incoerenti fra
    /// loro, e nulla è più veloce a far perdere fiducia in un pannello.
    ///
    /// La quantizzazione serve alla persistenza: una serie storica la cui finestra si sposta a ogni
    /// giro non è una serie, è una collezione di misure con pavimenti di rumore diversi.
    /// </summary>
    public static int SuggestWindowSize(int observations)
    {
        var target = observations / 10;
        var quantized = (int)Math.Round(target / 250.0, MidpointRounding.AwayFromZero) * 250;
        return Math.Clamp(quantized, 250, 3000);
    }

    public FactorDriftReport Analyze(FactorSpec spec, IReadOnlyList<OhlcvData> candles, FactorDriftConfig config)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(candles);
        config ??= new FactorDriftConfig();

        var horizon = Math.Max(1, config.ForwardHorizon);
        var window = Math.Max(10, config.WindowSize);

        var values = spec.Factor.Compute(candles, spec.Parameters);
        var forward = _evaluator.ForwardReturns(candles, horizon);

        // Coppie utilizzabili, in ordine temporale, con il timestamp che le ha generate: le finestre
        // vanno costruite su queste, non sugli indici delle candele, altrimenti il warm-up dei
        // fattori lunghi produrrebbe finestre iniziali quasi vuote.
        var x = new List<double>();
        var y = new List<double>();
        var ts = new List<DateTime>();
        for (var i = 0; i < candles.Count; i++)
        {
            if (values[i] is not { } v || forward[i] is not { } f) continue;
            x.Add((double)v);
            y.Add((double)f);
            ts.Add(candles[i].TimestampUtc);
        }

        var fullIc = x.Count >= 3 ? Correlation.Spearman(x, y) : 0d;

        // Finestre NON sovrapposte (vedi nota in testa al file), costruite A RITROSO dall'ultima
        // coppia disponibile.
        //
        // [2026-08-24] Prima il ciclo partiva da 0 e avanzava, quindi il RESTO — fino a window−1
        // barre — restava scoperto e cadeva sulla parte PIÙ RECENTE. Misurato sul database vero:
        // fra l'ultima finestra registrata e l'ultima candela disponibile c'erano in media 120
        // giorni sulle serie 1d (fino a 211), 84 sulle 1h, 46 sulle 4h — con le candele fresche di
        // poche ore. La finestra «recente», quella su cui si accende l'allarme, descriveva la
        // primavera mentre la pagina scriveva accanto «Ultimo calcolo» di stamattina. Il commento
        // del worker dice «le candele più RECENTI: la deriva è una domanda sul presente», e qui si
        // buttavano via proprio quelle.
        //
        // Partendo dal resto, lo scarto finisce sulla coda VECCHIA, dove non serve a nessuno, e
        // l'ultima finestra si chiude esattamente sull'ultima barra.
        var series = new List<FactorIcPoint>();
        for (var start = x.Count % window; start + window <= x.Count; start += window)
        {
            var wx = x.GetRange(start, window);
            var wy = y.GetRange(start, window);
            series.Add(new FactorIcPoint(ts[start], ts[start + window - 1], Correlation.Spearman(wx, wy), window));
        }

        return BuildReport(spec.FeatureName, spec.Factor.DisplayName, fullIc, series, window, config);
    }

    /// <summary>
    /// Verdetto su una serie di finestre GIÀ CALCOLATE — è la strada che usa la storia registrata su
    /// tabella dal worker (vedi <c>FactorIcHistory.cs</c>) per farsi giudicare senza ripassare dalle
    /// candele. Deliberatamente lo stesso <c>Judge</c> del calcolo fresco.
    ///
    /// Nota di onestà sul solo campo non ricostruibile: l'IC full-sample non è ricavabile dagli IC
    /// delle finestre (una correlazione di rango sull'unione non è la media delle correlazioni sui
    /// pezzi), quindi qui vale la MEDIA delle finestre. Il verdetto non lo usa — riferimento,
    /// recente e soglia sono tutti esatti — e la UI non lo mostra sulla storia registrata, proprio
    /// per non spacciare una media per un ricalcolo.
    /// </summary>
    public static FactorDriftReport JudgeSeries(
        string featureName, string displayName, IReadOnlyList<FactorIcPoint> series, FactorDriftConfig config)
    {
        ArgumentNullException.ThrowIfNull(series);
        config ??= new FactorDriftConfig();

        var window = series.Count > 0 ? series[^1].Observations : config.WindowSize;
        var meanIc = series.Count > 0 ? series.Average(p => p.InformationCoefficient) : 0d;
        return BuildReport(featureName, displayName, meanIc, series, window, config);
    }

    /// <summary>Dalla serie di finestre al verdetto: unica strada, usata sia dal calcolo fresco sia dalla storia registrata.</summary>
    private static FactorDriftReport BuildReport(
        string featureName, string displayName, double fullIc,
        IReadOnlyList<FactorIcPoint> series, int window, FactorDriftConfig config)
    {
        // Soglia operativa: il più severo fra il minimo economico e il pavimento di rumore.
        var threshold = Math.Max(config.MinAbsIc, NoiseFloorFor(window, config.NoiseFloorZ));

        if (series.Count < Math.Max(2, config.MinWindows))
        {
            return new FactorDriftReport(featureName, displayName, fullIc, 0, 0, threshold,
                FactorDriftStatus.Insufficient,
                $"Finestre insufficienti ({series.Count}, ne servono {Math.Max(2, config.MinWindows)}): allarga il periodo o riduci l'ampiezza della finestra.",
                series);
        }

        var recentCount = Math.Clamp(config.RecentWindows, 1, series.Count - 1);
        var recWindows = series.TakeLast(recentCount).Select(p => p.InformationCoefficient).ToList();
        var refWindows = series.Take(series.Count - recentCount).Select(p => p.InformationCoefficient).ToList();
        var recent = recWindows.Average();
        var reference = refWindows.Average();

        // ------------------------------------------------------------------------------------
        // [2026-08-24] IL CALO SI GIUDICA CONTRO IL PROPRIO ERRORE STANDARD, non contro una
        // soglia assoluta.
        //
        // Il difetto che questo blocco corregge: `reference` è la media di ~8 finestre e `recent`
        // la media di 2, e venivano confrontate entrambe con `threshold` = 1,96/√window, cioè
        // l'errore standard di UNA finestra sola. Al riferimento si chiedevano ~5,5 sigma, al
        // recente ~2,8: il riferimento veniva selezionato verso l'alto (chi supera 5,5 sigma lo fa
        // anche per fortuna) e il recente, tre volte più rumoroso, ci ricadeva sotto per
        // costruzione. Era un rilevatore di REGRESSIONE VERSO LA MEDIA travestito da rilevatore di
        // deriva — e la prova decisiva è il nullo che conserva il meccanismo di selezione:
        // applicando la stessa regola alle due finestre PIÙ VECCHIE invece che alle due più
        // recenti si ottenevano 85 allarmi su 165 contro i 39 su 131 del reale. Il nullo gridava
        // più forte del segnale, cioè la posizione «fine serie» non portava informazione.
        //
        // LE FINESTRE SI ORIENTANO SUL SEGNO DEL RIFERIMENTO. Un fattore che informa a −0,15 è
        // informativo quanto uno che informa a +0,15: la deriva è il calo del contenuto, non del
        // valore con segno. Orientando, «si è spento» e «si è invertito» diventano lo stesso
        // continuo — un'inversione è semplicemente un calo che passa oltre lo zero — e le due
        // dispersioni finiscono sulla stessa scala.
        var dir = reference >= 0 ? 1d : -1d;
        var refU = refWindows.Select(v => dir * v).ToList();
        var recU = recWindows.Select(v => dir * v).ToList();
        var drop = refU.Average() - recU.Average();
        var nRef = refU.Count;
        var nRec = recU.Count;

        // VARIANZA POOLED DENTRO I GRUPPI, non su tutte le finestre insieme.
        //
        // È l'errore che il controllo a decadimento piantato ha trovato subito: stimando la
        // dispersione sull'INTERA serie, un calo vero e grande finisce dentro la stima stessa e la
        // gonfia — lo strumento diventa cieco proprio quando c'è qualcosa da vedere. La varianza
        // pooled within-group è insensibile allo scostamento fra i due periodi, che è esattamente
        // la quantità sotto esame.
        var df = Math.Max(1, nRef + nRec - 2);
        var pooledVar = (Devianza(refU) + Devianza(recU)) / df;
        var dispersion = Math.Sqrt(Math.Max(0d, pooledVar));
        var se = dispersion * Math.Sqrt(1.0 / nRef + 1.0 / nRec);
        var t = se > 1e-12 ? drop / se : (drop > 0 ? double.PositiveInfinity : 0d);
        // Unilaterale: interessa il CALO, non una variazione qualunque. Un fattore che si rafforza
        // non è una deriva da segnalare.
        var pValue = double.IsPositiveInfinity(t)
            ? 0d
            : 1d - MathNet.Numerics.Distributions.StudentT.CDF(0d, 1d, df, t);

        // Le due soglie di significatività, ciascuna con la SUA numerosità, e col quantile della t
        // e NON della normale: con sei finestre di riferimento e due recenti, usare 1,96 al posto
        // di t(0,975; 6) = 2,45 è anticonservativo del 25% — e su otto fattori per serie quella
        // differenza è la distanza fra un pannello leggibile e uno che grida sul rumore.
        // Il riferimento resta vincolato anche dal minimo ECONOMICO: un IC statisticamente diverso
        // da zero ma di 0,005 non paga fee e slippage — significatività e rilevanza sono due
        // domande diverse, e servono entrambe.
        var quantile = MathNet.Numerics.Distributions.StudentT.InvCDF(0d, 1d, df, 1d - Alpha(config.NoiseFloorZ) / 2d);
        var refGate = Math.Max(config.MinAbsIc, quantile * dispersion / Math.Sqrt(nRef));
        var recGate = Math.Max(config.MinAbsIc, quantile * dispersion / Math.Sqrt(nRec));

        var alpha = Alpha(config.NoiseFloorZ);
        var (status, message) = Judge(reference, recent, refGate, recGate, t, pValue, alpha);
        return new FactorDriftReport(
            featureName, displayName, fullIc, reference, recent, threshold, status, message, series,
            t, pValue, refGate);
    }

    /// <summary>
    /// Il livello di significatività corrispondente alla z configurata (bilaterale): con z = 1,96
    /// è 0,05. Esiste perché la manopola del progetto è la z, e due numeri per la stessa idea
    /// divergono al primo cambio.
    /// </summary>
    internal static double Alpha(double z) => 2d * (1d - MathNet.Numerics.Distributions.Normal.CDF(0d, 1d, Math.Abs(z)));

    /// <summary>Deviazione standard CAMPIONARIA (n−1). Zero con meno di due elementi.</summary>
    internal static double SampleStdDev(IEnumerable<double> values)
    {
        var v = values as IReadOnlyList<double> ?? values.ToList();
        if (v.Count < 2) return 0d;
        var mean = v.Average();
        var ss = v.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(ss / (v.Count - 1));
    }

    /// <summary>Somma degli scarti quadratici dalla media del gruppo (la sua devianza).</summary>
    private static double Devianza(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0d;
        var mean = v.Average();
        return v.Sum(x => (x - mean) * (x - mean));
    }

    /// <summary>
    /// [2026-08-24] CORREZIONE PER MOLTEPLICITÀ (Benjamini-Hochberg) sui confronti di UNA serie.
    ///
    /// <para>Otto fattori giudicati insieme sono otto test: a α = 0,05 ci si aspetta un allarme
    /// falso ogni due giri e mezzo <i>per serie</i>, e con 222 serie in watchlist il pannello si
    /// riempirebbe di rumore che sembra segnale. BH controlla la frazione attesa di falsi fra i
    /// segnalati, che è la garanzia giusta per uno strumento di screening — Bonferroni sarebbe
    /// troppo severo e spegnerebbe anche i cali veri.</para>
    ///
    /// <para><b>Limite dichiarato, non nascosto:</b> la correzione è dentro la SERIE, perché è
    /// l'unità che l'analizzatore vede. Il conteggio mostrato in Home aggrega 222 serie
    /// indipendenti, e quella molteplicità resta non corretta: la pagina lo dice accanto al numero
    /// invece di lasciarlo credere.</para>
    ///
    /// <para>Applicata su ENTRAMBE le strade — calcolo fresco e storia registrata — perché due
    /// regole sulla stessa domanda sono due verdetti diversi sullo stesso fattore, ed è l'errore
    /// che questa cartella ha già pagato con le due <c>SuggestWindowSize</c>.</para>
    /// </summary>
    public static IReadOnlyList<FactorDriftReport> ApplyMultiplicityCorrection(
        IReadOnlyList<FactorDriftReport> reports, double z = 1.96)
    {
        ArgumentNullException.ThrowIfNull(reports);

        // m = i test DAVVERO ESEGUITI, non i soli che sembrano significativi.
        //
        // È l'errore che il controllo sul rumore ha trovato dentro questa stessa correzione: usando
        // come denominatore il numero di candidati, un solo allarme su otto fattori si sarebbe
        // confrontato con 1/1 × α invece che con 1/8 × α — cioè nessuna correzione affatto, proprio
        // nel caso in cui serve. Su otto fattori di puro rumore un p di 0,007 passava, e il
        // pannello dichiarava un «segno invertito» su una serie casuale.
        //
        // «Eseguito» = il fattore ha superato il cancello del riferimento ed è arrivato alla
        // domanda sul calo. Chi si ferma prima («non informava già») non è un test sulla deriva e
        // gonfierebbe il denominatore senza motivo.
        var eseguiti = reports.Count(r => r.WasTestedForDrift);
        var candidati = reports
            .Select((r, i) => (Report: r, Index: i))
            .Where(x => x.Report.IsAlert && !double.IsNaN(x.Report.PValue))
            .OrderBy(x => x.Report.PValue)
            .ToList();
        if (candidati.Count == 0) return reports;

        var alpha = Alpha(z);
        var m = Math.Max(candidati.Count, eseguiti);
        // Si scorrono i CANDIDATI (ordinati per p crescente), ma il denominatore è m = i test
        // eseguiti. Scorrere fino a m indicizzerebbe fuori dalla lista appena un fattore giudicato
        // non è candidato — cioè quasi sempre.
        var soglia = -1;
        for (var k = 0; k < candidati.Count; k++)
        {
            if (candidati[k].Report.PValue <= (k + 1) / (double)m * alpha) soglia = k;
        }

        var sopravvissuti = candidati.Take(soglia + 1).Select(x => x.Index).ToHashSet();
        var risultato = reports.ToArray();
        foreach (var x in candidati.Where(x => !sopravvissuti.Contains(x.Index)))
        {
            risultato[x.Index] = x.Report with
            {
                Status = FactorDriftStatus.Stable,
                StatusMessage =
                    $"Calo non significativo dopo la correzione per molteplicità ({m} fattori giudicati su questa serie): "
                    + $"p = {x.Report.PValue:F3} contro una soglia di {(soglia + 2) / (double)m * alpha:F3}. "
                    + $"Il calo da |IC| {Math.Abs(x.Report.ReferenceIc):F3} a {Math.Abs(x.Report.RecentIc):F3} è dentro la "
                    + "dispersione ordinaria fra le finestre di questo stesso fattore.",
            };
        }
        return risultato;
    }

    public IReadOnlyList<FactorDriftReport> AnalyzeMany(
        IReadOnlyList<FactorSpec> specs, IReadOnlyList<OhlcvData> candles, FactorDriftConfig config)
    {
        ArgumentNullException.ThrowIfNull(specs);
        config ??= new FactorDriftConfig();

        // La correzione per molteplicità PRIMA dell'ordinamento: i fattori di questa serie sono
        // giudicati insieme, quindi sono test simultanei. Vedi ApplyMultiplicityCorrection.
        var reports = specs.Select(s => Analyze(s, candles, config)).ToList();
        return ApplyMultiplicityCorrection(reports, config.NoiseFloorZ)
            .OrderByDescending(r => (int)r.Status)                      // prima gli allarmi
            .ThenByDescending(r => Math.Abs(r.ReferenceIc - r.RecentIc)) // poi il calo più marcato
            .ToList();
    }

    /// <summary>
    /// Il verdetto, contro la soglia operativa <paramref name="threshold"/> (minimo economico ∪
    /// pavimento di rumore). Nota la simmetria col <c>StrategyDecayMonitor</c>: quando il
    /// riferimento è già sotto soglia non si emette allarme, perché "un fattore che non informava e
    /// continua a non informare" non è un decadimento — è un fattore inutile, e dirlo come allarme
    /// sarebbe rumore in un pannello che deve restare leggibile.
    /// </summary>
    private static (FactorDriftStatus Status, string Message) Judge(
        double reference, double recent, double refGate, double recGate, double t, double pValue, double alpha)
    {
        var refAbs = Math.Abs(reference);
        var recentAbs = Math.Abs(recent);

        if (refAbs < refGate)
        {
            return (FactorDriftStatus.Stable,
                $"Non informava già nel periodo di riferimento (|IC| {refAbs:F3} sotto la soglia {refGate:F3}, "
                + "che è il suo errore standard sulle finestre di riferimento): non c'è un decadimento da segnalare, "
                + "è un fattore debole da sempre.");
        }

        if (recentAbs >= recGate && Math.Sign(recent) != Math.Sign(reference))
        {
            return (FactorDriftStatus.SignFlip,
                $"Segno INVERTITO: informava a {reference:F3}, ora a {recent:F3}, entrambi sopra la propria soglia "
                + $"(riferimento {refGate:F3}, recente {recGate:F3}). Un fattore che si capovolge è più pericoloso di "
                + "uno che si spegne — un modello addestrato prima lo userebbe al contrario.");
        }

        if (pValue <= alpha)
        {
            return (FactorDriftStatus.Weakening,
                $"Si è indebolito: da |IC| {refAbs:F3} a {recentAbs:F3}. Il calo vale {t:F2} errori standard "
                + $"(p = {pValue:F3}), cioè è più grande della dispersione ordinaria fra le finestre di questo fattore.");
        }

        return (FactorDriftStatus.Stable,
            $"In linea: |IC| {refAbs:F3} nel riferimento, {recentAbs:F3} nel recente. Il calo vale {t:F2} errori "
            + $"standard (p = {pValue:F3}): è dentro il rumore con cui l'IC di questo fattore oscilla da sempre.");
    }
}
