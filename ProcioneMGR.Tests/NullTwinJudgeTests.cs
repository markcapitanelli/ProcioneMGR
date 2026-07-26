using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

// ============================================================================
// [A1 roadmap integrazione] Il giudice del gemello nullo unificato: la POLICY
// (200 gemelli, 99°) vive in NullTwinJudge e basta — questi test inchiodano la
// matematica del verdetto e il comportamento dello stage di pipeline.
// ============================================================================

public class NullTwinJudgeEvaluateTests
{
    /// <summary>Distribuzione nulla nota: 1..100. Quantile con la convenzione ceil(q·n)−1.</summary>
    private static List<decimal> OneToHundred() => [.. Enumerable.Range(1, 100).Select(i => (decimal)i)];

    [Fact]
    public void SottoIlMinimoDiGemelliValidi_NessunVerdetto()
    {
        var pochi = Enumerable.Range(1, 40).Select(i => (decimal)i).ToList();
        Assert.Null(NullTwinJudge.Evaluate(realSharpe: 5m, pochi, minValidTwins: 100));
        Assert.NotNull(NullTwinJudge.Evaluate(realSharpe: 5m, pochi, minValidTwins: 40));
    }

    [Fact]
    public void QuantiliConConvenzioneDeiTool()
    {
        // Su 1..100: P99 = elemento ceil(0.99·100)−1 = indice 98 → valore 99.
        var v = NullTwinJudge.Evaluate(realSharpe: 100m, OneToHundred(), minValidTwins: 100)!;
        Assert.Equal(99m, v.P99);
        Assert.Equal(95m, v.P95);
        Assert.Equal(90m, v.P90);
        Assert.Equal(50m, v.Median);
        Assert.Equal(100m, v.Max);
        Assert.Equal(99m, v.Threshold);
    }

    [Fact]
    public void PassaSoloChiSuperaStrettamenteLaSoglia()
    {
        // Reale 100 > P99 (99) → passa; reale 99 NON supera strettamente 99 → bocciato.
        Assert.True(NullTwinJudge.Evaluate(100m, OneToHundred(), minValidTwins: 100)!.Passed);
        Assert.False(NullTwinJudge.Evaluate(99m, OneToHundred(), minValidTwins: 100)!.Passed);
    }

    [Fact]
    public void PercentileEPValueSonoComplementari()
    {
        // Reale 51: batte i gemelli 1..50 → percentile 50; p-value = 50/100 (gemelli ≥ 51).
        var v = NullTwinJudge.Evaluate(51m, OneToHundred(), minValidTwins: 100)!;
        Assert.Equal(50.0, v.PercentileOfReal, precision: 9);
        Assert.Equal(0.50, v.EmpiricalPValue, precision: 9);
        Assert.Equal(100, v.ValidTwins);
    }

    [Fact]
    public void LaPolicyDiDefaultEQuellaDichiarata()
    {
        // Il numero magico sta in UN posto: se cambia, deve cambiare qui a voce alta.
        Assert.Equal(200, NullTwinJudge.DefaultTwins);
        Assert.Equal(0.99, NullTwinJudge.DefaultRequiredPercentile);
    }
}

// ============================================================================
// Lo stage di pipeline: boccia dentro-il-nullo, fail-safe sui non giudicabili
// ============================================================================

file sealed class StubNullTwinJudge : INullTwinJudge
{
    private readonly Func<decimal, NullTwinVerdict?> _verdictFor;
    public List<(string Symbol, decimal RealSharpe, int Twins, double Percentile)> Calls { get; } = [];

    public StubNullTwinJudge(Func<decimal, NullTwinVerdict?> verdictFor) => _verdictFor = verdictFor;

    public Task<NullTwinVerdict?> JudgeAsync(
        BacktestConfiguration config,
        IReadOnlyList<OhlcvData> realCandles,
        decimal realSharpe,
        int twins = NullTwinJudge.DefaultTwins,
        double requiredPercentile = NullTwinJudge.DefaultRequiredPercentile,
        int seedBase = 2000,
        double meanBlockLength = 24,
        CancellationToken ct = default)
    {
        Calls.Add((config.Symbol, realSharpe, twins, requiredPercentile));
        return Task.FromResult(_verdictFor(realSharpe));
    }
}

