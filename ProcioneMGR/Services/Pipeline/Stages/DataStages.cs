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

        // [K49b, PRD autonomia-piena — Fase 3, 2026-09-02] LA POTATURA CHE MANCAVA.
        //
        // `CoversHoldout` esisteva, si accendeva a ogni run e NESSUNO lo leggeva a valle: il suo
        // unico effetto era una chiamata di rete a vuoto, ripetuta a ogni giro. È la famiglia
        // «gate senza strumento» rovesciata — qui lo strumento c'era e mancava il gate.
        //
        // Misurato sui 122 run delle due configurazioni attive negli ultimi 30 giorni:
        // `MKR/USDT` risultava scoperta **122 volte su 122**, senza una candela dal 2025-09-15
        // (351 giorni), e continuava a produrre 11 chiavi candidate a zero trade — 424 righe di
        // bocciatura, ~53 minuti di CPU ogni 30 giorni, e altrettanti tentativi nel denominatore
        // del DSR.
        //
        // LA DISTINZIONE CHE MANCAVA, ed è tutta qui: una serie scoperta perché NUOVA o con un buco
        // di ingestione va SCARICATA (ed è ciò che il ramo qui sopra fa, correttamente); una serie
        // che l'exchange ha SOSPESO va ESCLUSA. Il codice non le distingueva perché non guardava
        // `TrackedSeries`, cioè il posto dove quella differenza è scritta.
        //
        // E il verso: potare RIDUCE i tentativi, quindi abbassa SR* — sembra un allentamento del
        // gate. Misurato, è l'opposto: SR* si muove di +0,0017 e +0,0182 (config 17 e 18), cioè
        // il gate si STRINGE. Un candidato su una serie senza dati non è un tentativo vero, e
        // toglierlo dal denominatore corregge un conteggio, non lo allenta.
        var sospese = await SerieSospeseAsync(output.Series, ct);
        if (sospese.Count > 0)
        {
            var potate = ctx.Universe.Where(u => sospese.Contains((u.Symbol, u.Timeframe))).ToList();
            ctx.Universe = ctx.Universe.Where(u => !sospese.Contains((u.Symbol, u.Timeframe))).ToList();
            output.PrunedSuspended = potate.Select(p => $"{p.Symbol} {p.Timeframe}").ToList();
            ctx.LogLine($"[{Name}] POTATE {potate.Count} serie SOSPESE dall'exchange e senza copertura: "
                + string.Join(", ", output.PrunedSuspended)
                + ". Non sono tentativi: sono serie che non possono produrre un candidato, e finora "
                + "gonfiavano il denominatore del DSR a ogni run.");
        }

        ctx.DataStatus = output;
    }

    /// <summary>
    /// [K49b] Le serie dell'universo che sono <b>sospese dall'exchange</b> — disabilitate in
    /// <c>TrackedSeries</c> — <b>e</b> non coperte dai dati. Servono ENTRAMBE le condizioni:
    /// disabilitata ma coperta significa che i dati storici bastano ancora (il backtest è legittimo);
    /// scoperta ma abilitata è un buco di ingestione, che si ripara scaricando.
    /// </summary>
    private async Task<HashSet<(string Symbol, string Timeframe)>> SerieSospeseAsync(
        IReadOnlyList<SeriesDataStatus> stati, CancellationToken ct)
    {
        var scoperte = stati.Where(s => !s.CoversHoldout || !s.CoversSelection)
            .Select(s => (s.Symbol, s.Timeframe)).ToHashSet();
        if (scoperte.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var disabilitate = await db.TrackedSeries.AsNoTracking()
            .Where(t => !t.Enabled)
            .Select(t => new { t.Symbol, t.Timeframe })
            .ToListAsync(ct);

        return disabilitate
            .Select(d => (d.Symbol, d.Timeframe))
            .Where(scoperte.Contains)
            .ToHashSet();
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
            Text = $"{o.Series.Count} serie verificate, {covered} completamente coperte, {o.CandlesIngested} candele scaricate."
                 // [K49b] La potatura si DICE: un universo che si accorcia in silenzio e'
                 // indistinguibile da una configurazione modificata, e cambia il denominatore del DSR.
                 + (o.PrunedSuspended.Count > 0
                     ? $" POTATE {o.PrunedSuspended.Count} serie sospese dall'exchange e senza copertura ("
                       + string.Join(", ", o.PrunedSuspended)
                       + "): non sono tentativi, e toglierle dal denominatore STRINGE il gate."
                     : string.Empty),
            Metrics = new()
            {
                ["Serie"] = o.Series.Count,
                ["SerieCoperte"] = covered,
                ["CandeleScaricate"] = o.CandlesIngested,
                ["SeriePotate"] = o.PrunedSuspended.Count,
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
            // [2026-08-21] DIFENSIVO come lo snapshot qui sotto, e per la stessa ragione: questo
            // stage porta dati ACCESSORI (notizie, sentiment), non il prezzo su cui si fanno i
            // backtest. Fino a stanotte metà della dottrina era applicata — lo snapshot protetto,
            // la sync no — e una violazione di chiave univoca su `AltDataPoints` ha fatto fallire
            // l'INTERA caccia intraday al secondo stage su diciassette, con l'ingestione dei prezzi
            // già completata con successo. È il rovescio della regola 4: fail-closed sulla
            // sicurezza, fail-open sulla DIAGNOSTICA. Un run che perde le notizie di oggi vale
            // ancora; un run che non parte non vale niente.
            //
            // Il caso reale era `23505: duplicate key ... IX_AltDataPoints_DedupeKey`, cioè due
            // sync sovrapposte che ingeriscono la stessa notizia — raro (1 run su 170) e proprio per
            // questo insidioso: si manifesta quando si lancia una caccia a mano mentre il worker
            // periodico sta già sincronizzando.
            try
            {
                inserted = await altDataSync.SyncAllAsync(ct);
                ctx.LogLine($"[{Name}] {inserted} nuovi elementi ingeriti.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.LogLine($"[{Name}] sync dei dati alternativi FALLITA ({ex.GetType().Name}: {ex.Message}). "
                          + "Si prosegue con le notizie già presenti nel database: il run non si ferma per un dato accessorio.");
            }
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
