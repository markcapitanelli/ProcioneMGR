using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.ML.Labeling;

// =============================================================================================
//  [C4, consumo] L'anello che mancava fra la libreria di etichettatura e qualcosa di usabile.
//
//  Fin qui C4 era un insieme di classi corrette ma senza consumatori: registrate in DI e mai
//  chiamate da nessuno. Questo servizio fa il giro completo su dati veri — prende una strategia
//  reale, ne estrae i segnali barra per barra, li etichetta col triple-barrier, addestra il
//  meta-modello out-of-fold e restituisce un verdetto — ed è ciò che la UI mostra.
// =============================================================================================

/// <summary>Esito completo dell'analisi di meta-labeling su una strategia reale.</summary>
public sealed record MetaLabelingAnalysis(
    string StrategyName,
    int Bars,
    int PrimarySignalCount,
    int SamplesUsed,
    int SamplesScored,
    int FeatureCount,
    decimal ProfitTakePercent,
    decimal StopLossPercent,
    int VerticalBarrierBars,
    MetaLabelingReport Report,
    string Verdict,
    bool IsUsable);

/// <summary>Esegue la catena completa triple-barrier + meta-labeling su una strategia.</summary>
public interface IMetaLabelingAnalysisService
{
    Task<MetaLabelingAnalysis> RunAsync(
        IStrategy strategy,
        IReadOnlyDictionary<string, decimal> parameters,
        IReadOnlyList<OhlcvData> candles,
        int verticalBarrierBars,
        double threshold,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IMetaLabelingAnalysisService"/>
public sealed class MetaLabelingAnalysisService(
    ITripleBarrierLabeler labeler,
    IMetaLabeler metaLabeler,
    IMetaModelTrainer trainer,
    IAlphaFactorFactory factorFactory,
    ITechnicalIndicatorsService indicators) : IMetaLabelingAnalysisService
{
    /// <summary>
    /// Feature del meta-modello: gli stessi fattori che ML Lab propone di default. Non è una
    /// scelta di comodo — il meta-modello deve giudicare il segnale con informazione DIVERSA da
    /// quella che l'ha generato, e i fattori alpha sono ciò che la piattaforma già misura.
    /// </summary>
    private static readonly string[] FeatureFactors =
        ["Momentum", "RsiFactor", "MacdFactor", "RealizedVol", "RelativeVolume"];

    public async Task<MetaLabelingAnalysis> RunAsync(
        IStrategy strategy,
        IReadOnlyDictionary<string, decimal> parameters,
        IReadOnlyList<OhlcvData> candles,
        int verticalBarrierBars,
        double threshold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(candles);

        var horizon = Math.Max(1, verticalBarrierBars);
        var empty = new MetaLabelingReport(0, 0, 0m, 0, 0, 0m);

        if (candles.Count < horizon + 50)
        {
            return Fail(strategy.Name, candles.Count, horizon, empty,
                $"Servono almeno {horizon + 50} candele per un'analisi sensata: ce ne sono {candles.Count}.");
        }

        // 1. Segnali PRIMARI: la strategia vera, valutata barra per barra come nel backtest.
        var closes = candles.Select(c => c.Close).ToList();
        await strategy.InitializeAsync(closes, candles, parameters ?? new Dictionary<string, decimal>(), indicators, ct);

        var signals = new PrimarySignal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            signals[i] = strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc) switch
            {
                Signal.Long => PrimarySignal.Long,
                Signal.Short => PrimarySignal.Short,
                _ => PrimarySignal.None,   // Hold e Close non sono ingressi
            };
        }

        var signalCount = signals.Count(s => s != PrimarySignal.None);
        if (signalCount < 50)
        {
            return Fail(strategy.Name, candles.Count, horizon, empty,
                $"La strategia emette solo {signalCount} ingressi su {candles.Count} barre: troppo pochi perché un meta-modello abbia qualcosa da imparare.");
        }

        // 2. Barriere dai dati, non da numeri tondi.
        var barrier = labeler.SuggestConfig(candles, OrderSide.Buy, horizon);
        var samples = metaLabeler.BuildSamples(candles, signals, barrier);
        if (samples.Count < 50)
        {
            return Fail(strategy.Name, candles.Count, horizon, empty,
                $"Solo {samples.Count} segnali risolvibili entro l'orizzonte: allarga il periodo.", signalCount, barrier);
        }

        // 3. Feature allineate ai campioni. Una riga con un fattore in warm-up viene SCARTATA,
        //    non riempita con uno zero: uno zero finto sarebbe un valore inventato.
        var (features, kept) = BuildFeatures(candles, samples, ct);
        if (kept.Count < 50)
        {
            return Fail(strategy.Name, candles.Count, horizon, empty,
                $"Solo {kept.Count} campioni hanno tutti i fattori calcolabili: allarga il periodo.", signalCount, barrier);
        }

        // 4. Meta-modello out-of-fold e verdetto.
        var result = trainer.TrainOutOfFold(kept, features, barrier);
        if (result.SamplesScored == 0)
        {
            return Fail(strategy.Name, candles.Count, horizon, empty,
                "Il meta-modello non è addestrabile su questo campione (troppo piccolo o una sola classe di esito).",
                signalCount, barrier, kept.Count);
        }

        // I campioni rimasti senza probabilità (fold saltati) escono dal confronto invece di
        // ricevere un valore di comodo che ne falserebbe il conteggio.
        var scored = new List<MetaLabelSample>();
        var probabilities = new List<double>();
        for (var i = 0; i < kept.Count; i++)
        {
            if (result.OutOfFoldProbabilities[i] < 0) continue;
            scored.Add(kept[i]);
            probabilities.Add(result.OutOfFoldProbabilities[i]);
        }

        var report = metaLabeler.Evaluate(scored, probabilities, threshold);
        return new MetaLabelingAnalysis(
            strategy.DisplayName, candles.Count, signalCount, kept.Count, result.SamplesScored,
            features.Count > 0 ? features[0].Length : 0,
            barrier.ProfitTakePercent, barrier.StopLossPercent, horizon,
            report, BuildVerdict(report), IsUsable: true);
    }

