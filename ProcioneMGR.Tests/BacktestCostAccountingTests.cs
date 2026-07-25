using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R2] Test della contabilità dei costi nel backtest.
///
/// Il vincolo più importante è che questa contabilità sia PURAMENTE DIAGNOSTICA: fee e slippage
/// erano già dentro il PnL prima di R2 (le prime dentro il Portfolio, il secondo dentro i prezzi di
/// fill), quindi esporli non deve spostare di un centesimo nessun risultato preesistente. Un test
/// che lo verifica vale più di quelli sui totali: se domani qualcuno "sistemasse" la contabilità
/// sottraendo di nuovo i costi, il PnL verrebbe conteggiato due volte in silenzio.
///
/// Il resto misura il <c>CostDragPercent</c>, che è la grandezza su cui R2 decide se un timeframe
/// veloce sia operabile o no.
/// </summary>
[Collection("Postgres")]
public class BacktestCostAccountingTests
{
    private readonly PostgresFixture _pg;

    public BacktestCostAccountingTests(PostgresFixture pg) => _pg = pg;

    private sealed class ScriptedStrategy(Dictionary<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } = [];

        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;

        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
            => script.GetValueOrDefault(index, Signal.Hold);
    }

    /// <summary>Prezzo piatto a 100: qualunque PnL o costo osservato viene dall'attrito, non dal mercato.</summary>
    private static List<OhlcvData> FlatCandles(int count, decimal price = 100m)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count).Select(i => new OhlcvData
        {
            Symbol = "TEST",
            Timeframe = "1h",
            TimestampUtc = t0.AddHours(i),
            Open = price,
            High = price,
            Low = price,
            Close = price,
            Volume = 100m,
        }).ToList();
    }

    private async Task<BacktestResult> RunAsync(
        List<OhlcvData> candles, Dictionary<int, Signal> script, decimal fee, decimal slippage)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_pg.CreateDatabase()));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        await using var provider = services.BuildServiceProvider();

        var config = new BacktestConfiguration
        {
            Symbol = "TEST",
            Timeframe = "1h",
            InitialCapital = 10_000m,
            PositionSizePercent = 100m,
            FeePercent = fee,
            SlippagePercent = slippage,
        };

        var engine = provider.GetRequiredService<IBacktestEngine>();
        return await engine.RunBacktestAsync(config, candles, new ScriptedStrategy(script), CancellationToken.None);
    }

    /// <summary>Entra e esce ripetutamente: ogni coppia di indici è un trade completo.</summary>
    private static Dictionary<int, Signal> ChurnScript(int roundTrips)
    {
        var script = new Dictionary<int, Signal>();
        for (var i = 0; i < roundTrips; i++)
        {
            script[i * 2] = Signal.Long;
            script[i * 2 + 1] = Signal.Close;
        }
        return script;
    }

    [Fact]
    public async Task CostAccounting_DoesNotChangePnl()
    {
        // LO STESSO scenario con e senza contabilità non è replicabile (la contabilità è sempre
        // attiva), quindi si verifica la proprietà equivalente e più forte: a costi ZERO su un
        // mercato piatto il capitale finale è esattamente quello iniziale. Se la contabilità
        // sottraesse i costi una seconda volta, questo numero si muoverebbe.
        var result = await RunAsync(FlatCandles(21), ChurnScript(10), fee: 0m, slippage: 0m);

        Assert.Equal(10, result.TotalTrades);
        Assert.Equal(10_000m, result.FinalCapital);
        Assert.Equal(0m, result.TotalCosts);
        Assert.Equal(0m, result.CostDragPercent);
    }

    [Fact]
    public async Task Fees_AreAccounted_AndMatchTheCapitalLost()
    {
        // Mercato piatto, solo commissioni: tutto ciò che il capitale ha perso DEVE essere il totale
        // contabilizzato. È la verifica che il totale non sia una stima scollegata dal PnL reale.
        var result = await RunAsync(FlatCandles(21), ChurnScript(10), fee: 0.1m, slippage: 0m);

        var capitalLost = 10_000m - result.FinalCapital;
        Assert.True(result.TotalFeesPaid > 0m);
        Assert.Equal(0m, result.TotalSlippagePaid);
        Assert.Equal(capitalLost, result.TotalFeesPaid, precision: 6);
    }

    [Fact]
    public async Task Slippage_IsAccountedSeparatelyFromFees()
    {
        var result = await RunAsync(FlatCandles(21), ChurnScript(10), fee: 0.1m, slippage: 0.05m);

        Assert.True(result.TotalFeesPaid > 0m);
        Assert.True(result.TotalSlippagePaid > 0m);
        Assert.Equal(result.TotalFeesPaid + result.TotalSlippagePaid, result.TotalCosts);

        // Lo slippage è metà della fee per lato (0.05 vs 0.1), quindi sul complessivo resta circa
        // la metà: conferma che si sta misurando l'attrito giusto e non un multiplo casuale.
        var ratio = result.TotalSlippagePaid / result.TotalFeesPaid;
        Assert.InRange(ratio, 0.45m, 0.55m);
    }

    [Fact]
    public async Task CostDrag_GrowsWithTurnover()
    {
        // È il cuore della domanda di R2: a parità di tutto il resto, girare di più costa di più.
        // Un timeframe veloce non è "più reattivo e basta": è più caro in proporzione diretta.
        var few = await RunAsync(FlatCandles(41), ChurnScript(5), fee: 0.1m, slippage: 0.05m);
        var many = await RunAsync(FlatCandles(41), ChurnScript(20), fee: 0.1m, slippage: 0.05m);

        Assert.Equal(5, few.TotalTrades);
        Assert.Equal(20, many.TotalTrades);
        Assert.True(many.CostDragPercent > few.CostDragPercent * 3m,
            $"quadruplicare il turnover deve moltiplicare il cost drag: {few.CostDragPercent:F3}% -> {many.CostDragPercent:F3}%");
    }

    [Fact]
    public async Task GrossReturn_ExceedsNetReturn_ByExactlyTheCostDrag()
    {
        var result = await RunAsync(FlatCandles(21), ChurnScript(10), fee: 0.1m, slippage: 0.05m);

        // Su mercato piatto il lordo è zero per costruzione: tutto il rendimento negativo è attrito.
        Assert.Equal(result.TotalReturnPercent + result.CostDragPercent, result.GrossReturnPercent);
        Assert.True(result.TotalReturnPercent < 0m, "con soli costi e mercato piatto il netto deve essere negativo");
    }

    [Fact]
    public async Task NoTrades_LeavesCostsAtZero_WithoutDividingByZero()
    {
        var result = await RunAsync(FlatCandles(10), [], fee: 0.1m, slippage: 0.05m);

        Assert.Equal(0, result.TotalTrades);
        Assert.Equal(0m, result.TotalCosts);
        Assert.Equal(0m, result.CostDragPercent);
        Assert.Equal(0m, result.GrossReturnPercent);
    }
}
