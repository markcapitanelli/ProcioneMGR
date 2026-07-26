using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [B0 PRD core-caldo] Il lease di esecuzione per corsia su Postgres VERO: "mai due esecutori
/// sulla stessa corsia" deve essere applicato dal database, non dalla disciplina di deploy.
/// Ogni factory qui simula un PROCESSO distinto (connessione propria, senza pool — il pool
/// terrebbe vivo il lock dopo la Dispose, ed è esattamente il bug che Pooling=false previene).
/// </summary>
[Collection("Postgres")]
public sealed class LaneExecutionLeaseTests(PostgresFixture fixture)
{
    private NpgsqlLaneLeaseFactory Factory(string connectionString) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgresConnection"] = connectionString,
        }).Build(),
        NullLogger<NpgsqlLaneLeaseFactory>.Instance);

    [Fact]
    public async Task StessaCorsia_IlSecondoContendenteVieneRespinto()
    {
        var cs = fixture.CreateDatabase();
        var processoA = Factory(cs);
        var processoB = Factory(cs);

        await using var lease = await processoA.TryAcquireAsync(laneId: 0);
        Assert.NotNull(lease);
        Assert.Equal(0, lease!.LaneId);

        // Il "deploy incoerente": un secondo processo prova a eseguire la stessa corsia.
        Assert.Null(await processoB.TryAcquireAsync(laneId: 0));
    }

    [Fact]
    public async Task CorsieDiverse_ConvivonoSenzaContesa()
    {
        var cs = fixture.CreateDatabase();
        await using var lane0 = await Factory(cs).TryAcquireAsync(laneId: 0);
        await using var lane1 = await Factory(cs).TryAcquireAsync(laneId: 1);
        Assert.NotNull(lane0);
        Assert.NotNull(lane1);
    }

    [Fact]
    public async Task IlRilascio_RendeLaCorsiaRiacquisibile()
    {
        var cs = fixture.CreateDatabase();
        var processoA = Factory(cs);
        var processoB = Factory(cs);

        var lease = await processoA.TryAcquireAsync(laneId: 2);
        Assert.NotNull(lease);
        Assert.Null(await processoB.TryAcquireAsync(laneId: 2));

        // La chiusura della sessione DEVE liberare il lock lato server, subito: è la garanzia
        // che regge anche sul crash del processo (nessuna unlock esplicita da ricordare).
        await lease!.DisposeAsync();
        await using var riacquisito = await processoB.TryAcquireAsync(laneId: 2);
        Assert.NotNull(riacquisito);
    }

    [Fact]
    public async Task IsAlive_VeroFinchéLaSessioneVive()
    {
        var cs = fixture.CreateDatabase();
        var lease = await Factory(cs).TryAcquireAsync(laneId: 3);
        Assert.NotNull(lease);
        Assert.True(await lease!.IsAliveAsync());
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task DatabaseDiversi_NonSiVedono()
    {
        // Due database = due piattaforme: nessuna contesa incrociata (il lock è per-database).
        await using var a = await Factory(fixture.CreateDatabase()).TryAcquireAsync(laneId: 0);
        await using var b = await Factory(fixture.CreateDatabase()).TryAcquireAsync(laneId: 0);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}
