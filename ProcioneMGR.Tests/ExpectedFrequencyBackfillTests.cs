using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J9, PRD autonomia-operativa 2026-08-25] La ricostruzione delle frequenze attese. Il cerchio
/// che rompe: il ritiro per inedia pretende ExpectedTradesPerMonth, scritto SOLO da un nuovo
/// schieramento — e le corsie schierate prima di I11 hanno null su ogni gamba, quindi l'inedia non
/// giudica mai e nessuna corsia si libera mai. Questi test pinnano: la ricostruzione per identità
/// canonica (non per DisplayName), la STESSA aritmetica dello schieramento nuovo, il candidato non
/// trovato che resta null col perché dichiarato (mai un denominatore inventato), l'anteprima che
/// non scrive.
/// </summary>
[Collection("Postgres")]
public class ExpectedFrequencyBackfillTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ExpectedFrequencyBackfillTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeEnsembleManager(int laneId, EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => laneId;
        public EnsembleConfiguration Config { get; private set; } = config;
        public int Saves { get; private set; }
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration config, ProcioneMGR.Services.Ensemble.ConfigWriteContext writtenBy, CancellationToken ct = default)
        {
            Config = config;
            Saves++;
            return Task.CompletedTask;
        }
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private async Task<(ExpectedFrequencyBackfill Backfill, FakeEnsembleManager[] Managers, IDbContextFactory<ApplicationDbContext> DbFactory)>
        BuildAsync(params EnsembleConfiguration[] configs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var managers = new FakeEnsembleManager[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var cfg = lane < configs.Length ? configs[lane] : new EnsembleConfiguration();
            managers[lane] = new FakeEnsembleManager(lane, cfg);
            services.AddKeyedSingleton<IEnsembleManager>(lane, managers[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var backfill = new ExpectedFrequencyBackfill(dbFactory, provider, NullLogger<ExpectedFrequencyBackfill>.Instance);
        return (backfill, managers, dbFactory);
    }

    private static EnsembleConfiguration LaneConfig(string symbol, string timeframe, string strategy, Dictionary<string, decimal> parameters) => new()
    {
        Symbol = symbol,
        Timeframe = timeframe,
        Strategies =
        [
            new EnsembleStrategy
            {
                StrategyName = strategy,
                DisplayName = $"{strategy} (fascia grigia, run xxxxxxxx)",
                Parameters = parameters,
                IsActive = true,
                CurrentAllocation = 100m,
            },
        ],
    };

    /// <summary>Semina il candidato di provenienza con la finestra di holdout della sua config (4 mesi).</summary>
    private static async Task SeedProvenanceAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, string key, int holdoutTrades)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var to = DateTime.UtcNow.AddDays(-3);
        var config = new PipelineConfiguration
        {
            Name = "Provenienza", CreatedBy = "test",
            DateRangesJson = System.Text.Json.JsonSerializer.Serialize(new PipelineDateRanges
            {
                SelectionFrom = to.AddMonths(-16), SelectionTo = to.AddMonths(-4),
                HoldoutFrom = to.AddMonths(-4), HoldoutTo = to,
            }),
        };
        db.PipelineConfigurations.Add(config);
        await db.SaveChangesAsync();

        var runId = Guid.NewGuid();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId, ConfigurationId = config.Id, StartedAt = to.AddMinutes(-40),
            CompletedAt = to, Status = "Completed", Trigger = "Campaign",
        });
        db.ResearchCandidates.Add(new ResearchCandidate
        {
            RunId = runId, RunCompletedUtc = to, StrategyName = "GridMeanReversion",
            Symbol = "UNI/USDT", Timeframe = "4h", CandidateKey = key,
            ParametersJson = "{}", BestStopVariant = "base",
            HoldoutTrades = holdoutTrades, HoldoutSharpe = 1.2m, IsGrey = true,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Ricostruisce_PerIdentitaCanonica_ConLaStessaAritmeticaDelloSchieramento()
    {
        var parameters = new Dictionary<string, decimal> { ["period"] = 14m, ["threshold"] = 0.5m };
        var cfg = LaneConfig("UNI/USDT", "4h", "GridMeanReversion", parameters);
        var key = PipelineCandidateKey.Build("GridMeanReversion", "UNI/USDT", "4h", parameters);

        var (backfill, managers, dbFactory) = await BuildAsync(cfg);
        await SeedProvenanceAsync(dbFactory, key, holdoutTrades: 12);

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(1, report.Updated);
        var leg = managers[0].Config.Strategies.Single();
        // 12 trade su ~4 mesi ≈ 3 trade/mese (la stessa aritmetica di HoldoutWindow/TradeFrequency).
        Assert.NotNull(leg.ExpectedTradesPerMonth);
        Assert.InRange(leg.ExpectedTradesPerMonth!.Value, 2.5m, 3.5m);
        // La provenienza dichiara che è una RICOSTRUZIONE, non il numero dello schieramento.
        Assert.Contains("[J9", leg.ExpectedTradesSource);
        Assert.Equal(1, managers[0].Saves);
    }

    [Fact]
    public async Task CandidatoNonTrovato_RestaNull_ColPercheDichiarato()
    {
        var cfg = LaneConfig("XRP/USDT", "4h", "GridMeanReversion", new() { ["period"] = 20m });
        var (backfill, managers, _) = await BuildAsync(cfg);

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, report.Updated);
        var outcome = Assert.Single(report.Legs, l => l.Leg.Contains("XRP"));
        Assert.Contains("non trovato", outcome.Detail);
        Assert.Null(managers[0].Config.Strategies.Single().ExpectedTradesPerMonth);
        Assert.Equal(0, managers[0].Saves); // niente da salvare = nessuna scrittura
    }

    [Fact]
    public async Task Anteprima_NonScrive()
    {
        var parameters = new Dictionary<string, decimal> { ["period"] = 14m };
        var cfg = LaneConfig("UNI/USDT", "4h", "GridMeanReversion", parameters);
        var key = PipelineCandidateKey.Build("GridMeanReversion", "UNI/USDT", "4h", parameters);

        var (backfill, managers, dbFactory) = await BuildAsync(cfg);
        await SeedProvenanceAsync(dbFactory, key, holdoutTrades: 12);

        var report = await backfill.RunAsync(dryRun: true);

        Assert.Equal(1, report.Updated); // ricostruibile...
        Assert.Null(managers[0].Config.Strategies.Single().ExpectedTradesPerMonth); // ...ma non scritto
        Assert.Equal(0, managers[0].Saves);
    }

    [Fact]
    public async Task GambaConAttesoGiaDichiarato_NonSiTocca()
    {
        var cfg = LaneConfig("UNI/USDT", "4h", "GridMeanReversion", new() { ["period"] = 14m });
        cfg.Strategies[0].ExpectedTradesPerMonth = 7m;
        cfg.Strategies[0].ExpectedTradesSource = "holdout del run abcd1234";

        var (backfill, managers, _) = await BuildAsync(cfg);
        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, report.Legs.Count(l => l.Leg.Contains("UNI"))); // non esaminata: non manca nulla
        Assert.Equal(7m, managers[0].Config.Strategies.Single().ExpectedTradesPerMonth);
        Assert.Equal(0, managers[0].Saves);
    }
}
