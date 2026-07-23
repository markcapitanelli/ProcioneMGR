using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [F4 roadmap frontiere-profitto] Accumulo liquidazioni: parsing dello stream pubblico
/// (!forceOrder@arr), aggregazione per (ticker, ora, lato) e flush IDEMPOTENTE su
/// SentimentMetricPoints. Il dato non è ricostruibile a posteriori: il contratto qui fissato è
/// che nessun percorso (payload sporchi, doppi flush, riavvii) possa corromperlo in silenzio.
/// </summary>
[Collection("Postgres")]
public sealed class LiquidationAccumulationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LiquidationAccumulationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // Payload reale (forma documentata Binance USDT-M): long liquidato = l'exchange VENDE.
    private const string LongLiquidationJson = """
        {"e":"forceOrder","E":1768000000000,"o":{"s":"BTCUSDT","S":"SELL","o":"LIMIT","f":"IOC","q":"0.5","p":"50000","ap":"49900","X":"FILLED","l":"0.5","z":"0.5","T":1768000000000}}
        """;

    // ------------------------------------------------------------------ Mapper

    [Fact]
    public void Parse_LongLiquidation_UsesAvgPriceAndBaseTicker()
    {
        var e = BinanceLiquidationMapper.Parse(LongLiquidationJson);

        Assert.NotNull(e);
        Assert.Equal("BTC", e!.BaseTicker);
        Assert.True(e.LongLiquidated);                       // SELL = long liquidato
        Assert.Equal(0.5m * 49900m, e.Notional);             // quantità × prezzo MEDIO (ap)
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1768000000000).UtcDateTime, e.TimestampUtc);
    }

    [Fact]
    public void Parse_ShortLiquidation_NonUsdt_Malformed_AndOtherEvents()
    {
        var shortLiq = BinanceLiquidationMapper.Parse(
            """{"e":"forceOrder","o":{"s":"ETHUSDT","S":"BUY","q":"2","p":"3000","ap":"0","T":1768000000000}}""");
        Assert.NotNull(shortLiq);
        Assert.False(shortLiq!.LongLiquidated);
        Assert.Equal(2m * 3000m, shortLiq.Notional);          // ap=0 → fallback sul prezzo ordine

        Assert.Null(BinanceLiquidationMapper.Parse(
            """{"e":"forceOrder","o":{"s":"BTCUSDC","S":"SELL","q":"1","p":"50000","T":1}}"""));   // non /USDT
        Assert.Null(BinanceLiquidationMapper.Parse("""{"e":"aggTrade","p":"1"}"""));               // altro evento
        Assert.Null(BinanceLiquidationMapper.Parse("{ rotto"));                                    // malformato: mai eccezioni
        Assert.Null(BinanceLiquidationMapper.Parse(
            """{"e":"forceOrder","o":{"s":"BTCUSDT","S":"SELL","q":"0","p":"50000","T":1}}"""));   // quantità nulla
    }

    // ------------------------------------------------------------------ Aggregatore

    [Fact]
    public void Aggregator_BucketsPerTickerHourAndSide_AndPrunes()
    {
        var agg = new LiquidationAggregator();
        var h0 = new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc);

        agg.Add(new LiquidationEvent(h0.AddMinutes(5), "BTC", LongLiquidated: true, Notional: 1000m));
        agg.Add(new LiquidationEvent(h0.AddMinutes(40), "BTC", LongLiquidated: true, Notional: 500m));
        agg.Add(new LiquidationEvent(h0.AddMinutes(41), "BTC", LongLiquidated: false, Notional: 200m));
        agg.Add(new LiquidationEvent(h0.AddMinutes(10), "ETH", LongLiquidated: true, Notional: 300m));
        agg.Add(new LiquidationEvent(h0.AddHours(1).AddMinutes(1), "BTC", LongLiquidated: true, Notional: 700m));

        var snap = agg.Snapshot();
        Assert.Equal(3, snap.Count);   // (BTC,h0), (ETH,h0), (BTC,h1)

        var btcH0 = snap.Single(b => b.BaseTicker == "BTC" && b.HourUtc == h0);
        Assert.Equal(1500m, btcH0.LongNotional);
        Assert.Equal(2, btcH0.LongCount);
        Assert.Equal(200m, btcH0.ShortNotional);
        Assert.Equal(1, btcH0.ShortCount);

        agg.PruneBefore(h0.AddHours(1));
        Assert.Single(agg.Snapshot());  // resta solo (BTC, h1)
    }

    // ------------------------------------------------------------------ Worker: flush idempotente

    private sealed class NeverConnectFactory : IWebSocketTransportFactory
    {
        public IWebSocketTransport Create() => throw new InvalidOperationException("il test non apre canali");
    }

    private async Task<(LiquidationSyncWorker Worker, IDbContextFactory<ApplicationDbContext> Db)> BuildWorkerAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService>(new PassthroughEncryption());
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var worker = new LiquidationSyncWorker(
            new NeverConnectFactory(), dbFactory,
            Options.Create(new LiquidationsOptions()), NullLogger<LiquidationSyncWorker>.Instance);
        return (worker, dbFactory);
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task Flush_WritesHourTotals_AndSecondFlushUpdatesInsteadOfDuplicating()
    {
        var (worker, dbFactory) = await BuildWorkerAsync();

        worker.ProcessMessage(LongLiquidationJson);
        await worker.FlushAsync(CancellationToken.None);

        var eventTs = DateTimeOffset.FromUnixTimeMilliseconds(1768000000000).UtcDateTime;
        var hour = new DateTime(eventTs.Year, eventTs.Month, eventTs.Day, eventTs.Hour, 0, 0, DateTimeKind.Utc);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var notional = await db.SentimentMetricPoints.SingleAsync(p =>
                p.Source == SentimentMetricSources.BinanceLiquidations
                && p.Metric == SentimentMetrics.LongLiquidationNotional && p.Symbol == "BTC");
            Assert.Equal(0.5m * 49900m, notional.Value);
            Assert.Equal(hour, notional.TimestampUtc);
            // Niente righe di zeri: il lato short di quest'ora non è mai stato toccato.
            Assert.False(await db.SentimentMetricPoints.AnyAsync(p => p.Metric == SentimentMetrics.ShortLiquidationNotional));
        }

        // Secondo evento nella STESSA ora + secondo flush: la riga si AGGIORNA al nuovo totale.
        worker.ProcessMessage(LongLiquidationJson);
        await worker.FlushAsync(CancellationToken.None);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var rows = await db.SentimentMetricPoints
                .Where(p => p.Metric == SentimentMetrics.LongLiquidationNotional && p.Symbol == "BTC")
                .ToListAsync();
            var row = Assert.Single(rows);                    // upsert, non duplicato
            Assert.Equal(2 * 0.5m * 49900m, row.Value);       // totale del secchio, non delta

            var count = await db.SentimentMetricPoints.SingleAsync(p => p.Metric == SentimentMetrics.LongLiquidationCount);
            Assert.Equal(2m, count.Value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
