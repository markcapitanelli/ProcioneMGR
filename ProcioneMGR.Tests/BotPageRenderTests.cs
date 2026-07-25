using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3] Rendering della Modalità Semplice (<c>/bot</c>).
///
/// Due cose vanno viste sullo SCHERMO, non solo nei servizi:
///  - il costo annuo implicito del profilo, che è la lezione di R2 tradotta per chi non conosce il
///    dominio: se sparisse dalla pagina, l'utente sceglierebbe la frequenza operativa senza sapere
///    che la sta pagando;
///  - l'assenza del pulsante di avvio quando non c'è nulla da far girare, invece di un pulsante che
///    accende un motore inerte.
/// </summary>
public class BotPageRenderTests : BunitContext
{
    public BotPageRenderTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class FakeEnsembleManager(EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => Task.CompletedTask;
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StoppedEngine : ITradingEngine
    {
        public int LaneId => 0;
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { IsRunning = false, Mode = TradingMode.Paper });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetStopLossTakeProfitAsync(string p, decimal? sl, decimal? tp, decimal? tsl = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string o, string? u, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectOrderAsync(string o, string? u, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Il rendering non deve toccare il DB: se lo fa, il test lo dice invece di rallentare in silenzio.</summary>
    private sealed class ThrowingDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Il test di rendering non deve toccare il DB.");
    }

    private sealed class ThrowingApplier : IPipelineApplier
    {
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public EnsembleSummary SummarizeRecommendation(PipelineRecommendation r) => throw new NotSupportedException();
    }

    private void RegisterServices(bool withStrategies, string? profileName = null, string timeframe = "4h")
    {
        var cfg = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = timeframe,
            TotalCapital = 10_000m, RiskProfileName = profileName,
            Strategies = withStrategies
                ? [new EnsembleStrategy { StrategyId = "s1", StrategyName = "EmaCross", DisplayName = "EMA", IsActive = true }]
                : [],
        };

        Services.AddLogging();
        Services.AddKeyedSingleton<IEnsembleManager>(BotPageService.BotLaneId, new FakeEnsembleManager(cfg));
        Services.AddKeyedSingleton<ITradingEngine>(BotPageService.BotLaneId, new StoppedEngine());
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ThrowingDbFactory());
        Services.AddSingleton<IPipelineApplier>(new ThrowingApplier());
        Services.AddScoped<BotPageService>();
    }

    private void Authorize()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("utente");
        auth.SetRoles(AppRoles.Manager);
    }

    [Fact]
    public void Bot_ShowsTheThreeProfiles_AndTheirImpliedAnnualCost()
    {
        Authorize();
        RegisterServices(withStrategies: true, profileName: RiskProfiles.Equilibrato);

        var cut = Render<ProcioneMGR.Components.Pages.Bot>();

        foreach (var p in RiskProfiles.All)
        {
            Assert.Contains(p.DisplayName, cut.Markup);
        }

        // Il costo implicito deve essere VISIBILE: è ciò che impedisce di scegliere la frequenza
        // operativa senza sapere quanto costa.
        Assert.Contains("Commissioni a pieno regime", cut.Markup);
        var expected = RiskProfiles.Balanced.EstimatedAnnualCostPercent(BotPageService.RoundTurnPercent).ToString("F1");
        Assert.Contains(expected, cut.Markup);
    }

    [Fact]
    public void Bot_WithoutStrategies_OffersResearch_NotAStartButton()
    {
        Authorize();
        RegisterServices(withStrategies: false);

        var cut = Render<ProcioneMGR.Components.Pages.Bot>();

        Assert.Contains("Manca ancora una strategia", cut.Markup);
        Assert.DoesNotContain("Avvia il bot", cut.Markup);
    }

    [Fact]
    public void Bot_WithStrategies_OffersStart_AndSaysItIsSimulationOnly()
    {
        Authorize();
        RegisterServices(withStrategies: true);

        var cut = Render<ProcioneMGR.Components.Pages.Bot>();

        Assert.Contains("Avvia il bot in simulazione", cut.Markup);
        // Il confine verso il denaro reale va detto sulla pagina, non solo nel codice.
        Assert.Contains("solo in simulazione", cut.Markup);
    }

    [Fact]
    public void Bot_WarnsWhenLaneTimeframeDivergesFromTheProfile()
    {
        // Corsia a 15m sotto un profilo Prudente (che preferisce 4h/1d): il bot funzionerà, ma
        // il tetto di operazioni lo frenerà spesso — e senza avviso sembrerebbe rotto.
        Authorize();
        RegisterServices(withStrategies: true, profileName: RiskProfiles.Prudente, timeframe: "15m");

        var cut = Render<ProcioneMGR.Components.Pages.Bot>();

        Assert.Contains("lunghi periodi di inattività", cut.Markup);
    }

    [Fact]
    public void Bot_NoScalpingProfileIsOfferedOnScreen()
    {
        Authorize();
        RegisterServices(withStrategies: true);

        var cut = Render<ProcioneMGR.Components.Pages.Bot>();

        Assert.DoesNotContain("scalping", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
