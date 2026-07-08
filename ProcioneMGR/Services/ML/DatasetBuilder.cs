using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Services.ML;

/// <summary>Implementazione di <see cref="IDatasetBuilder"/>. Pura/stateless -> registrabile Singleton.</summary>
public sealed class DatasetBuilder : IDatasetBuilder
{
    // Cache dei fattori (Fase 4): condivisa via DI in produzione; interna se costruito senza DI.
    private readonly Alpha.IFactorCache _factorCache;

    public DatasetBuilder(Alpha.IFactorCache? factorCache = null)
        => _factorCache = factorCache ?? new Alpha.FactorCache();

    public MlDataset Build(IReadOnlyList<OhlcvData> candles, IReadOnlyList<FactorSpec> factors, int forwardHorizon,
        IReadOnlyList<int>? regimeIds = null, int regimeCount = 0)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(factors);
        if (factors.Count == 0) throw new ArgumentException("Serve almeno un fattore.", nameof(factors));
        if (forwardHorizon < 1) throw new ArgumentOutOfRangeException(nameof(forwardHorizon));

        var withRegime = regimeCount > 0 && regimeIds is not null;
        if (withRegime && regimeIds!.Count != candles.Count)
            throw new ArgumentException("regimeIds deve essere allineato per indice a candles.", nameof(regimeIds));

        var n = candles.Count;

        // Ogni fattore calcolato UNA VOLTA su tutta la serie (contratto anti-look-ahead di IAlphaFactor),
        // attraverso la cache: gli stessi (fattore+parametri, candele) non si ricalcolano.
        var featureSeries = new IReadOnlyList<decimal?>[factors.Count];
        for (var f = 0; f < factors.Count; f++)
        {
            featureSeries[f] = _factorCache.GetOrCompute(factors[f].Factor, factors[f].Parameters, candles);
        }

        var forward = new FactorEvaluator().ForwardReturns(candles, forwardHorizon);

        var rows = new List<FeatureRow>();
        var timestamps = new List<DateTime>();
        for (var i = 0; i < n; i++)
        {
            if (!forward[i].HasValue) continue;

            var vec = new float[factors.Count];
            var complete = true;
            for (var f = 0; f < factors.Count; f++)
            {
                var v = featureSeries[f][i];
                if (!v.HasValue) { complete = false; break; }
                vec[f] = (float)v.Value;
            }
            if (!complete) continue;

            var finalVec = withRegime ? RegimeAugmentation.Append(vec, regimeIds![i], regimeCount) : vec;
            rows.Add(new FeatureRow { Features = finalVec, Label = (float)forward[i]!.Value });
            timestamps.Add(candles[i].TimestampUtc);
        }

        var featureNames = factors.Select(f => f.FeatureName).ToList();
        if (withRegime) featureNames.AddRange(RegimeAugmentation.OneHotNames(regimeCount));

        return new MlDataset
        {
            Rows = rows,
            FeatureNames = featureNames,
            Timestamps = timestamps,
        };
    }
}
