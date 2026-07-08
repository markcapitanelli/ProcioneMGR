using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica end-to-end del motore di backtest su dati reali BTC/USDT 1h:
/// completamento senza errori, coerenza equity/candele, e DETERMINISMO
/// (stesso input -> stesso output). Saltato se il DB non e' disponibile.
/// </summary>
public class BacktestEngineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EmaCross_OnRealData_Completes_And_IsDeterministic()
    {
        var dbPath = FindAppDb();
        if (dbPath is null)
        {
            output.WriteLine("app.db non trovato: test saltato.");
            return;
        }

        await using var provider = BuildProvider(dbPath);

        // Pre-check: ci sono abbastanza candele?
        await using (var db = await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var count = await db.OhlcvData.CountAsync(c => c.Symbol == "BTC/USDT" && c.Timeframe == "1h");
            output.WriteLine($"Candele BTC/USDT 1h disponibili: {count}");
            if (count < 60)
            {
                output.WriteLine("Dati insufficienti: test saltato.");
                return;
            }
        }

        var config = new BacktestConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "1h",
            From = DateTime.UtcNow.AddDays(-60),
            To = DateTime.UtcNow,
            InitialCapital = 10_000m,
            PositionSizePercent = 20m,
            FeePercent = 0.1m,
            StrategyName = "EmaCross",
            StrategyParameters = new() { ["FastPeriod"] = 12m, ["SlowPeriod"] = 26m },
        };

        var engine1 = provider.GetRequiredService<IBacktestEngine>();
        var r1 = await engine1.RunBacktestAsync(config, CancellationToken.None);

        // completamento + coerenza
        Assert.True(r1.CandlesEvaluated > 0);
        Assert.Equal(r1.CandlesEvaluated, r1.EquityCurve.Count);
        Assert.Equal(r1.WinningTrades + r1.LosingTrades, r1.Trades.Count(t => t.Pnl != 0));
        Assert.True(r1.FinalCapital > 0m);

        output.WriteLine($"FinalCapital={r1.FinalCapital:F2}  Return={r1.TotalReturnPercent:F2}%  " +
                         $"Trades={r1.TotalTrades}  WinRate={r1.WinRate:F1}%  MaxDD={r1.MaxDrawdownPercent:F2}%");

        // DETERMINISMO: seconda esecuzione con un engine nuovo, stesso input.
        var engine2 = provider.GetRequiredService<IBacktestEngine>();
        var r2 = await engine2.RunBacktestAsync(config, CancellationToken.None);

        Assert.Equal(r1.FinalCapital, r2.FinalCapital);
        Assert.Equal(r1.TotalReturnPercent, r2.TotalReturnPercent);
        Assert.Equal(r1.TotalTrades, r2.TotalTrades);
        Assert.Equal(r1.MaxDrawdownPercent, r2.MaxDrawdownPercent);
        Assert.Equal(r1.EquityCurve.Count, r2.EquityCurve.Count);
        Assert.Equal(r1.EquityCurve[^1].Capital, r2.EquityCurve[^1].Capital);
        output.WriteLine("Determinismo: run1 == run2 ✓");
    }

    [Fact]
    public async Task EmptyRange_ReturnsInitialCapital_NoTrades()
    {
        var dbPath = FindAppDb();
        if (dbPath is null) return;

        await using var provider = BuildProvider(dbPath);
        var engine = provider.GetRequiredService<IBacktestEngine>();

        var config = new BacktestConfiguration
        {
            Symbol = "NONEXISTENT/PAIR",
            Timeframe = "1h",
            From = DateTime.UtcNow.AddDays(-5),
            To = DateTime.UtcNow,
            InitialCapital = 5000m,
            StrategyName = "EmaCross",
            StrategyParameters = new() { ["FastPeriod"] = 12m, ["SlowPeriod"] = 26m },
        };

        var r = await engine.RunBacktestAsync(config, CancellationToken.None);
        Assert.Equal(5000m, r.FinalCapital);
        Assert.Empty(r.Trades);
        Assert.Equal(0, r.TotalTrades);
    }

    private static ServiceProvider BuildProvider(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o =>
            o.UseSqlite($"DataSource={dbPath};Mode=ReadOnly;Cache=Shared"));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        return services.BuildServiceProvider();
    }

    private static string? FindAppDb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ProcioneMGR", "Data", "app.db");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
