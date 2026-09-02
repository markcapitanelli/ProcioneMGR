using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K51, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Il journal come registro: l'intento si scrive
/// prima, e ciò che resta aperto si dichiara.</b>
///
/// <para><b>Il fatto.</b> Il 2026-08-31 <b>due schieramenti su quattro</b> e <b>quattro arresti su
/// quattro</b> sono avvenuti senza lasciare riga. La causa non era solo l'ordine (journal scritto
/// per ultimo, fuori da ogni transazione con la scrittura della configurazione e l'avvio del
/// motore): era che lo stato <i>«è stato deciso di toccare la corsia N e non si sa come sia
/// finita»</i> <b>non era esprimibile</b> — quindi era invisibile.</para>
///
/// <para><b>La proprietà difesa</b> è che un intento rimasto aperto diventi <c>Unknown</c> e
/// <b>mai</b> <c>Applied</c> per somiglianza con lo stato attuale della corsia: sarebbe una
/// deduzione presentata come misura, la trappola già pagata più volte in questo filone — l'ultima
/// volta appena ieri, sul backfill della provenienza.</para>
/// </summary>
[Collection("Postgres")]
public class JournalComeIntentoK51Tests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public JournalComeIntentoK51Tests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedReader : IFleetStateReader
    {
        public Task<FleetState> ReadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private async Task<(FleetOrchestratorWorker Worker, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();

        var worker = new FleetOrchestratorWorker(
            new UnusedReader(), db,
            new FleetOptions { TickMinutes = 15 }.AsMonitor(),
            new CarryOptions().AsMonitor(),
            provider, NullLogger<FleetOrchestratorWorker>.Instance);
        return (worker, db);
    }

    private static OrchestratorDecision Intento(DateTime quando, int laneId = 4) => new()
    {
        AtUtc = quando,
        Kind = "Assign",
        LaneId = laneId,
        Source = "fleet",
        Outcome = DecisionOutcome.Intended,
        Applied = false,
        DryRun = false,
        Reason = "INTENTO: prova",
        VotesJson = "[]",
    };

    [Fact]
    public async Task UnINTENTOappesoOLTREdueTICK_diventaESITOignoto()
    {
        var (worker, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.OrchestratorDecisions.Add(Intento(DateTime.UtcNow.AddMinutes(-45)));   // 3 tick fa
            await c.SaveChangesAsync();
        }

        await worker.RiconciliaPerTestAsync(15);

        await using var check = await db.CreateDbContextAsync();
        var riga = Assert.Single(await check.OrchestratorDecisions.AsNoTracking().ToListAsync());
        Assert.Equal(DecisionOutcome.Unknown, riga.Outcome);
        Assert.Contains("esito ignoto", riga.Error!, StringComparison.OrdinalIgnoreCase);
        // E NON diventa «eseguita»: la corsia potrebbe portare l'ipotesi giusta per caso, e
        // dedurlo sarebbe presentare una somiglianza come una misura.
        Assert.False(riga.Applied);
    }

    [Fact]
    public async Task IlNULLO_diK51_unINTENTOapPENAaperto_NONsiTOCCA()
    {
        // Senza questo, una riconciliazione che marca tutto passerebbe la prova qui sopra e
        // dichiarerebbe «esito ignoto» su ogni schieramento in corso — cioè esattamente mentre sta
        // funzionando.
        var (worker, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.OrchestratorDecisions.Add(Intento(DateTime.UtcNow.AddMinutes(-2)));
            await c.SaveChangesAsync();
        }

        await worker.RiconciliaPerTestAsync(15);

        await using var check = await db.CreateDbContextAsync();
        Assert.Equal(DecisionOutcome.Intended,
            (await check.OrchestratorDecisions.AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task LeRIGHEgiaCHIUSE_nonSiRIAPRONO()
    {
        // Una riconciliazione che tocca le righe concluse riscriverebbe la storia a ogni tick.
        var (worker, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            var applicata = Intento(DateTime.UtcNow.AddDays(-3));
            applicata.Outcome = DecisionOutcome.Applied;
            applicata.Applied = true;
            var rifiutata = Intento(DateTime.UtcNow.AddDays(-3), laneId: 5);
            rifiutata.Outcome = DecisionOutcome.Refused;
            c.OrchestratorDecisions.AddRange(applicata, rifiutata);
            await c.SaveChangesAsync();
        }

        await worker.RiconciliaPerTestAsync(15);

        await using var check = await db.CreateDbContextAsync();
        var righe = await check.OrchestratorDecisions.AsNoTracking().OrderBy(d => d.LaneId).ToListAsync();
        Assert.Equal([DecisionOutcome.Applied, DecisionOutcome.Refused], righe.Select(r => r.Outcome));
    }

    [Fact]
    public async Task PIUintentiAPPESI_siCHIUDONOtutti()
    {
        var (worker, db) = await BuildAsync();
        await using (var c = await db.CreateDbContextAsync())
        {
            c.OrchestratorDecisions.AddRange(
                Intento(DateTime.UtcNow.AddHours(-5), laneId: 3),
                Intento(DateTime.UtcNow.AddHours(-4), laneId: 4),
                Intento(DateTime.UtcNow.AddMinutes(-1), laneId: 6));   // questo no: è appena nato
            await c.SaveChangesAsync();
        }

        await worker.RiconciliaPerTestAsync(15);

        await using var check = await db.CreateDbContextAsync();
        var righe = await check.OrchestratorDecisions.AsNoTracking().OrderBy(d => d.LaneId).ToListAsync();
        Assert.Equal(
            [DecisionOutcome.Unknown, DecisionOutcome.Unknown, DecisionOutcome.Intended],
            righe.Select(r => r.Outcome));
    }

    [Fact]
    public void ICINQUEstati_sonoDISTINTI()
    {
        // Il valore di K51 è distinguere cose che prima stavano su un booleano: se due costanti
        // collassassero, il difetto tornerebbe senza che nessun test se ne accorgesse.
        string[] stati =
        [
            DecisionOutcome.Intended, DecisionOutcome.Applied, DecisionOutcome.Failed,
            DecisionOutcome.Refused, DecisionOutcome.Unknown,
        ];

        Assert.Equal(5, stati.Distinct(StringComparer.Ordinal).Count());
        // E stanno tutti nella colonna, che è la lezione di K45.
        Assert.All(stati, s => Assert.True(s.Length <= 32));
    }
}
