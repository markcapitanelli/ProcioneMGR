using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K54 e K57, revisione 2026-09-04] I due lettori delle rimisurazioni su Postgres vero.
///
/// <para><b>K54</b>: l'àncora dell'evidenza è il RUN che ha prodotto il numero, non l'ora dello
/// schieramento. La corsia 6 porta un numero del 21/08 ma è stata schierata il 31/08: contando
/// «dopo» dallo schieramento le rivalutazioni erano meno di cinque, la gamba non era giudicabile
/// e il falso allarme che K54 doveva togliere restava.</para>
///
/// <para><b>K57</b>: il gate di stabilità considera solo le righe del motore corrente
/// (<c>Fleet:StabilitaDaUtc</c>): con le righe di due motori il ventaglio misurava il cambio di
/// motore, non l'ipotesi.</para>
/// </summary>
[Collection("Postgres")]
public class AncoraEvidenzaK54Tests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public AncoraEvidenzaK54Tests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private static readonly Dictionary<string, decimal> Parametri = new() { ["GridStep"] = 0.5m, ["Levels"] = 4m };
    private static readonly string Chiave = PipelineCandidateKey.Build("GridMeanReversion", "DOGE/USDT", "15m", Parametri);

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync(params (DateTime quando, decimal sharpe)[] misure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var c = await db.CreateDbContextAsync();
        await c.Database.EnsureCreatedAsync();
        foreach (var (quando, sharpe) in misure)
        {
            c.ResearchCandidates.Add(new ResearchCandidate
            {
                RunId = Guid.NewGuid(), RunCompletedUtc = quando,
                StrategyName = "GridMeanReversion", Symbol = "DOGE/USDT", Timeframe = "15m",
                CandidateKey = Chiave, HoldoutSharpe = sharpe, HoldoutTrades = 12,
            });
        }
        await c.SaveChangesAsync();
        return db;
    }

    private static DateTime Giorno(int d) => new(2026, 8, d, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Il caso della corsia 6, coi numeri veri del PRD.</summary>
    private static readonly (DateTime, decimal)[] Corsia6 =
    [
        (Giorno(21), 1.8754m),                                   // il numero che la corsia porta
        (Giorno(25), 0.2329m), (Giorno(26), 0.4787m), (Giorno(26).AddHours(6), 0.4720m), (Giorno(27), 0.3212m),
        (Giorno(28), 0.3244m), (Giorno(28).AddHours(6), 0.4531m), (Giorno(29), 0.6743m), (Giorno(31), 0.5902m),
        (Giorno(31).AddHours(3), 0.5522m), (Giorno(31).AddHours(6), 0.5180m), (new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), 0.5901m),
    ];

    private static (EnsembleConfiguration Cfg, EnsembleStrategy Gamba) GambaSchierataIl(DateTime schieramento)
    {
        var cfg = new EnsembleConfiguration { Symbol = "DOGE/USDT", Timeframe = "15m" };
        var gamba = new EnsembleStrategy
        {
            StrategyName = "GridMeanReversion", Parameters = new Dictionary<string, decimal>(Parametri),
            ExpectedSharpe = 1.8754m, ExpectedSharpeAtUtc = schieramento,
        };
        return (cfg, gamba);
    }

    [Fact]
    public async Task LANCORAeILrunCHEhaPRODOTTOilNUMERO_nonLOschieramento()
    {
        var db = await DbAsync(Corsia6);
        var reader = new ExpectationEvidenceReader(db, NullLogger<ExpectationEvidenceReader>.Instance);
        // Schierata il 31/08 alle 20:22, dieci giorni dopo il numero.
        var (cfg, gamba) = GambaSchierataIl(new DateTime(2026, 8, 31, 20, 22, 0, DateTimeKind.Utc));

        var ev = await reader.ReadAsync(cfg, gamba);

        Assert.NotNull(ev);
        Assert.True(ev.AncoraDalRun);
        Assert.Equal(Giorno(21), ev.Ancora);
        Assert.Equal(11, ev.MisureDopo);              // tutte le undici, non le 1-2 dopo lo schieramento
        Assert.True(ev.Giudicabile);
        Assert.True(ev.Contraddetta);
        Assert.InRange(ev.MedianaDopo!.Value, 0.47m, 0.49m);
        Assert.Contains("il run del 2026-08-21", ev.Racconto, StringComparison.Ordinal);
    }

    /// <summary>Il nullo: se nessun run ha prodotto quel numero, l'àncora resta lo schieramento, e lo dice.</summary>
    [Fact]
    public async Task SENZAunRUNsorgente_lANCORArestaLOschieramento()
    {
        var db = await DbAsync(Corsia6.Skip(1).ToArray());     // il run del 21/08 non è in archivio
        var reader = new ExpectationEvidenceReader(db, NullLogger<ExpectationEvidenceReader>.Instance);
        var schieramento = new DateTime(2026, 8, 31, 20, 22, 0, DateTimeKind.Utc);
        var (cfg, gamba) = GambaSchierataIl(schieramento);

        var ev = await reader.ReadAsync(cfg, gamba);

        Assert.NotNull(ev);
        Assert.False(ev.AncoraDalRun);
        Assert.Equal(schieramento, ev.Ancora);
        Assert.Equal(1, ev.MisureDopo);               // solo la misura del 01/09
        Assert.False(ev.Giudicabile);
        Assert.Contains("lo schieramento del 2026-08-31", ev.Racconto, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ K57

    [Fact]
    public async Task ILgateDIstabilita_vedeSOLOilMOTOREcorrente()
    {
        // Sei misure larghe del motore vecchio (prima del 23/08) e sei strette del nuovo.
        var db = await DbAsync(
            (Giorno(10), 0.05m), (Giorno(12), 1.9m), (Giorno(14), 0.1m), (Giorno(16), 1.8m), (Giorno(18), 0.2m), (Giorno(20), 1.7m),
            (Giorno(24), 0.90m), (Giorno(25), 0.95m), (Giorno(26), 1.00m), (Giorno(27), 1.05m), (Giorno(28), 1.10m), (Giorno(29), 0.98m));

        var conFiltro = new StabilitaReader(db, new FleetOptions().AsMonitor());   // default: dal 2026-08-23
        var senzaFiltro = new StabilitaReader(db, new FleetOptions { StabilitaDaUtc = null }.AsMonitor());

        var nuovo = (await conFiltro.ReadAsync([Chiave]))[Chiave];
        var tutti = (await senzaFiltro.ReadAsync([Chiave]))[Chiave];

        Assert.Equal(6, nuovo.Misure);
        Assert.False(nuovo.Instabile);                // ventaglio 0,20 su mediana ~1,0
        Assert.Equal(12, tutti.Misure);
        Assert.True(tutti.Instabile);                 // il ventaglio dei due motori insieme (1,85) supera la mediana
    }
}
