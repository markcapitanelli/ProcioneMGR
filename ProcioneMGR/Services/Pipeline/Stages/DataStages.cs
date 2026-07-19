using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 1 — verifies OHLCV coverage for the whole universe over [SelectionFrom, HoldoutTo]
/// and (optionally) ingests only the MISSING head/tail deltas via the existing idempotent
/// ingestion service. Never re-downloads what the DB already has.
/// </summary>
public sealed class DataIngestionStage(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOhlcvIngestionService ingestion) : IPipelineStage
{
    public string Name => "DataIngestion";
    public string DisplayName => "Ingestione dati";
    public string Description => "Verifica la copertura OHLCV dell'universo e scarica solo i delta mancanti.";
    public int DefaultOrder => 1;
    public IReadOnlyList<StageDependency> Dependencies => [];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("syncData", "Scarica i dati mancanti", "true", "false = solo verifica copertura, nessuna chiamata di rete"),
        new("coverageToleranceDays", "Tolleranza copertura (giorni)", "3", "margine ammesso ai bordi del range prima di considerare la serie scoperta"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Count == 0 ? "L'universo è vuoto: aggiungi almeno una serie (symbol + timeframe)." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var sync = config.GetBool("syncData", true);
        var tolerance = TimeSpan.FromDays(config.GetInt("coverageToleranceDays", 3));
        var from = ctx.Ranges.SelectionFrom;
        var to = ctx.Ranges.HoldoutTo;

        var output = new DataIngestionOutput();
        foreach (var series in ctx.Universe)
        {
            ct.ThrowIfCancellationRequested();
            var status = await QueryStatusAsync(series, from, to, tolerance, ctx, ct);

            if (sync && (!status.CoversSelection || !status.CoversHoldout))
            {
                // Only the missing head (before the first candle) and tail (after the last).
                var ingestFrom = status.FirstUtc is DateTime first && first > from ? from : status.LastUtc ?? from;
                var ingestTo = status.FirstUtc is DateTime f2 && f2 > from ? f2 : to;
                if (status.CandleCount == 0) { ingestFrom = from; ingestTo = to; }

                ctx.LogLine($"[{Name}] {series.Symbol} {series.Timeframe}: ingest {ingestFrom:yyyy-MM-dd} → {ingestTo:yyyy-MM-dd}…");
                var result = await ingestion.IngestHistoricalDataAsync(ctx.ExchangeName, series.Symbol, series.Timeframe, ingestFrom, ingestTo, null, ct);
                output.CandlesIngested += result.CandlesProcessed;

                // Tail delta (after last candle), if the first pass covered only the head.
                var refreshed = await QueryStatusAsync(series, from, to, tolerance, ctx, ct);
                if (!refreshed.CoversHoldout && refreshed.LastUtc is DateTime last && last < to)
                {
                    var tail = await ingestion.IngestHistoricalDataAsync(ctx.ExchangeName, series.Symbol, series.Timeframe, last, to, null, ct);
                    output.CandlesIngested += tail.CandlesProcessed;
                    refreshed = await QueryStatusAsync(series, from, to, tolerance, ctx, ct);
                }
                status = refreshed;
            }

            output.Series.Add(status);
            ctx.LogLine($"[{Name}] {series.Symbol} {series.Timeframe}: {status.CandleCount} candele, selection {(status.CoversSelection ? "OK" : "SCOPERTA")}, holdout {(status.CoversHoldout ? "OK" : "SCOPERTO")}.");
        }
        ctx.DataStatus = output;
    }

