using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K48, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Chi riscrive una corsia lascia il suo nome.</b>
///
/// <para>Riscrivere la configurazione di una corsia è l'azione <b>meno reversibile</b> della
/// piattaforma: <c>EnsembleStates</c> tiene un solo <c>ConfigurationJson</c>, quindi ciò che c'era
/// prima non esiste più da nessuna parte. Eppure la si poteva fare senza lasciare traccia — ed è
/// successo: il 2026-08-31 le corsie 4 e 6 sono state riscritte, e K37 ha poi dovuto dichiarare la
/// loro provenienza <b>non accertabile</b> su un campo che governa il tetto grigio.</para>
///
/// <para>E gli scrittori sono <b>dieci</b>, non le tre porte di schieramento su cui erano state
/// messe le guardie: fra loro c'è una pagina che scrive sempre sulla corsia 0 qualunque corsia
/// l'operatore stia guardando. Una guardia su tre porte non copre un problema che ne ha dieci: per
/// questo il contesto è un parametro <b>obbligatorio</b> del metodo, così l'undicesimo scrittore non
/// compila finché non dice chi è.</para>
/// </summary>
[Collection("Postgres")]
public class RegistroScrittoriK48Tests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RegistroScrittoriK48Tests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedRegimeDetector : IRegimeDetector
    {
        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, string symbol, string timeframe, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class UnusedFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private async Task<(EnsembleManager Manager, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(int laneId = 4)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();

        return (new EnsembleManager(laneId, provider.GetRequiredService<IServiceScopeFactory>(),
            new UnusedRegimeDetector(), new UnusedFeatureExtractor(), new StrategyDecayMonitor(),
            NullLogger<EnsembleManager>.Instance), db);
    }

    private static async Task<List<TradingAuditLog>> ScrittureAsync(IDbContextFactory<ApplicationDbContext> db)
    {
        await using var c = await db.CreateDbContextAsync();
        return await c.TradingAuditLogs.AsNoTracking()
            .Where(a => a.Action == "EnsembleConfigWritten").OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task OgniSCRITTURAlasciaCHIeraEperche()
    {
        var (manager, db) = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "XLM/USDT";

        await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create(
            ConfigWriteSources.GreyDeployer, "schieramento automatico della flotta"));

        var riga = Assert.Single(await ScrittureAsync(db));
        Assert.Equal(4, riga.LaneId);
        Assert.Contains(ConfigWriteSources.GreyDeployer, riga.Details, StringComparison.Ordinal);
        Assert.Contains("schieramento automatico della flotta", riga.Details, StringComparison.Ordinal);
        Assert.Contains("XLM/USDT", riga.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IlNULLO_diK48_unSAVEcheNONcambiaNULLA_nonEunEVENTO()
    {
        // Senza questo, un registro che scrive a ogni Save passerebbe il test qui sopra e
        // riempirebbe l'audit di righe per operatori che aprono la pagina e salvano senza toccare —
        // rendendo illeggibile il registro proprio a chi cerca l'unica riga che conta.
        var (manager, db) = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "XLM/USDT";
        await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create("test", "prima scrittura"));

        // Stessa identica configurazione, di nuovo.
        var uguale = await manager.GetConfigurationAsync();
        await manager.UpdateConfigurationAsync(uguale, ConfigWriteContext.Create("test", "salvataggio a vuoto"));

        Assert.Single(await ScrittureAsync(db));
    }

    [Fact]
    public async Task UnCONTESTOvuoto_NONeUNAdichiarazione()
    {
        // Un contesto con fonte o motivo vuoti sarebbe una dichiarazione che non dichiara: la
        // validazione sta alla costruzione, così non può arrivare fino al database.
        Assert.Throws<ArgumentException>(() => ConfigWriteContext.Create("", "motivo"));
        Assert.Throws<ArgumentException>(() => ConfigWriteContext.Create("fonte", "   "));
    }

    [Fact]
    public async Task LeSCRITTUREinterneDELmanager_siDICHIARANOanchELORO()
    {
        // Accendere l'ensemble non passa da nessuna porta: è il manager che scrive per conto
        // proprio. Se fosse esente, la domanda «chi ha toccato questa corsia» avrebbe di nuovo una
        // risposta parziale — ed è esattamente il tipo di eccezione da cui i buchi ricrescono.
        var (manager, db) = await BuildAsync();
        await manager.StartAsync();

        var riga = Assert.Single(await ScrittureAsync(db));
        Assert.Contains(ConfigWriteSources.EnsembleManagerInternal, riga.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaSCRITTURAeLaCONFIGURAZIONEvivonoNELLAstessaTRANSAZIONE()
    {
        // Un registro che può fallire DOPO l'azione registra solo le azioni fortunate. Qui la prova
        // è indiretta ma esatta: dopo una scrittura, configurazione e riga d'audit esistono
        // entrambe e portano lo stesso istante.
        var (manager, db) = await BuildAsync(laneId: 5);
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "UNI/USDT";
        await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create("test", "prova di atomicita'"));

        await using var c = await db.CreateDbContextAsync();
        var stato = Assert.Single(await c.EnsembleStates.AsNoTracking().Where(e => e.LaneId == 5).ToListAsync());
        var riga = Assert.Single(await c.TradingAuditLogs.AsNoTracking()
            .Where(a => a.Action == "EnsembleConfigWritten" && a.LaneId == 5).ToListAsync());

        Assert.Equal(stato.LastUpdatedUtc, riga.TimestampUtc);
    }
}
