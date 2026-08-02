using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Health;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF5.1] L'heartbeat incrociato. I test difendono due proprietà:
/// 1) il RUMORE non accende niente — un battito in ritardo sotto la soglia, o l'assenza totale
///    della riga (feature spenta sull'altro host), non producono notifiche;
/// 2) le transizioni notificano UNA volta per verso, mai a raffica.
/// </summary>
public sealed class HeartbeatMonitorLogicTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    [Fact]
    public void NoRow_IsUnknown_NotStale()
        => Assert.Equal(HeartbeatHealth.Unknown, HeartbeatMonitorLogic.Evaluate(null, Now, Stale));

    [Theory]
    [InlineData(0, HeartbeatHealth.Healthy)]
    [InlineData(9, HeartbeatHealth.Healthy)]
    [InlineData(10, HeartbeatHealth.Healthy)]   // esattamente sulla soglia: non ancora muto
    [InlineData(11, HeartbeatHealth.Stale)]
    [InlineData(600, HeartbeatHealth.Stale)]
    public void Staleness_IsAThreshold_NotAMood(int minutesAgo, HeartbeatHealth expected)
        => Assert.Equal(expected, HeartbeatMonitorLogic.Evaluate(Now.AddMinutes(-minutesAgo), Now, Stale));

    [Fact]
    public void Transitions_NotifyOncePerDirection()
    {
        var tracker = new HeartbeatTransitionTracker("engine");

        Assert.Null(tracker.Observe(HeartbeatHealth.Unknown));            // mai visto: silenzio
        Assert.Null(tracker.Observe(HeartbeatHealth.Healthy));            // primo battito: silenzio
        Assert.Null(tracker.Observe(HeartbeatHealth.Healthy));            // rumore sano: silenzio

        var down = tracker.Observe(HeartbeatHealth.Stale);                // diventa muto: Warning
        Assert.NotNull(down);
        Assert.Equal(NotificationSeverity.Warning, down.Severity);

        Assert.Null(tracker.Observe(HeartbeatHealth.Stale));              // ancora muto: già detto

        var up = tracker.Observe(HeartbeatHealth.Healthy);                // torna: Info
        Assert.NotNull(up);
        Assert.Equal(NotificationSeverity.Info, up.Severity);

        Assert.Null(tracker.Observe(HeartbeatHealth.Healthy));            // di nuovo sano: silenzio
    }

    [Fact]
    public void StaleAtFirstObservation_StillNotifies()
    {
        // L'altro host era già morto quando questo è partito: il primo sguardo trova una riga
        // vecchia. È esattamente il caso per cui il canale esiste — deve gridare subito.
        var tracker = new HeartbeatTransitionTracker("shell");
        var notice = tracker.Observe(HeartbeatHealth.Stale);
        Assert.NotNull(notice);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
    }

    [Fact]
    public void UnknownForever_NeverNotifies()
    {
        // Feature spenta sull'altro host: la riga non c'è e non ci sarà. Silenzio per sempre —
        // un allarme costruito sull'ignoranza è la classe di difetto gemella del controllo che
        // rassicura a prescindere.
        var tracker = new HeartbeatTransitionTracker("engine");
        for (var i = 0; i < 100; i++)
        {
            Assert.Null(tracker.Observe(HeartbeatHealth.Unknown));
        }
    }
}

/// <summary>
/// Il giro completo su Postgres vero: il writer fa upsert della SOLA riga del proprio ruolo, e la
/// riga si aggiorna invece di moltiplicarsi.
/// </summary>
[Collection("Postgres")]
public sealed class HostHeartbeatPersistenceTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly string _connString = pg.CreateDatabase();
    private ServiceProvider? _provider;

    private sealed class PassthroughEncryption : ProcioneMGR.Services.Security.IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    [Fact]
    public async Task Writer_UpsertsItsOwnRow_NeverDuplicates()
    {
        var dbFactory = await DbAsync();

        // Due battiti dello stesso ruolo = una riga aggiornata, non due righe.
        for (var beat = 0; beat < 2; beat++)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.HostHeartbeats.FirstOrDefaultAsync(h => h.Host == HostHeartbeat.ShellRole);
            if (row is null)
            {
                db.HostHeartbeats.Add(new HostHeartbeat { Host = HostHeartbeat.ShellRole, LastUtc = DateTime.UtcNow, Version = "test" });
            }
            else
            {
                row.LastUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        await using var check = await dbFactory.CreateDbContextAsync();
        var rows = await check.HostHeartbeats.AsNoTracking().ToListAsync();
        var single = Assert.Single(rows);
        Assert.Equal(HostHeartbeat.ShellRole, single.Host);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
