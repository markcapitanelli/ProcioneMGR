using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il numero di corsie era una costante di compilazione: aumentarlo voleva dire ricompilare, e in
/// pratica ha significato per anni "tre prove in parallelo, non una di più". Ora si configura.
///
/// La parte delicata non è leggere un intero dalla configurazione: è che quel numero <b>non può
/// cambiare a metà partita</b>. Le corsie sono registrate keyed nel contenitore DI, e UI, worker e
/// validatore del gRPC si basano tutti sullo stesso conteggio: se cambiasse dopo che qualcuno lo ha
/// letto, resterebbero motori senza corsia o corsie senza motore. Da qui il congelamento alla prima
/// lettura, che questi test difendono.
///
/// Collezione serializzata di proposito: questi test cambiano uno static di processo, e girando in
/// parallelo farebbero fallire a intermittenza tutti gli altri che quel numero lo leggono.
/// </summary>
[Collection("TradingLanes")]
public sealed class TradingLanesCountTests : IDisposable
{
    public TradingLanesCountTests() => TradingLanes.ResetForTests();

    public void Dispose() => TradingLanes.ResetForTests();

    [Fact]
    public void DefaultIsUnchanged_SoNothingMovesForWhoNeverConfiguresIt()
    {
        Assert.Equal(3, TradingLanes.DefaultCount);
        Assert.Equal(TradingLanes.DefaultCount, TradingLanes.Count);
    }

    [Fact]
    public void Configure_AcceptsTheAllowedRange()
    {
        TradingLanes.Configure(8);
        Assert.Equal(8, TradingLanes.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TradingLanes.MaxCount + 1)]
    public void Configure_RefusesNonsense(int count)
    {
        // Il tetto non è una stima di capacità, è protezione dal refuso: ogni corsia avvia tre
        // worker, quindi un "LaneCount": 300 scritto per sbaglio creerebbe novecento cicli di fondo.
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingLanes.Configure(count));
    }

    [Fact]
    public void Configure_WithTheSameValue_IsHarmlessEvenAfterUse()
    {
        // I test — e l'host di trading standalone — costruiscono più contenitori DI nello stesso
        // processo: ripetere la stessa configurazione non deve essere un errore.
        TradingLanes.Configure(5);
        _ = TradingLanes.Count;
        TradingLanes.Configure(5);

        Assert.Equal(5, TradingLanes.Count);
    }

    [Fact]
    public void Configure_WithADifferentValueAfterUse_FailsLoudly()
    {
        // È l'invariante che protegge tutto il resto: se il conteggio cambiasse dopo la
        // registrazione delle corsie, resterebbero motori senza corsia o corsie senza motore — e lo
        // si scoprirebbe come un errore di risoluzione DI, molto lontano dalla causa.
        TradingLanes.Configure(4);
        _ = TradingLanes.Count;

        var ex = Assert.Throws<InvalidOperationException>(() => TradingLanes.Configure(6));
        Assert.Contains("già stato usato", ex.Message);
    }

    [Fact]
    public void AddTradingLanes_RegistersExactlyTheConfiguredNumberOfEngines()
    {
        // Si ispezionano le REGISTRAZIONI, non le istanze: risolvere un TradingEngine richiederebbe
        // tutto il suo cono di dipendenze, che qui non è l'oggetto del test.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:LaneCount"] = "7",
            ["ConnectionStrings:PostgresConnection"] = "Host=localhost;Database=unused;Username=x;Password=x",
        }).Build();

        var services = new ServiceCollection();
        services.AddTradingLanes(configuration);

        Assert.Equal(7, TradingLanes.Count);

        var engineKeys = services
            .Where(d => d.ServiceType == typeof(ITradingEngine) && d.IsKeyedService)
            .Select(d => (int)d.ServiceKey!)
            .OrderBy(k => k)
            .ToList();

        Assert.Equal(Enumerable.Range(0, 7), engineKeys);
    }

    [Fact]
    public void AddTradingLanes_RefusesAnImpossibleLaneCount()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:LaneCount"] = "999",
            ["ConnectionStrings:PostgresConnection"] = "Host=localhost;Database=unused;Username=x;Password=x",
        }).Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddTradingLanes(configuration));
    }
}

