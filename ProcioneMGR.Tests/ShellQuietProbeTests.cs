using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Health;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K3, PRD autonomia-piena 2026-08-31] <b>«Posso fermarti adesso?»</b>
///
/// <para>È la domanda che l'aggiornamento automatico del guscio deve poter fare prima di
/// riavviarlo. La plancia non può rispondersi da sola: non ha, di proposito, alcun riferimento ai
/// progetti dell'applicazione — deve poter dire «il guscio non compila» anche quando il guscio non
/// compila — quindi non sa nulla di run di pipeline o di campagne. Chi sa è il guscio.</para>
///
/// <para>La proprietà che questi test difendono è una sola, ed è quella che distingue questa sonda
/// da tutte le altre della piattaforma: <b>qui il fail-open è vietato</b>. Le sonde diagnostiche
/// degradano verso il permesso e lo dichiarano (regola 4); questa autorizza un'AZIONE che ferma un
/// processo, e non sapere non è permesso.</para>
/// </summary>
public class ShellQuietProbeFailClosedTests
{
    /// <summary>Una factory che non può produrre nulla: il caso «Postgres è giù».</summary>
    private sealed class FactoryRotta : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            throw new InvalidOperationException("connessione rifiutata");
    }

    [Fact]
    public async Task DatabaseIrraggiungibile_NONautorizzaIlRiavvio()
    {
        var probe = new ShellQuietProbe(new FactoryRotta(), NullLogger<ShellQuietProbe>.Instance);

        var v = await probe.ProbeAsync();

        Assert.False(v.Quiet);
        Assert.Contains("non autorizzo", v.Reason);
    }
}

[Collection("Postgres")]
public class ShellQuietProbeTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ShellQuietProbeTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(ShellQuietProbe Probe, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();
        return (new ShellQuietProbe(db, NullLogger<ShellQuietProbe>.Instance), db);
    }

    [Fact]
    public async Task NienteInVolo_QUIETO()
    {
        var (probe, _) = await BuildAsync();

        var v = await probe.ProbeAsync();

        Assert.True(v.Quiet);
        // Anche il sì dice cosa ha guardato: un permesso senza motivo non si può rileggere dopo.
        Assert.Contains("nessun run in volo", v.Reason);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Pending")]
    [InlineData("Queued")]
    public async Task UnRunNONterminale_NIENTEriavvio(string stato)
    {
        // Il costo vero di sbagliare qui: il run finisce Paused e viene affidato all'auto-resume,
        // che ha un budget finito — in tabella ci sono tre run di luglio che quel budget lo hanno
        // esaurito e non sono mai ripartiti.
        var (probe, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.PipelineRuns.Add(new PipelineRun { Id = Guid.NewGuid(), ConfigurationId = 1, Status = stato, StartedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }

        var v = await probe.ProbeAsync();

        Assert.False(v.Quiet);
        Assert.Contains("in volo", v.Reason);
    }

    [Fact]
    public async Task UnRunCompletato_NONimpediscenulla()
    {
        // Il complemento del test precedente: senza, la sonda potrebbe dire sempre di no e
        // nessuno se ne accorgerebbe — un permesso che non si concede mai è un aggiornamento
        // automatico che non avviene mai, cioè il difetto che K3 esiste per chiudere.
        var (probe, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.PipelineRuns.Add(new PipelineRun
            {
                Id = Guid.NewGuid(), ConfigurationId = 1, Status = "Completed",
                StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        }

        Assert.True((await probe.ProbeAsync()).Quiet);
    }

    [Fact]
    public async Task UnaCampagnaAPPESA_aUnRun_NIENTEriavvio()
    {
        var (probe, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.VettingCampaigns.Add(new VettingCampaign
            {
                Name = "prova", Enabled = true, Status = "Rotating",
                PendingRunId = Guid.NewGuid(), ConfigStatesJson = "[]",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        }

        var v = await probe.ProbeAsync();

        Assert.False(v.Quiet);
        Assert.Contains("campagna", v.Reason);
    }

    [Fact]
    public async Task UnaCampagnaDISABILITATA_nonBloccaNiente()
    {
        // Una campagna spenta con un run appeso è un residuo, non un'attesa.
        var (probe, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.VettingCampaigns.Add(new VettingCampaign
            {
                Name = "spenta", Enabled = false, Status = "Rotating",
                PendingRunId = Guid.NewGuid(), ConfigStatesJson = "[]",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        }

        Assert.True((await probe.ProbeAsync()).Quiet);
    }
}
