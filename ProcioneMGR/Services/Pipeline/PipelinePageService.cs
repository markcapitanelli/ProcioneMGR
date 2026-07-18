using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Esito di un'azione con messaggio per l'operatore.</summary>
public sealed record PipelineActionResult(string Message, bool IsError)
{
    public static PipelineActionResult Ok(string message) => new(message, false);
    public static PipelineActionResult Error(string message) => new(message, true);
}

/// <summary>
/// Bozza dell'editor di configurazione: la config in modifica (copia di lavoro, mai la riga
/// tracciata) + universo/date/fasi deserializzati per il binding del form.
/// </summary>
public sealed class PipelineConfigDraft
{
    public required PipelineConfiguration Config { get; init; }
    public required List<SeriesSpec> Universe { get; init; }
    public required PipelineDateRanges Ranges { get; init; }
    public required List<StageConfig> Stages { get; init; }
}

/// <summary>Esito del salvataggio config: eventuale lista di problemi delle fasi da mostrare nell'editor.</summary>
public sealed record PipelineSaveResult(string Message, bool IsError, IReadOnlyList<string> Problems)
{
    public static PipelineSaveResult Ok(string message) => new(message, false, []);
    public static PipelineSaveResult Error(string message, IReadOnlyList<string>? problems = null) => new(message, true, problems ?? []);
}

