using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Opzioni del trigger contestuale (Fase 2, PRD Autonomia §5), sezione <c>RegimeTrigger</c>.</summary>
public sealed class RegimeTriggerOptions
{
    /// <summary>
    /// Default ON: il trigger è additivo e parla SOLO col planner, che ha già il suo gate
    /// (<c>Campaign:Enabled</c> default OFF) — senza campagne abilitate non succede nulla.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadenza del check (letta all'avvio del worker).</summary>
    public int CheckIntervalMinutes { get; set; } = 30;

    /// <summary>Cooldown tra due wake (PRD: default 6h): il regime non "cambia" ogni mezz'ora.</summary>
    public int CooldownHours { get; set; } = 6;

    /// <summary>
    /// Banda di volatilità: scatta se la realized esce da [forecast/k, forecast×k] rispetto al
    /// forecast GARCH dell'ultimo run (PRD: es. realized &gt; 1,5× forecast — l'espansione attesa
    /// su SOL; la compressione oltre banda è a sua volta un cambio di contesto).
    /// </summary>
    public double VolBandMultiple { get; set; } = 1.5;
}

/// <summary>Esito di un check del trigger (con i valori osservati, per log/notifica/test).</summary>
public sealed class RegimeTriggerCheck
{
    public bool Triggered { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int? BaselineRegimeId { get; init; }
    public int? CurrentRegimeId { get; init; }
    public double? BaselineForecastVolatility { get; init; }
    public double? RealizedVolatility { get; init; }
    public Guid BaselineRunId { get; init; }
}

/// <summary>
/// Rileva un cambio di contesto rispetto all'ULTIMO run completato delle campagne abilitate
/// (Fase 2, PRD Autonomia §5): la caccia gira alle 03:00, ma il regime cambia quando cambia.
/// Riusa SOLO calcoli esistenti: cluster K-means corrente (IMarketFeatureExtractor +
/// IRegimeDetector, stesso percorso dell'EnsembleManager) contro il CurrentRegimeId persistito
/// nel checkpoint del run; volatilità realizzata (stddev dei log-rendimenti recenti, per-periodo)
/// contro il forecast GARCH a 24 passi dello stesso run.
/// </summary>
public interface IRegimeChangeDetector
{
    /// <summary>Null quando manca la base di confronto (nessun run di campagna completato, niente dati/modello).</summary>
    Task<RegimeTriggerCheck?> CheckAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IRegimeChangeDetector"/>
public sealed class RegimeChangeDetector(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMarketFeatureExtractor featureExtractor,
    IRegimeDetector regimeDetector,
    IOptionsMonitor<RegimeTriggerOptions> options,
    ILogger<RegimeChangeDetector> logger) : IRegimeChangeDetector
{
    /// <summary>Finestra di estrazione feature: abbastanza da coprire warmup (50) + smoothing anche su 4h/1d.</summary>
    private const int FeatureLookbackDays = 30;

    /// <summary>Rendimenti usati per la realized vol: stessa scala del forecast GARCH a 24 passi.</summary>
    private const int RealizedVolWindow = 24;

    public async Task<RegimeTriggerCheck?> CheckAsync(CancellationToken ct = default)
    {
        // 1. Baseline: l'ultimo run COMPLETATO tra quelli lanciati dalle campagne abilitate.
        PipelineRun? baselineRun;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var campaigns = await db.VettingCampaigns.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
            var runIds = campaigns
                .SelectMany(c => CampaignPlanner.ParseConfigStates(c.ConfigStatesJson))
                .Where(s => s.LastRunId is not null)
                .Select(s => s.LastRunId!.Value)
                .Distinct()
                .ToList();
            if (runIds.Count == 0) return null;

            baselineRun = await db.PipelineRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id) && r.Status == "Completed")
                .OrderByDescending(r => r.CompletedAt)
                .FirstOrDefaultAsync(ct);
        }
        if (baselineRun is null) return null;

