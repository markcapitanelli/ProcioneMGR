using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA SCHEDA CHE NON POTEVA ACCENDERSI.
///
/// <para>Dal 2026-08-05 al 2026-08-24 la Home non ha mai potuto mostrare un alert di decadimento
/// delle gambe, e nessuno se n'era accorto. La pagina iniettava <see cref="IEnsembleManager"/>
/// <b>non keyed</b>, e quella registrazione è un fallback dichiarato «per i consumer non ancora
/// aggiornati» che risolve <b>sempre la corsia 0</b>. Quando la flotta è stata riassegnata, la
/// corsia 0 è rimasta con configurazione vuota e le otto gambe Paper vive sono finite sulle corsie
/// 1-7: il ciclo sulle gambe non eseguiva mai e il conteggio era zero per costruzione.</para>
///
/// <para>Il difetto è passato inosservato perché quel blocco — solo — non aveva il ramo «nessun
/// allarme» che entrambe le derive hanno: la Home non stampava nulla, e uno schermo vuoto è
/// indistinguibile da «nessuna gamba sta decadendo». Era l'unico allarme della pagina che parla
/// di <b>capitale in movimento</b>.</para>
///
/// <para>Questi test difendono le due proprietà che lo rendono di nuovo un controllo: la Home
/// guarda TUTTE le corsie configurate (non la sola 0), e quando non ha niente da dire lo dice
/// invece di tacere.</para>
/// </summary>
[Collection("Postgres")]
public sealed class HomeDecayWiringTests : BunitContext
{
    private readonly string _connString;