/// <summary>
/// Orchestrazione estratta da <c>Components/Pages/Pipeline.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): caricamento di configurazioni/storico/raccomandazioni, CRUD delle
/// configurazioni con la catena di validazione (nome, universo, date selection/holdout mai
/// sovrapposte, problemi delle fasi dal motore), costruzione delle bozze dell'editor (nuova, o
/// copia di lavoro con merge delle fasi aggiunte alla piattaforma dopo il salvataggio), controllo
/// dei run (start/resume/pause/cancel), dettaglio run con confronto col precedente e decisione
/// della ri-applica automatica, applicazione della raccomandazione ed export markdown — tutta la
/// logica che prima viveva nel blocco <c>@code</c> del componente senza test indipendenti da
/// Blazor. Il componente resta responsabile solo di ciò che è Blazor: binding della bozza,
/// PollingTimer del tick, messaggi, badge di stato.
/// Registrato Scoped: in Blazor Server uno scope = un circuito, un'istanza per sessione utente.
/// </summary>
public sealed class PipelinePageService(
    IPipelineEngine engine,
    IPipelineStageCatalog catalog,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineApplier applier)
{
    // --- Stato caricato (letto dal markup, mai scritto dal componente) --------------------------

    public List<PipelineConfiguration> Configs { get; private set; } = [];
    public List<PipelineRun> Runs { get; private set; } = [];
    public PipelineLiveStatus? Live { get; private set; }
    public PipelineRecommendation? LastRecommendation { get; private set; }
    public PipelineRun? LastRecommendationRun { get; private set; }

    public PipelineRun? SelectedRun { get; private set; }
    public List<StageSummary> SelectedSummaries { get; private set; } = [];
    public List<StageSummary> PreviousRunSummaries { get; private set; } = [];
    public PipelineRecommendation? SelectedRecommendation { get; private set; }
    public AutoReapplyDecisionArtifact? SelectedDecision { get; private set; }

    private bool _wasRunning;

    // --- Caricamento ---------------------------------------------------------------------------

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Configs = await db.PipelineConfigurations.AsNoTracking().OrderByDescending(c => c.UpdatedAt).ToListAsync(ct);
        Runs = await db.PipelineRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(25).ToListAsync(ct);
        Live = engine.GetLiveStatus();
        _wasRunning = Live is not null;

        var lastCompleted = Runs.FirstOrDefault(r => r.Status == "Completed" && !string.IsNullOrEmpty(r.RecommendationJson) && r.RecommendationJson != "{}");
        LastRecommendationRun = lastCompleted;
        LastRecommendation = lastCompleted is null
            ? null
            : JsonSerializer.Deserialize<PipelineRecommendation>(lastCompleted.RecommendationJson);
    }

    /// <summary>
    /// Aggiorna lo stato live dal motore (tick di polling). True quando un run APPENA finito
    /// (running → null): il chiamante deve ricaricare lo storico.
    /// </summary>
    public bool RefreshLive()
    {
        var live = engine.GetLiveStatus();
        var justFinished = _wasRunning && live is null;
        Live = live;
        _wasRunning = live is not null;
        return justFinished;
    }

    // -------------------------------------------------------------- bozze dell'editor

    public PipelineConfigDraft BuildNewConfigDraft()
    {
        var now = DateTime.UtcNow.Date;
        return new PipelineConfigDraft
        {
            Config = new PipelineConfiguration
            {
                Name = "Nuovo pipeline",
                ExchangeName = "Binance",
                InitialCapital = 10_000m,
                ExecutionMode = "Paper",
            },
            Universe = [new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1h" }],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = now.AddMonths(-24),
                SelectionTo = now.AddMonths(-4),
                HoldoutFrom = now.AddMonths(-4),
                HoldoutTo = now,
            },
            Stages = catalog.DefaultStages(),
        };
    }

    /// <summary>
    /// Copia di lavoro per l'editor (mai la riga tracciata). Le fasi aggiunte alla piattaforma DOPO
    /// il salvataggio della config vengono proposte disabilitate (nessun cambio di comportamento),
    /// così le config esistenti possono adottarle senza doverle ricreare da zero.
    /// </summary>
    public PipelineConfigDraft BuildEditDraft(PipelineConfiguration config)
    {
        var stages = JsonSerializer.Deserialize<List<StageConfig>>(config.StagesJson) ?? catalog.DefaultStages();
        foreach (var proto in catalog.Prototypes)
        {
            if (!stages.Any(s => s.Type.Equals(proto.Name, StringComparison.OrdinalIgnoreCase)))
            {
                stages.Add(new StageConfig
                {
                    Type = proto.Name,
                    Order = proto.DefaultOrder,
                    Enabled = false,
                    Parameters = proto.ParameterDefinitions.ToDictionary(d => d.Key, d => d.DefaultValue),
                });
            }
        }

        return new PipelineConfigDraft
        {
            Config = new PipelineConfiguration
            {
                Id = config.Id,
                Name = config.Name,
                Description = config.Description,
                CreatedBy = config.CreatedBy,
                CreatedAt = config.CreatedAt,
                ExchangeName = config.ExchangeName,
                InitialCapital = config.InitialCapital,
                Seed = config.Seed,
                ExecutionMode = config.ExecutionMode,
                Schedule = config.Schedule,
                ScheduleEnabled = config.ScheduleEnabled,
                NextRunAt = config.NextRunAt,
            },
            Universe = JsonSerializer.Deserialize<List<SeriesSpec>>(config.UniverseJson) ?? [],
            Ranges = JsonSerializer.Deserialize<PipelineDateRanges>(config.DateRangesJson) ?? new PipelineDateRanges(),
            Stages = stages.OrderBy(s => s.Order).ToList(),
        };
    }

    /// <summary>Scambia due fasi adiacenti e rinumera Order 1..N.</summary>
    public static void MoveStage(List<StageConfig> stages, int index, int delta)
    {
        var other = index + delta;
        if (other < 0 || other >= stages.Count) return;
        (stages[index], stages[other]) = (stages[other], stages[index]);
        for (var i = 0; i < stages.Count; i++) stages[i].Order = i + 1;
    }

    /// <summary>
    /// Valida e salva la bozza. La catena di validazione è identica all'originale: nome
    /// obbligatorio, almeno una serie (le righe a symbol vuoto vengono rimosse dalla bozza),
    /// range di date validi, holdout MAI sovrapposto alla selezione, zero problemi delle fasi
    /// (dal motore). Se cron o abilitazione cambiano, NextRunAt viene azzerato così lo
    /// scheduler ricalcola dall'espressione NUOVA invece di tenere l'occorrenza vecchia.
    /// </summary>
    public async Task<PipelineSaveResult> SaveConfigAsync(PipelineConfigDraft draft, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Config.Name)) return PipelineSaveResult.Error("Il nome è obbligatorio.");
        draft.Universe.RemoveAll(u => string.IsNullOrWhiteSpace(u.Symbol));
        if (draft.Universe.Count == 0) return PipelineSaveResult.Error("L'universo deve avere almeno una serie.");
        if (draft.Ranges.SelectionTo <= draft.Ranges.SelectionFrom || draft.Ranges.HoldoutTo <= draft.Ranges.HoldoutFrom)
            return PipelineSaveResult.Error("Range di date non validi.");
        if (draft.Ranges.HoldoutFrom < draft.Ranges.SelectionTo)
            return PipelineSaveResult.Error("L'holdout deve iniziare DOPO la fine della selezione (mai sovrapposti).");

        var problems = engine.ValidateConfiguration(draft.Stages);
        if (problems.Count > 0) return PipelineSaveResult.Error("Correggere i problemi delle fasi prima di salvare.", problems);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        PipelineConfiguration row;
        if (draft.Config.Id == 0)
        {
            row = new PipelineConfiguration { CreatedBy = userId ?? "", CreatedAt = DateTime.UtcNow };
            db.PipelineConfigurations.Add(row);
        }
        else
        {
            row = await db.PipelineConfigurations.FirstAsync(c => c.Id == draft.Config.Id, ct);
        }
        var scheduleChanged = row.Schedule != draft.Config.Schedule || row.ScheduleEnabled != draft.Config.ScheduleEnabled;

        row.Name = draft.Config.Name;
        row.Description = draft.Config.Description;
        row.ExchangeName = draft.Config.ExchangeName;
        row.InitialCapital = draft.Config.InitialCapital;
        row.Seed = draft.Config.Seed;
        row.ExecutionMode = draft.Config.ExecutionMode;
        row.Schedule = draft.Config.Schedule;
        row.ScheduleEnabled = draft.Config.ScheduleEnabled;
        if (scheduleChanged) row.NextRunAt = null;
        row.UniverseJson = JsonSerializer.Serialize(draft.Universe);
        row.DateRangesJson = JsonSerializer.Serialize(draft.Ranges);
        row.StagesJson = JsonSerializer.Serialize(draft.Stages);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await ReloadAsync(ct);
        return PipelineSaveResult.Ok("Configurazione salvata.");
    }

    public async Task<PipelineActionResult> CloneConfigAsync(PipelineConfiguration config, string? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.PipelineConfigurations.Add(new PipelineConfiguration
        {
            Name = config.Name + " (copia)",
            Description = config.Description,
            CreatedBy = userId ?? "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExchangeName = config.ExchangeName,
            UniverseJson = config.UniverseJson,
            DateRangesJson = config.DateRangesJson,
            StagesJson = config.StagesJson,
            InitialCapital = config.InitialCapital,
            Seed = config.Seed,
            ExecutionMode = config.ExecutionMode,
            Schedule = config.Schedule,
        });
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        return PipelineActionResult.Ok("Configurazione clonata.");
    }

    public async Task<PipelineActionResult> DeleteConfigAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.PipelineConfigurations.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is not null)
        {
            db.PipelineConfigurations.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        await ReloadAsync(ct);
        return PipelineActionResult.Ok("Configurazione eliminata.");
    }

    // -------------------------------------------------------------- controllo run

    public async Task<PipelineActionResult> StartRunAsync(int configId, string? userId, CancellationToken ct = default)
    {
        await engine.StartRunAsync(configId, "Manual", userId, ct);
        Live = engine.GetLiveStatus();
        _wasRunning = true;
        return PipelineActionResult.Ok("Run avviato.");
    }

    public async Task<PipelineActionResult> ResumeRunAsync(Guid runId, string? userId, CancellationToken ct = default)
    {
        await engine.ResumeRunAsync(runId, userId, ct);
        Live = engine.GetLiveStatus();
        _wasRunning = true;
        return PipelineActionResult.Ok("Run ripreso dal checkpoint.");
    }

    public void PauseLiveRun()
    {
        if (Live is not null) engine.RequestPause(Live.RunId);
    }

    public void CancelLiveRun()
    {
        if (Live is not null) engine.Cancel(Live.RunId);
    }

    // -------------------------------------------------------------- dettaglio run

    /// <summary>
    /// Seleziona un run: deserializza le fasi e la raccomandazione, individua il run COMPLETATO
    /// precedente della stessa config per il confronto, e carica (best-effort) la decisione della
    /// ri-applica automatica col giudizio del supervisore AI.
    /// </summary>
    public async Task SelectRunAsync(PipelineRun run, CancellationToken ct = default)
    {
        SelectedRun = run;
        SelectedSummaries = JsonSerializer.Deserialize<List<StageSummary>>(run.StageSummariesJson) ?? [];
        SelectedRecommendation = string.IsNullOrEmpty(run.RecommendationJson) || run.RecommendationJson == "{}"
            ? null
            : JsonSerializer.Deserialize<PipelineRecommendation>(run.RecommendationJson);

        var previous = Runs
            .Where(r => r.ConfigurationId == run.ConfigurationId && r.StartedAt < run.StartedAt && r.Status == "Completed")
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();
        PreviousRunSummaries = previous is null
            ? []
            : JsonSerializer.Deserialize<List<StageSummary>>(previous.StageSummariesJson) ?? [];

        SelectedDecision = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var art = await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.RunId == run.Id && a.Kind == AutoReapplyArtifactKinds.Decision)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (art is not null)
            {
                SelectedDecision = JsonSerializer.Deserialize<AutoReapplyDecisionArtifact>(art.PayloadJson);
            }
        }
        catch { /* la UI resta valida anche senza la decisione */ }
    }

    public void CloseSelectedRun() => SelectedRun = null;

    /// <summary>Metriche comuni fra il run selezionato e il precedente, chiave "Fase: Metrica".</summary>
    public IEnumerable<(string Label, decimal Prev, decimal Curr)> CompareRuns()
    {
        var prev = PreviousRunSummaries.SelectMany(s => s.Metrics.Select(m => ($"{s.DisplayName}: {m.Key}", m.Value))).ToDictionary(x => x.Item1, x => x.Value);
        foreach (var s in SelectedSummaries)
        {
            foreach (var m in s.Metrics)
            {
                var key = $"{s.DisplayName}: {m.Key}";
                if (prev.TryGetValue(key, out var p)) yield return (key, p, m.Value);
            }
        }
    }

    // -------------------------------------------------------------- applica & export

    /// <summary>
    /// Distribuisce l'ensemble sulle corsie isolate delegando a <see cref="IPipelineApplier"/> — la
    /// STESSA logica usata dalla ri-applica automatica dello scheduler (una sola implementazione,
    /// nessuna divergenza). Scrive solo la configurazione: nessun trading viene avviato qui.
    /// Null = niente da applicare (raccomandazione assente o senza gambe), nessun messaggio.
    /// </summary>
    public async Task<PipelineActionResult?> ApplyRecommendationAsync(PipelineRecommendation? recommendation, CancellationToken ct = default)
    {
        if (recommendation is null || recommendation.EnsembleLegs.Count == 0) return null;
        var result = await applier.ApplyRecommendationAsync(recommendation, ct);
        return PipelineActionResult.Ok(result.Message);
    }

    /// <summary>Report markdown del run come data-URI scaricabile (nessun endpoint server necessario).</summary>
    public static string ExportHref(PipelineRun? run)
    {
        if (run is null) return "#";
        var md = $"# Pipeline run {run.Id}\n\n- Avviato: {run.StartedAt:yyyy-MM-dd HH:mm:ss} UTC\n- Stato: {run.Status}\n\n## Conclusione\n\n```\n{run.Conclusion}\n```\n\n## Fasi\n\n";
        var summaries = JsonSerializer.Deserialize<List<StageSummary>>(run.StageSummariesJson) ?? [];
        foreach (var s in summaries.OrderBy(s => s.Order))
        {
            md += $"### {s.DisplayName} — {s.Status} ({s.Duration:mm\\:ss})\n\n{s.Text}\n\n";
        }
        return "data:text/markdown;charset=utf-8," + Uri.EscapeDataString(md);
    }

    /// <summary>Riassunto compatto dell'universo di una config ("BTC/USDT 1h, ETH/USDT 4h +2").</summary>
    public static string UniverseSummary(PipelineConfiguration config)
    {
        var universe = JsonSerializer.Deserialize<List<SeriesSpec>>(config.UniverseJson) ?? [];
        return universe.Count == 0 ? "—" : string.Join(", ", universe.Take(4).Select(u => $"{u.Symbol} {u.Timeframe}")) + (universe.Count > 4 ? $" +{universe.Count - 4}" : "");
    }
}
