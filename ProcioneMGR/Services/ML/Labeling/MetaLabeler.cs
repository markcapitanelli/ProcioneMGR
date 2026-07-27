using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.ML.Labeling;

// =============================================================================================
//  [C4 roadmap — seconda metà di M1] META-LABELING (López de Prado, AFML §3.6).
//
//  Il modello primario decide il LATO (compro o vendo). Il meta-modello decide se AGIRE su quel
//  segnale — cioè filtra i falsi positivi. È l'unico modo onesto di "fare più operazioni utili":
//  non generare più ingressi, ma scartare quelli che il primario sbaglia sistematicamente.
//
//  L'ONESTÀ CHE VA DETTA PRIMA DI QUALUNQUE NUMERO: il meta-labeling **amplifica** un edge, non
//  lo crea. Applicato a un segnale morto alza la precision buttando via operazioni a caso, e la
//  precision di un campione più piccolo è più rumorosa, non più vera. Per questo
//  <see cref="MetaLabelingReport"/> riporta SEMPRE anche quante operazioni restano: una precision
//  che sale mentre il campione crolla non è un miglioramento, è un altro modo di non misurare
//  niente. Vale la pena usarlo solo sopra i segnali che hanno già passato i gate della piattaforma.
// =============================================================================================

/// <summary>Decisione di lato del modello primario su una barra.</summary>
public enum PrimarySignal
{
    None = 0,
    Long = 1,
    Short = -1,
}

/// <summary>
/// Un campione di addestramento per il meta-modello: il segnale primario su quella barra, e se
/// quel segnale — portato fino a una delle tre barriere — si sia rivelato giusto.
/// </summary>
public sealed record MetaLabelSample(
    int EntryIndex,
    DateTime EntryUtc,
    PrimarySignal Side,
    bool WasProfitable,
    TripleBarrierOutcome Outcome,
    decimal ReturnPercent,
    double Weight);

/// <summary>
/// Confronto fra il primario da solo e il primario filtrato dal meta-modello. Il numero di
/// operazioni superstiti è parte del verdetto, non un dettaglio.
/// </summary>
public sealed record MetaLabelingReport(
    int PrimaryCount,
    int PrimaryWins,
    decimal PrimaryMeanReturnPercent,
    int FilteredCount,
    int FilteredWins,
    decimal FilteredMeanReturnPercent)
{
    /// <summary>Quota di segnali primari andati a buon fine.</summary>
    public double PrimaryPrecision => PrimaryCount > 0 ? (double)PrimaryWins / PrimaryCount : 0;

    /// <summary>Quota di segnali andati a buon fine fra quelli che il meta-modello ha lasciato passare.</summary>
    public double FilteredPrecision => FilteredCount > 0 ? (double)FilteredWins / FilteredCount : 0;

    /// <summary>Quota dei segnali VINCENTI del primario che il filtro ha conservato.</summary>
    public double Recall => PrimaryWins > 0 ? (double)FilteredWins / PrimaryWins : 0;

    /// <summary>Quota di segnali sopravvissuti al filtro: se crolla, la precision che sale vale poco.</summary>
    public double SurvivalRate => PrimaryCount > 0 ? (double)FilteredCount / PrimaryCount : 0;

    /// <summary>
    /// Il filtro migliora davvero? Serve che la precision salga E che resti un campione non
    /// ridicolo (almeno 30 operazioni e un quinto dei segnali originali). Le due condizioni
    /// insieme sono il minimo per non farsi ingannare da un campione ritagliato.
    /// </summary>
    public bool IsImprovement =>
        FilteredPrecision > PrimaryPrecision && FilteredCount >= 30 && SurvivalRate >= 0.2;
}

/// <summary>
/// Costruzione dei campioni di meta-labeling e valutazione del filtro. Puro e deterministico.
/// </summary>
public interface IMetaLabeler
{
    /// <summary>
    /// Per ogni barra in cui il primario ha un segnale, applica il triple-barrier DAL LATO DEL
    /// SEGNALE e registra se ha funzionato. I pesi vengono dall'unicità media delle finestre.
    /// </summary>
    IReadOnlyList<MetaLabelSample> BuildSamples(
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyList<PrimarySignal> signals,
        TripleBarrierConfig config);

    /// <summary>
    /// Confronta il primario da solo col primario filtrato: passano solo i campioni la cui
    /// probabilità stimata dal meta-modello raggiunge <paramref name="threshold"/>.
    /// </summary>
    MetaLabelingReport Evaluate(
        IReadOnlyList<MetaLabelSample> samples,
        IReadOnlyList<double> probabilities,
        double threshold);
}

