using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Components.Layout;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using System.Text.Json;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Rendering di <c>/admin/protections</c> (audit 2026-07-29).
///
/// La pagina nasce da un buco preciso: quattro protezioni che decidono se un'operazione può
/// aprirsi o chiudersi — feed real-time, esposizione correlata, router di regime, watchdog degli
/// invarianti — esistevano nel codice, giravano in produzione, e si configuravano SOLO editando
/// appsettings.json a mano. Questi test proteggono le due proprietà che rendono la pagina
/// utilizzabile senza fare danni: che i controlli ci siano davvero, e che i due interruttori
/// «decide» restino subordinati al loro interruttore «osserva».
/// </summary>
public class ProtectionsPageRenderTests : BunitContext
{
    public ProtectionsPageRenderTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>
    /// Store di prova: tiene le sezioni in memoria e registra le scritture. Sostituisce sia il
    /// percorso locale che quello gRPC — alla pagina non interessa quale sia, ed è il punto
    /// dell'astrazione.
    /// </summary>
    private sealed class FakeEngineConfigStore(bool remote = false, bool reachable = true) : IEngineConfigStore
    {
        public bool IsRemote => remote;
        public readonly List<(string Section, object Options)> Saved = [];
        public readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase);
        public string? WarningToReturn { get; set; }

        public Task<EngineConfigSnapshot> ReadAsync(IEnumerable<string>? sections = null, CancellationToken ct = default)
        {
            if (!reachable)
            {
                return Task.FromResult(new EngineConfigSnapshot([], string.Empty, false, false, "motore non raggiungibile"));
            }
            var views = Sections
                .Select(kv => new EngineConfigSectionView(kv.Key, kv.Value, Writable: true, Source: "appsettings.json"))
                .ToList();
            return Task.FromResult(new EngineConfigSnapshot(views, "/app/appsettings.json", Writable: true));
        }