    /// <summary>Il verdetto in parole, con il perché — la UI mostra questo accanto ai numeri.</summary>
    private static string BuildVerdict(MetaLabelingReport r)
    {
        if (r.IsImprovement)
        {
            return $"Il filtro MIGLIORA: precision da {r.PrimaryPrecision:P1} a {r.FilteredPrecision:P1} " +
                   $"tenendo {r.SurvivalRate:P0} dei segnali ({r.FilteredCount} operazioni), e batte una selezione " +
                   $"casuale della stessa numerosità di {r.SelectionZScore:F2} errori standard. " +
                   "Ricorda che il meta-labeling amplifica un edge, non lo crea: vale solo se il segnale primario ne aveva già uno.";
        }
        if (r.FilteredCount < 30)
            return $"Restano solo {r.FilteredCount} operazioni dopo il filtro: campione troppo piccolo per dire alcunché.";
        if (r.SurvivalRate < 0.2)
            return $"Il filtro scarta l'{1 - r.SurvivalRate:P0} dei segnali: una precision calcolata su quel che resta non misura più nulla.";
        if (r.FilteredPrecision <= r.PrimaryPrecision)
            return $"Nessun miglioramento: precision da {r.PrimaryPrecision:P1} a {r.FilteredPrecision:P1}. Il meta-modello non sa distinguere i segnali buoni.";
        return $"Il guadagno di precision ({r.PrimaryPrecision:P1} → {r.FilteredPrecision:P1}) NON supera quello che una " +
               $"selezione casuale della stessa numerosità produrrebbe ({r.SelectionZScore:F2} errori standard, ne servono 1,96). " +
               "È rumore, non un filtro.";
    }

    private (List<float[]> Features, List<MetaLabelSample> Kept) BuildFeatures(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<MetaLabelSample> samples, CancellationToken ct)
    {
        var series = new List<IReadOnlyList<decimal?>>(FeatureFactors.Length);
        foreach (var name in FeatureFactors)
        {
            ct.ThrowIfCancellationRequested();
            var factor = factorFactory.Create(name);
            var defaults = factor.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);
            series.Add(factor.Compute(candles, defaults));
        }

        var features = new List<float[]>(samples.Count);
        var kept = new List<MetaLabelSample>(samples.Count);
        foreach (var s in samples)
        {
            var vec = new float[series.Count];
            var complete = true;
            for (var f = 0; f < series.Count; f++)
            {
                if (series[f][s.EntryIndex] is not decimal v) { complete = false; break; }
                vec[f] = (float)v;
            }
            if (!complete) continue;
            features.Add(vec);
            kept.Add(s);
        }
        return (features, kept);
    }

    private static MetaLabelingAnalysis Fail(
        string strategyName, int bars, int horizon, MetaLabelingReport empty, string message,
        int signalCount = 0, TripleBarrierConfig? barrier = null, int samples = 0)
        => new(strategyName, bars, signalCount, samples, 0, 0,
            barrier?.ProfitTakePercent ?? 0m, barrier?.StopLossPercent ?? 0m, horizon,
            empty, message, IsUsable: false);
}
