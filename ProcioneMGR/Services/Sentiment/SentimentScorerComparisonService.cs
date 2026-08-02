using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Richiesta di confronto scorer (dal pannello in /sentiment).</summary>
public sealed record ScorerComparisonRequest(
    string Symbol,
    string Timeframe,
    DateTime FromUtc,
    DateTime ToUtc,
    int LookbackHours,
    int ForwardHorizon,
    int MaxItems,
    bool IncludeLlm,
    bool IncludeOnnx);

/// <summary>Una riga del confronto: lo scorer, se era davvero disponibile, e le sue metriche IC.</summary>
public sealed record ScorerComparisonEntry(
    string Scorer,
    bool Available,
    string Note,
    FactorEvaluationResult? Evaluation);

/// <summary>Una notizia su cui gli scorer sono in disaccordo (per capire COME differiscono, non solo di quanto).</summary>
public sealed record ScorerDisagreement(
    DateTime WhenUtc,
    string Title,
    decimal KeywordScore,
    decimal? LlmScore,
    decimal? OnnxScore);

/// <summary>Esito complessivo del confronto.</summary>
public sealed record ScorerComparisonResult(
    int NewsScored,
    int CandleCount,
    int LlmScoredByLlm,
    IReadOnlyList<ScorerComparisonEntry> Entries,
    IReadOnlyList<ScorerDisagreement> TopDisagreements);