    public HomeDecayWiringTests(PostgresFixture pg)
    {
        _connString = pg.CreateDatabase();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ----------------------------------------------------------------------------------------
    //  Impalcatura: corsie e manager finti, così il test misura il CABLAGGIO e non l'algoritmo
    //  di decadimento (che ha già i suoi test).
    // ----------------------------------------------------------------------------------------

    private sealed class FakeLaneDirectory(IReadOnlyList<LaneSummary> lanes) : ILaneDirectory
    {
        public Task<IReadOnlyList<LaneSummary>> ListAsync(CancellationToken ct = default) => Task.FromResult(lanes);
    }

    /// <summary>Manager di corsia che restituisce i report che il test gli mette in bocca.</summary>
    private sealed class FakeEnsembleManager(int laneId, IReadOnlyList<DecayReport> reports) : IEnsembleManager
    {
        public int LaneId => laneId;

        /// <summary>Quante volte la Home ha chiesto i report a QUESTA corsia.</summary>
        public int DecayCalls { get; private set; }

        public Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default)
        {
            DecayCalls++;
            return Task.FromResult(reports);
        }

        // Il resto dell'interfaccia non serve alla Home: se un giorno la toccasse, il test deve
        // rompersi rumorosamente invece di restituire un default plausibile.
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateConfigurationAsync(EnsembleConfiguration config, ProcioneMGR.Services.Ensemble.ConfigWriteContext writtenBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static DecayReport Leg(string name, bool alert, bool measurable = true, int trades = 39) => new()
    {
        StrategyId = name.ToLowerInvariant(),
        StrategyName = name,
        DisplayName = name,
        TradeCount = trades,
        IsAlert = alert,
        IsMeasurable = measurable,
        RealizedSharpe = alert ? -0.4m : 1.2m,
        ExpectedSharpe = 1.5m,
        SharpeRatio = alert ? -0.27m : 0.8m,
    };

    private static LaneSummary Lane(int id, string symbol, string tf = "4h", bool running = true) =>
        new(id, symbol, tf, "Paper", running);

    /// <summary>Registra la Home con le corsie e i report indicati. Ritorna i manager per ispezione.</summary>
    private Dictionary<int, FakeEnsembleManager> Register(
        IReadOnlyList<LaneSummary> lanes,
        IReadOnlyDictionary<int, IReadOnlyList<DecayReport>> reportsByLane)
    {
        Services.AddLogging();
        Services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        Services.AddSingleton<ILaneDirectory>(new FakeLaneDirectory(lanes));
        Services.AddSingleton<ProcioneMGR.Services.Alpha.FactorDriftSnapshot>();
        Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftSnapshot>();
        Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentHeritageSnapshot>();
        // [J3] La Home inietta la sonda «la ricerca è viva»: sul DB di test vuoto degrada da sola
        // al verdetto FERMA («nessun run completato»), che per questi test è solo sfondo.
        Services.AddSingleton(new ProcioneMGR.Services.Pipeline.CampaignOptions().AsMonitor());
        Services.AddSingleton<ProcioneMGR.Services.Health.ResearchLivenessProbe>();
        // [K7/K8 — superficie UI] Stesso passo già fatto per la sonda J3: una sonda nuova iniettata
        // nella Home e non registrata qui fa cadere OGNI render test con un errore di DI, che è
        // rumore — non il difetto che questi test sorvegliano.
        Services.AddSingleton<ProcioneMGR.Services.Health.HeartbeatBoardProbe>();

        var managers = new Dictionary<int, FakeEnsembleManager>();
        foreach (var lane in lanes)
        {
            var mgr = new FakeEnsembleManager(lane.Id, reportsByLane.GetValueOrDefault(lane.Id, []));
            managers[lane.Id] = mgr;
            Services.AddKeyedSingleton<IEnsembleManager>(lane.Id, mgr);
        }

        var auth = AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles(AppRoles.Admin);
        return managers;
    }

    /// <summary>Schema + render. L'attesa è su un marcatore che compare solo a caricamento finito.</summary>
    private async Task<IRenderedComponent<ProcioneMGR.Components.Pages.Home>> RenderHomeAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var cut = Render<ProcioneMGR.Components.Pages.Home>();
        // Le stat card compaiono solo quando `_stats` è valorizzato, cioè DOPO che le corsie sono
        // state lette e il monitor di decadimento interrogato. Aspettare un testo statico della
        // pagina (che c'è già al primo render) renderebbe l'attesa finta.
        cut.WaitForAssertion(() => Assert.Contains("Serie tracciate", cut.Markup), TimeSpan.FromSeconds(30));
        return cut;
    }

    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task LaRegressione_UnAlertSullaCorsia2NonRestaInvisibilePerchéLa0EVuota()
    {
        // Lo stato REALE del 2026-08-24: corsia 0 configurata a vuoto, gambe vive sulle 1-7.
        var lanes = new[] { Lane(0, ""), Lane(1, "DOT/USDT", "15m"), Lane(2, "ADA/USDT") };
        Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>
        {
            [1] = [Leg("GridMeanReversion", alert: false)],
            [2] = [Leg("Composite", alert: true)],
        });

        var cut = await RenderHomeAsync();

        // Prima della correzione qui non compariva NULLA: il manager non-keyed risolveva la corsia 0,
        // la sua configurazione era vuota e il ciclo sulle gambe non eseguiva mai.
        Assert.Contains("in alert di decadimento", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("corsia 2", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ADA/USDT", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaHomeInterrogaOgniCorsiaCONFIGURATA_ENonSoloLaZero()
    {
        var lanes = new[] { Lane(0, ""), Lane(1, "DOT/USDT", "15m"), Lane(2, "ADA/USDT"), Lane(3, "ETC/USDT") };
        var managers = Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>
        {
            [1] = [Leg("A", alert: false)],
            [2] = [Leg("B", alert: false)],
            [3] = [Leg("C", alert: false)],
        });

        await RenderHomeAsync();

        Assert.Equal(1, managers[1].DecayCalls);
        Assert.Equal(1, managers[2].DecayCalls);
        Assert.Equal(1, managers[3].DecayCalls);
        // La corsia 0 non è configurata: interrogarla costerebbe una lettura per non trovare nulla.
        Assert.Equal(0, managers[0].DecayCalls);
    }

    [Fact]
    public async Task SenzaAllarmi_LaHomeLoDICE_InveceDiTacere()
    {
        // È il ramo che mancava, ed è la ragione per cui la cecità è passata inosservata per
        // diciannove giorni: senza, «nessun allarme» e «non ho guardato» producono lo stesso
        // schermo vuoto.
        var lanes = new[] { Lane(1, "DOT/USDT", "15m"), Lane(2, "ADA/USDT") };
        Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>
        {
            [1] = [Leg("A", alert: false)],
            [2] = [Leg("B", alert: false)],
        });

        var cut = await RenderHomeAsync();

        Assert.Contains("Decadimento delle gambe: nessun allarme", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("in alert di decadimento", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaCoperturaDichiaraLeGambeNONMisurabili()
    {
        // «Nessun allarme» su gambe che non hanno abbastanza trade per un verdetto è un via libera
        // costruito su ciò che nessuno ha potuto giudicare — la stessa regola che /ensemble applica
        // col suo verdetto di misurabilità.
        var lanes = new[] { Lane(1, "DOT/USDT", "15m"), Lane(2, "ADA/USDT"), Lane(3, "STX/USDT") };
        Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>
        {
            [1] = [Leg("A", alert: false, measurable: true, trades: 39)],
            [2] = [Leg("B", alert: false, measurable: true, trades: 26)],
            [3] = [Leg("C", alert: false, measurable: false, trades: 1)],
        });

        var cut = await RenderHomeAsync();

        Assert.Contains("2 gambe misurabili su 3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("non è interpretabile", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NessunaCorsiaConfigurata_NonEUnViaLibera()
    {
        var lanes = new[] { Lane(0, "") };
        Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>());

        var cut = await RenderHomeAsync();

        // Nemmeno qui la Home deve restare muta: zero corsie configurate è un'informazione.
        Assert.DoesNotContain("in alert di decadimento", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Decadimento delle gambe: nessun allarme", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaStatCardTrading_ContaLeCorsie_InveceDiMostrarneUnaACaso()
    {
        // Prima: `TradingEngineStates.FirstOrDefaultAsync()`, l'unica delle dieci letture di quella
        // tabella senza filtro su LaneId e senza OrderBy. Postgres non garantisce un ordine: la
        // card mostrava lo stato di UNA CORSIA A CASO spacciandolo per «il Trading», e poteva
        // cambiare da sola a ogni UPDATE che riscriveva la tupla.
        var lanes = new[]
        {
            Lane(0, ""),
            Lane(1, "DOT/USDT", "15m", running: true),
            Lane(2, "ADA/USDT", "4h", running: true),
            Lane(3, "ETC/USDT", "4h", running: false),
        };
        Register(lanes, new Dictionary<int, IReadOnlyList<DecayReport>>());

        var cut = await RenderHomeAsync();

        Assert.Contains("2/3 in Paper", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il guardiano che impedisce la ricaduta, e che vale per OGNI pagina: la registrazione
    /// non-keyed di <see cref="IEnsembleManager"/>/<see cref="ITradingEngine"/> è un fallback che
    /// risolve sempre la corsia 0. In una pagina è quasi sempre un bug — silenzioso, perché la
    /// corsia 0 esiste e risponde: non lancia, restituisce il vuoto.
    /// </summary>
    [Fact]
    public void NessunaPagina_IniettaIlFallbackNonKeyed_SenzaUnaRagioneDichiarata()
    {
        // Eccezioni note, con la ragione. /regimes è un difetto APERTO e non un permesso: la sua
        // manopola «Regime-Aware Weighting» legge e SCRIVE la configurazione della corsia 0 senza
        // alcun selettore, quindi tocca una corsia vuota mentre l'operatore crede di configurare
        // l'ensemble. È elencata qui perché il guardiano nasca verde e la corregga la passata che
        // le darà un selettore di corsia — vedi docs/ROADMAP.md.
        var eccezioni = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Regimes.razor"] = "DIFETTO APERTO 2026-08-24: legge e scrive la corsia 0 senza selettore.",
        };

        var root = RepoRoot();
        var pagine = Directory.EnumerateFiles(
            Path.Combine(root, "ProcioneMGR", "Components"), "*.razor", SearchOption.AllDirectories);

        var colpevoli = new List<string>();
        foreach (var file in pagine)
        {
            var testo = File.ReadAllText(file);
            var iniettaNonKeyed =
                testo.Contains("@inject IEnsembleManager ", StringComparison.Ordinal)
                || testo.Contains("@inject ITradingEngine ", StringComparison.Ordinal);
            if (iniettaNonKeyed && !eccezioni.ContainsKey(Path.GetFileName(file)))
            {
                colpevoli.Add(Path.GetFileName(file));
            }
        }

        Assert.True(colpevoli.Count == 0,
            "Pagine che iniettano il manager/motore NON keyed (risolve sempre la corsia 0, che può essere vuota): "
            + string.Join(", ", colpevoli)
            + ". Risolvi per chiave con ILaneDirectory + GetKeyedService<T>(laneId), come fanno /ensemble e /trading.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
