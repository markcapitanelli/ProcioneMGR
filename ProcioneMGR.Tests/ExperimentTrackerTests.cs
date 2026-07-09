using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'Experiment Tracker (rif. <c>docs/ROADMAP-QLIB.md §1.3</c>): ciclo di vita di un run
/// (Running → metriche → Completed), merge delle metriche, hash "git-like" dei parametri
/// (config identiche ⇒ hash identico), e robustezza best-effort degli helper Safe* (non lanciano).
/// </summary>
[Collection("Postgres")]
public sealed class ExperimentTrackerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ExperimentTrackerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(ExperimentTracker Tracker, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        return (new ExperimentTracker(dbFactory), dbFactory);
    }

    [Fact]
    public async Task StartLogComplete_PersistsRunWithMetrics()
    {
        var (tracker, dbFactory) = await BuildAsync();

        var runId = await tracker.StartRunAsync(
            "MlTraining", "test run", new { ModelType = "Linear", Factors = 8 }, "BTCUSDT", "1h", "user-1");

        await tracker.LogMetricsAsync(runId, new Dictionary<string, decimal> { ["TrainRows"] = 100m, ["Corr"] = 0.12m });
        await tracker.LogMetricsAsync(runId, new Dictionary<string, decimal> { ["Corr"] = 0.15m, ["Extra"] = 1m }); // merge + override
        await tracker.CompleteAsync(runId, "Completed");

        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.ExperimentRuns.SingleAsync(r => r.Id == runId);

        Assert.Equal("MlTraining", run.Kind);
        Assert.Equal("BTCUSDT", run.Symbol);
        Assert.Equal("user-1", run.CreatedBy);
        Assert.Equal("Completed", run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.NotEmpty(run.ParametersHash);

        var metrics = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(run.MetricsJson)!;
        Assert.Equal(100m, metrics["TrainRows"]);
        Assert.Equal(0.15m, metrics["Corr"]);   // sovrascritto dal secondo log
        Assert.Equal(1m, metrics["Extra"]);
    }

    [Fact]
    public async Task IdenticalParameters_ProduceIdenticalHash_DifferentDoNot()
    {
        var (tracker, dbFactory) = await BuildAsync();

        var a = await tracker.StartRunAsync("Backtest", "a", new { Symbol = "BTCUSDT", Lookback = 20 });
        var b = await tracker.StartRunAsync("Backtest", "b", new { Symbol = "BTCUSDT", Lookback = 20 });
        var c = await tracker.StartRunAsync("Backtest", "c", new { Symbol = "BTCUSDT", Lookback = 30 });

        await using var db = await dbFactory.CreateDbContextAsync();
        var runs = await db.ExperimentRuns.ToDictionaryAsync(r => r.Id, r => r.ParametersHash);

        Assert.Equal(runs[a], runs[b]);        // stessa config ⇒ stesso hash
        Assert.NotEqual(runs[a], runs[c]);     // config diversa ⇒ hash diverso
    }

    [Fact]
    public async Task LogArtifact_IsPersistedAgainstRun()
    {
        var (tracker, dbFactory) = await BuildAsync();
        var runId = await tracker.StartRunAsync("Discovery", "d", new { N = 3 });
        await tracker.LogArtifactAsync(runId, "EquityCurve", new[] { 1, 2, 3 });

        await using var db = await dbFactory.CreateDbContextAsync();
        var art = await db.ExperimentArtifacts.SingleAsync(a => a.RunId == runId);
        Assert.Equal("EquityCurve", art.KindTag);
        Assert.Contains("1", art.PayloadJson);
    }

    [Fact]
    public async Task SafeHelpers_NeverThrow_EvenForMissingRun()
    {
        var (tracker, _) = await BuildAsync();

        // Nessun run aperto (Guid.Empty) e run inesistente: gli helper best-effort restano silenziosi.
        await tracker.SafeLogMetricsAsync(Guid.Empty, new Dictionary<string, decimal> { ["x"] = 1m });
        await tracker.SafeCompleteAsync(Guid.Empty, "Completed");
        await tracker.SafeLogMetricsAsync(Guid.NewGuid(), new Dictionary<string, decimal> { ["x"] = 1m });

        var runId = await tracker.SafeStartRunAsync("Backtest", "ok", new { A = 1 });
        Assert.NotEqual(Guid.Empty, runId);
        await tracker.SafeCompleteAsync(runId, "Completed");
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
