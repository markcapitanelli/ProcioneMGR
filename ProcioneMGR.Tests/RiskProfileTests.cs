using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3] Test dei profili di rischio della Modalità Semplice e della loro applicazione per corsia.
///
/// Il test più importante è quello sull'INVARIANTE di sizing: un profilo che lo viola non produce
/// una corsia "aggressiva", produce una corsia che non fa MAI trading, perché il SafetyChecker
/// rifiuta ogni singolo ordine. È un guasto silenzioso e assurdo da diagnosticare dal vivo, mentre
/// qui costa un assert.
/// </summary>
public class RiskProfileTests
{
    public static TheoryData<string> ProfileNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in RiskProfiles.All) data.Add(p.Name);
        return data;
    }

    private static RiskProfile ByName(string name) => RiskProfiles.Find(name)!;

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EveryProfile_RespectsTheSizingInvariant(string name)
    {
        // PositionSizePercent × leva ≤ MaxPositionSizePercent ≤ MaxTotalExposurePercent.
        // È la stessa condizione che TradingEngine.StartAsync valida rifiutando l'avvio.
        var p = ByName(name);
        var notional = p.PositionSizePercent * p.MaxLeverageAllowed;

        Assert.True(notional <= p.MaxPositionSizePercent,
            $"{name}: nozionale {notional}% (size {p.PositionSizePercent}% × leva {p.MaxLeverageAllowed}x) " +
            $"oltre MaxPositionSizePercent {p.MaxPositionSizePercent}% — la corsia non aprirebbe mai una posizione.");
        Assert.True(p.MaxPositionSizePercent <= p.MaxTotalExposurePercent,
            $"{name}: MaxPositionSizePercent {p.MaxPositionSizePercent}% oltre MaxTotalExposurePercent {p.MaxTotalExposurePercent}%.");
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EveryProfile_HasCoherentAndPositiveLimits(string name)
    {
        var p = ByName(name);

        Assert.True(p.PositionSizePercent > 0m);
        Assert.True(p.MaxDailyLossPercent > 0m);
        Assert.True(p.MaxOpenPositions >= 1);
        Assert.True(p.MinOrderIntervalSeconds > 0);
        Assert.True(p.MaxLeverageAllowed >= 1);
        Assert.NotEmpty(p.PreferredTimeframes);

        // La perdita giornaliera tollerata deve stare SOTTO il drawdown massimo: se fosse maggiore,
        // il drawdown scatterebbe prima e il limite giornaliero non entrerebbe mai in funzione.
        Assert.True(p.MaxDailyLossPercent < p.MaxDrawdownPercent,
            $"{name}: perdita giornaliera {p.MaxDailyLossPercent}% ≥ drawdown massimo {p.MaxDrawdownPercent}%.");
    }

    [Fact]
    public void Profiles_AreOrderedFromProudentToDynamic()
    {
        // I tre profili devono essere davvero una SCALA: se "Dinamico" non rischiasse più di
        // "Prudente" su ogni dimensione, la scelta offerta all'utente sarebbe finta.
        var c = RiskProfiles.Conservative;
        var b = RiskProfiles.Balanced;
        var d = RiskProfiles.Dynamic;

        Assert.True(c.PositionSizePercent < b.PositionSizePercent && b.PositionSizePercent < d.PositionSizePercent);
        Assert.True(c.MaxTotalExposurePercent < b.MaxTotalExposurePercent && b.MaxTotalExposurePercent < d.MaxTotalExposurePercent);
        Assert.True(c.MaxDrawdownPercent < b.MaxDrawdownPercent && b.MaxDrawdownPercent < d.MaxDrawdownPercent);
        Assert.True(c.MaxLeverageAllowed <= b.MaxLeverageAllowed && b.MaxLeverageAllowed < d.MaxLeverageAllowed);

        // Turnover: intervallo DECRESCENTE fra ordini ⇒ più operazioni consentite.
        Assert.True(c.MaxTradesPerDay < b.MaxTradesPerDay && b.MaxTradesPerDay < d.MaxTradesPerDay);
    }

    [Fact]
    public void TurnoverCaps_StayWithinWhatR2MeasuredAsSustainable()
    {
        // R2 (docs/REPORT-1M-COSTI-R2.md) ha misurato cost drag 3,4% su sei mesi a ~0,6 trade/giorno
        // e 77% a ~28 trade/giorno. I profili devono CIRCONDARE il valore sostenibile, non superarlo
        // di un ordine di grandezza: una prima stesura proponeva 3/6/12 al giorno, che secondo la
        // formula del costo annuo valevano 16% / 66% / 131% l'anno di sole commissioni.
        Assert.True(RiskProfiles.Dynamic.MaxTradesPerDay <= 2m,
            $"il profilo più dinamico consente {RiskProfiles.Dynamic.MaxTradesPerDay:F2} operazioni/giorno: troppo per i costi misurati in R2.");
        Assert.True(RiskProfiles.Conservative.MaxTradesPerDay <= 0.6m);
    }

    [Fact]
    public void EstimatedAnnualCost_GrowsWithTurnover_AndIsReadable()
    {
        const decimal roundTurn = 0.30m;   // fee 0,1%/lato + slippage 0,05%/fill, come in R2

        var c = RiskProfiles.Conservative.EstimatedAnnualCostPercent(roundTurn);
        var b = RiskProfiles.Balanced.EstimatedAnnualCostPercent(roundTurn);
        var d = RiskProfiles.Dynamic.EstimatedAnnualCostPercent(roundTurn);

        Assert.True(c < b && b < d, $"il costo stimato deve crescere col turnover: {c:F2} / {b:F2} / {d:F2}");

        // Devono restare numeri che una persona può leggere e usare per decidere, non percentuali
        // assurde: se il più dinamico superasse il 30%/anno di soli costi, il profilo andrebbe
        // ricalibrato, non mostrato.
        Assert.InRange(c, 0.5m, 5m);
        Assert.InRange(d, 5m, 30m);
    }

    [Fact]
    public void NoScalpingProfile_IsOffered()
    {
        // R2 lo ha escluso PER MISURA: a 1m servono ~8,9 candele tipiche catturate per pareggiare i
        // costi contro 1,1 a 1h, con cost drag 22 volte più alto. Se un giorno qualcuno lo
        // reintroducesse, questo test lo ferma e il commento spiega perché.
        Assert.DoesNotContain(RiskProfiles.All, p =>
            p.Name.Contains("scalp", StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Contains("scalp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NonEsiste")]
    public void Find_UnknownOrEmpty_ReturnsNull_MeaningGlobalThresholds(string? name)
    {
        // null non è un errore: significa "questa corsia non usa la Modalità Semplice".
        Assert.Null(RiskProfiles.Find(name));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        Assert.Equal(RiskProfiles.Balanced, RiskProfiles.Find("equilibrato"));
        Assert.Equal(RiskProfiles.Balanced, RiskProfiles.Find("EQUILIBRATO"));
    }

    // ------------------------------------------------------------------ applicazione alle soglie

    private static SafetyConfiguration Global() => new()
    {
        // Fatti della piazza: valori riconoscibili, per verificare che il profilo NON li tocchi.
        FeePercent = 0.06m,
        MaintenanceMarginPercent = 0.7m,
        MaxFillPriceDeviationPercent = 12m,
        MaxFillQuantityDeviationPercent = 3m,
        UseExchangeRestingStops = true,
        RequireManualConfirmationForLive = true,
        // Appetito al rischio: valori che il profilo DEVE sostituire.
        PositionSizePercent = 99m,
        MaxDrawdownPercent = 99m,
        MinOrderIntervalSeconds = 1,
        MaxLeverageAllowed = 99,
    };

    [Fact]
    public void Apply_ProfileOwnsRiskAppetite()
    {
        var effective = RiskProfiles.Conservative.Apply(Global());

        Assert.Equal(RiskProfiles.Conservative.PositionSizePercent, effective.PositionSizePercent);
        Assert.Equal(RiskProfiles.Conservative.MaxDrawdownPercent, effective.MaxDrawdownPercent);
        Assert.Equal(RiskProfiles.Conservative.MinOrderIntervalSeconds, effective.MinOrderIntervalSeconds);
        Assert.Equal(RiskProfiles.Conservative.MaxLeverageAllowed, effective.MaxLeverageAllowed);
    }

    [Fact]
    public void Apply_GlobalKeepsVenueFactsAndSafetyNets()
    {
        // Un utente non deve poter "scegliere" la commissione del proprio exchange, e un profilo non
        // deve poter disattivare la conferma manuale degli ordini Live o allargare le bande di
        // plausibilità dei fill (la rete che ha fermato il bug B1).
        var effective = RiskProfiles.Dynamic.Apply(Global());

        Assert.Equal(0.06m, effective.FeePercent);
        Assert.Equal(0.7m, effective.MaintenanceMarginPercent);
        Assert.Equal(12m, effective.MaxFillPriceDeviationPercent);
        Assert.Equal(3m, effective.MaxFillQuantityDeviationPercent);
        Assert.True(effective.UseExchangeRestingStops);
        Assert.True(effective.RequireManualConfirmationForLive);
    }

    // ------------------------------------------------------------------ monitor per corsia

    [Fact]
    public void LaneMonitor_WithoutProfile_IsTransparent()
    {
        // Nessun profilo ⇒ comportamento IDENTICO a prima di R3: si vede la configurazione globale.
        var global = Global();
        var monitor = new LaneSafetyMonitor(global.AsMonitor());

        Assert.Null(monitor.Profile);
        Assert.Equal(99m, monitor.CurrentValue.PositionSizePercent);
        Assert.Equal(1, monitor.CurrentValue.MinOrderIntervalSeconds);
    }

    [Fact]
    public void LaneMonitor_WithProfile_OverlaysIt()
    {
        var monitor = new LaneSafetyMonitor(Global().AsMonitor());

        monitor.SetProfile(RiskProfiles.Conservative);

        Assert.Equal(RiskProfiles.Conservative.PositionSizePercent, monitor.CurrentValue.PositionSizePercent);
        Assert.Equal(0.06m, monitor.CurrentValue.FeePercent);   // il fatto di piazza resta
    }

    [Fact]
    public void LaneMonitor_ProfileCanBeCleared()
    {
        var monitor = new LaneSafetyMonitor(Global().AsMonitor());
        monitor.SetProfile(RiskProfiles.Dynamic);
        Assert.Equal(RiskProfiles.Dynamic.PositionSizePercent, monitor.CurrentValue.PositionSizePercent);

        monitor.SetProfile(null);

        Assert.Null(monitor.Profile);
        Assert.Equal(99m, monitor.CurrentValue.PositionSizePercent);
    }

    [Fact]
    public void LaneMonitors_AreIndependentOfEachOther()
    {
        // È il punto dell'intero cambiamento: prima le soglie erano globali e due corsie non
        // potevano avere appetiti al rischio diversi.
        var shared = Global().AsMonitor();
        var lane0 = new LaneSafetyMonitor(shared);
        var lane1 = new LaneSafetyMonitor(shared);
        var lane2 = new LaneSafetyMonitor(shared);

        lane0.SetProfile(RiskProfiles.Conservative);
        lane1.SetProfile(RiskProfiles.Dynamic);
        // lane2 senza profilo

        Assert.Equal(5m, lane0.CurrentValue.PositionSizePercent);
        Assert.Equal(10m, lane1.CurrentValue.PositionSizePercent);
        Assert.Equal(99m, lane2.CurrentValue.PositionSizePercent);
    }

    [Fact]
    public void LaneMonitor_StillSeesGlobalChanges_ForFieldsTheProfileDoesNotOwn()
    {
        // L'hot-reload di appsettings.json deve continuare a funzionare: se cambia la commissione
        // reale dell'exchange, la corsia col profilo deve vederla senza riavvio.
        var mutable = new MutableOptionsMonitor<SafetyConfiguration>(Global());
        var monitor = new LaneSafetyMonitor(mutable);
        monitor.SetProfile(RiskProfiles.Balanced);
        Assert.Equal(0.06m, monitor.CurrentValue.FeePercent);

        var updated = Global();
        updated.FeePercent = 0.02m;
        mutable.CurrentValue = updated;

        Assert.Equal(0.02m, monitor.CurrentValue.FeePercent);
        // …e il profilo continua a governare l'appetito al rischio.
        Assert.Equal(RiskProfiles.Balanced.PositionSizePercent, monitor.CurrentValue.PositionSizePercent);
    }
}
