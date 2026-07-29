using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Components.Layout;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
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

    private sealed class RecordingConfigWriter : IAppConfigWriter
    {
        public readonly List<(string Section, object Options)> Saved = [];

        public Task SaveSectionAsync<T>(string sectionPath, T options, CancellationToken ct = default)
        {
            Saved.Add((sectionPath, options!));
            return Task.CompletedTask;
        }
    }

    private RecordingConfigWriter RegisterServices(
        RealtimeFeedOptions? realtime = null,
        RegimeRoutingOptions? routing = null,
        CorrelatedExposureOptions? correlated = null)
    {
        var writer = new RecordingConfigWriter();
        Services.AddLogging();
        Services.AddSingleton<IAppConfigWriter>(writer);
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RealtimeFeedOptions>>(
            new StaticOptionsMonitor<RealtimeFeedOptions>(realtime ?? new RealtimeFeedOptions()));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ProtectiveExitShadowOptions>>(
            new StaticOptionsMonitor<ProtectiveExitShadowOptions>(new ProtectiveExitShadowOptions()));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<CorrelatedExposureOptions>>(
            new StaticOptionsMonitor<CorrelatedExposureOptions>(correlated ?? new CorrelatedExposureOptions()));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RegimeRoutingOptions>>(
            new StaticOptionsMonitor<RegimeRoutingOptions>(routing ?? new RegimeRoutingOptions()));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<LaneInvariantOptions>>(
            new StaticOptionsMonitor<LaneInvariantOptions>(new LaneInvariantOptions()));
        Services.AddSingleton<IStrategyFactory, StrategyFactory>();
        return writer;
    }

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
        var writer = RegisterServices(correlated: new CorrelatedExposureOptions
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

        Assert.Empty(writer.Saved);
        Assert.Contains("alert-danger", cut.Markup);
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
