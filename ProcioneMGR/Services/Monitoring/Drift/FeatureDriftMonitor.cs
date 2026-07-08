using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// Implementazione di <see cref="IFeatureDriftMonitor"/>. Ricostruisce i fattori del modello dal
/// suo <c>FactorsJson</c> (stesso round-trip di <c>SavedMlModel</c> in /ml), calcola le serie sul
/// periodo di training (reference, letto dal DB) e sulle candele recenti (current), e passa i due
/// campioni a ogni detector. Il calcolo dei fattori rispetta l'invariante anti-look-ahead di
/// <see cref="IAlphaFactor"/> (nessuna feature legge dati futuri).
/// </summary>
public sealed class FeatureDriftMonitor : IFeatureDriftMonitor
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IAlphaFactorFactory _factorFactory;
    private readonly IReadOnlyList<IFeatureDriftDetector> _detectors;

    public FeatureDriftMonitor(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IAlphaFactorFactory factorFactory,
        IEnumerable<IFeatureDriftDetector> detectors)
    {
        _dbFactory = dbFactory;
        _factorFactory = factorFactory;
        _detectors = detectors.ToList();
    }

    public async Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
        SavedMlModel model,
        IReadOnlyList<OhlcvData> recentCandles,
        DriftThresholds? thresholds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        recentCandles ??= [];
        thresholds ??= new DriftThresholds();

        var specs = JsonSerializer.Deserialize<List<SavedFactorSpecDto>>(model.FactorsJson) ?? [];
        if (specs.Count == 0) return [];

        // Reference = candele della finestra di training del modello (stesso symbol/timeframe).
        List<OhlcvData> referenceCandles;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            referenceCandles = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                            && c.TimestampUtc >= model.TrainingDataFrom && c.TimestampUtc <= model.TrainingDataTo)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }

        var reports = new List<FactorDriftReport>(specs.Count);
        foreach (var spec in specs)
        {
            ct.ThrowIfCancellationRequested();

            IAlphaFactor factor;
            try { factor = _factorFactory.Create(spec.FactorName); }
            catch (NotSupportedException)
            {
                reports.Add(new FactorDriftReport
                {
                    FeatureName = spec.FeatureName,
                    Results = [new DriftResult("—", 0d, null, DriftSeverity.None, $"Fattore '{spec.FactorName}' non più disponibile.")],
                });
                continue;
            }

            var refValues = NonNull(factor.Compute(referenceCandles, spec.Parameters));
            var curValues = NonNull(factor.Compute(recentCandles, spec.Parameters));

            var results = _detectors.Select(d => d.Detect(refValues, curValues, thresholds)).ToList();
            reports.Add(new FactorDriftReport
            {
                FeatureName = spec.FeatureName,
                ReferenceCount = refValues.Count,
                CurrentCount = curValues.Count,
                Results = results,
            });
        }

        return reports;
    }

    private static List<decimal> NonNull(IReadOnlyList<decimal?> series)
    {
        var r = new List<decimal>(series.Count);
        foreach (var v in series) if (v.HasValue) r.Add(v.Value);
        return r;
    }
}
