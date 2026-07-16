using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Tests.Infrastructure;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 2.1 — stress dell'ingestione OHLCV con un exchange FINTO deterministico (nessuna
/// rete): N simboli in parallelo su Postgres reale (Testcontainers), migliaia di candele 1m per
/// simbolo, con misure di tempo/throughput/allocazioni riportate nell'output del test. Verifica
/// funzionale: conteggi esatti, idempotenza dell'upsert (re-ingestione = zero duplicati),
/// nessuna ritenzione di memoria anomala a fine corsa.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Stress")]
public sealed class AuditStressIngestionTests
{
    private readonly string _connString;
    private readonly ITestOutputHelper _output;

    public AuditStressIngestionTests(PostgresFixture pg, ITestOutputHelper output)
    {
        _connString = pg.CreateDatabase();
        _output = output;
    }

    /// <summary>
    /// Exchange finto: genera candele 1m sintetiche deterministiche (random walk con seed dal
    /// simbolo), 1500 per richiesta, senza I/O. I metodi di trading non servono all'ingestione.
    /// </summary>
    private sealed class FakeExchangeClient : IExchangeClient
    {
        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1500;

        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)
        {
            var tfMs = Timeframes.ToMilliseconds(timeframe);
            var count = Math.Min(limit, MaxCandlesPerRequest);
            var list = new List<Ohlcv>(count);
            for (var i = 0; i < count; i++)
            {
                var ts = since + i * tfMs;
                // Prezzo deterministico funzione (simbolo, timestamp): re-fetch => stessi valori.
                var seed = (StringComparer.Ordinal.GetHashCode(symbol) * 397) ^ ts.GetHashCode();
                var price = 100m + Math.Abs(seed % 1000) / 100m + (decimal)(new Random(seed).NextDouble() - 0.5);
                list.Add(new Ohlcv(
                    DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                    price, price + 0.2m, price - 0.2m, price + 0.05m,
                    50m + Math.Abs(seed % 500)));
            }
            return Task.FromResult(list);
        }

        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeExchangeFactory : IExchangeClientFactory
    {
        private readonly FakeExchangeClient _client = new();
        public IExchangeClient Create(ExchangeName exchange) => _client;
        public IExchangeClient Create(string exchangeName) => _client;
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        return dbFactory;
    }

    [Fact]
    public async Task MassiveParallelIngestion_10Symbols_30DaysOf1m_CountsExact_NoLeak()
    {
        var dbFactory = await BuildDbAsync();
        var service = new OhlcvIngestionService(
            new FakeExchangeFactory(), dbFactory, NullLogger<OhlcvIngestionService>.Instance);

        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(30); // 43.200 candele 1m per simbolo
        var symbols = Enumerable.Range(0, 10).Select(i => $"SYM{i}/USDT").ToList();
        // L'intervallo del servizio è inclusivo su ENTRAMBI gli estremi (c.TimestampMs <= toMs):
        // la candela che apre esattamente a 'to' è inclusa, quindi 30 giorni di 1m = 43.200 + 1.
        const long expectedPerSymbol = 30L * 24 * 60 + 1;

        var allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();

        var results = await Task.WhenAll(symbols.Select(s =>
            service.IngestHistoricalDataAsync("Binance", s, "1m", from, to)));

        sw.Stop();
        var allocAfter = GC.GetTotalAllocatedBytes();

        // --- Correttezza: conteggi esatti per simbolo, sia riportati che persistiti ---
        foreach (var r in results)
        {
            Assert.False(r.Cancelled);
        }
        long totalRows;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            totalRows = await db.OhlcvData.LongCountAsync();
            foreach (var s in symbols)
            {
                var n = await db.OhlcvData.LongCountAsync(c => c.Symbol == s && c.Timeframe == "1m");
                Assert.Equal(expectedPerSymbol, n);
            }
        }
        Assert.Equal(expectedPerSymbol * symbols.Count, totalRows);

        // --- Metriche (riportate, non asserite: variano con la macchina) ---
        var totalIngested = results.Sum(r => r.CandlesProcessed);
        var rowsPerSec = totalRows / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        var allocatedMb = (allocAfter - allocBefore) / 1024.0 / 1024.0;
        _output.WriteLine($"Ingestione: {totalRows:N0} righe in {sw.Elapsed.TotalSeconds:F1}s = {rowsPerSec:N0} righe/s");
        _output.WriteLine($"Allocazioni gestite: {allocatedMb:N0} MB ({allocatedMb * 1024 / totalRows:F2} KB/riga)");
        _output.WriteLine($"Working set: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024:N0} MB");
        _output.WriteLine($"Riportate ingerite: {totalIngested:N0}");

        // --- Nessuna ritenzione anomala: dopo full GC l'heap non deve trattenere il dataset ---
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedMb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
        _output.WriteLine($"Heap gestito dopo GC: {retainedMb:N0} MB");
        Assert.True(retainedMb < 500, $"Ritenzione sospetta: {retainedMb:N0} MB ancora vivi dopo full GC");
    }

    [Fact]
    public async Task Reingestion_SameRange_IsIdempotent_NoDuplicates()
    {
        var dbFactory = await BuildDbAsync();
        var service = new OhlcvIngestionService(
            new FakeExchangeFactory(), dbFactory, NullLogger<OhlcvIngestionService>.Instance);

        var from = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(2); // 2.880 candele 1m
        const string symbol = "IDEM/USDT";

        await service.IngestHistoricalDataAsync("Binance", symbol, "1m", from, to);
        long afterFirst;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            afterFirst = await db.OhlcvData.LongCountAsync(c => c.Symbol == symbol);
        }

        // Seconda passata sullo STESSO intervallo: l'upsert deve aggiornare, mai duplicare.
        await service.IngestHistoricalDataAsync("Binance", symbol, "1m", from, to);
        await using (var db2 = await dbFactory.CreateDbContextAsync())
        {
            var afterSecond = await db2.OhlcvData.LongCountAsync(c => c.Symbol == symbol);
            Assert.Equal(afterFirst, afterSecond);

            // Nessun timestamp duplicato (l'indice univoco è l'ultima difesa; qui verifichiamo i dati).
            var dupes = await db2.OhlcvData.Where(c => c.Symbol == symbol)
                .GroupBy(c => c.TimestampUtc).Where(g => g.Count() > 1).CountAsync();
            Assert.Equal(0, dupes);
        }
    }
}
