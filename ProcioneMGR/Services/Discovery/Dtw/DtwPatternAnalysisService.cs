using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Services.Discovery.Dtw;

// =============================================================================================
//  [D4, misura] Dalla forma trovata al verdetto: il pattern anticipa qualcosa, o è rumore?
//
//  Trovare occorrenze è la parte facile — DTW ne trova sempre, se si abbassa la soglia. La
//  domanda che conta è se DOPO quelle occorrenze succeda qualcosa che non succede a caso, e la
//  risposta arriva dall'`EventStudy` che la piattaforma ha già: rendimento anormale rispetto alla
//  baseline del titolo, finestra pre-evento per smascherare l'anticipazione, e soprattutto il
//  PLACEBO — la stessa statistica su insiemi di date casuali.
//
//  Non si costruisce nessuna pista di validazione nuova: il PRD del filone D lo vieta
//  esplicitamente, e DTW resta un GENERATORE di candidati per il collaudo esistente.
//
//  IL PUNTO PIÙ IMPORTANTE DI QUESTO FILE — il placebo a date casuali NON basta qui.
//
//  Trovato dai test, non temuto: con il solo placebo dell'EventStudy, pattern casuali su serie di
//  puro rumore venivano dichiarati "segnale" **8 volte su 15**. La ragione è meccanica, non un
//  bug: selezionare finestre PER FORMA su dati z-normalizzati induce da solo un andamento nelle
//  barre successive. Se si cercano finestre che scendono e risalgono, per costruzione l'evento
//  cade su un massimo locale, e nel rumore quello che segue è una regressione — un "rendimento
//  anormale" perfettamente riproducibile senza alcun segnale. Il placebo a DATE CASUALI non ha
//  questa proprietà di selezione, quindi confronta mele con pere e assolve qualunque forma.
//
//  Il nullo corretto deve conservare il MECCANISMO DI SELEZIONE: si ripete l'intera procedura con
//  pattern casuali presi dalla serie stessa, e si guarda dove cade il pattern osservato in quella
//  distribuzione. È la stessa lezione già pagata dalla piattaforma con il t = 141 sugli asset
//  correlati: randomizzare lungo la dimensione sbagliata fabbrica significatività.
//
//  Due letture che il verdetto non lascia passare:
//   - la CAAR PRE-evento già significativa non è un buon segno, è un sospetto di look-ahead nel
//     modo in cui l'evento è stato definito;
//   - poche occorrenze su un lungo periodo non fanno una strategia operabile, per quanto belle
//     siano: l'obiettivo dichiarato è intraday/swing breve, quindi la frequenza è parte del
//     verdetto, non una curiosità.
// =============================================================================================

/// <summary>Esito dell'analisi di un pattern: occorrenze, event-study, nullo per forma, verdetto.</summary>
public sealed record DtwPatternAnalysis(
    int TemplateLength,
    int SeriesBars,
    int MatchCount,
    double MeanDistance,
    double OccurrencesPerMonth,
    EventStudyResult? Study,
    ShapeMatchedNull? ShapeNull,
    string Verdict,
    bool IsUsable)
{
    /// <summary>
    /// Il pattern anticipa un movimento che pattern QUALUNQUE, cercati allo stesso modo, non
    /// producono. Il giudice è il nullo per forma, non il placebo a date casuali: vedi la nota in
    /// testa a <see cref="DtwPatternAnalysisService"/>.
    /// </summary>
    public bool IsPredictive =>
        IsUsable
        && ShapeNull is { IsUsable: true, PValue: <= 0.05 }
        && Math.Abs(Study?.CaarPost ?? 0) >= DtwPatternAnalysisService.MinEconomicEffect;
}

/// <summary>
/// Distribuzione del rendimento anormale post-evento ottenuta ripetendo l'INTERA procedura con
/// pattern casuali: è il nullo corretto per eventi selezionati per forma.
/// </summary>
public sealed record ShapeMatchedNull(
    int Samples,
    int UsableSamples,
    double ObservedCaarPost,
    double MeanNullCaarPost,
    double NullPercentile95,
    double PValue,
    bool IsUsable);

/// <summary>Misura il valore predittivo di un pattern trovato per forma.</summary>
public interface IDtwPatternAnalysisService
{
    DtwPatternAnalysis Analyze(
        IReadOnlyList<OhlcvData> series,
        IReadOnlyList<decimal> template,
        DtwConfig config,
        EventStudyConfig? studyConfig = null,
        int shapeNullSamples = 60);
}

