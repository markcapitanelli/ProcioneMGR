using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica che i tipi "sensibili al provider" sopravvivano a un round-trip persistenza→reload
/// SENZA perdita di informazione: blob binari (modelli ML), decimal ad alta precisione (prezzi
/// crypto) e stringhe JSON. Gira su un database PostgreSQL effimero (Testcontainers), l'unico
/// provider supportato.
/// </summary>
[Collection("Postgres")]
public class ProviderCompatibilityTests
{
    private readonly PostgresFixture _pg;

    public ProviderCompatibilityTests(PostgresFixture pg) => _pg = pg;

    private Func<ApplicationDbContext> MakeFactory()
    {
        var conn = _pg.CreateDatabase();
        ApplicationDbContext Factory() => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options,
            new PassthroughEncryption());
        using (var db = Factory()) db.Database.EnsureCreated();
        return Factory;
    }

    // Prezzi crypto ad alta precisione: 8 decimali significativi, il limite della scala numeric(18,8).
    private const decimal PepePrice = 0.00001234m;
    private const decimal BtcPrice = 68234.12345678m;

    private static async Task RoundTripAsync(Func<ApplicationDbContext> factory)
    {
        // Un utente FK-valido per SavedMlModel.
        const string userId = "compat-user";
        var blob = new byte[512];
        new Random(1234).NextBytes(blob);           // blob binario non banale
        var json = """{"a":1,"b":[2,3],"nested":{"txt":"ünïcödé € 中文"}}""";

        var runId = Guid.NewGuid();

        await using (var db = factory())
        {
            if (!await db.Users.AnyAsync(u => u.Id == userId))
                db.Users.Add(new ApplicationUser { Id = userId, UserName = "compat@x.io", NormalizedUserName = "COMPAT@X.IO" });

            db.SavedMlModels.Add(new SavedMlModel
            {
                UserId = userId,
                Name = "Compat model",
                ModelType = "RandomForest",
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                TrainingDataFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                TrainingDataTo = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Unspecified),
                ForwardHorizon = 1,
                FactorsJson = json,
                ModelBytes = blob,
                TrainRowCount = 1000,
                TrainCorrelation = 0.4242,
            });

            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0,
                Symbol = "PEPE/USDT",
                Side = OrderSide.Buy,
                EntryPrice = PepePrice,
                Quantity = 123456.78901234m,
                StopLoss = PepePrice * 0.98m,
                OpenedAtUtc = new DateTime(2024, 5, 5, 12, 0, 0, DateTimeKind.Unspecified),
                CurrentPrice = BtcPrice,
                UnrealizedPnl = -1.23456789m,
                UnrealizedPnlPercent = -0.5m,
            });

            db.PipelineRuns.Add(new PipelineRun
            {
                Id = runId,
                ConfigurationId = 1,
                StartedAt = new DateTime(2024, 5, 5, 12, 0, 0, DateTimeKind.Unspecified),
                Status = "Completed",
                Trigger = "Manual",
                ContextSnapshotJson = json,
                StageSummariesJson = "[]",
                Conclusion = "ok",
            });

            await db.SaveChangesAsync();
        }

        // Reload da un contesto NUOVO (niente identity-map: forza una lettura reale dal DB).
        await using (var db = factory())
        {
            var model = await db.SavedMlModels.SingleAsync(m => m.UserId == userId);
            Assert.Equal(blob, model.ModelBytes);                    // blob byte-per-byte
            Assert.Equal(json, model.FactorsJson);                   // JSON incl. unicode

            var pos = await db.OpenPositions.SingleAsync(p => p.Symbol == "PEPE/USDT");
            Assert.Equal(PepePrice, pos.EntryPrice);                 // decimal alta precisione
            Assert.Equal(123456.78901234m, pos.Quantity);
            Assert.Equal(BtcPrice, pos.CurrentPrice);
            Assert.Equal(-1.23456789m, pos.UnrealizedPnl);

            var run = await db.PipelineRuns.SingleAsync(r => r.Id == runId);
            Assert.Equal(json, run.ContextSnapshotJson);             // JSON grande
            Assert.Equal("Completed", run.Status);
        }
    }

    [Fact]
    public async Task Postgres_PreservesBlobDecimalAndJson()
    {
        await RoundTripAsync(MakeFactory());
    }
}
