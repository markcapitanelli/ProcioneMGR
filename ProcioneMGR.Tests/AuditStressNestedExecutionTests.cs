using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Tests.Infrastructure;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 2.3 — nested execution ad alta frequenza: il loop decisione-coarse -> piano ->
/// simulazione su candele fini, martellato in parallelo da più thread. Verifiche: purezza e
/// determinismo (stesso input => stesso risultato da qualunque thread), conservazione esatta
/// della quantità sotto parallelismo, e concorrenza dell'ExperimentTracker (metriche concorrenti
/// sullo stesso run, run paralleli, nessun deadlock).
/// </summary>
[Trait("Category", "Stress")]
public sealed class AuditStressNestedExecutionTests
{
    private readonly ITestOutputHelper _output;

    public AuditStressNestedExecutionTests(ITestOutputHelper output) => _output = output;

    private static List<OhlcvData> FineCandles(int n, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            price = Math.Max(1m, price + (decimal)(rnd.NextDouble() - 0.5));
            list.Add(new OhlcvData
            {
                Symbol = "BTCUSDT", Timeframe = "5m", TimestampUtc = t0.AddMinutes(5 * i),
                Open = price, High = price + 0.6m, Low = price - 0.6m, Close = price + 0.1m,
                Volume = 100m + (decimal)(rnd.NextDouble() * 900),
            });
        }
        return list;
    }

    [Fact]
    public void HighFrequencyNestedLoop_AllAlgorithms_ParallelThreads_DeterministicAndExact()
    {
        var factory = new ExecutionAlgorithmFactory();
        var simulator = new ExecutionSimulator();
        var algos = new[] { "Immediate", "Twap", "Vwap", "Iceberg", "Adaptive" };
        var parameters = new ExecutionParameters { MaxSlices = 8, IcebergClipFraction = 0.25m };

        // Riferimento single-thread: per ogni (algo, scenario) il risultato "giusto".
        var scenarios = Enumerable.Range(0, 50).Select(s => new
        {
            Seed = s,
            Candles = FineCandles(24, s),
            Intent = new ExecutionIntent("BTCUSDT", s % 2 == 0 ? ExecutionSide.Buy : ExecutionSide.Sell,
                50m + s, 100m + s),
        }).ToList();

        var reference = new Dictionary<(string, int), ExecutionResult>();
        foreach (var algo in algos)
        {
            foreach (var sc in scenarios)
            {
                var plan = factory.Create(algo).BuildPlan(sc.Intent, sc.Candles, parameters);
                reference[(algo, sc.Seed)] = simulator.Simulate(plan, sc.Intent, sc.Candles, parameters);
            }
        }

        // Loop ad alta frequenza: 16 worker × 2.000 iterazioni, ordine casuale per worker.
        var failures = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        var totalIterations = 0L;
        Parallel.For(0, 16, worker =>
        {
            var rnd = new Random(worker);
            for (var i = 0; i < 2_000; i++)
            {
                var algo = algos[rnd.Next(algos.Length)];
                var sc = scenarios[rnd.Next(scenarios.Count)];
                var plan = factory.Create(algo).BuildPlan(sc.Intent, sc.Candles, parameters);

                var qty = plan.Slices.Sum(s => s.Quantity);
                if (qty != sc.Intent.TotalQuantity)
                {
                    failures.Add($"{algo}@{sc.Seed}: quantità {qty} != {sc.Intent.TotalQuantity}");
                }

                var result = simulator.Simulate(plan, sc.Intent, sc.Candles, parameters);
                var expected = reference[(algo, sc.Seed)];
                if (result.FilledQuantity != expected.FilledQuantity ||
                    result.AverageFillPrice != expected.AverageFillPrice ||
                    result.SlippageBps != expected.SlippageBps)
                {
                    failures.Add($"{algo}@{sc.Seed}: risultato non deterministico sotto parallelismo");
                }
                Interlocked.Increment(ref totalIterations);
            }
        });
        sw.Stop();

        Assert.True(failures.IsEmpty, string.Join(" | ", failures.Take(10)));
        _output.WriteLine($"Nested loop: {totalIterations:N0} piani+simulazioni in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({totalIterations / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0}/s su 16 thread)");
    }
}

/// <summary>
/// Audit FASE 2.3 (parte DB) — l'ExperimentTracker sotto concorrenza reale su Postgres:
/// metriche loggate in parallelo sullo STESSO run non devono perdersi (il read-modify-write
/// del JSON è la superficie a rischio), e run interi in parallelo devono completare tutti
/// senza deadlock.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Stress")]
public sealed class AuditStressExperimentTrackerConcurrencyTests
{
    private readonly string _connString;
    private readonly ITestOutputHelper _output;

    public AuditStressExperimentTrackerConcurrencyTests(PostgresFixture pg, ITestOutputHelper output)
    {
        _connString = pg.CreateDatabase();
        _output = output;
    }

    private async Task<(ExperimentTracker Tracker, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        return (new ExperimentTracker(dbFactory), dbFactory);
    }

    [Fact]
    public async Task ConcurrentMetricLogging_OnSameRun_MustNotLoseAnyMetric()
    {
        var (tracker, dbFactory) = await BuildAsync();
        var runId = await tracker.StartRunAsync("Stress", "concurrent metrics", new { });

        // 32 scrittori paralleli, ciascuno con la SUA chiave: alla fine devono esserci TUTTE.
        const int writers = 32;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            tracker.LogMetricsAsync(runId, new Dictionary<string, decimal> { [$"m{i:D2}"] = i })));

        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.ExperimentRuns.SingleAsync(r => r.Id == runId);
        var metrics = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(run.MetricsJson)!;

        _output.WriteLine($"Metriche sopravvissute: {metrics.Count}/{writers}");
        var missing = Enumerable.Range(0, writers).Select(i => $"m{i:D2}").Where(k => !metrics.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0,
            $"LOST UPDATE: {missing.Count}/{writers} metriche perse ({string.Join(",", missing.Take(8))}...) — " +
            "il read-modify-write di MetricsJson non è atomico");
    }

    [Fact]
    public async Task ParallelFullRuns_AllCompleteWithArtifacts_NoDeadlock()
    {
        var (tracker, dbFactory) = await BuildAsync();

        const int runs = 16;
        var sw = Stopwatch.StartNew();
        var ids = await Task.WhenAll(Enumerable.Range(0, runs).Select(async i =>
        {
            var id = await tracker.StartRunAsync("Stress", $"run {i}", new { Index = i, Alpha = 0.1m * i });
            await tracker.LogMetricsAsync(id, new Dictionary<string, decimal> { ["Sharpe"] = 1.0m + i, ["Trades"] = i });
            await tracker.LogArtifactAsync(id, "params", new { Window = i });
            await tracker.CompleteAsync(id, "Completed");
            return id;
        }));
        sw.Stop();

        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var id in ids)
        {
            var run = await db.ExperimentRuns.SingleAsync(r => r.Id == id);
            Assert.Equal("Completed", run.Status);
            Assert.NotNull(run.CompletedAt);
            Assert.Contains("Sharpe", run.MetricsJson);
            Assert.Equal(1, await db.ExperimentArtifacts.CountAsync(a => a.RunId == id));
        }

        // Parametri diversi => hash diversi (confrontabilità deterministica).
        var hashes = await db.ExperimentRuns.Where(r => ids.Contains(r.Id)).Select(r => r.ParametersHash).ToListAsync();
        Assert.Equal(runs, hashes.Distinct().Count());
        _output.WriteLine($"{runs} run completi in parallelo in {sw.Elapsed.TotalSeconds:F1}s");
    }
}
