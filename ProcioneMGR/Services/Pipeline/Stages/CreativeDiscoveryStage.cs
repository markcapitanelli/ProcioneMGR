using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 8-bis — CREATIVE discovery: instead of sweeping parameters of known strategies, the
/// <see cref="IStrategyComposer"/> GENERATES brand-new strategy specs (composite signal rules,
/// event triggers, regime maps), screens them on the selection range and confirms the best per
/// series with a fixed-parameter walk-forward. Confirmed candidates are injected into
/// <see cref="PipelineContext.Candidates"/> exactly like classic discovery output, so the
/// holdout gauntlet (validation → robustness → ensemble) treats them identically — the
/// composer proposes, the backtests dispose.
/// </summary>
public sealed class CreativeDiscoveryStage(
    IStrategyComposer composer,
    IDbContextFactory<ApplicationDbContext> dbFactory) : IPipelineStage
{
    public string Name => "CreativeDiscovery";
    public string DisplayName => "Scoperta creativa";
    public string Description => "Genera strategie composite/event/regime mai codificate e le valida in walk-forward.";
    public int DefaultOrder => 8; // affianca la Discovery classica, prima della validazione holdout
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("maxCandidates", "Spec da generare", "200", "quante strategie candidate creare (100-2000)"),
        new("enableComposite", "Genera composite", "true", "combinazioni di segnali elementari (AND/OR)"),
        new("enableEvent", "Genera event-trigger", "true", "eventi discreti di mercato + uscita a tempo"),
        new("enableRegime", "Genera regime-conditional", "true", "mappe regime→strategia"),
        new("signalPool", "Pool segnali (csv di id)", "", "vuoto = tutto il catalogo (0-8)"),
        new("minScreenSharpe", "Sharpe minimo allo screening", "0.3", ""),
        new("minTrades", "Trade minimi", "12", ""),
        new("confirmTopN", "Conferme walk-forward per serie", "3", "quante spec per serie passano alla conferma WF"),
        new("oosWindowMonths", "Finestra OOS della conferma (mesi)", "2", ""),
        new("minOosSharpe", "Sharpe OOS minimo confermato", "0.3", ""),
        new("timeLimitMinutes", "Limite di tempo (minuti)", "10", "oltre questo la generazione si ferma alle serie già elaborate"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Count == 0 ? "Universo vuoto." : null;

    /// <summary>Set by ExecuteAsync for Summarize (per-run instance, transient).</summary>
    private int _generated;
    private int _seriesDone;
    private int _injected;
    private bool _timedOut;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var composerConfig = new ComposerConfiguration
        {
            MaxCandidates = Math.Clamp(config.GetInt("maxCandidates", 200), 10, 2000),
            Seed = ctx.Seed,
            EnableComposite = config.GetBool("enableComposite", true),
            EnableEvent = config.GetBool("enableEvent", true),
            EnableRegime = config.GetBool("enableRegime", true),
            SignalPool = config.GetList("signalPool")
                .Select(s => int.TryParse(s, out var v) ? v : -1)
                .Where(v => v >= 0)
                .ToList(),
        };
        _generated = composer.Compose(composerConfig).Count; // deterministic preview for the summary
        ctx.LogLine($"[{Name}] {_generated} spec generate (seed {ctx.Seed}): screening sull'universo…");

        var timeLimit = TimeSpan.FromMinutes(Math.Max(1, config.GetInt("timeLimitMinutes", 10)));
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<string>(m => ctx.LogLine($"[{Name}] {m}"));

        _seriesDone = 0;
        _injected = 0;
        _timedOut = false;
        foreach (var series in ctx.Universe)
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > timeLimit)
            {
                _timedOut = true;
                ctx.LogLine($"[{Name}] Limite di tempo raggiunto dopo {_seriesDone}/{ctx.Universe.Count} serie: mi fermo qui (candidate già trovate conservate).");
                break;
            }

            var screening = new ComposerScreeningConfiguration
            {
                ExchangeName = ctx.ExchangeName,
                Symbol = series.Symbol,
                Timeframe = series.Timeframe,
                From = ctx.Ranges.SelectionFrom,
                To = ctx.Ranges.SelectionTo,
                InitialCapital = ctx.InitialCapital,
                MinScreenSharpe = config.GetDecimal("minScreenSharpe", 0.3m),
                MinTrades = config.GetInt("minTrades", 12),
                ConfirmTopN = config.GetInt("confirmTopN", 3),
                OosWindowMonths = config.GetInt("oosWindowMonths", 2),
                MinOosSharpe = config.GetDecimal("minOosSharpe", 0.3m),
            };

            var confirmed = await composer.ComposeAndScreenAsync(composerConfig, screening, progress, ct);
            ctx.Candidates.AddRange(confirmed);
            _injected += confirmed.Count;
            _seriesDone++;
        }

        // Traceability: one audit row per run with the aggregate outcome.
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        db.TradingAuditLogs.Add(new TradingAuditLog
        {
            TimestampUtc = DateTime.UtcNow,
            Action = "CreativeCompose",
            Details = System.Text.Json.JsonSerializer.Serialize(new
            {
                runId = ctx.RunId,
                specsGenerated = _generated,
                seriesScreened = _seriesDone,
                candidatesConfirmed = _injected,
                timedOut = _timedOut,
            }),
            UserId = ctx.UserId,
            Mode = TradingMode.Paper,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public StageSummary Summarize(PipelineContext ctx)
        => new()
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{_generated} spec generate, {_seriesDone} serie analizzate, {_injected} candidate confermate in walk-forward"
                 + (_timedOut ? " (interrotto per limite di tempo)." : "."),
            Metrics = new()
            {
                ["SpecGenerate"] = _generated,
                ["SerieAnalizzate"] = _seriesDone,
                ["CandidateConfermate"] = _injected,
            },
        };
}