/// <inheritdoc cref="IMetaLabeler"/>
public sealed class MetaLabeler(ITripleBarrierLabeler? labeler = null) : IMetaLabeler
{
    private readonly ITripleBarrierLabeler _labeler = labeler ?? new TripleBarrierLabeler();

    public IReadOnlyList<MetaLabelSample> BuildSamples(
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyList<PrimarySignal> signals,
        TripleBarrierConfig config)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.Count != candles.Count)
            throw new ArgumentException("I segnali devono essere allineati per indice alle candele.", nameof(signals));
        config ??= new TripleBarrierConfig();

        // Il triple-barrier va applicato UNA VOLTA PER LATO: le barriere di un long e di uno short
        // sono orientate all'opposto, e riusare le etichette di un lato per l'altro darebbe
        // esattamente il segno sbagliato.
        var byIndexLong = IndexLabels(candles, config, OrderSide.Buy, signals, PrimarySignal.Long);
        var byIndexShort = IndexLabels(candles, config, OrderSide.Sell, signals, PrimarySignal.Short);

        var samples = new List<MetaLabelSample>();
        var weightsLong = Weights(byIndexLong, candles.Count);
        var weightsShort = Weights(byIndexShort, candles.Count);

        for (var i = 0; i < signals.Count; i++)
        {
            var side = signals[i];
            if (side == PrimarySignal.None) continue;

            var source = side == PrimarySignal.Long ? byIndexLong : byIndexShort;
            var weights = side == PrimarySignal.Long ? weightsLong : weightsShort;
            if (!source.TryGetValue(i, out var entry)) continue; // barra in coda, non risolvibile

            var (label, position) = entry;
            samples.Add(new MetaLabelSample(
                i, candles[i].TimestampUtc, side,
                // "Ha funzionato" = ha toccato il profitto. Una chiusura a tempo leggermente
                // positiva NON conta come successo: con costi e slippage non lo sarebbe.
                label.Outcome == TripleBarrierOutcome.Profit,
                label.Outcome, label.ReturnPercent,
                position < weights.Count ? weights[position] : 1.0));
        }
        return samples;
    }

    /// <summary>Etichette del lato richiesto, indicizzate per barra d'ingresso, se serve a qualche segnale.</summary>
    private Dictionary<int, (TripleBarrierLabel Label, int Position)> IndexLabels(
        IReadOnlyList<OhlcvData> candles, TripleBarrierConfig config, OrderSide side,
        IReadOnlyList<PrimarySignal> signals, PrimarySignal wanted)
    {
        var result = new Dictionary<int, (TripleBarrierLabel, int)>();
        if (!signals.Contains(wanted)) return result;   // nessun segnale di questo lato: niente da calcolare

        var sideConfig = new TripleBarrierConfig
        {
            ProfitTakePercent = config.ProfitTakePercent,
            StopLossPercent = config.StopLossPercent,
            VerticalBarrierBars = config.VerticalBarrierBars,
            Side = side,
        };
        var labels = _labeler.Label(candles, sideConfig);
        for (var k = 0; k < labels.Count; k++) result[labels[k].EntryIndex] = (labels[k], k);
        return result;
    }

    private IReadOnlyList<double> Weights(Dictionary<int, (TripleBarrierLabel Label, int Position)> indexed, int barCount)
    {
        if (indexed.Count == 0) return [];
        var ordered = indexed.Values.OrderBy(v => v.Position).Select(v => v.Label).ToList();
        return _labeler.AverageUniqueness(ordered, barCount);
    }

    public MetaLabelingReport Evaluate(
        IReadOnlyList<MetaLabelSample> samples,
        IReadOnlyList<double> probabilities,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(probabilities);
        if (probabilities.Count != samples.Count)
            throw new ArgumentException("Le probabilità devono essere allineate per indice ai campioni.", nameof(probabilities));

        var primaryWins = samples.Count(s => s.WasProfitable);
        var primaryMean = samples.Count > 0 ? samples.Average(s => s.ReturnPercent) : 0m;

        var kept = new List<MetaLabelSample>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (probabilities[i] >= threshold) kept.Add(samples[i]);
        }

        return new MetaLabelingReport(
            samples.Count, primaryWins, primaryMean,
            kept.Count, kept.Count(s => s.WasProfitable),
            kept.Count > 0 ? kept.Average(s => s.ReturnPercent) : 0m);
    }
}
