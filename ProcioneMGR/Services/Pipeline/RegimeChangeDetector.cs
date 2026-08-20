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
/// contro il forecast a 24 passi dello stesso run.
///
/// [A5, 2026-08-20] Il confronto di volatilità regge solo se le due misure hanno la stessa base
/// temporale. Dal 2026-07-26 al 2026-08-20 non l'hanno avuta: il ramo log-HAR scriveva un sigma
/// GIORNALIERO in <c>ForecastVolatility24</c> mentre qui la realizzata è per candela, quindi su ogni
/// timeframe intraday il ramo «compressione» era vero per aritmetica (r/f = 0,20 su 1h) e il ramo
/// «espansione» — il caso per cui questa classe è nata — chiedeva un'esplosione di 7,3× invece di
/// 1,5×. La correzione sta a monte (AnalysisStages riporta il log-HAR a scala di candela): qui non
/// c'è conversione, e non deve essercene — se un domani il forecast tornasse su un'altra scala, il
/// posto giusto per accorgersene resta il contratto di <c>VolatilityOutput</c>.
/// </summary>
public interface IRegimeChangeDetector
{
    /// <summary>Null quando manca la base di confronto (nessun run di campagna completato, niente dati/modello).</summary>
    Task<RegimeTriggerCheck?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// [A5b, 2026-08-20] I due bracci del trigger sanno produrre un verdetto, oggi? Serve a chi deve
    /// dire se una campagna in <c>WaitingForTrigger</c> può davvero ripartire. Vedi
    /// <see cref="RegimeTriggerHealth"/> per il motivo per cui «acceso» non basta.
    /// </summary>
    Task<RegimeTriggerHealth> DescribeHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// [A5b] Armamento dei due bracci del trigger contestuale, con le ragioni di chi è cieco.
///
/// <para><b>Perché esiste.</b> Una campagna che ha esaurito la rotazione entra in
/// <c>WaitingForTrigger</c> e da lì riparte SOLO con un wake del trigger. Fino al 2026-08-20 la sonda
/// di stato guardava il solo flag <c>RegimeTrigger:Enabled</c> e concludeva «un wake la rimette in
/// rotazione da solo»: una promessa che regge unicamente se almeno un braccio è in grado di
/// esprimersi. Il braccio K-means pretende che lo snapshot del run di baseline porti un
/// <c>CurrentRegimeId</c> <b>e</b> che esista un modello di regime ATTIVO per quella serie; il braccio
/// volatilità pretende un forecast positivo nello snapshot. Se cadono entrambi, la campagna resta
/// ferma per sempre e nessuna superficie lo dice — la classe di difetto «controllo che rassicura a
/// prescindere dalla realtà», già pagata col Filone E.</para>
///
/// <para>Il rischio è diventato concreto proprio il 2026-08-20: fino a quel giorno il braccio
/// volatilità scattava per un errore di unità (vedi [A5] sopra), quindi le campagne si svegliavano
/// comunque — e la cecità di un braccio sarebbe passata inosservata. Tolta la sveglia spuria, la
/// domanda «può ancora ripartire?» va posta e mostrata.</para>
/// </summary>
public sealed record RegimeTriggerHealth(bool RegimeArmArmed, bool VolatilityArmArmed, IReadOnlyList<string> Reasons)
{
    /// <summary>Vero se almeno un braccio può esprimersi: sotto questa condizione un wake è possibile.</summary>
    public bool AnyArmArmed => RegimeArmArmed || VolatilityArmArmed;

    /// <summary>Nessuna base di confronto: nemmeno un run di campagna completato da cui partire.</summary>
    public static RegimeTriggerHealth NoBaseline { get; } =
        new(false, false, ["nessun run di campagna completato: manca la base di confronto"]);
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

    /// <summary>
    /// Rendimenti usati per la realized vol: stessa scala per-candela del forecast a 24 passi
    /// (vale per entrambe le sorgenti, <c>garch</c> e <c>har-log-rv</c>, dopo [A1]).
    /// </summary>
    private const int RealizedVolWindow = 24;

    /// <summary>
    /// [A5b] L'ultimo run COMPLETATO fra quelli lanciati dalle campagne abilitate, deserializzato.
    /// Estratto perché <see cref="CheckAsync"/> e <see cref="DescribeHealthAsync"/> devono guardare
    /// ESATTAMENTE la stessa baseline: due query diverse darebbero due verdetti sulla stessa domanda.
    /// </summary>
    private async Task<(PipelineRun Run, PipelineContext Context)?> LoadBaselineAsync(CancellationToken ct)
    {
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
        return baseline is null || baseline.Universe.Count == 0 ? null : (baselineRun, baseline);
    }

