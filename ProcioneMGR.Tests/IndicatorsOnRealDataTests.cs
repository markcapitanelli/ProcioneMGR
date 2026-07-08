using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test richiesto dallo spec: calcola gli indicatori su dati reali BTC/USDT 1h
/// presenti nel DB dell'app (app.db) e verifica invarianti strutturali.
/// Se il DB o i dati non sono disponibili, il test viene saltato (non fallisce).
/// </summary>
public class IndicatorsOnRealDataTests(ITestOutputHelper output)
{
    private readonly TechnicalIndicatorsService _svc = new();

    [Fact]
    public async Task Indicators_On_Real_BtcUsdt_RespectInvariants()
    {
        var dbPath = FindAppDb();
        if (dbPath is null)
        {
            output.WriteLine("app.db non trovato: test saltato.");
            return;
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"DataSource={dbPath};Mode=ReadOnly;Cache=Shared")
            .Options;

        await using var db = new ApplicationDbContext(options, new PassthroughEncryption());

        var closes = await db.OhlcvData
            .Where(c => c.Symbol == "BTC/USDT" && c.Timeframe == "1h")
            .OrderBy(c => c.TimestampUtc)
            .Select(c => c.Close)
            .ToListAsync();

        output.WriteLine($"Candele BTC/USDT 1h caricate dal DB: {closes.Count}");
        if (closes.Count < 30)
        {
            output.WriteLine("Dati insufficienti (<30): test saltato.");
            return;
        }

        // RSI(14): warm-up null + valori in [0,100]
        var rsi = await _svc.CalculateRsiAsync(closes, 14);
        Assert.Equal(closes.Count, rsi.Count);
        for (var i = 0; i < 14; i++) Assert.Null(rsi[i]);
        Assert.NotNull(rsi[14]);
        foreach (var v in rsi.Where(v => v.HasValue))
        {
            Assert.InRange(v!.Value, 0m, 100m);
        }
        output.WriteLine($"RSI[14]={rsi[14]:F2}  RSI[last]={rsi[^1]:F2}");

        // EMA(20): prima valorizzazione all'indice 19
        var ema20 = await _svc.CalculateEmaAsync(closes, 20);
        Assert.Null(ema20[18]);
        Assert.NotNull(ema20[19]);
        // l'EMA deve restare dentro il range dei prezzi osservati
        Assert.InRange(ema20[^1]!.Value, closes.Min(), closes.Max());
        output.WriteLine($"EMA20[last]={ema20[^1]:F2}");

        // Bollinger(20): upper >= middle >= lower
        var (upper, middle, lower) = await _svc.CalculateBollingerAsync(closes, 20, 2.0m);
        for (var i = 0; i < closes.Count; i++)
        {
            if (middle[i].HasValue)
            {
                Assert.True(upper[i]!.Value >= middle[i]!.Value);
                Assert.True(middle[i]!.Value >= lower[i]!.Value);
            }
        }

        // MACD(12,26,9): histogram == macd - signal
        var (macd, signal, hist) = await _svc.CalculateMacdAsync(closes, 12, 26, 9);
        for (var i = 0; i < closes.Count; i++)
        {
            if (macd[i].HasValue && signal[i].HasValue)
            {
                Assert.True(Math.Abs((double)(hist[i]!.Value - (macd[i]!.Value - signal[i]!.Value))) < 1e-10);
            }
        }
        output.WriteLine($"MACD[last]={macd[^1]:F2} Signal[last]={signal[^1]:F2} Hist[last]={hist[^1]:F2}");
    }

    /// <summary>Risale le cartelle dalla bin/ del test fino a trovare ProcioneMGR/Data/app.db.</summary>
    private static string? FindAppDb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ProcioneMGR", "Data", "app.db");
            if (File.Exists(candidate))
            {
                return candidate;
            }
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