/// <inheritdoc cref="IDtwPatternAnalysisService"/>
public sealed class DtwPatternAnalysisService(IDtwMatcher matcher) : IDtwPatternAnalysisService
{
    /// <summary>Minimo di occorrenze perché un event-study abbia senso.</summary>
    private const int MinMatches = 20;

    /// <summary>
    /// PAVIMENTO DI RILEVANZA ECONOMICA: sotto questo rendimento anormale cumulato un effetto non è
    /// operabile, per quanto significativo risulti.
    ///
    /// Nasce da un test fallito: con il solo p-value, un effetto di **−0,48%** veniva dichiarato
    /// "segnale" perché il nullo per forma era ancora più piccolo. Statisticamente vero, e
    /// commercialmente inesistente: le assunzioni di costo della piattaforma (0,1% di fee per lato
    /// più 0,05% di slippage) fanno ~0,3% di andata e ritorno, quindi un effetto sotto lo 0,5%
    /// viene mangiato dai costi prima di arrivare al conto. Significatività e rilevanza sono due
    /// domande diverse e servono entrambe.
    /// </summary>
    public const double MinEconomicEffect = 0.005;

    public DtwPatternAnalysis Analyze(
        IReadOnlyList<OhlcvData> series,
        IReadOnlyList<decimal> template,
        DtwConfig config,
        EventStudyConfig? studyConfig = null,
        int shapeNullSamples = 60)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(template);
        config ??= new DtwConfig();

        if (template.Count < 3 || series.Count < template.Count + 100)
        {
            return Empty(template.Count, series.Count,
                "Serie o pattern troppo corti per una misura sensata (servono almeno 100 barre oltre la lunghezza del pattern).");
        }

        var matches = matcher.FindMatches(series, template, config);
        var span = series[^1].TimestampUtc - series[0].TimestampUtc;
        var months = Math.Max(1e-9, span.TotalDays / 30.0);
        var perMonth = matches.Count / months;
        var meanDistance = matches.Count > 0 ? matches.Average(m => m.Distance) : 0;

        if (matches.Count < MinMatches)
        {
            return new DtwPatternAnalysis(
                template.Count, series.Count, matches.Count, meanDistance, perMonth, null, null,
                $"Solo {matches.Count} occorrenze: sotto {MinMatches} un event-study non distingue il segnale dal caso. " +
                "Alza la soglia di distanza o allarga il periodo.",
                IsUsable: false);
        }

        // L'evento è la barra di CHIUSURA del pattern: prima di quella il pattern non è completo.
        var eventTimes = matches.Select(m => m.EndUtc).ToList();
        var study = EventStudy.Run(series, eventTimes, studyConfig);
        var shapeNull = BuildShapeMatchedNull(
            series, template.Count, config, studyConfig, study.CaarPost, matches.Count, shapeNullSamples);

