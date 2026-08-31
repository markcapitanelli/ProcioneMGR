using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Health;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K7+K8 — superficie UI, 2026-08-31] Il quadro dei battiti in Home.
///
/// <para>Nasce da un audit della copertura UI della Fase 0: le manopole nuove avevano tutte il loro
/// pannello, ma <b>quello che i battiti dicono non si vedeva da nessuna parte in app</b> — solo in
/// <c>procione stato</c>, una console che si apre quando si sospetta già qualcosa. Ed è il dato che
/// quel giorno è passato da UNA riga a quattro.</para>
///
/// <para>La proprietà difesa qui è una sola, e non è ovvia: <b>un ruolo atteso che non ha riga deve
/// comparire lo stesso</b>. Mostrare solo ciò che il database contiene produce un quadro verde per
/// sottrazione — «non ha mai battuto» sparirebbe, ed è il più grave dei due modi di tacere.</para>
/// </summary>
[Collection("Postgres")]
public class HeartbeatBoardProbeTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public HeartbeatBoardProbeTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(HeartbeatBoardProbe Probe, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();
        return (new HeartbeatBoardProbe(db, NullLogger<HeartbeatBoardProbe>.Instance), db);
    }

    private static async Task BatteAsync(IDbContextFactory<ApplicationDbContext> db, string ruolo, DateTime quando, string? versione = null)
    {
        await using var c = await db.CreateDbContextAsync();
        c.HostHeartbeats.Add(new HostHeartbeat { Host = ruolo, LastUtc = quando, Version = versione ?? "x" });
        await c.SaveChangesAsync();
    }

    [Fact]
    public async Task UnRuoloATTESO_senzaRiga_COMPARE_comeMuto()
    {
        // Il cuore del test. Con la tabella vuota il quadro «non ha righe», e mostrarlo così
        // sarebbe verde per sottrazione: il caso «non ha MAI battuto» — il più grave — sparirebbe.
        var (probe, _) = await BuildAsync();

        var b = await probe.ProbeAsync();

        var guscio = Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.ShellRole);
        var motore = Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.EngineRole);
        Assert.True(guscio.Muto && guscio.Atteso);
        Assert.True(motore.Muto && motore.Atteso);
        Assert.Null(guscio.UltimoUtc);
    }

    [Fact]
    public async Task BattitoFRESCO_nonEmuto_eLaVersioneViaggiaConLui()
    {
        var (probe, db) = await BuildAsync();
        await BatteAsync(db, HostHeartbeat.ShellRole, DateTime.UtcNow.AddMinutes(-1), "4e0c72a5");
        await BatteAsync(db, HostHeartbeat.EngineRole, DateTime.UtcNow.AddMinutes(-2));

        var b = await probe.ProbeAsync();

        var guscio = Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.ShellRole);
        Assert.False(guscio.Muto);
        Assert.Equal("4e0c72a5", guscio.Versione);
    }

    [Fact]
    public async Task BattitoVECCHIO_eMuto_eLaSogliaViaggiaColVerdetto()
    {
        var (probe, db) = await BuildAsync();
        await BatteAsync(db, HostHeartbeat.ShellRole, DateTime.UtcNow.AddMinutes(-1));
        await BatteAsync(db, HostHeartbeat.EngineRole, DateTime.UtcNow.AddHours(-3));

        var b = await probe.ProbeAsync();

        Assert.True(Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.EngineRole).Muto);
        Assert.False(Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.ShellRole).Muto);
        // Un «muto da 12 minuti» significa cose diverse a seconda di quanto si aspettava: senza il
        // metro accanto, chi legge deve fidarsi.
        Assert.Equal(HeartbeatBoardProbe.Soglia, b.Soglia);
    }

    [Fact]
    public async Task UnRuoloNONatteso_muto_NONeUnGuasto()
    {
        // `carry` batte solo col carry acceso, e quella configurazione vive nel POD: il guscio non
        // la conosce. Segnarlo «atteso» produrrebbe un rosso che non può rientrare — la classe di
        // allarme che questo progetto ha già imparato a non scrivere (LiquidationsMinStartUtc).
        var (probe, db) = await BuildAsync();
        await BatteAsync(db, HostHeartbeat.CarryRole, DateTime.UtcNow.AddDays(-2));

        var b = await probe.ProbeAsync();

        var carry = Assert.Single(b.Righe, r => r.Ruolo == HostHeartbeat.CarryRole);
        Assert.True(carry.Muto);
        Assert.False(carry.Atteso);
    }

    [Fact]
    public async Task GliATTESI_vengonoPRIMA()
    {
        // L'ordine è parte del messaggio: chi apre la Home deve vedere per primo ciò che, tacendo,
        // è un guasto.
        var (probe, db) = await BuildAsync();
        await BatteAsync(db, HostHeartbeat.IngestionSyncRole, DateTime.UtcNow);
        await BatteAsync(db, HostHeartbeat.ShellRole, DateTime.UtcNow);

        var b = await probe.ProbeAsync();

        Assert.True(b.Righe.Take(2).All(r => r.Atteso));
    }
}
