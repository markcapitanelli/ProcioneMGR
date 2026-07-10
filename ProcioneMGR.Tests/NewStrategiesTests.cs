using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica che le nuove strategie (MACD Trend, Bollinger Mean Reversion, Momentum)
/// — e quelle esistenti — generino trade su dati reali BTC/USDT 1h. Saltato se il DB
/// non è disponibile.
/// </summary>
public class NewStrategiesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("MacdTrend")]
    [InlineData("BollingerMeanReversion")]
    [InlineData("Momentum")]
    [InlineData("EmaCross")]
    [InlineData("RsiOversold")]
    [InlineData("Supertrend")]
    [InlineData("Stochastic")]
    [InlineData("VwapReversion")]
    public async Task Strategy_OnRealData_GeneratesTrades(string strategyName)
    {
        if (!RealMarketDb.IsAvailable())
        {
            output.WriteLine("DB procionemgr non disponibile: test saltato.");
            return;
        }

        await using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IStrategyFactory>();
        var engine = provider.GetRequiredService<IBacktestEngine>();

        // Parametri = default delle ParameterDefinitions della strategia.
        var proto = factory.Prototypes.First(p => p.Name == strategyName);
        var parameters = proto.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);

        var config = new BacktestConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "1h",
            From = DateTime.UtcNow.AddDays(-120),
            To = DateTime.UtcNow,
            InitialCapital = 10_000m,
            PositionSizePercent = 20m,
            FeePercent = 0.1m,
            StrategyName = strategyName,
            StrategyParameters = parameters,
        };

        var result = await engine.RunBacktestAsync(config, CancellationToken.None);

        output.WriteLine($"{proto.DisplayName}: candele={result.CandlesEvaluated}, trade={result.TotalTrades}, " +
                         $"return={result.TotalReturnPercent:F2}%, winRate={result.WinRate:F1}%, maxDD={result.MaxDrawdownPercent:F2}%");

        if (result.CandlesEvaluated == 0)
        {
            output.WriteLine("Nessuna candela nel range: test saltato.");
            return;
        }

        Assert.True(result.CandlesEvaluated > 0);
        Assert.True(result.TotalTrades > 0, $"{strategyName} non ha generato trade.");
        Assert.True(result.FinalCapital > 0m);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(RealMarketDb.ConnectionString));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        return services.BuildServiceProvider();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
