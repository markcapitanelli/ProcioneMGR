using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.ML;

/// <summary>Configurazione della selezione di feature per Information Coefficient.</summary>
public sealed class IcFeatureSelectionConfig
{
    /// <summary>Orizzonte del rendimento forward target dell'IC (coerente col dataset ML che seguirà).</summary>
    public int ForwardHorizon { get; set; } = 1;

    /// <summary>Quante feature tenere al massimo (le prime per |IC|).</summary>
    public int TopN { get; set; } = 20;

    /// <summary>|IC| minimo perché una feature sia tenuta (scarta i fattori-rumore).</summary>
    public double MinAbsIc { get; set; }

    /// <summary>Information Ratio minimo (stabilità dell'IC nel tempo). 0 = nessun filtro.</summary>
    public double MinInformationRatio { get; set; }

    /// <summary>
    /// Se true, tiene solo i fattori il cui IC rolling è coerente in segno con l'IC full-sample nella
    /// maggioranza delle finestre (IcConsistency ≥ 0.5): evita i fattori "a caso" col segno instabile.
    /// </summary>
    public bool RequireConsistentSign { get; set; }
}

/// <summary>Un fattore candidato con la sua valutazione IC — l'unità ordinabile della selezione.</summary>
public sealed record ScoredFactor(FactorSpec Spec, FactorEvaluationResult Evaluation)
{
    /// <summary>|IC| full-sample: il criterio primario di ordinamento (un segnale vale sia positivo che negativo).</summary>
    public double AbsIc => Math.Abs(Evaluation.InformationCoefficient);
}

/// <summary>
/// Selezione automatica delle feature per <b>Information Coefficient</b> (Fase 3): ordina/filtra un
/// insieme di <see cref="FactorSpec"/> candidati usando il <see cref="IFactorEvaluator"/> ESISTENTE
/// (IC di Spearman, Information Ratio, consistenza), così la scelta delle feature per i modelli ML
/// smette di essere manuale e diventa guidata dalla misura. L'output è un sottoinsieme di
/// <see cref="FactorSpec"/> pronto per <see cref="IDatasetBuilder"/> — zero modifiche a valle.
/// Deterministico (l'IC è deterministico). Rif. Fase 3 §3.3 (strumenti sottoutilizzati).
/// </summary>
public interface IIcFeatureSelector
{
    /// <summary>Tutti i candidati valutati e ordinati per |IC| decrescente (nessun filtro, per la UI).</summary>
    IReadOnlyList<ScoredFactor> Rank(
        IReadOnlyList<FactorSpec> candidates, IReadOnlyList<OhlcvData> candles, IcFeatureSelectionConfig config);

    /// <summary>I candidati che superano i filtri, ordinati per |IC| e troncati a TopN (per il dataset).</summary>
    IReadOnlyList<FactorSpec> Select(
        IReadOnlyList<FactorSpec> candidates, IReadOnlyList<OhlcvData> candles, IcFeatureSelectionConfig config);
}

/// <inheritdoc cref="IIcFeatureSelector"/>
public sealed class IcFeatureSelector : IIcFeatureSelector
{
    // Come DatasetBuilder, usa direttamente FactorEvaluator (stateless/deterministico).
    private readonly IFactorEvaluator _evaluator = new FactorEvaluator();

    public IReadOnlyList<ScoredFactor> Rank(
        IReadOnlyList<FactorSpec> candidates, IReadOnlyList<OhlcvData> candles, IcFeatureSelectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(candles);
        config ??= new IcFeatureSelectionConfig();

        var evalConfig = new FactorEvaluationConfig { ForwardHorizon = Math.Max(1, config.ForwardHorizon) };
        var scored = new List<ScoredFactor>(candidates.Count);
        foreach (var spec in candidates)
        {
            var eval = _evaluator.Evaluate(spec.Factor, candles, spec.Parameters, evalConfig);
            scored.Add(new ScoredFactor(spec, eval));
        }
        // Ordinamento stabile e deterministico: |IC| desc, poi nome per rompere i pareggi.
        return scored
            .OrderByDescending(s => s.AbsIc)
            .ThenBy(s => s.Spec.FeatureName, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<FactorSpec> Select(
        IReadOnlyList<FactorSpec> candidates, IReadOnlyList<OhlcvData> candles, IcFeatureSelectionConfig config)
    {
        config ??= new IcFeatureSelectionConfig();
        return Rank(candidates, candles, config)
            .Where(s => s.AbsIc >= config.MinAbsIc
                        && Math.Abs(s.Evaluation.InformationRatio) >= config.MinInformationRatio
                        && (!config.RequireConsistentSign || s.Evaluation.IcConsistency >= 0.5))
            .Take(Math.Max(1, config.TopN))
            .Select(s => s.Spec)
            .ToList();
    }
}
