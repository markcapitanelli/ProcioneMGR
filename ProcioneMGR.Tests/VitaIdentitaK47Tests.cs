using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K47, PRD autonomia-piena — Fase 3, 2026-09-02] <b>La storia delle identità di corsia.</b>
///
/// <para>Fino a oggi <c>FleetLaneObservations</c> teneva una riga per corsia e la <b>sovrascriveva</b>
/// a ogni cambio di identità: <c>FirstSeenUtc</c> e <c>ObservedSeconds</c> ripartivano da zero e
/// l'esperimento precedente spariva. Ma ogni criterio di ritiro è ancorato a quel primo
/// avvistamento, quindi <b>ogni soglia era denominata in una grandezza di cui non esisteva la
/// distribuzione</b>. Sette avversari indipendenti, su due ondate di misure, hanno chiesto tutti e
/// sette lo stesso numero mancante.</para>
///
/// <para>La parte che decide non è la mediana: è <b>quanti esperimenti sarebbero arrivati ai
/// cancelli in vigore</b>. Una soglia più lunga della vita tipica non è severa — è <b>spenta</b>, e
/// finge di essere un meccanismo di governo.</para>
/// </summary>
public class VitaIdentitaK47Tests
{
    // ---------------------------------------------------------------- il calcolo, puro

    private static (long, double) Ep(double giorniOsservati, double giorniCalendario)
        => ((long)(giorniOsservati * 86400), giorniCalendario);

    [Fact]
    public void SenzaEPISODI_nonSiINVENTAunaDistribuzione()
    {
        var r = LaneIdentityLifetimeReader.Calcola([], 10, 21);

        Assert.Equal(0, r.Episodi);
        Assert.True(r.CampioneTroppoPiccolo);
        // Zero episodi non significa «i cancelli sono irraggiungibili»: significa «non lo so».
        Assert.True(r.CancelloInediaRaggiungibile);
        Assert.True(r.CancelloSharpeRaggiungibile);
    }

    [Fact]
    public void LaMEDIANAeIcancelli_suiQUATTROepisodiVERIdellaFlotta()
    {
        // I quattro episodi chiusi ricostruiti a mano dal journal il 2026-09-01, con il duty
        // misurato dell'88%: 10,15 · 25,82 · 28,23 · 28,26 giorni di calendario.
        var r = LaneIdentityLifetimeReader.Calcola(
            [Ep(8.93, 10.15), Ep(22.72, 25.82), Ep(24.84, 28.23), Ep(24.87, 28.26)],
            giorniCancelloInedia: 10, giorniCancelloSharpe: 21);

        Assert.Equal(4, r.Episodi);
        Assert.Equal(23.78, r.MedianaGiorniOsservati, 2);   // (22,72 + 24,84) / 2
        Assert.Equal(8.93, r.MinGiorniOsservati, 2);
        Assert.Equal(27.025, r.MedianaGiorniCalendario, 2);   // (25,82 + 28,23) / 2
        // A 10 giorni ci arrivano tre su quattro; a 21 anche.
        Assert.Equal(3, r.RaggiungonoCancelloInedia);
        Assert.Equal(3, r.RaggiungonoCancelloSharpe);
    }

    [Fact]
    public void UnCANCELLOcheNESSUNOraggiunge_vieneDICHIARATOspento()
    {
        // IL RISULTATO CHE HA CAMBIATO LA DECISIONE. Con StarvationMinDays a 27 giorni, nessuno dei
        // quattro esperimenti realmente vissuti ci sarebbe arrivato: la soglia non è severa, è
        // spenta — e prima di K47 non c'era modo di vederlo.
        var r = LaneIdentityLifetimeReader.Calcola(
            [Ep(8.93, 10.15), Ep(22.72, 25.82), Ep(24.84, 28.23), Ep(24.87, 28.26)],
            giorniCancelloInedia: 27, giorniCancelloSharpe: 21);

        Assert.Equal(0, r.RaggiungonoCancelloInedia);
        Assert.False(r.CancelloInediaRaggiungibile);
        Assert.True(r.CancelloSharpeRaggiungibile);   // il nullo: l'altro cancello resta raggiungibile
    }

    [Fact]
    public void IlNULLO_diK47_unCancelloBASSOeRaggiuntoDATUTTI()
    {
        // Senza questo, un calcolo che dichiara sempre «spento» passerebbe il test qui sopra.
        var r = LaneIdentityLifetimeReader.Calcola([Ep(8.93, 10.15), Ep(22.72, 25.82)], 1, 1);

        Assert.Equal(2, r.RaggiungonoCancelloInedia);
        Assert.True(r.CancelloInediaRaggiungibile);
    }

    // ---------------------------------------------------------------- lo scrittore, su Postgres

    [Collection("Postgres")]
    public sealed class Archivio : IAsyncDisposable
    {
        private readonly string _connString;
        private ServiceProvider? _provider;