        var baseline = DeserializeContext(baselineRun.ContextSnapshotJson);
        if (baseline is null || baseline.Universe.Count == 0) return null;
        var primary = baseline.PrimarySeries;

        // 2. Stato CORRENTE della serie primaria del run (stesso percorso dell'EnsembleManager).
        var to = DateTime.UtcNow;
        var features = await featureExtractor.ExtractFeaturesAsync(
            baseline.ExchangeName, primary.Symbol, primary.Timeframe, to.AddDays(-FeatureLookbackDays), to, ct);
        if (features.Count == 0)
        {
            logger.LogDebug("Trigger regime: nessuna feature per {Symbol} {Timeframe}, check saltato.", primary.Symbol, primary.Timeframe);
            return null;
        }

        int? baselineRegime = baseline.Regimes is { CurrentRegimeId: >= 0 } ? baseline.Regimes.CurrentRegimeId : null;
        int? currentRegime = null;
        if (baselineRegime is not null)
        {
            var model = await regimeDetector.LoadLatestModelAsync(ct);
            if (model is not null && model.Symbol == primary.Symbol && model.Timeframe == primary.Timeframe)
            {
                await regimeDetector.LabelFeaturesAsync(features, ct);
                currentRegime = features.LastOrDefault(f => f.RegimeId is not null)?.RegimeId;
            }
        }

        var realized = ComputeRealizedVolatility(features.Select(f => f.Price).ToList(), RealizedVolWindow);
        double? forecast = baseline.Volatility is { ForecastVolatility24: > 0 } ? baseline.Volatility.ForecastVolatility24 : null;

        return Evaluate(baselineRegime, currentRegime, forecast, realized,
            Math.Max(1.01, options.CurrentValue.VolBandMultiple), baselineRun.Id);
    }

    /// <summary>Decisione PURA (testabile senza DB/modelli): cluster cambiato o vol fuori banda.</summary>
    public static RegimeTriggerCheck Evaluate(
        int? baselineRegime, int? currentRegime, double? forecastVol, double? realizedVol,
        double volBandMultiple, Guid baselineRunId)
    {
        var reasons = new List<string>();

        if (baselineRegime is int b && currentRegime is int c && b != c)
        {
            reasons.Add($"cluster K-means cambiato {b} → {c} rispetto all'ultimo run della campagna");
        }

        if (forecastVol is double f && f > 0 && realizedVol is double r && r > 0)
        {
            if (r > f * volBandMultiple)
            {
                reasons.Add(FormattableString.Invariant(
                    $"vol realizzata {r:0.####} oltre {volBandMultiple:0.##}× il forecast GARCH {f:0.####} (espansione)"));
            }
            else if (r < f / volBandMultiple)
            {
                reasons.Add(FormattableString.Invariant(
                    $"vol realizzata {r:0.####} sotto il forecast GARCH {f:0.####}/{volBandMultiple:0.##} (compressione)"));
            }
        }

        return new RegimeTriggerCheck
        {
            Triggered = reasons.Count > 0,
            Reason = string.Join("; ", reasons),
            BaselineRegimeId = baselineRegime,
            CurrentRegimeId = currentRegime,
            BaselineForecastVolatility = forecastVol,
            RealizedVolatility = realizedVol,
            BaselineRunId = baselineRunId,
        };
    }

    /// <summary>Stddev per-periodo dei log-rendimenti sulle ultime <paramref name="window"/> osservazioni. Pura.</summary>
    public static double? ComputeRealizedVolatility(IReadOnlyList<decimal> prices, int window)
    {
        if (prices.Count < window + 1) return null;
        var returns = new List<double>(window);
        for (var i = prices.Count - window; i < prices.Count; i++)
        {
            var prev = (double)prices[i - 1];
            var curr = (double)prices[i];
            if (prev <= 0 || curr <= 0) return null;
            returns.Add(Math.Log(curr / prev));
        }
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        return Math.Sqrt(variance);
    }

    private static PipelineContext? DeserializeContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<PipelineContext>(json); }
        catch { return null; }
    }
}
