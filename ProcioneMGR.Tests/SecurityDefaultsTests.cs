using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C-02, Fase 1 PRD-RISANAMENTO] Le "regole da non violare" di CLAUDE.md rese ESEGUIBILI.
/// Fin qui vivevano solo in un file Markdown, e infatti una era già stata violata senza che
/// nessuno se ne accorgesse: <c>DriveProtectiveExits</c> aveva default <c>true</c> mentre la
/// misura B3 (docs/REPORT-B3-EXITLAG-2026-07-28.md: uscire al tocco è peggio in 24/24
/// configurazioni) e la regola 7 dicevano <c>false</c> — chi accendeva il feed ereditava in
/// silenzio l'assetto bocciato. Da oggi un default di sicurezza che cambia fa fallire la CI,
/// e chi lo cambia DELIBERATAMENTE deve aggiornare anche questo file, cioè dichiararlo.
/// </summary>
public sealed class SecurityDefaultsTests
{
    [Fact]
    public void RealtimeFeed_IsObservationalByDefault()
    {
        var o = new RealtimeFeedOptions();
        Assert.False(o.Enabled);              // il feed è additivo: spento = comportamento storico
        Assert.False(o.DriveProtectiveExits); // B3: il tick OSSERVA, non decide (regola 7)
    }

    [Fact]
    public void RegimeRouting_ClassifiesButDoesNotDecide()
    {
        var o = new RegimeRoutingOptions();
        Assert.False(o.DriveDecisions); // regola 7: risultato di misura, non svista
    }

    [Fact]
    public void Promotion_NeverAutomatesTowardsLive()
    {
        var o = new PromotionEvaluatorOptions();
        // Paper→Testnet automatico è AMMESSO (è il disegno). Verso Live: nulla.
        Assert.False(o.AutoDemoteLiveToTestnet); // l'unica automazione che TOCCA Live è opt-in...
        Assert.True(o.DemoteLiveDryRun);         // ...e anche da accesa parte in dry-run (regola 3)
    }

    [Fact]
    public void LiveOrders_RequireManualConfirmation()
    {
        Assert.True(new SafetyConfiguration().RequireManualConfirmationForLive);
    }

    [Fact]
    public void VolatilityScaling_CanOnlyReduceExposure()
    {
        var cfg = new SafetyConfiguration();
        Assert.False(cfg.VolatilityTargetingEnabled);
        // Tetto 1,0: il dosaggio può solo RIDURRE la dimensione, mai superare i limiti validati.
        Assert.Equal(1.0m, cfg.MaxExposureMultiplier);
    }

    [Fact]
    public void CarryMode_CannotRepresentLive()
    {
        // Il failsafe più forte della piattaforma: Live è IRRAPPRESENTABILE nel tipo — nessun
        // percorso di codice, config o bug può portare il carry a operare con denaro reale.
        Assert.DoesNotContain(Enum.GetNames<CarryMode>(), n => n.Contains("Live", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FleetOrchestrator_IsOffAndDryRunByDefault()
    {
        var o = new FleetOptions();
        Assert.False(o.Enabled);
        Assert.True(o.DryRun); // anche da acceso: prima si osserva il journal, poi si agisce
    }

    [Fact]
    public void SlicedLiveExecution_IsOffByDefault()
    {
        Assert.False(new LiveExecutionOptions().Enabled);
    }

    [Fact]
    public void CarryForwardTest_IsOffByDefault()
    {
        Assert.False(new CarryOptions().Enabled);
    }
}