        public Task<EngineConfigWriteResult> WriteAsync(string section, object options, CancellationToken ct = default)
        {
            Saved.Add((section, options));
            Sections[section] = JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions);
            return Task.FromResult(new EngineConfigWriteResult(Sections[section], WarningToReturn));
        }
    }

    private FakeEngineConfigStore RegisterServices(
        RealtimeFeedOptions? realtime = null,
        RegimeRoutingOptions? routing = null,
        CorrelatedExposureOptions? correlated = null,
        bool remoteTrading = false,
        bool reachable = true)
    {
        var store = new FakeEngineConfigStore(remoteTrading, reachable);
        Seed(store, RealtimeFeedOptions.SectionName, realtime ?? new RealtimeFeedOptions());
        Seed(store, "Trading:ProtectiveExitShadow", new ProtectiveExitShadowOptions());
        Seed(store, "Trading:CorrelatedExposure", correlated ?? new CorrelatedExposureOptions());
        Seed(store, "Trading:RegimeRouting", routing ?? new RegimeRoutingOptions());
        Seed(store, "Trading:LaneInvariants", new LaneInvariantOptions());

        Services.AddLogging();
        Services.AddSingleton<IEngineConfigStore>(store);
        Services.AddSingleton<IStrategyFactory, StrategyFactory>();
        return store;
    }

    private static void Seed<T>(FakeEngineConfigStore store, string section, T options) =>
        store.Sections[section] = JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions);

    private void Authorize()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles(AppRoles.Admin);
    }

    [Fact]
    public void Protections_ShowsEveryGuard_WithItsOwnSwitch()
    {
        Authorize();
        RegisterServices();

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        // Le quattro protezioni trasversali, ciascuna col proprio interruttore.
        Assert.Contains("Feed di prezzo real-time", cut.Markup);
        Assert.Contains("Sentinella d'ombra", cut.Markup);
        Assert.Contains("Limite di esposizione correlata", cut.Markup);
        Assert.Contains("Router di regime", cut.Markup);
        Assert.Contains("Watchdog degli invarianti", cut.Markup);

        foreach (var id in new[] { "rt_en", "sh_en", "ce_en", "rr_en", "li_en" })
        {
            Assert.Single(cut.FindAll($"#{id}"));
        }
    }

    [Fact]
    public void ObserveBeforeDecide_TheDecideSwitchesAreDisabledWhileTheFeatureIsOff()
    {
        // Il punto dell'intero disegno: «guida le uscite» e «le regole filtrano» non devono poter
        // essere accesi mentre la funzione che li alimenta è spenta — sarebbe una configurazione
        // che dichiara un potere che nessuno esercita, e che si attiva di sorpresa il giorno in cui
        // qualcun altro accende l'interruttore principale.
        Authorize();
        RegisterServices(
            realtime: new RealtimeFeedOptions { Enabled = false },
            routing: new RegimeRoutingOptions { Enabled = false });

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.True(cut.Find("#rt_drive").HasAttribute("disabled"));
        Assert.True(cut.Find("#rr_drive").HasAttribute("disabled"));
    }

    [Fact]
    public void ObserveBeforeDecide_TheDecideSwitchesOpenUpOnceTheFeatureIsOn()
    {
        Authorize();
        RegisterServices(
            realtime: new RealtimeFeedOptions { Enabled = true },
            routing: new RegimeRoutingOptions { Enabled = true });

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.False(cut.Find("#rt_drive").HasAttribute("disabled"));
        Assert.False(cut.Find("#rr_drive").HasAttribute("disabled"));
    }

    [Fact]
    public void RegimeRules_EmptyStrategyList_IsShownAsADecision_NotAsAnEmptyRow()
    {
        // Una riga vuota che sembra incompleta invita a "sistemarla": qui la si dichiara per quello
        // che è, cioè "in questo regime la corsia sta ferma".
        Authorize();
        RegisterServices(routing: new RegimeRoutingOptions
        {
            Enabled = true,
            Rules = [new RegimeRoutingRule { RegimeId = 2, Strategies = [] }],
        });

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.Contains("corsia ferma in questo regime", cut.Markup);
    }

    [Fact]
    public async Task InvalidThreshold_IsRefusedBeforeTouchingTheConfigFile()
    {
        // La validazione lato server è l'intero motivo per cui AdminConfigRules esiste: qui si
        // verifica che sia CABLATA, non solo che esista.
        Authorize();
        var store = RegisterServices(correlated: new CorrelatedExposureOptions
        {
            // Finestra più corta delle barre sovrapposte richieste: nessuna correlazione sarebbe
            // mai stimabile, e il guard sembrerebbe acceso lasciando passare tutto.
            LookbackBars = 10,
            MinOverlappingBars = 100,
        });

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();
        var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Salva")
                                                     && b.ParentElement?.ParentElement?.TextContent.Contains("ρ") == true);
        await cut.InvokeAsync(() => save.Click());

        Assert.Empty(store.Saved);
        Assert.Contains("alert-danger", cut.Markup);
    }

    [Fact]
    public void RemoteTrading_ThePageSaysWhereItIsWriting()
    {
        // Il banner ha cambiato SIGNIFICATO il 2026-07-29 (secondo giro): prima avvisava che i
        // valori mostrati non erano quelli in vigore, perché il guscio leggeva il proprio file.
        // Ora la pagina interroga il motore, quindi i valori SONO quelli applicati e il banner
        // spiega dove si sta scrivendo — che resta un fatto da sapere prima di premere Salva.
        Authorize();
        RegisterServices(remoteTrading: true);

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.Contains("Stai configurando il motore remoto", cut.Markup);
        Assert.Contains("sta applicando adesso", cut.Markup);
        Assert.Contains("/app/appsettings.json", cut.Markup); // dice SU COSA scrive il motore
    }

    [Fact]
    public void LocalTrading_NoBannerAtAll()
    {
        // Col motore in-process non c'è nulla di sorprendente da dire: l'avviso sarebbe rumore che
        // insegna a ignorare gli avvisi.
        Authorize();
        RegisterServices(remoteTrading: false);

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.DoesNotContain("Stai configurando il motore remoto", cut.Markup);
    }

    [Fact]
    public void UnreachableEngine_ShowsDefaultsAndSaysSo_InsteadOfPassingThemOffAsTruth()
    {
        // Il caso che conta quando il core è giù: la pagina deve restare apribile (è il momento in
        // cui uno vuole guardarla) SENZA spacciare i default per la configurazione del motore.
        Authorize();
        RegisterServices(remoteTrading: true, reachable: false);

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();

        Assert.Contains("Configurazione del motore non leggibile", cut.Markup);
        Assert.Contains("default del codice", cut.Markup);
        // Non basta mostrarli: la pagina deve dire che NON sono quelli in vigore.
        Assert.Contains("non quelli in vigore", cut.Markup);
    }

    [Fact]
    public async Task SaveGoesToTheEngineStore_NotToTheLocalFile()
    {
        // La regressione da impedire: che qualcuno ricablasse il pannello su IAppConfigWriter,
        // riportandolo a scrivere nel file del guscio — cioè al bug di partenza.
        Authorize();
        var store = RegisterServices(remoteTrading: true);

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();
        var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Salva")
                                                     && b.ParentElement?.ParentElement?.TextContent.Contains("ρ") == true);
        await cut.InvokeAsync(() => save.Click());

        var saved = Assert.Single(store.Saved);
        Assert.Equal("Trading:CorrelatedExposure", saved.Section);
    }

    [Fact]
    public async Task WhenTheEngineWarnsThatAnEnvOverridesTheFile_ThePanelRepeatsIt()
    {
        // Un salvataggio che riesce e non cambia nulla è il difetto che questo lavoro combatte:
        // l'avvertimento del motore deve arrivare all'operatore, non fermarsi nel payload.
        Authorize();
        var store = RegisterServices(remoteTrading: true);
        store.WarningToReturn = "Enabled arriva da variabili d'ambiente, che ha la precedenza sul file";

        var cut = Render<ProcioneMGR.Components.Pages.Admin.Protections>();
        var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Salva")
                                                     && b.ParentElement?.ParentElement?.TextContent.Contains("ρ") == true);
        await cut.InvokeAsync(() => save.Click());

        Assert.Contains("precedenza sul file", cut.Markup);
        Assert.Contains("alert-danger", cut.Markup); // non un successo verde: il valore NON è in vigore
    }

    [Fact]
    public void Protections_IsReachableFromTheSidebar_ForAdminsOnly()
    {
        // Una pagina che esiste ma che nessuno trova non è integrata: la voce di menu è parte del
        // wiring, non un dettaglio estetico.
        var item = NavModel.Sections
            .SelectMany(s => s.Items)
            .Single(i => i.Href == "admin/protections");

        Assert.Equal([AppRoles.Admin], item.Roles);
        Assert.Equal(("Configurazione", "Protezioni"), NavModel.Resolve("/admin/protections"));
    }
}
