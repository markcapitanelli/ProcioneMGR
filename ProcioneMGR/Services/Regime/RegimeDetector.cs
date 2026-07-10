using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Rilevamento dei regimi di mercato via K-means (Microsoft.ML). Singleton: il modello
/// attivo è in cache e letto in modo thread-safe; l'addestramento è serializzato con un
/// <see cref="SemaphoreSlim"/>. I servizi scoped (BacktestEngine) sono risolti per-uso.
///
/// DOPPIA NOZIONE DI REGIME (chiarimento — non è un bug):
///  • QUESTO rilevatore (K-means multi-feature persistito) è la nozione "ricca": guida la
///    pesatura regime-aware dell'ensemble e la profilatura strategia↔regime.
///  • <see cref="Backtesting.RegimeConditionalStrategy"/> usa invece un proxy causale DB-free
///    (slope della SMA) perché le strategie devono restare senza dipendenze per girare negli
///    sweep dell'OptimizationEngine e nel motore live. Le due possono discordare: è voluto.
///    Chi governa cosa: il K-means qui → allocazione/analisi; il proxy SMA → segnale intra-strategia.
///
/// K può essere FISSO (<see cref="TrainingConfiguration.NumberOfRegimes"/>) o AUTO-SELEZIONATO
/// per Silhouette (<see cref="TrainingConfiguration.AutoSelectK"/> → <see cref="SelectBestK"/>).
/// </summary>
public sealed class RegimeDetector(
    IMarketFeatureExtractor extractor,
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IStrategyFactory strategyFactory,
    ILogger<RegimeDetector> logger) : IRegimeDetector
{
    private readonly MLContext _ml = new(seed: 1); // deterministico
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new();

    // Cache del modello attivo.
    private RegimeModel? _cached;
    private float[][]? _cachedCentroids;
    private FeatureScaling? _cachedScaling;
    private List<RegimeProfile>? _cachedProfiles;

    public async Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default)
    {
        var features = await extractor.ExtractFeaturesAsync(
            config.ExchangeName, config.Symbol, config.Timeframe, config.From, config.To, ct);

        // Con auto-K la sufficienza dati va misurata sul K massimo che potremmo scegliere.
        var maxK = config.AutoSelectK ? Math.Max(config.MinRegimes, config.MaxRegimes) : config.NumberOfRegimes;
        if (features.Count < Math.Max(maxK * 30, 200))
        {
            throw new InvalidOperationException($"Dati insufficienti per il training ({features.Count} feature). Servono più candele.");
        }

        var (matrix, scaling) = FeatureNormalizer.NormalizeFeatures(features);

        // --- K-means (Microsoft.ML): K fisso, oppure auto-selezione per Silhouette ---
        int chosenK;
        float[][] centroids;
        double silhouette;
        if (config.AutoSelectK)
        {
            var (bestK, bestCentroids, bestSil, scores) =
                SelectBestK(_ml, matrix, Math.Max(2, config.MinRegimes), Math.Max(Math.Max(2, config.MinRegimes), config.MaxRegimes), config.MaxIterations, ct);
            chosenK = bestK;
            centroids = bestCentroids;
            silhouette = bestSil;
            logger.LogInformation("Auto-K per {Symbol} {Tf}: scelto K={K} (silhouette {Sil:F3}). Punteggi: {Scores}",
                config.Symbol, config.Timeframe, chosenK, silhouette,
                string.Join(", ", scores.Select(s => $"K{s.K}={s.Silhouette:F3}")));
            config.NumberOfRegimes = chosenK; // il K scelto diventa il K effettivo (visibile al chiamante)
        }
        else
        {
            chosenK = config.NumberOfRegimes;
            (centroids, silhouette) = FitKMeans(_ml, matrix, chosenK, config.MaxIterations);
        }

        if (silhouette < 0.3)
        {
            logger.LogWarning("Silhouette Score basso ({Score:F3}): clustering di qualità modesta.", silhouette);
        }

        // Assegnazione (nearest-centroid, coerente con l'inference) + smoothing.
        var rawLabels = RegimeAssignment.AssignRaw(matrix, centroids);
        var smoothed = RegimeAssignment.SmoothRolling(rawLabels, SmoothWindow(config.Timeframe), confirmFrames: 3, chosenK);

        // Profili (mean feature per regime, su assegnazione smoothed).
        var profiles = BuildProfiles(features, smoothed, chosenK);

        // Profilatura strategie per regime.
        await ProfileStrategiesAsync(config, features, smoothed, profiles, chosenK, ct);

        var regimeModel = new RegimeModel
        {
            ExchangeName = config.ExchangeName,
            Symbol = config.Symbol,
            Timeframe = config.Timeframe,
            TrainedAtUtc = DateTime.UtcNow,
            TrainingDataFrom = config.From,
            TrainingDataTo = config.To,
            NumberOfRegimes = chosenK,
            CentroidsJson = JsonSerializer.Serialize(centroids, Json),
            FeatureScalingJson = JsonSerializer.Serialize(scaling, Json),
            RegimeProfilesJson = JsonSerializer.Serialize(profiles, Json),
            SilhouetteScore = silhouette,
            IsActive = true,
        };

        if (activate)
        {
            await SaveModelAsync(regimeModel, ct);
            UpdateCache(regimeModel, centroids, scaling, profiles);
        }

        logger.LogInformation("Modello regime addestrato per {Symbol} {Tf}: K={K}, Silhouette={Sil:F3} (attivato={Act}).",
            config.Symbol, config.Timeframe, chosenK, silhouette, activate);
        return regimeModel;
    }

    public async Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default)
    {
        await SaveModelAsync(model, ct);
        var centroids = JsonSerializer.Deserialize<float[][]>(model.CentroidsJson, Json)!;
        var scaling = JsonSerializer.Deserialize<FeatureScaling>(model.FeatureScalingJson, Json)!;
        var profiles = JsonSerializer.Deserialize<List<RegimeProfile>>(model.RegimeProfilesJson, Json)!;
        UpdateCache(model, centroids, scaling, profiles);
        logger.LogInformation("Modello regime attivato per {Symbol} {Tf} (Silhouette {Sil:F3}).",
            model.Symbol, model.Timeframe, model.SilhouetteScore);
    }

    public async Task<int> PredictRegimeAsync(MarketFeatures features, CancellationToken ct = default)
    {
        var loaded = await EnsureCacheAsync(ct);
        if (loaded is null)
        {
            return -1;
        }
        var normalized = loaded.Value.Scaling.Transform(features.ToClusteringVector());
        return RegimeAssignment.NearestCentroid(normalized, loaded.Value.Centroids);
    }

    public async Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default)
    {
        var loaded = await EnsureCacheAsync(ct);
        if (loaded is null || features.Count == 0)
        {
            return features;
        }

        var matrix = features.Select(f => loaded.Value.Scaling.Transform(f.ToClusteringVector())).ToArray();
        var raw = RegimeAssignment.AssignRaw(matrix, loaded.Value.Centroids);
        var smoothed = RegimeAssignment.SmoothRolling(raw, SmoothWindow(_cached?.Timeframe ?? "1h"), confirmFrames: 3, loaded.Value.Centroids.Length);

        var labelByRegime = loaded.Value.Profiles.ToDictionary(p => p.RegimeId, p => p.SuggestedLabel);
        for (var i = 0; i < features.Count; i++)
        {
            features[i].RegimeId = smoothed[i];
            features[i].RegimeLabel = labelByRegime.GetValueOrDefault(smoothed[i], $"Regime {smoothed[i]}");
        }
        return features;
    }

    public async Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RegimeModels
            .Where(m => m.IsActive)
            .OrderByDescending(m => m.TrainedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    // ---------------------------------------------------------------- K-means + auto-selezione di K

    /// <summary>
    /// Addestra un K-means (Microsoft.ML) su <paramref name="matrix"/> normalizzata e restituisce
    /// i centroidi nello spazio normalizzato più il Silhouette Score dell'assegnazione nearest-centroid.
    /// Puro rispetto a DB e stato d'istanza (usa solo l'<see cref="MLContext"/> passato) → testabile.
    /// </summary>
    internal static (float[][] Centroids, double Silhouette) FitKMeans(MLContext ml, float[][] matrix, int k, int maxIterations)
    {
        var rows = matrix.Select(m => new FeatureRow { Features = m }).ToList();
        var dv = ml.Data.LoadFromEnumerable(rows);
        var options = new KMeansTrainer.Options
        {
            NumberOfClusters = k,
            MaximumNumberOfIterations = maxIterations,
            FeatureColumnName = nameof(FeatureRow.Features),
        };
        var model = ml.Clustering.Trainers.KMeans(options).Fit(dv);

        VBuffer<float>[] centroidBuffers = default!;
        model.Model.GetClusterCentroids(ref centroidBuffers, out _);
        var centroids = centroidBuffers.Select(v => v.DenseValues().ToArray()).ToArray();

        var labels = RegimeAssignment.AssignRaw(matrix, centroids);
        var silhouette = RegimeAssignment.Silhouette(matrix, labels, k, sampleSize: 2000, seed: 1);
        return (centroids, silhouette);
    }

    /// <summary>
    /// Auto-selezione di K: addestra un K-means per ogni K in [<paramref name="minK"/>..<paramref name="maxK"/>]
    /// e sceglie quello col Silhouette Score massimo. Il gap-statistic sarebbe l'alternativa; la silhouette
    /// è più economica e sufficiente qui. Restituisce anche l'elenco dei punteggi (per log/diagnostica).
    /// A parità di silhouette preferisce il K più piccolo (modello più parsimonioso).
    /// </summary>
    internal static (int BestK, float[][] Centroids, double Silhouette, List<(int K, double Silhouette)> Scores)
        SelectBestK(MLContext ml, float[][] matrix, int minK, int maxK, int maxIterations, CancellationToken ct = default)
    {
        var scores = new List<(int K, double Silhouette)>();
        int bestK = minK;
        float[][] bestCentroids = default!;
        var bestSil = double.NegativeInfinity;

        for (var k = minK; k <= maxK; k++)
        {
            ct.ThrowIfCancellationRequested();
            var (centroids, silhouette) = FitKMeans(ml, matrix, k, maxIterations);
            scores.Add((k, silhouette));
            if (silhouette > bestSil) // > (non >=): a parità tiene il K più piccolo già trovato
            {
                bestSil = silhouette;
                bestK = k;
                bestCentroids = centroids;
            }
        }

        return (bestK, bestCentroids, bestSil, scores);
    }

    // ---------------------------------------------------------------- profili

    private static List<RegimeProfile> BuildProfiles(List<MarketFeatures> features, int[] labels, int k)
    {
        var profiles = new List<RegimeProfile>();
        for (var r = 0; r < k; r++)
        {
            var idxs = Enumerable.Range(0, features.Count).Where(i => labels[i] == r).ToList();
            var profile = new RegimeProfile { RegimeId = r, SampleCount = idxs.Count };
            if (idxs.Count > 0)
            {
                profile.MeanVolatility = idxs.Average(i => (double)features[i].Volatility);
                profile.MeanTrendStrength = idxs.Average(i => (double)features[i].TrendStrength);
                profile.MeanTrendDirection = idxs.Average(i => (double)features[i].TrendDirection);
                profile.MeanVolumeRatio = idxs.Average(i => (double)features[i].VolumeRatio);
                profile.MeanAtrNormalized = idxs.Average(i => (double)features[i].AtrNormalized);
                profile.MeanRsiLevel = idxs.Average(i => (double)features[i].RsiLevel);
                profile.MeanDistanceFromMa = idxs.Average(i => (double)features[i].DistanceFromMa);
            }
            profiles.Add(profile);
        }

        // Label suggerite (relative alle altre per il livello di volatilità).
        var vols = profiles.Select(p => p.MeanVolatility).OrderBy(v => v).ToList();
        var lowT = vols[Math.Min(vols.Count - 1, k / 3)];
        var highT = vols[Math.Min(vols.Count - 1, 2 * k / 3)];
        foreach (var p in profiles)
        {
            p.SuggestedLabel = GenerateLabel(p, lowT, highT);
        }
        return profiles;
    }

    private static string GenerateLabel(RegimeProfile p, double lowVolT, double highVolT)
    {
        var volLabel = p.MeanVolatility <= lowVolT ? "Low-Vol" : p.MeanVolatility >= highVolT ? "High-Vol" : "Mid-Vol";
        var dir = p.MeanTrendDirection;

        // Trend debole + alta volatilità => mercato "choppy".
        if (Math.Abs(dir) <= 0.15 && p.MeanVolatility >= highVolT)
        {
            return "Choppy/Volatile";
        }
        if (Math.Abs(dir) <= 0.15)
        {
            return "Sideways";
        }
        return dir > 0 ? $"Trend Up {volLabel}" : $"Bear {volLabel}";
    }

    // ---------------------------------------------------------------- profilatura strategie

    private async Task ProfileStrategiesAsync(
        TrainingConfiguration config, List<MarketFeatures> features, int[] labels, List<RegimeProfile> profiles, int k, CancellationToken ct)
    {
        // Candele allineate alle feature (stesso insieme e stesso ordine).
        using var scope = scopeFactory.CreateScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();

        var firstTs = features[0].Timestamp;
        var lastTs = features[^1].Timestamp;
        List<OhlcvData> candles;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData
                .Where(c => c.Symbol == config.Symbol && c.Timeframe == config.Timeframe
                            && c.TimestampUtc >= firstTs && c.TimestampUtc <= lastTs)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }
        if (candles.Count != features.Count)
        {
            logger.LogWarning("Mismatch candele/feature ({C} vs {F}) nella profilatura: salto.", candles.Count, features.Count);
            return;
        }

        var tsToIndex = new Dictionary<DateTime, int>(candles.Count);
        for (var j = 0; j < candles.Count; j++)
        {
            tsToIndex[DateTime.SpecifyKind(candles[j].TimestampUtc, DateTimeKind.Utc)] = j;
        }

        var ppy = Statistics.PeriodsPerYear(config.Timeframe);

        foreach (var proto in strategyFactory.Prototypes)
        {
            var cfg = new BacktestConfiguration
            {
                ExchangeName = config.ExchangeName,
                Symbol = config.Symbol,
                Timeframe = config.Timeframe,
                InitialCapital = 10_000m,
                PositionSizePercent = 100m,
                FeePercent = 0.1m,
                StrategyName = proto.Name,
                StrategyParameters = proto.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default),
            };

            BacktestResult res;
            try { res = await engine.RunBacktestAsync(cfg, candles, ct); }
            catch { continue; }

            var eq = res.EquityCurve;
            // Rendimenti per-candela bucketizzati per regime.
            var regimeReturns = new List<decimal>[k];
            for (var r = 0; r < k; r++) regimeReturns[r] = new();
            for (var j = 1; j < eq.Count && j < labels.Length; j++)
            {
                var prev = eq[j - 1].Capital;
                var ret = prev > 0m ? (eq[j].Capital - prev) / prev : 0m;
                regimeReturns[labels[j]].Add(ret);
            }

            // Trade bucketizzati per regime (per entry time).
            var tradeCount = new int[k];
            var tradeWins = new int[k];
            var tradePnlSum = new decimal[k];
            foreach (var tr in res.Trades)
            {
                if (tsToIndex.TryGetValue(DateTime.SpecifyKind(tr.EntryTime, DateTimeKind.Utc), out var j))
                {
                    var r = labels[j];
                    tradeCount[r]++;
                    if (tr.Pnl > 0m) tradeWins[r]++;
                    tradePnlSum[r] += tr.PnlPercent;
                }
            }

            for (var r = 0; r < k; r++)
            {
                profiles[r].StrategyPerformances[proto.Name] = new StrategyPerformanceInRegime
                {
                    StrategyName = proto.Name,
                    AverageSharpe = SharpeFromReturns(regimeReturns[r], ppy),
                    AverageReturn = tradeCount[r] > 0 ? tradePnlSum[r] / tradeCount[r] : 0m,
                    WinRate = tradeCount[r] > 0 ? (decimal)tradeWins[r] / tradeCount[r] * 100m : 0m,
                    TotalTrades = tradeCount[r],
                };
            }
        }
    }

    /// <summary>Finestra (in candele) del voto di maggioranza per lo smoothing (~3 giorni).</summary>
    private static int SmoothWindow(string timeframe)
    {
        var perDay = Math.Max(1, Statistics.PeriodsPerYear(timeframe) / 365);
        return 5 * perDay;
    }

    private static decimal SharpeFromReturns(List<decimal> returns, int ppy)
    {
        if (returns.Count < 3) return 0m;
        var eq = new List<EquityPoint>(returns.Count + 1);
        var cap = 100m;
        eq.Add(new EquityPoint { Capital = cap });
        foreach (var r in returns)
        {
            cap *= 1m + r;
            eq.Add(new EquityPoint { Capital = cap });
        }
        return Statistics.SharpeRatio(eq, ppy);
    }

    // ---------------------------------------------------------------- persistenza + cache

    private async Task SaveModelAsync(RegimeModel model, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var previous = await db.RegimeModels
            .Where(m => m.IsActive && m.ExchangeName == model.ExchangeName && m.Symbol == model.Symbol && m.Timeframe == model.Timeframe)
            .ToListAsync(ct);
        foreach (var p in previous) p.IsActive = false;

        db.RegimeModels.Add(model);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(float[][] Centroids, FeatureScaling Scaling, List<RegimeProfile> Profiles)?> EnsureCacheAsync(CancellationToken ct)
    {
        if (_cached is not null && _cachedCentroids is not null && _cachedScaling is not null && _cachedProfiles is not null)
        {
            return (_cachedCentroids, _cachedScaling, _cachedProfiles);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is null)
            {
                var latest = await LoadLatestModelAsync(ct);
                if (latest is null) return null;
                var centroids = JsonSerializer.Deserialize<float[][]>(latest.CentroidsJson, Json)!;
                var scaling = JsonSerializer.Deserialize<FeatureScaling>(latest.FeatureScalingJson, Json)!;
                var profiles = JsonSerializer.Deserialize<List<RegimeProfile>>(latest.RegimeProfilesJson, Json)!;
                UpdateCache(latest, centroids, scaling, profiles);
            }
            return (_cachedCentroids!, _cachedScaling!, _cachedProfiles!);
        }
        finally { _gate.Release(); }
    }

    private void UpdateCache(RegimeModel model, float[][] centroids, FeatureScaling scaling, List<RegimeProfile> profiles)
    {
        _cached = model;
        _cachedCentroids = centroids;
        _cachedScaling = scaling;
        _cachedProfiles = profiles;
    }

    // ML.NET schema row.
    private sealed class FeatureRow
    {
        [VectorType(FeatureScaling.FeatureCount)]
        public float[] Features { get; set; } = [];
    }
}