    /// <inheritdoc />
    public async Task<RegimeTriggerHealth> DescribeHealthAsync(CancellationToken ct = default)
    {
        var loaded = await LoadBaselineAsync(ct);
        if (loaded is null)
        {
            return RegimeTriggerHealth.NoBaseline;
        }
        var (_, baseline) = loaded.Value;

        var primary = baseline.PrimarySeries;
        var reasons = new List<string>();

        // Braccio K-means: servono ENTRAMBE le cose, e sono due guasti diversi.
        var hasBaselineRegime = baseline.Regimes is { CurrentRegimeId: >= 0 };
        var hasActiveModel = false;
        if (hasBaselineRegime)
        {
            try
            {
                hasActiveModel = await regimeDetector.LoadActiveModelAsync(primary.Symbol, primary.Timeframe, ct) is not null;
                if (!hasActiveModel)
                {
                    reasons.Add($"braccio K-means cieco: nessun modello di regime ATTIVO per {primary.Symbol} {primary.Timeframe}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reasons.Add($"braccio K-means cieco: il modello di regime per {primary.Symbol} {primary.Timeframe} non è caricabile ({ex.GetType().Name})");
            }
        }
        else
        {
            reasons.Add("braccio K-means cieco: lo snapshot del run di baseline non porta un regime corrente");
        }

        // Braccio volatilità: basta il forecast, la realizzata si calcola sempre dalle candele.
        var volArmed = baseline.Volatility is { ForecastVolatility24: > 0 };
        if (!volArmed)
        {
            reasons.Add("braccio volatilità cieco: il run di baseline non porta un forecast di volatilità (stage disabilitato o fallito)");
        }

        return new RegimeTriggerHealth(hasBaselineRegime && hasActiveModel, volArmed, reasons);
    }

    public async Task<RegimeTriggerCheck?> CheckAsync(CancellationToken ct = default)
    {
        // 1. Baseline: l'ultimo run COMPLETATO tra quelli lanciati dalle campagne abilitate.
        var loaded = await LoadBaselineAsync(ct);
        if (loaded is null)
        {
            return null;
        }
        var (baselineRun, baseline) = loaded.Value;
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
            var model = await regimeDetector.LoadActiveModelAsync(primary.Symbol, primary.Timeframe, ct);
            if (model is not null)
            {
                await regimeDetector.LabelFeaturesAsync(features, primary.Symbol, primary.Timeframe, ct);
                currentRegime = features.LastOrDefault(f => f.RegimeId is not null)?.RegimeId;
            }
        }

        var realized = ComputeRealizedVolatility(features.Select(f => f.Price).ToList(), RealizedVolWindow);
        double? forecast = baseline.Volatility is { ForecastVolatility24: > 0 } ? baseline.Volatility.ForecastVolatility24 : null;

        return Evaluate(baselineRegime, currentRegime, forecast, realized,
            Math.Max(1.01, options.CurrentValue.VolBandMultiple), baselineRun.Id,
            baseline.Volatility?.ForecastSource ?? "garch");
    }

    /// <summary>
    /// Decisione PURA (testabile senza DB/modelli): cluster cambiato o vol fuori banda.
    /// <paramref name="forecastSource"/> serve solo al racconto: la motivazione diceva «forecast
    /// GARCH» anche quando il previsore era il log-HAR, e chi leggeva la notifica non aveva modo
    /// di sapere quale dei due aveva prodotto il numero.
    /// </summary>
    public static RegimeTriggerCheck Evaluate(
        int? baselineRegime, int? currentRegime, double? forecastVol, double? realizedVol,
        double volBandMultiple, Guid baselineRunId, string forecastSource = "garch")
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
                    $"vol realizzata {r:0.####} oltre {volBandMultiple:0.##}× il forecast [{forecastSource}] {f:0.####} (espansione)"));
            }
            else if (r < f / volBandMultiple)
            {
                reasons.Add(FormattableString.Invariant(
                    $"vol realizzata {r:0.####} sotto il forecast [{forecastSource}] {f:0.####}/{volBandMultiple:0.##} (compressione)"));
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
