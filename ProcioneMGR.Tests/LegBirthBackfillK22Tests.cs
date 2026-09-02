using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K22, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Il timbro di nascita si RICOSTRUISCE da una
/// data registrata, o non si scrive.</b>
///
/// <para>Da [K39] una gamba senza <c>ExpectedSharpeAtUtc</c> non è misurabile dal monitor di
/// decadimento — verso giusto — ma 4 gambe su 7 restano fuori dal giudizio per sempre. Il rimedio
/// del PRD (togliere e rimettere le gambe) conierebbe uno <c>StrategyId</c> nuovo: azzererebbe
/// l'identità della corsia e l'orologio dell'osservazione, cioè dieci giorni di cancello per
/// guadagnare un campo.</para>
///
/// <para><b>La proprietà difesa da questi test è una sola, e vale più della copertura:</b> l'ancora
/// scritta <b>non può mai precedere la nascita vera</b>. <c>FirstSeenUtc</c> è il primo tick in cui
/// la flotta ha visto quell'identità, quindi è ≥ lo schieramento per costruzione. Sbagliare per
/// eccesso significa «il giudizio parte più tardi» — si perdono dati; sbagliare per difetto
/// significa far entrare nel giudizio i trade di un'ipotesi precedente, che è esattamente il
/// difetto che K39 ha corretto.</para>
/// </summary>
[Collection("Postgres")]
public class LegBirthBackfillK22Tests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LegBirthBackfillK22Tests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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
        public ConfigWriteContext? UltimoScrittore { get; private set; }
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration config, ConfigWriteContext writtenBy, CancellationToken ct = default)
        {
            Config = config;
            UltimoScrittore = writtenBy;
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

    private async Task<(LegBirthBackfill Backfill, FakeEnsembleManager[] Managers, IDbContextFactory<ApplicationDbContext> Db)>
        BuildAsync(params (int Lane, EnsembleConfiguration Cfg)[] configs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var managers = new FakeEnsembleManager[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var cfg = configs.FirstOrDefault(c => c.Lane == lane).Cfg ?? new EnsembleConfiguration();
            managers[lane] = new FakeEnsembleManager(lane, cfg);
            services.AddKeyedSingleton<IEnsembleManager>(lane, managers[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();

        return (new LegBirthBackfill(db, provider, NullLogger<LegBirthBackfill>.Instance), managers, db);
    }

    private static EnsembleConfiguration Corsia(EnsembleStrategy gamba) => new()
    {
        Symbol = "UNI/USDT",
        Timeframe = "4h",
        Strategies = [gamba],
    };

    private static EnsembleStrategy Gamba(DateTime? timbro = null) => new()
    {
        StrategyName = "GridMeanReversion",
        DisplayName = "GridMeanReversion (fascia grigia)",
        Parameters = new Dictionary<string, decimal> { ["Period"] = 14m },
        IsActive = true,
        CurrentAllocation = 100m,
        ExpectedSharpeAtUtc = timbro,
    };

    private static async Task SeminaLedgerAsync(
        IDbContextFactory<ApplicationDbContext> db, int laneId, string identity, DateTime firstSeen)
    {
        await using var c = await db.CreateDbContextAsync();
        c.FleetLaneObservations.Add(new FleetLaneObservation
        {
            LaneId = laneId,
            Identity = identity,
            FirstSeenUtc = firstSeen,
            ObservedSeconds = 3600,
            LastTickUtc = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ il caso che funziona

    [Fact]
    public async Task LaSTESSAidentita_riceveIlPRIMOavvistamentoCOMEancora()
    {
        var gamba = Gamba();
        var (backfill, managers, db) = await BuildAsync((1, Corsia(gamba)));
        var nascita = new DateTime(2026, 8, 25, 12, 26, 11, DateTimeKind.Utc);
        await SeminaLedgerAsync(db, 1, $"UNI/USDT|4h|{gamba.StrategyId}", nascita);

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(1, report.Updated);
        Assert.Equal(nascita, managers[1].Config.Strategies[0].ExpectedSharpeAtUtc);
        Assert.Equal(1, managers[1].Saves);
        // [K48] E lo scrittore lascia il proprio nome: una scrittura anonima sulla configurazione
        // è la cosa che K48 ha appena reso impossibile.
        Assert.Equal(ConfigWriteSources.Backfill, managers[1].UltimoScrittore!.Source);
        Assert.Contains("K22", managers[1].UltimoScrittore!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InANTEPRIMA_nonSCRIVEnulla()
    {
        var gamba = Gamba();
        var (backfill, managers, db) = await BuildAsync((1, Corsia(gamba)));
        await SeminaLedgerAsync(db, 1, $"UNI/USDT|4h|{gamba.StrategyId}", DateTime.UtcNow.AddDays(-8));

        var report = await backfill.RunAsync(dryRun: true);

        Assert.Equal(1, report.Updated);                                       // dice cosa FAREBBE
        Assert.Null(managers[1].Config.Strategies[0].ExpectedSharpeAtUtc);     // e non lo fa
        Assert.Equal(0, managers[1].Saves);
    }

    // ------------------------------------------------------------------ i nulli, che sono il punto

    /// <summary>
    /// <b>Il nullo principale.</b> Il ledger osserva un'identità DIVERSA sulla stessa corsia — è ciò
    /// che succede a ogni riassegnazione, perché la riga viene riscritta. Prendere quella data e
    /// metterla sulla gamba di oggi darebbe al monitor i trade dell'ipotesi precedente: il difetto
    /// che K39 ha corretto, reintrodotto da un backfill che voleva ripararlo.
    /// </summary>
    [Fact]
    public async Task ILNULLO_seIlLEDGERosservaUNALTRAipotesi_nonSiTIMBRA()
    {
        var gamba = Gamba();
        var (backfill, managers, db) = await BuildAsync((1, Corsia(gamba)));
        await SeminaLedgerAsync(db, 1, "UNI/USDT|4h|00000000000000000000000000000000", DateTime.UtcNow.AddDays(-30));

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, report.Updated);
        Assert.Null(managers[1].Config.Strategies[0].ExpectedSharpeAtUtc);
        Assert.Equal(0, managers[1].Saves);
        Assert.Contains(report.Legs, l => l.Detail.Contains("ALTRA identità", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nessuna riga di ledger: nessuna data registrata. Non si inventa — inventarla renderebbe
    /// misurabile una gamba su un'ancora finta, che è peggio di non misurarla.
    /// </summary>
    [Fact]
    public async Task SENZAledger_laGAMBArestaSENZAtimbro()
    {
        var (backfill, managers, _) = await BuildAsync((1, Corsia(Gamba())));

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, report.Updated);
        Assert.Null(managers[1].Config.Strategies[0].ExpectedSharpeAtUtc);
        Assert.Contains(report.Legs, l => l.Detail.Contains("nessuna data registrata", StringComparison.Ordinal));
    }

    /// <summary>
    /// Una gamba che il timbro ce l'ha già non si tocca: il backfill riempie i buchi, non riscrive
    /// la storia. Senza questa prova, rieseguirlo sposterebbe l'ancora a ogni click.
    /// </summary>
    [Fact]
    public async Task UNtimbroGIAesistente_nonSiSOVRASCRIVE()
    {
        var originale = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var gamba = Gamba(originale);
        var (backfill, managers, db) = await BuildAsync((1, Corsia(gamba)));
        await SeminaLedgerAsync(db, 1, $"UNI/USDT|4h|{gamba.StrategyId}", new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc));

        var report = await backfill.RunAsync(dryRun: false);

        Assert.Equal(0, report.Updated);
        Assert.Equal(originale, managers[1].Config.Strategies[0].ExpectedSharpeAtUtc);
        Assert.Equal(0, managers[1].Saves);
    }

    /// <summary>
    /// La proprietà di sicurezza, dichiarata come test invece che come commento: l'ancora scritta
    /// non precede mai il primo avvistamento — cioè non può precedere la nascita, che è ≤ esso.
    /// </summary>
    [Fact]
    public async Task LancoraNONprecedeMAIilPRIMOavvistamento()
    {
        var gamba = Gamba();
        var (backfill, managers, db) = await BuildAsync((1, Corsia(gamba)));
        var primoAvvistamento = new DateTime(2026, 8, 25, 12, 26, 11, DateTimeKind.Utc);
        await SeminaLedgerAsync(db, 1, $"UNI/USDT|4h|{gamba.StrategyId}", primoAvvistamento);

        await backfill.RunAsync(dryRun: false);

        Assert.True(managers[1].Config.Strategies[0].ExpectedSharpeAtUtc >= primoAvvistamento);
    }
}