/// <summary>
/// [AF0] La flotta può crescere (8 corsie), ma l'impronta dell'auto-apply della pipeline NON deve
/// crescerle dietro: una ri-applica schedulata che di colpo distribuisce gambe sulle corsie
/// dormienti è un cambio di comportamento che nessuno ha chiesto. Le corsie oltre l'impronta le
/// schiera solo l'orchestratore di flotta, con corsie bersaglio esplicite (AF2).
/// </summary>
[Collection("TradingLanes")]
public sealed class PipelineApplierFootprintTests : IDisposable
{
    public PipelineApplierFootprintTests() => TradingLanes.ResetForTests();

    public void Dispose() => TradingLanes.ResetForTests();

    private static Services.Pipeline.PipelineApplier Applier()
        => new(null!, null!, null!); // solo la proprietà LaneCount: nessuna dipendenza toccata

    [Fact]
    public void FleetGrowth_DoesNotWidenTheAutoApplyFootprint()
    {
        TradingLanes.Configure(8);
        Assert.Equal(3, Applier().LaneCount);
    }

    [Fact]
    public void FleetSmallerThanTheFootprint_ShrinksIt()
    {
        // Il caso latente di prima: con 2 corsie fisiche, il vecchio "3" fisso avrebbe chiesto al
        // contenitore una corsia keyed inesistente al primo ensemble con 3 gruppi-simbolo.
        TradingLanes.Configure(2);
        Assert.Equal(2, Applier().LaneCount);
    }
}

/// <summary>
/// L'elenco che alimenta il selettore di corsia. Non cambia il conteggio: si misura contro quello
/// corrente, così può convivere con qualunque configurazione senza diventare capriccioso.
/// </summary>
[Collection("Postgres")]
public sealed class LaneDirectoryTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly string _connString = pg.CreateDatabase();
    private ServiceProvider? _provider;

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    [Fact]
    public async Task ListsEveryLane_IncludingTheNeverConfiguredOnes()
    {
        // Una corsia mai configurata deve comunque comparire: senza scheda non ci si può cliccare
        // sopra per andare a configurarla, che è precisamente ciò che si fa con una corsia nuova.
        var dbFactory = await DbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.EnsembleStates.Add(new EnsembleState
            {
                LaneId = 0,
                ConfigurationJson = """{"symbol":"BTC/USDT","timeframe":"1h"}""",
            });
            db.TradingEngineStates.Add(new TradingEngineState
            {
                LaneId = 0, Symbol = "BTC/USDT", ExchangeName = "Binance",
                Mode = TradingMode.Testnet, IsRunning = true,
            });
            await db.SaveChangesAsync();
        }

        var lanes = await new LaneDirectory(dbFactory).ListAsync();

        Assert.Equal(TradingLanes.Count, lanes.Count);
        Assert.Equal("BTC/USDT", lanes[0].Symbol);
        Assert.Equal("Testnet", lanes[0].Mode);
        Assert.True(lanes[0].IsRunning);
        Assert.True(lanes[0].IsConfigured);

        Assert.All(lanes.Skip(1), l =>
        {
            Assert.False(l.IsConfigured);
            Assert.False(l.IsRunning);
            Assert.Equal("Paper", l.Mode);   // default prudente per una corsia mai avviata
        });
    }

    [Fact]
    public async Task SurvivesAnUnreadableConfiguration()
    {
        // Una configurazione illeggibile non deve far sparire la corsia dal selettore: senza scheda
        // non ci si può nemmeno cliccare sopra per andare a sistemarla.
        var dbFactory = await DbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.EnsembleStates.Add(new EnsembleState { LaneId = 1, ConfigurationJson = "{ questo non è json" });
            await db.SaveChangesAsync();
        }

        var lanes = await new LaneDirectory(dbFactory).ListAsync();

        Assert.Equal(TradingLanes.Count, lanes.Count);
        Assert.False(lanes[1].IsConfigured);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