/// <summary>
/// Confronto A/B/C fra gli scorer di sentiment (Keyword / Llm / Onnx) sul giudice che la
/// piattaforma usa per OGNI fattore: si rigiocano le notizie storiche (AltDataPoints) attraverso
/// ciascuno scorer, si costruisce un <see cref="SentimentAlphaFactor"/> per scorer e si misura
/// l'IC con lo STESSO <see cref="IFactorEvaluator"/> (Spearman, t-stat Newey-West, IR, quantili)
/// sulle STESSE candele — nessuna infrastruttura di gate nuova, e i verdetti sono confrontabili
/// per costruzione. Offline puro: non tocca i punteggi salvati né il percorso di sync.
///
/// <para>Il replay LLM usa <see cref="LlmSentimentScorer.ScoreBatchAsync"/> (N titoli per
/// chiamata) col tetto <c>MaxItems</c>: il costo del confronto è dichiarato e limitato PRIMA di
/// partire, non scoperto dalla bolletta.</para>
/// </summary>
public sealed class SentimentScorerComparisonService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    KeywordSentimentScorer keyword,
    LlmSentimentScorer llm,
    OnnxSentimentScorer onnx,
    IFactorEvaluator factorEvaluator,
    ILogger<SentimentScorerComparisonService> logger)
{
    public async Task<ScorerComparisonResult> CompareAsync(ScorerComparisonRequest request, CancellationToken ct)
    {
        // 1) Candele del giudice.
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == request.Symbol && c.Timeframe == request.Timeframe
                         && c.TimestampUtc >= request.FromUtc && c.TimestampUtc <= request.ToUtc)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }
        if (candles.Count < 30)
        {
            return new ScorerComparisonResult(0, candles.Count, 0, [], []);
        }

        // 2) Notizie TESTUALI nella finestra (le strutturali hanno override, non testo: identiche
        //    per ogni scorer, quindi fuori dal confronto). Se superano il tetto si tengono le più
        //    recenti: il tetto è il controllo del costo LLM.
        var newsFrom = request.FromUtc.AddHours(-Math.Max(1, request.LookbackHours));
        List<(DateTime When, string Title, string? Summary, string SymbolsJson)> news;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            news = (await db.AltDataPoints.AsNoTracking()
                    .Where(a => a.TimestampUtc >= newsFrom && a.TimestampUtc <= request.ToUtc)
                    .Where(a => a.Category != nameof(NewsCategory.EconomicCalendar)
                             && a.Category != nameof(NewsCategory.RetailSentiment))
                    .OrderByDescending(a => a.TimestampUtc)
                    .Take(Math.Clamp(request.MaxItems, 10, 1000))
                    .Select(a => new { a.TimestampUtc, a.Title, a.Summary, a.SymbolsJson })
                    .ToListAsync(ct))
                .Select(a => (a.TimestampUtc, a.Title, a.Summary, a.SymbolsJson))
                .OrderBy(a => a.TimestampUtc)
                .ToList();
        }
        if (news.Count == 0)
        {
            return new ScorerComparisonResult(0, candles.Count, 0, [], []);
        }

        var symbolsPerNews = news.Select(n => ParseSymbols(n.SymbolsJson)).ToList();
        var baseTicker = request.Symbol.Split('/')[0].Trim().ToUpperInvariant();
        var parameters = new Dictionary<string, decimal> { ["LookbackHours"] = Math.Max(1, request.LookbackHours) };
        var config = new FactorEvaluationConfig { ForwardHorizon = Math.Max(1, request.ForwardHorizon) };

        var entries = new List<ScorerComparisonEntry>();

        // 3) Lessico: sempre disponibile, è il baseline del confronto.
        var keywordScores = news.Select(n => keyword.Score(n.Title, n.Summary)).ToList();
        entries.Add(Evaluate(SentimentScorerProviders.Keyword, keywordScores,
            "lessico 25 parole (baseline)", news, symbolsPerNews, baseTicker, candles, parameters, config));

        // 4) LLM (opt-in dal pannello): batch per contenere costi e tempi. Se OGNI batch è
        //    ripiegato sul lessico, la riga sarebbe un duplicato travestito: si dichiara
        //    non-disponibile invece di mostrare un confronto che non confronta niente.
        IReadOnlyList<decimal>? llmScores = null;
        var fromLlm = 0;
        if (request.IncludeLlm)
        {
            (llmScores, fromLlm) = await llm.ScoreBatchAsync(news.Select(n => (n.Title, n.Summary)).ToList(), ct);
            if (fromLlm == 0)
            {
                entries.Add(new ScorerComparisonEntry(SentimentScorerProviders.Llm, false,
                    "LLM non disponibile (chiave assente, breaker aperto o risposta non valida): nessun punteggio reale da confrontare.", null));
                llmScores = null;
            }
            else
            {
                var note = fromLlm == news.Count
                    ? "provider AI attivo, replay in batch"
                    : $"provider AI attivo; ATTENZIONE: solo {fromLlm}/{news.Count} punteggi dall'LLM (il resto è ripiegato sul lessico)";
                entries.Add(Evaluate(SentimentScorerProviders.Llm, llmScores,
                    note, news, symbolsPerNews, baseTicker, candles, parameters, config));
            }
        }

        // 5) ONNX locale (se il pilota è addestrato).
        IReadOnlyList<decimal>? onnxScores = null;
        if (request.IncludeOnnx)
        {
            if (!onnx.IsAvailable)
            {
                entries.Add(new ScorerComparisonEntry(SentimentScorerProviders.Onnx, false,
                    "modello non addestrato: usa \"Addestra pilota ONNX\" qui sopra.", null));
            }
            else
            {
                var scores = new List<decimal>(news.Count);
                foreach (var n in news)
                {
                    ct.ThrowIfCancellationRequested();
                    scores.Add(await onnx.ScoreAsync(n.Title, n.Summary, ct));
                }
                onnxScores = scores;
                entries.Add(Evaluate(SentimentScorerProviders.Onnx, scores,
                    "inferenza locale (distillazione del lessico, Livello 1)", news, symbolsPerNews, baseTicker, candles, parameters, config));
            }
        }

        // 6) I disaccordi più grossi: dove gli scorer leggono la stessa notizia in modo diverso.
        var disagreements = new List<(decimal Spread, ScorerDisagreement Row)>();
        for (var i = 0; i < news.Count; i++)
        {
            var values = new List<decimal> { keywordScores[i] };
            if (llmScores is not null) values.Add(llmScores[i]);
            if (onnxScores is not null) values.Add(onnxScores[i]);
            if (values.Count < 2) continue;

            var spread = values.Max() - values.Min();
            if (spread <= 0m) continue;
            disagreements.Add((spread, new ScorerDisagreement(
                news[i].When, news[i].Title, keywordScores[i],
                llmScores?[i], onnxScores?[i])));
        }

        logger.LogInformation(
            "Confronto scorer su {Symbol} {Timeframe}: {News} notizie, {Candles} candele, {Entries} scorer valutati (LLM reali: {FromLlm}).",
            request.Symbol, request.Timeframe, news.Count, candles.Count, entries.Count(e => e.Available), fromLlm);

        return new ScorerComparisonResult(
            news.Count, candles.Count, fromLlm, entries,
            disagreements.OrderByDescending(d => d.Spread).Take(10).Select(d => d.Row).ToList());
    }

    private ScorerComparisonEntry Evaluate(
        string scorerName,
        IReadOnlyList<decimal> scores,
        string note,
        List<(DateTime When, string Title, string? Summary, string SymbolsJson)> news,
        List<IReadOnlyList<string>> symbolsPerNews,
        string baseTicker,
        List<OhlcvData> candles,
        Dictionary<string, decimal> parameters,
        FactorEvaluationConfig config)
    {
        var scored = news
            .Select((n, i) => new ScoredNewsItem(DateTime.SpecifyKind(n.When, DateTimeKind.Utc), scores[i], symbolsPerNews[i]))
            .ToList();
        var factor = new SentimentAlphaFactor(scored, symbolFilter: baseTicker);
        var evaluation = factorEvaluator.Evaluate(factor, candles, parameters, config);
        return new ScorerComparisonEntry(scorerName, true, note, evaluation);
    }

    private static IReadOnlyList<string> ParseSymbols(string symbolsJson)
    {
        if (string.IsNullOrWhiteSpace(symbolsJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(symbolsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