    private async Task<SeriesDataStatus> QueryStatusAsync(SeriesSpec series, DateTime from, DateTime to, TimeSpan tolerance, PipelineContext ctx, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.OhlcvData.AsNoTracking()
            .Where(c => c.Symbol == series.Symbol && c.Timeframe == series.Timeframe && c.TimestampUtc >= from && c.TimestampUtc <= to);
        var count = await query.CountAsync(ct);
        DateTime? first = count > 0 ? await query.MinAsync(c => c.TimestampUtc, ct) : null;
        DateTime? last = count > 0 ? await query.MaxAsync(c => c.TimestampUtc, ct) : null;
        return new SeriesDataStatus
        {
            Symbol = series.Symbol,
            Timeframe = series.Timeframe,
            CandleCount = count,
            FirstUtc = first,
            LastUtc = last,
            CoversSelection = first is DateTime f && f <= from + tolerance && last is DateTime l1 && l1 >= ctx.Ranges.SelectionTo - tolerance,
            CoversHoldout = last is DateTime l2 && l2 >= to - tolerance,
        };
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.DataStatus ?? new DataIngestionOutput();
        var covered = o.Series.Count(s => s.CoversSelection && s.CoversHoldout);
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Series.Count} serie verificate, {covered} completamente coperte, {o.CandlesIngested} candele scaricate.",
            Metrics = new()
            {
                ["Serie"] = o.Series.Count,
                ["SerieCoperte"] = covered,
                ["CandeleScaricate"] = o.CandlesIngested,
            },
        };
    }
}

/// <summary>Stage 2 — syncs the alternative-data sources (news RSS, retail sentiment) and summarizes the last 24h.</summary>
public sealed class AltDataSyncStage(
    IAltDataSyncService altDataSync,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProcioneMGR.Services.Sentiment.ISentimentSnapshotService? snapshotService = null) : IPipelineStage
{
    public string Name => "AltDataSync";
    public string DisplayName => "Sync dati alternativi";
    public string Description => "Sincronizza notizie/sentiment dalle fonti configurate e misura il sentiment recente.";
    public int DefaultOrder => 2;
    public IReadOnlyList<StageDependency> Dependencies => [];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("sync", "Esegui la sync di rete", "true", "false = usa solo le notizie già presenti nel DB"),
    ];

    public string? ValidateInput(PipelineContext ctx) => null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var inserted = 0;
        if (config.GetBool("sync", true))
        {
            inserted = await altDataSync.SyncAllAsync(ct);
            ctx.LogLine($"[{Name}] {inserted} nuovi elementi ingeriti.");
        }

        var since = DateTime.UtcNow.AddHours(-24);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.AltDataPoints.AsNoTracking()
            .Where(a => a.TimestampUtc >= since && a.SentimentScore != null)
            .Select(a => a.SentimentScore!.Value)
            .ToListAsync(ct);

        ctx.AltData = new AltDataOutput
        {
            InsertedCount = inserted,
            NewsLast24h = recent.Count,
            AvgSentimentLast24h = recent.Count > 0 ? (double)recent.Average() : 0.0,
        };

        // Sentiment 2.0: snapshot composite (mood di mercato + per-simbolo). DIFENSIVO: uno
        // snapshot assente o fallito non deve mai far fallire lo stage — il run continua col
        // solo sentiment legacy delle news.
        if (snapshotService is not null)
        {
            try
            {
                ctx.AltData.Snapshot = await snapshotService.ComputeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.LogLine($"[{Name}] snapshot mood non calcolato ({ex.Message}): si prosegue col solo sentiment news.");
            }
        }
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.AltData ?? new AltDataOutput();
        var moodText = o.Snapshot is null ? "" : $" Mood composite {o.Snapshot.CompositeScore:+0.00;-0.00}" +
            (o.Snapshot.FearGreedValue is null ? "" : $", F&G {o.Snapshot.FearGreedValue:F0}") +
            (o.Snapshot.Extremes.Count > 0 ? $", {o.Snapshot.Extremes.Count} estremi" : "") + ".";
        var summary = new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.InsertedCount} nuovi elementi; ultime 24h: {o.NewsLast24h} notizie, sentiment medio {o.AvgSentimentLast24h:F3}.{moodText}",
            Metrics = new()
            {
                ["NuoviElementi"] = o.InsertedCount,
                ["News24h"] = o.NewsLast24h,
                ["SentimentMedio24h"] = (decimal)o.AvgSentimentLast24h,
            },
        };
        if (o.Snapshot is not null)
        {
            summary.Metrics["MoodComposite"] = (decimal)o.Snapshot.CompositeScore;
            if (o.Snapshot.FearGreedValue is not null) summary.Metrics["FearGreed"] = (decimal)o.Snapshot.FearGreedValue.Value;
        }
        return summary;
    }
}