        return new DtwPatternAnalysis(
            template.Count, series.Count, matches.Count, meanDistance, perMonth, study, shapeNull,
            BuildVerdict(study, shapeNull, perMonth), IsUsable: true);
    }

    /// <summary>
    /// Il NULLO PER FORMA: ripete l'intera procedura (cerca occorrenze, misura il rendimento
    /// anormale) con pattern casuali presi dalla serie stessa. Conserva quindi il meccanismo di
    /// selezione, che è ciò che il placebo a date casuali non fa — vedi la nota in testa al file.
    /// </summary>
    private ShapeMatchedNull? BuildShapeMatchedNull(
        IReadOnlyList<OhlcvData> series, int templateLength, DtwConfig config,
        EventStudyConfig? studyConfig, double observedCaarPost, int observedMatches, int samples)
    {
        if (samples < 10) return null;

        var rnd = new Random(20260727);   // deterministico come tutto il resto della piattaforma
        var nullCaars = new List<double>(samples);
        var maxStart = series.Count - templateLength - 1;
        if (maxStart <= 0) return null;

        // CONFRONTO A PARITÀ DI NUMEROSITÀ: per ogni forma casuale si prendono le sue N occorrenze
        // MIGLIORI, con N pari a quelle osservate, invece di applicare la stessa soglia di distanza.
        // Con la soglia fissa una forma casuale ne troverebbe pochissime, e si finirebbe a
        // confrontare una CAAR su 60 eventi con CAAR su 20 — dove la seconda è più rumorosa per
        // costruzione, non perché il pattern sia speciale. Qui il numero di eventi è identico e
        // l'unica differenza resta la forma.
        var nullConfig = new DtwConfig
        {
            BandPercent = config.BandPercent,
            MaxDistance = double.MaxValue,
            MaxMatches = observedMatches,
            MinSeparationBars = config.MinSeparationBars > 0 ? config.MinSeparationBars : templateLength,
        };

        for (var s = 0; s < samples; s++)
        {
            // Pattern casuale = una finestra qualunque della serie: stessa distribuzione di forme
            // di quello osservato, nessuna ragione a priori per essere predittivo.
            var start = rnd.Next(0, maxStart);
            var randomTemplate = new decimal[templateLength];
            for (var k = 0; k < templateLength; k++) randomTemplate[k] = series[start + k].Close;

            var m = matcher.FindMatches(series, randomTemplate, nullConfig);
            if (m.Count < MinMatches) continue;

            var st = EventStudy.Run(series, m.Select(x => x.EndUtc).ToList(), studyConfig);
            nullCaars.Add(st.CaarPost);
        }

        if (nullCaars.Count < 10)
        {
            return new ShapeMatchedNull(samples, nullCaars.Count, observedCaarPost, 0, 0, 1, IsUsable: false);
        }

        var absObserved = Math.Abs(observedCaarPost);
        var atLeastAsExtreme = nullCaars.Count(c => Math.Abs(c) >= absObserved);
        // +1 al numeratore e al denominatore: il p-value non può essere esattamente zero con un
        // numero finito di estrazioni, e fingere che lo sia sarebbe la stessa illusione da cui
        // questo nullo nasce.
        var p = (atLeastAsExtreme + 1.0) / (nullCaars.Count + 1.0);

        var sorted = nullCaars.Select(Math.Abs).OrderBy(v => v).ToList();
        var p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];

        return new ShapeMatchedNull(samples, nullCaars.Count, observedCaarPost, nullCaars.Average(), p95, p, IsUsable: true);
    }

    private static string BuildVerdict(EventStudyResult s, ShapeMatchedNull? shapeNull, double perMonth)
    {
        var freq = $"{perMonth:F1} occorrenze/mese";

        if (shapeNull is not { IsUsable: true })
        {
            return $"NON GIUDICABILE: non è stato possibile costruire il nullo per forma (troppo pochi pattern " +
                   $"casuali producono abbastanza occorrenze da confrontare). Rendimento anormale osservato " +
                   $"{s.CaarPost:P2}, ma senza un termine di paragone non significa nulla. {freq}.";
        }

        if (shapeNull.PValue > 0.05)
        {
            return $"NESSUN SEGNALE: il rendimento anormale dopo il pattern ({s.CaarPost:P2}) rientra in quello che " +
                   $"producono pattern QUALUNQUE cercati allo stesso modo (p {shapeNull.PValue:F3} su " +
                   $"{shapeNull.UsableSamples} forme casuali; il loro 95° percentile è {shapeNull.NullPercentile95:P2}). " +
                   $"Il pattern si trova, ma non dice niente che una forma a caso non direbbe. {freq}.";
        }

        if (Math.Abs(s.CaarPost) < MinEconomicEffect)
        {
            return $"IRRILEVANTE: l'effetto ({s.CaarPost:P2}) batte il nullo per forma (p {shapeNull.PValue:F3}) ma è " +
                   $"sotto il pavimento economico di {MinEconomicEffect:P2} — le sole fee e slippage di andata e " +
                   $"ritorno valgono ~0,30%. Significativo non vuol dire operabile. {freq}.";
        }

        // Oltre il nullo per forma: prima di festeggiare, si guarda la finestra PRE-evento.
        var suspicion = Math.Abs(s.CaarPre) >= Math.Abs(s.CaarPost) * 0.5
            ? " ATTENZIONE: la CAAR PRE-evento (" + s.CaarPre.ToString("P2") + ") è dello stesso ordine di quella post, " +
              "il che di solito significa che il movimento era già in corso quando il pattern si è completato — " +
              "cioè che il pattern lo descrive invece di anticiparlo."
            : string.Empty;

        return $"SEGNALE: rendimento anormale cumulato {s.CaarPost:P2} nelle barre successive, oltre il nullo per " +
               $"forma (p {shapeNull.PValue:F3} su {shapeNull.UsableSamples} pattern casuali, 95° percentile " +
               $"{shapeNull.NullPercentile95:P2}), su {s.EventsUsable} occorrenze utilizzabili. {freq}." + suspicion +
               " Resta da passare il collaudo completo (holdout, DSR con il conteggio dei tentativi, PBO): " +
               "superare il nullo per forma è una condizione necessaria, non sufficiente.";
    }

    private static DtwPatternAnalysis Empty(int templateLength, int bars, string message)
        => new(templateLength, bars, 0, 0, 0, null, null, message, IsUsable: false);
}
