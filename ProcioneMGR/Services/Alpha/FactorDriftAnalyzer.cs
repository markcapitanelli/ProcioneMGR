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
    IReadOnlyList<FactorIcPoint> Series)
{
    /// <summary>Vero quando il fattore merita attenzione (indebolito o invertito).</summary>
    public bool IsAlert => Status is FactorDriftStatus.Weakening or FactorDriftStatus.SignFlip;
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

        // Finestre NON sovrapposte (vedi nota in testa al file).
        var series = new List<FactorIcPoint>();
        for (var start = 0; start + window <= x.Count; start += window)
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
        var recent = series.TakeLast(recentCount).Average(p => p.InformationCoefficient);
        var reference = series.Take(series.Count - recentCount).Average(p => p.InformationCoefficient);

        var (status, message) = Judge(reference, recent, threshold);
        return new FactorDriftReport(featureName, displayName, fullIc, reference, recent, threshold, status, message, series);
    }

    public IReadOnlyList<FactorDriftReport> AnalyzeMany(
        IReadOnlyList<FactorSpec> specs, IReadOnlyList<OhlcvData> candles, FactorDriftConfig config)
    {
        ArgumentNullException.ThrowIfNull(specs);
        return specs
            .Select(s => Analyze(s, candles, config))
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
    private static (FactorDriftStatus Status, string Message) Judge(double reference, double recent, double threshold)
    {
        var refAbs = Math.Abs(reference);
        var recentAbs = Math.Abs(recent);

        if (refAbs < threshold)
        {
            return (FactorDriftStatus.Stable,
                $"Non informava già nel periodo di riferimento (|IC| {refAbs:F3} sotto la soglia {threshold:F3}): non c'è un decadimento da segnalare, è un fattore debole da sempre.");
        }

        if (recentAbs >= threshold && Math.Sign(recent) != Math.Sign(reference))
        {
            return (FactorDriftStatus.SignFlip,
                $"Segno INVERTITO: informava a {reference:F3}, ora a {recent:F3}, entrambi sopra la soglia {threshold:F3}. Un fattore che si capovolge è più pericoloso di uno che si spegne — un modello addestrato prima lo userebbe al contrario.");
        }

        if (recentAbs < threshold)
        {
            return (FactorDriftStatus.Weakening,
                $"Si è spento: da |IC| {refAbs:F3} a {recentAbs:F3}, sotto la soglia {threshold:F3}.");
        }

        return (FactorDriftStatus.Stable,
            $"In linea: |IC| {refAbs:F3} nel riferimento, {recentAbs:F3} nel recente (soglia {threshold:F3}).");
    }
}