        public Archivio(PostgresFixture pg) => _connString = pg.CreateDatabase();

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null) await _provider.DisposeAsync();
        }

        private sealed class PassthroughEncryption : IEncryptionService
        {
            public string Encrypt(string plaintext) => plaintext;
            public string Decrypt(string ciphertext) => ciphertext;
        }

        private async Task<(LaneObservationLedger Ledger, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEncryptionService, PassthroughEncryption>();
            services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
            _provider = services.BuildServiceProvider();
            var db = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();
            return (new LaneObservationLedger(db, NullLogger<LaneObservationLedger>.Instance), db);
        }

        [Fact]
        public async Task IlCambioDIidentita_ARCHIVIAlEsperimentoPRIMAdiAzzerare()
        {
            var (ledger, db) = await BuildAsync();
            var t0 = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            await ledger.AccumulateAsync(4, "DOGE/USDT|15m|aaa", isRunning: true, t0);
            await ledger.AccumulateAsync(4, "DOGE/USDT|15m|aaa", isRunning: true, t0.AddMinutes(15));
            // Nuova identità: l'orologio riparte, e l'esperimento precedente deve restare scritto.
            await ledger.AccumulateAsync(4, "UNI/USDT|4h|bbb", isRunning: true, t0.AddDays(10));

            await using var c = await db.CreateDbContextAsync();
            var episodio = Assert.Single(await c.FleetLaneIdentityEpisodes.AsNoTracking().ToListAsync());

            Assert.Equal(4, episodio.LaneId);
            Assert.Equal("DOGE/USDT|15m|aaa", episodio.Identity);
            Assert.Equal("UNI/USDT|4h|bbb", episodio.NextIdentity);
            Assert.Equal(t0, episodio.FirstSeenUtc);
            Assert.Equal(t0.AddDays(10), episodio.ClosedUtc);
            // 15 minuti accreditati: il tetto per buco impedisce di regalare i dieci giorni.
            Assert.Equal(900, episodio.ObservedSeconds);

            // E l'orologio vivo è ripartito, come prima.
            var viva = Assert.Single(await c.FleetLaneObservations.AsNoTracking().ToListAsync());
            Assert.Equal("UNI/USDT|4h|bbb", viva.Identity);
            Assert.Equal(0, viva.ObservedSeconds);
        }

        [Fact]
        public async Task IlNULLO_dellArchivio_senzaCAMBIOnonSiSCRIVEniente()
        {
            // Senza questo, uno scrittore che archivia a ogni tick passerebbe il test qui sopra e
            // riempirebbe la tabella di 96 righe al giorno per corsia — rendendo la mediana della
            // «vita di un esperimento» un numero senza senso.
            var (ledger, db) = await BuildAsync();
            var t0 = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            for (var i = 0; i < 5; i++)
            {
                await ledger.AccumulateAsync(4, "DOGE/USDT|15m|aaa", isRunning: true, t0.AddMinutes(15 * i));
            }

            await using var c = await db.CreateDbContextAsync();
            Assert.Empty(await c.FleetLaneIdentityEpisodes.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task LaPRIMAvoltaCHEsiVEDEunaCORSIA_nonEunEPISODIOchiuso()
        {
            // La riga nasce in questo stesso metodo: archiviarla sarebbe registrare un esperimento
            // di durata zero che non è mai esistito, e abbasserebbe la mediana con del nulla.
            var (ledger, db) = await BuildAsync();
            await ledger.AccumulateAsync(7, "STX/USDT|4h|ccc", isRunning: true, DateTime.UtcNow);

            await using var c = await db.CreateDbContextAsync();
            Assert.Empty(await c.FleetLaneIdentityEpisodes.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task PIUepisodiINcatena_siACCUMULANO_eSiRILEGGONO()
        {
            var (ledger, db) = await BuildAsync();
            var t0 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            await ledger.AccumulateAsync(5, "A|4h|1", true, t0);
            await ledger.AccumulateAsync(5, "A|4h|1", true, t0.AddMinutes(15));
            await ledger.AccumulateAsync(5, "B|4h|2", true, t0.AddDays(10));
            await ledger.AccumulateAsync(5, "B|4h|2", true, t0.AddDays(10).AddMinutes(15));
            await ledger.AccumulateAsync(5, "C|4h|3", true, t0.AddDays(40));

            await using var c = await db.CreateDbContextAsync();
            var episodi = await c.FleetLaneIdentityEpisodes.AsNoTracking().OrderBy(e => e.FirstSeenUtc).ToListAsync();

            Assert.Equal(2, episodi.Count);
            Assert.Equal(["A|4h|1", "B|4h|2"], episodi.Select(e => e.Identity));
            Assert.Equal(["B|4h|2", "C|4h|3"], episodi.Select(e => e.NextIdentity));
        }
    }
}
