using Bunit;
using ProcioneMGR.Components.Shared;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il selettore di corsia. Prima era una <c>&lt;select&gt;</c>: la corsia corrente era un numero e
/// basta, e per sapere cosa ci girasse bisognava sceglierla e guardare. Con tre corsie si poteva
/// tenere a mente; con dodici, no.
///
/// Ciò che questi test difendono non è l'estetica ma le due regole che rendono il componente utile
/// quando le corsie sono tante: <b>chi resta visibile lo decide l'utilità, non l'id</b> (prima chi
/// opera, poi chi è configurato, infine le vuote), e <b>la corsia selezionata non finisce mai
/// nascosta</b> — altrimenti sceglierla dal menu la farebbe sparire dentro il menu stesso.
/// </summary>
public class LaneSelectorTests : BunitContext
{
    public LaneSelectorTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static LaneSummary Lane(int id, string symbol = "", bool running = false, string mode = "Paper") =>
        new(id, symbol, symbol.Length > 0 ? "1h" : "", mode, running);

    private IRenderedComponent<LaneSelector> Render(IReadOnlyList<LaneSummary> lanes, int selected, int maxVisible = 6) =>
        base.Render<LaneSelector>(p => p
            .Add(c => c.Lanes, lanes)
            .Add(c => c.Selected, selected)
            .Add(c => c.MaxVisible, maxVisible));

    [Fact]
    public void ShowsEveryLane_WhenTheyFit()
    {
        var lanes = new[] { Lane(0, "BTC/USDT"), Lane(1, "ETH/USDT"), Lane(2) };

        var chips = Render(lanes, selected: 0).FindAll("button.lane-chip");

        Assert.Equal(3, chips.Count);
        Assert.DoesNotContain("+", chips[^1].TextContent);
    }

    [Fact]
    public void UnconfiguredLane_IsShownAsSuch_NotHidden()
    {
        // Una corsia mai configurata deve restare cliccabile: è proprio quella su cui si va a
        // configurare qualcosa di nuovo.
        var markup = Render([Lane(0, "BTC/USDT"), Lane(1)], selected: 0).Markup;

        Assert.Contains("non configurata", markup);
    }

    [Fact]
    public void RunningLane_ShowsTheIndicator()
    {
        var lanes = new[] { Lane(0, "BTC/USDT", running: true), Lane(1, "ETH/USDT") };

        var dots = Render(lanes, selected: 0).FindAll("span.lane-dot");

        Assert.Single(dots);
    }

    [Fact]
    public void SelectedLane_IsMarkedActive()
    {
        var cut = Render([Lane(0, "BTC/USDT"), Lane(1, "ETH/USDT")], selected: 1);

        var active = cut.FindAll("button.lane-chip.active");

        Assert.Single(active);
        Assert.Contains("ETH/USDT", active[0].TextContent);
    }

    [Fact]
    public void ClickingALane_RaisesTheChange()
    {
        var chosen = -1;
        var cut = base.Render<LaneSelector>(p => p
            .Add(c => c.Lanes, new[] { Lane(0, "BTC/USDT"), Lane(1, "ETH/USDT") })
            .Add(c => c.Selected, 0)
            .Add(c => c.SelectedChanged, (int id) => chosen = id));

        cut.FindAll("button.lane-chip")[1].Click();

        Assert.Equal(1, chosen);
    }

    [Fact]
    public void ClickingTheAlreadySelectedLane_DoesNothing()
    {
        // Rieseguire il caricamento di una corsia già aperta costa due query e non cambia niente.
        var raised = 0;
        var cut = base.Render<LaneSelector>(p => p
            .Add(c => c.Lanes, new[] { Lane(0, "BTC/USDT"), Lane(1, "ETH/USDT") })
            .Add(c => c.Selected, 0)
            .Add(c => c.SelectedChanged, (int _) => raised++));

        cut.FindAll("button.lane-chip")[0].Click();

        Assert.Equal(0, raised);
    }

    // --- Il caso per cui il componente esiste: molte corsie ------------------------------------

    [Fact]
    public void WithManyLanes_TheExcessCollapsesUnderAMoreButton()
    {
        var lanes = Enumerable.Range(0, 12).Select(i => Lane(i, $"SYM{i}/USDT")).ToList();

        var cut = Render(lanes, selected: 0, maxVisible: 6);

        // Sei schede + il pulsante "+6".
        Assert.Equal(7, cut.FindAll("button.lane-chip").Count);
        Assert.Contains("+6", cut.Find("button.lane-chip-more").TextContent);
        Assert.Equal(6, cut.FindAll("ul.lane-dropdown button").Count);
    }

    [Fact]
    public void RunningLanesWinTheVisibleSlots_EvenWithHighIds()
    {
        // LA REGOLA CHE CONTA: una corsia che sta muovendo denaro non deve finire dietro un menu
        // perché ha un id alto. Qui le corsie 10 e 11 operano, le prime dieci no.
        var lanes = Enumerable.Range(0, 12)
            .Select(i => Lane(i, $"SYM{i}/USDT", running: i >= 10))
            .ToList();

        var cut = Render(lanes, selected: 0, maxVisible: 3);

        var visible = cut.FindAll("button.lane-chip:not(.lane-chip-more)")
            .Select(c => c.TextContent).ToList();

        Assert.Contains(visible, t => t.Contains("SYM10/USDT"));
        Assert.Contains(visible, t => t.Contains("SYM11/USDT"));
        Assert.Contains(visible, t => t.Contains("SYM0/USDT"));   // ...e la selezionata resta
    }

    [Fact]
    public void TheSelectedLane_IsNeverHiddenBehindTheMenu()
    {
        // Se la selezionata potesse finire nel menu, sceglierla la farebbe sparire dentro il menu
        // stesso: si cliccherebbe una corsia per vederla scomparire.
        var lanes = Enumerable.Range(0, 12).Select(i => Lane(i, $"SYM{i}/USDT")).ToList();

        var cut = Render(lanes, selected: 11, maxVisible: 3);

        var visible = cut.FindAll("button.lane-chip:not(.lane-chip-more)").Select(c => c.TextContent).ToList();
        Assert.Contains(visible, t => t.Contains("SYM11/USDT"));
    }

    [Fact]
    public void ConfiguredLanesComeBeforeEmptyOnes()
    {
        // Con molte corsie vuote, quelle che hanno qualcosa dentro devono restare a vista.
        var lanes = new List<LaneSummary> { Lane(0), Lane(1), Lane(2), Lane(3), Lane(4, "SOL/USDT") };

        var cut = Render(lanes, selected: 0, maxVisible: 2);

        var visible = cut.FindAll("button.lane-chip:not(.lane-chip-more)").Select(c => c.TextContent).ToList();
        Assert.Contains(visible, t => t.Contains("SOL/USDT"));
    }

    [Fact]
    public void VisibleLanes_StayInNumericOrder()
    {
        // La priorità decide CHI si vede, non DOVE: le schede restano ordinate per id, altrimenti
        // cambierebbero posizione a ogni refresh, quando una corsia parte o si ferma.
        var lanes = Enumerable.Range(0, 6).Select(i => Lane(i, $"SYM{i}/USDT", running: i == 5)).ToList();

        var cut = Render(lanes, selected: 0, maxVisible: 6);

        var ids = cut.FindAll("span.lane-chip-id").Select(e => int.Parse(e.TextContent)).ToList();
        Assert.Equal(Enumerable.Range(0, 6), ids);
    }
}