file sealed class InMemoryCandleCache : IPipelineCandleCache
{
    private readonly Dictionary<string, List<OhlcvData>> _series = new();

    public void Add(string symbol, string timeframe, List<OhlcvData> candles)
        => _series[$"{symbol}|{timeframe}"] = candles;

    public Task<IReadOnlyList<OhlcvData>> GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)
    {
        var all = _series.TryGetValue($"{symbol}|{timeframe}", out var list) ? list : [];
        IReadOnlyList<OhlcvData> filtered = all.Where(c => c.TimestampUtc >= from && c.TimestampUtc <= to).ToList();
        return Task.FromResult(filtered);
    }
}

public class NullTwinValidationStageTests
{
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<OhlcvData> Flat(string symbol, int count)
        => [.. Enumerable.Range(0, count).Select(i => new OhlcvData
        {
            Symbol = symbol,
            Timeframe = "1h",
            TimestampUtc = Start.AddHours(i),
            Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1000m,
        })];

    private static PipelineContext Context(params ValidatedCandidate[] validated)
    {
        var cache = new InMemoryCandleCache();
        foreach (var v in validated) cache.Add(v.Symbol, v.Timeframe, Flat(v.Symbol, 300));
        var ctx = new PipelineContext
        {
            ExchangeName = "Binance",
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = Start,
                SelectionTo = Start.AddHours(200),
                HoldoutFrom = Start,
                HoldoutTo = Start.AddHours(300),
            },
            Candles = cache,
        };
        ctx.Validated.AddRange(validated);
        return ctx;
    }

    private static ValidatedCandidate Candidate(string symbol, decimal holdoutSharpe, bool survived = true) => new()
    {
        StrategyName = "EventTrigger",
        Symbol = symbol,
        Timeframe = "1h",
        HoldoutSharpe = holdoutSharpe,
        Survived = survived,
    };

    private static NullTwinVerdict Verdict(decimal real, bool passed, double percentileOfReal) => new()
    {
        ValidTwins = 200,
        RealSharpe = real,
        Median = 0m, P90 = 1m, P95 = 1.5m, P99 = 2.5m, Max = 3m,
        RequiredPercentile = 0.99,
        Threshold = 2.5m,
        PercentileOfReal = percentileOfReal,
        EmpiricalPValue = 1 - percentileOfReal / 100.0,
        Passed = passed,
    };

    [Fact]
    public async Task BocciaIlFinalistaDentroIlNullo_EPromuoveChiLoBatte()
    {
        var a = Candidate("AAA/USDT", holdoutSharpe: 2.0m);
        var b = Candidate("BBB/USDT", holdoutSharpe: 1.0m);
        // AAA resta dentro il nullo (86°), BBB lo batte.
        var judge = new StubNullTwinJudge(real => real == 2.0m
            ? Verdict(real, passed: false, percentileOfReal: 86)
            : Verdict(real, passed: true, percentileOfReal: 99.5));
        var stage = new NullTwinValidationStage(judge);

        await stage.ExecuteAsync(Context(a, b), new StageConfig(), CancellationToken.None);

        Assert.False(a.Survived);
        Assert.StartsWith("Gemello nullo", a.RejectReason);
        Assert.Equal(86.0, a.NullTwinPercentile!.Value, precision: 9);
        Assert.True(b.Survived);
        Assert.Null(b.RejectReason);
        Assert.Equal(99.5, b.NullTwinPercentile!.Value, precision: 9);
        Assert.Equal(2, judge.Calls.Count);
    }

    [Fact]
    public async Task FailSafe_NonGiudicabileLasciaPassareAVoceAlta()
    {
        var a = Candidate("AAA/USDT", holdoutSharpe: 1.2m);
        var judge = new StubNullTwinJudge(_ => null); // gemelli validi insufficienti
        var stage = new NullTwinValidationStage(judge);

        var ctx = Context(a);
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.True(a.Survived); // un giudice che non può giudicare non boccia al buio
        Assert.Null(a.NullTwinPercentile);
        Assert.Single(judge.Calls);
    }

    [Fact]
    public async Task RispettaIlTettoDeiCandidati_IMiglioriPerSharpeHoldout()
    {
        var top = Candidate("TOP/USDT", holdoutSharpe: 3.0m);
        var mid = Candidate("MID/USDT", holdoutSharpe: 2.0m);
        var low = Candidate("LOW/USDT", holdoutSharpe: 1.0m);
        var judge = new StubNullTwinJudge(real => Verdict(real, passed: true, percentileOfReal: 99.5));
        var stage = new NullTwinValidationStage(judge);

        var config = new StageConfig { Parameters = new() { ["maxCandidates"] = "1" } };
        await stage.ExecuteAsync(Context(low, top, mid), config, CancellationToken.None);

        var call = Assert.Single(judge.Calls);
        Assert.Equal("TOP/USDT", call.Symbol); // giudicato solo il migliore
        Assert.True(low.Survived && mid.Survived); // i non giudicati restano com'erano
    }

    [Fact]
    public async Task HoldoutTroppoCorto_NonChiamaIlGiudiceELasciaPassare()
    {
        var a = Candidate("AAA/USDT", holdoutSharpe: 1.5m);
        var judge = new StubNullTwinJudge(real => Verdict(real, passed: false, percentileOfReal: 10));
        var stage = new NullTwinValidationStage(judge);

        // Cache con sole 100 barre nel range holdout: sotto il minimo di 200.
        var cache = new InMemoryCandleCache();
        cache.Add(a.Symbol, a.Timeframe, Flat(a.Symbol, 100));
        var ctx = new PipelineContext
        {
            ExchangeName = "Binance",
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = Start,
                SelectionTo = Start.AddHours(80),
                HoldoutFrom = Start,
                HoldoutTo = Start.AddHours(100),
            },
            Candles = cache,
        };
        ctx.Validated.Add(a);

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.Empty(judge.Calls);
        Assert.True(a.Survived);
    }

    [Fact]
    public async Task IgnoraICandidatiGiaBocciatiDallHoldout()
    {
        var morto = Candidate("DEAD/USDT", holdoutSharpe: 5.0m, survived: false);
        var vivo = Candidate("LIVE/USDT", holdoutSharpe: 1.0m);
        var judge = new StubNullTwinJudge(real => Verdict(real, passed: true, percentileOfReal: 99.5));
        var stage = new NullTwinValidationStage(judge);

        await stage.ExecuteAsync(Context(morto, vivo), new StageConfig(), CancellationToken.None);

        var call = Assert.Single(judge.Calls);
        Assert.Equal("LIVE/USDT", call.Symbol);
        Assert.False(morto.Survived);
    }

    [Fact]
    public void IlCatalogoEsponeLoStage_PrimaDellaRobustnessProbe()
    {
        // A parità di DefaultOrder (10) l'ordine del catalogo decide: il nullo boccia, la probe
        // arricchisce — se questo ordine cambia, deve cambiare per scelta, non per sbadataggine.
        var types = PipelineStageCatalog.StageTypes.ToList();
        var nullTwin = types.IndexOf(typeof(NullTwinValidationStage));
        var probe = types.IndexOf(typeof(RobustnessProbeStage));
        var holdout = types.IndexOf(typeof(HoldoutValidationStage));
        Assert.True(nullTwin > holdout, "il gemello giudica DOPO l'holdout");
        Assert.True(nullTwin < probe, "il gemello sta PRIMA della probe nel catalogo");
    }
}
