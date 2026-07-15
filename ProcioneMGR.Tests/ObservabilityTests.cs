using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'osservabilità (Fase 5): i contatori del <see cref="ProcioneMetrics"/> emettono le
/// misure attese, e i worker di autonomia le producono sugli eventi chiave. Verificato col
/// <see cref="MeterListener"/> del BCL — nessuna dipendenza da OpenTelemetry (che è solo l'export).
/// </summary>
[Collection("Postgres")]
public class ObservabilityTests
{
    private readonly PostgresFixture _pg;

    public ObservabilityTests(PostgresFixture pg) => _pg = pg;

    /// <summary>Raccoglie le misure long emesse dal meter "ProcioneMGR" per la durata dello scope.</summary>
    private static (MeterListener listener, List<(string Name, long Value)> longs, List<(string Name, double Value)> doubles) Listen()
    {
        var longs = new List<(string, long)>();
        var doubles = new List<(string, double)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == ProcioneMetrics.MeterName) l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) => longs.Add((inst.Name, val)));
        listener.SetMeasurementEventCallback<double>((inst, val, _, _) => doubles.Add((inst.Name, val)));
        listener.Start();
        return (listener, longs, doubles);
    }

    [Fact]
    public void ProcioneMetrics_RecordsAllCounters()
    {
        using var metrics = new ProcioneMetrics();
        var (listener, longs, doubles) = Listen();
        using (listener)
        {
            metrics.RecordLanePromotion(1, "Testnet");
            metrics.RecordDriftAlerts("BTCUSDT", "1h", 3);
            metrics.RecordModelRetired("BTCUSDT", "1h");
            metrics.RecordPipelineRun("Scheduled");
            metrics.RecordTradeExecuted("Paper", "Buy");
            metrics.RecordExecutionJob("Twap", "Completed");
            metrics.RecordExecutionSlippage(12.5, "Twap");
        }

        Assert.Contains(("procione.lane.promotions", 1L), longs);
        Assert.Contains(("procione.drift.alerts", 3L), longs);     // il valore è il numero di alert
        Assert.Contains(("procione.models.retired", 1L), longs);
        Assert.Contains(("procione.pipeline.runs", 1L), longs);
        Assert.Contains(("procione.trades.executed", 1L), longs);
        Assert.Contains(("procione.execution.jobs", 1L), longs);
        Assert.Contains(("procione.execution.slippage_bps", 12.5), doubles);
    }

    [Fact]
    public async Task DriftWorker_EmitsDriftAndRetirementMetrics_ForChampion()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_pg.CreateDatabase()));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        // Seed: un utente e un modello Champion.
        string userId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var user = new ApplicationUser { UserName = "t", Email = "t@t" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            db.SavedMlModels.Add(new SavedMlModel
            {
                UserId = userId, Name = "champ", ModelType = "Linear", Symbol = "BTCUSDT", Timeframe = "1h",
                FactorsJson = "[]", ModelBytes = new byte[] { 1 }, DeflatedSharpe = 0.9, Stage = ModelStage.Champion,
                PromotedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var metrics = new ProcioneMetrics();
        var registry = new ModelRegistry(factory, new ModelRegistryOptions(), NullLogger<ModelRegistry>.Instance);
        var worker = new FeatureDriftWorker(
            factory, new AlertMonitor(), registry,
            new DriftMonitorOptions { RetireChampionOnAlert = true, MinAlertsToRetire = 1 }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance, metrics);

        var (listener, longs, _) = Listen();
        using (listener) await worker.TickAsync(CancellationToken.None);

        Assert.Contains(("procione.drift.alerts", 1L), longs);   // AlertMonitor emette 1 feature in alert
        Assert.Contains(("procione.models.retired", 1L), longs); // Champion ritirato dal ciclo chiuso
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Monitor che segnala sempre una feature in drift Alert (come in ModelRegistryTests).</summary>
    private sealed class AlertMonitor : IFeatureDriftMonitor
    {
        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            IReadOnlyList<FactorDriftReport> reports = new[]
            {
                new FactorDriftReport
                {
                    FeatureName = "Mom1",
                    Results = new[] { new DriftResult("Psi", 0.5, null, DriftSeverity.Alert, "shift") },
                },
            };
            return Task.FromResult(reports);
        }
    }
}

/// <summary>
/// Test di fumo del wiring OTLP opt-in (Fase 0 microservizi): con Observability:Enabled=true il
/// container DI si costruisce e i provider OTel si risolvono senza che alcun collector sia in
/// ascolto (l'exporter OTLP è fire-and-forget); con il flag OFF non viene registrato nulla.
/// Nessuna dipendenza da Postgres: classe separata fuori dalla collection.
/// </summary>
public class ObservabilityWiringTests
{
    private static IConfiguration BuildConfig(bool enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Enabled"] = enabled ? "true" : "false",
            ["Observability:OtlpEndpoint"] = "http://localhost:4317",
        }).Build();

    [Fact]
    public void AddProcioneObservability_Enabled_BuildsContainerWithoutCollector()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProcioneObservability(BuildConfig(enabled: true));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>());
        Assert.NotNull(provider.GetRequiredService<OpenTelemetry.Logs.LoggerProvider>());
    }

    [Fact]
    public void AddProcioneObservability_Disabled_RegistersNothing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProcioneObservability(BuildConfig(enabled: false));

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<OpenTelemetry.Metrics.MeterProvider>());
        Assert.Null(provider.GetService<OpenTelemetry.Logs.LoggerProvider>());
    }
}
