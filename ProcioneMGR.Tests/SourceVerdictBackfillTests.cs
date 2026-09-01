using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K13, PRD autonomia-piena — Fase 1, 2026-08-31] La provenienza delle gambe schierate prima
/// dell'etichetta.
///
/// <para>Il campo <c>SourceVerdict</c> lo scrive <c>GreyDeployer</c> da [T1] in poi. Le cinque
/// corsie di flotta — tutte da click F5 documentati a journal fra il 3 e il 13 agosto — non ce
/// l'hanno, e <c>LaneDirectory.HasGreyLegs</c> restituisce <c>null</c>. Il tetto grigio tratta
/// l'ignoto come grigio, ed è il verso giusto: <b>non sapere non allarga il permesso</b>. Ma
/// <c>greyRunning</c> contava cinque corsie «grigie» senza che nessuna superficie potesse
/// spiegarlo: il tetto funzionava per la ragione giusta e lo diceva nel modo sbagliato.</para>
///
/// <para>La proprietà che questi test difendono è quella che rende la ricostruzione onesta:
/// <b>dove il candidato non si trova, l'etichetta NON si scrive</b>. Su un campo che governa un
/// tetto, un'etichetta inventata è peggio dell'ignoranza — che almeno è fail-closed. È la stessa
/// decisione di J9 sul denominatore dell'inedia.</para>
/// </summary>
[Collection("Postgres")]
public class SourceVerdictBackfillTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SourceVerdictBackfillTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeEnsembleManager(int laneId, EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => laneId;
        public EnsembleConfiguration Config { get; private set; } = config;
        public int Saves { get; private set; }
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration config, CancellationToken ct = default)
        {
            Config = config;
            Saves++;
            return Task.CompletedTask;
        }
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static readonly Dictionary<string, decimal> Parametri = new() { ["Period"] = 14m };

    private async Task<(SourceVerdictBackfill Backfill, FakeEnsembleManager[] Managers, IDbContextFactory<ApplicationDbContext> Db)>
        BuildAsync(params EnsembleConfiguration[] configs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var managers = new FakeEnsembleManager[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var cfg = lane < configs.Length ? configs[lane] : new EnsembleConfiguration();
            managers[lane] = new FakeEnsembleManager(lane, cfg);
            services.AddKeyedSingleton<IEnsembleManager>(lane, managers[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();

        return (new SourceVerdictBackfill(db, provider, NullLogger<SourceVerdictBackfill>.Instance), managers, db);
    }

    private static EnsembleConfiguration Corsia(string strategia = "RsiOversold", string? etichettaGia = null) => new()
    {
        Symbol = "ETC/USDT",
        Timeframe = "4h",
        Strategies =
        [
            new EnsembleStrategy
            {
                StrategyName = strategia,
                DisplayName = $"{strategia} (fascia grigia, run 1d5cd47e)",
                Parameters = new Dictionary<string, decimal>(Parametri),
                IsActive = true,
                CurrentAllocation = 100m,
                SourceVerdict = etichettaGia,
            },
        ],
    };

    private async Task<Guid> SeminaCandidatoAsync(
        IDbContextFactory<ApplicationDbContext> db, string strategia, bool survived, bool isGrey = true)
    {
        var key = PipelineCandidateKey.Build(strategia, "ETC/USDT", "4h", Parametri);
        var runId = Guid.NewGuid();
        await using var c = await db.CreateDbContextAsync();
        c.ResearchCandidates.Add(new ResearchCandidate
        {
            RunId = runId,
            RunCompletedUtc = DateTime.UtcNow.AddDays(-2),
            StrategyName = strategia,
            Symbol = "ETC/USDT",
            Timeframe = "4h",
            CandidateKey = key,
            ParametersJson = "{}",
            BestStopVariant = "base",
            Survived = survived,
            IsGrey = isGrey,
            RejectReason = survived ? null : "Solo 18 trade in holdout (< 20)",
        });
        await c.SaveChangesAsync();
        return runId;
    }

    /// <summary>Una riga di journal che dichiara da quale run la corsia è stata schierata.</summary>
    private static async Task SeminaAssignAsync(IDbContextFactory<ApplicationDbContext> db, int laneId, Guid runId)
    {
        await using var c = await db.CreateDbContextAsync();
        c.OrchestratorDecisions.Add(new OrchestratorDecision
        {
            AtUtc = DateTime.UtcNow.AddDays(-2),
            Kind = "Assign",
            LaneId = laneId,
            RunId = runId,
            Source = "human",
            Applied = true,
            DryRun = false,
            Reason = "seme di prova",
            VotesJson = string.Empty,
        });
        await c.SaveChangesAsync();
    }

    [Fact]
    public async Task CandidatoNONsopravvissuto_diventaGrey()
    {
        var (backfill, managers, db) = await BuildAsync(Corsia());
        await SeminaCandidatoAsync(db, "RsiOversold", survived: false);

        var rep = await backfill.RunAsync(dryRun: false);

        Assert.Equal(1, rep.Updated);
        Assert.Equal("Grey", managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Equal(1, managers[0].Saves);
    }

    [Fact]
    public async Task CandidatoSOPRAVVISSUTO_diventaSurvived()
    {
        // Il complemento indispensabile: se la ricostruzione scrivesse «Grey» sempre, il test
        // precedente passerebbe e la funzione sarebbe sbagliata.
        var (backfill, managers, db) = await BuildAsync(Corsia());
        await SeminaCandidatoAsync(db, "RsiOversold", survived: true, isGrey: false);

        await backfill.RunAsync(dryRun: false);

        Assert.Equal("Survived", managers[0].Config.Strategies[0].SourceVerdict);
    }

    [Fact]
    public async Task CandidatoBOCCIATO_INPIENO_nonDiventaGrey()
    {
        // [K37] Il terzo stato. L'archivio ne tiene TRE — sopravvissuto, grigio, bocciato in pieno —
        // e `Survived ? "Survived" : "Grey"` ne schiacciava due in uno, promuovendo il peggiore.
        //
        // Non è un caso di scuola: al 2026-09-01 il candidato della corsia 7
        // (BollingerMeanReversion STX/USDT 4h) era stato retrocesso il 21/08 con «Sharpe holdout
        // 0,11 < 0,5», quindi né sopravvissuto né grigio. La prima esecuzione del backfill gli
        // avrebbe scritto «Grey» — cioè un giudizio MIGLIORE di quello d'archivio, su un campo che
        // governa un tetto di rischio.
        var (backfill, managers, db) = await BuildAsync(Corsia());
        await SeminaCandidatoAsync(db, "RsiOversold", survived: false, isGrey: false);

        var rep = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, rep.Updated);
        Assert.Null(managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Equal(0, managers[0].Saves);
        Assert.Contains(rep.Legs, l => l.Detail.Contains("BOCCIATO IN PIENO"));
    }

    [Fact]
    public async Task LEtichettaVieneDalRunDiSCHIERAMENTO_nonDallUltimoGiudizio()
    {
        // [K37] Il cuore della correzione. La stessa ipotesi viene rivalutata a ogni giro di caccia,
        // e il verdetto CAMBIA: misurato sull'archivio vero, 71 chiavi su 1.028 cambiano `IsGrey`
        // fra un run e l'altro. Leggere «l'ultimo run che ricapita sulla chiave» significa
        // etichettare una gamba schierata con il giudizio di un esperimento che non è il suo.
        //
        // Qui: la gamba è stata schierata da un run che la giudicava GRIGIA; un run successivo la
        // promuove a sopravvissuta. L'etichetta deve restare quella della sua provenienza.
        var (backfill, managers, db) = await BuildAsync(Corsia());
        var runSchieramento = await SeminaCandidatoAsync(db, "RsiOversold", survived: false, isGrey: true);
        await SeminaAssignAsync(db, laneId: 0, runSchieramento);

        // Un run PIÙ RECENTE sulla stessa chiave, con verdetto opposto.
        var key = PipelineCandidateKey.Build("RsiOversold", "ETC/USDT", "4h", Parametri);
        await using (var c = await db.CreateDbContextAsync())
        {
            c.ResearchCandidates.Add(new ResearchCandidate
            {
                RunId = Guid.NewGuid(),
                RunCompletedUtc = DateTime.UtcNow,   // più recente del run di schieramento
                StrategyName = "RsiOversold",
                Symbol = "ETC/USDT",
                Timeframe = "4h",
                CandidateKey = key,
                ParametersJson = "{}",
                BestStopVariant = "base",
                Survived = true,
                IsGrey = false,
            });
            await c.SaveChangesAsync();
        }

        await backfill.RunAsync(dryRun: false);

        Assert.Equal("Grey", managers[0].Config.Strategies[0].SourceVerdict);
    }

    [Fact]
    public async Task JournalCheDICEunAltroRun_nonEtichetta()
    {
        // [K37] Il verso fail-closed della stessa regola, e il caso REALE: le corsie 4 e 6 sono
        // state riassegnate il 2026-08-31 senza lasciare riga a journal, quindi il loro ultimo
        // `Assign` applicato descrive un'identità RITIRATA (corsia 4: GridMeanReversion XRP/USDT 4h
        // del 3 agosto). Vincolare la ricerca a quel run non trova nulla — ed è giusto così: la
        // provenienza non è accertabile, e su un campo che governa un tetto l'ignoto è meglio di
        // un'etichetta plausibile.
        var (backfill, managers, db) = await BuildAsync(Corsia());
        await SeminaCandidatoAsync(db, "RsiOversold", survived: false, isGrey: true);
        await SeminaAssignAsync(db, laneId: 0, Guid.NewGuid());   // un run che non ha mai visto questa chiave

        var rep = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, rep.Updated);
        Assert.Null(managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Contains(rep.Legs, l => l.Detail.Contains("riassegnata senza"));
    }

    [Fact]
    public async Task CandidatoNONtrovato_NONsiEtichetta_eIlPercheSiDICE()
    {
        // Il cuore. Su un campo che governa un tetto, un'etichetta inventata è peggio
        // dell'ignoranza: l'ignoto conta come grigio, che è il verso prudente.
        var (backfill, managers, _) = await BuildAsync(Corsia());

        var rep = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, rep.Updated);
        Assert.Null(managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Equal(0, managers[0].Saves);
        Assert.Contains(rep.Legs, l => l.Detail.Contains("non trovato") && l.Detail.Contains("ignoto"));
    }

    [Fact]
    public async Task Anteprima_NONscrive()
    {
        var (backfill, managers, db) = await BuildAsync(Corsia());
        await SeminaCandidatoAsync(db, "RsiOversold", survived: false);

        var rep = await backfill.RunAsync(dryRun: true);

        Assert.True(rep.DryRun);
        Assert.Equal(1, rep.Updated);           // dice cosa FAREBBE
        Assert.Null(managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Equal(0, managers[0].Saves);     // e non lo fa
    }

    [Fact]
    public async Task UnaGambaGIAetichettata_nonSiRIETICHETTA()
    {
        // Idempotenza, e non è pedanteria: l'archivio è derivato e si ricostruisce: se il backfill
        // riscrivesse le etichette esistenti, una ricostruzione dell'indice potrebbe cambiare la
        // provenienza di una gamba GIÀ schierata, cioè riscrivere la storia sotto al tetto.
        var (backfill, managers, db) = await BuildAsync(Corsia(etichettaGia: "Survived"));
        await SeminaCandidatoAsync(db, "RsiOversold", survived: false);

        var rep = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, rep.Updated);
        Assert.Empty(rep.Legs);
        Assert.Equal("Survived", managers[0].Config.Strategies[0].SourceVerdict);
        Assert.Equal(0, managers[0].Saves);
    }
}
