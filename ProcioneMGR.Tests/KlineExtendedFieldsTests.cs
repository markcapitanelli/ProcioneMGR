using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T0.3 roadmap macchina-ricerca] Stop allo scarto dei campi klines.
///
/// Le klines Binance consegnano in ogni download quote volume (k[7]), numero di trade (k[8]) e
/// volume TAKER (k[9]/k[10] — l'order flow aggressivo), e il parsing leggeva solo k[0..5]. Questi
/// test fissano: il parsing completo, la robustezza a payload corti, e la regola di merge
/// dell'ingestione — un aggiornamento da una fonte che NON espone i campi (Bitget) non deve
/// cancellare quelli già raccolti da una che li espone.
/// </summary>
public class KlineExtendedFieldsTests
{
    // ---------------------------------------------------------------- parsing Binance

    private sealed class CannedHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private static BinanceClient Client(string json)
        => new(new HttpClient(new CannedHandler(json)) { BaseAddress = new Uri("https://example.invalid") },
               NullLogger<BinanceClient>.Instance);

    [Fact]
    public async Task Binance_FullKline_ExtractsTheFourPreviouslyDiscardedFields()
    {
        // Kline reale a 12 campi: [openTime, o, h, l, c, vol, closeTime, QUOTE, TRADES, TAKER_BASE, TAKER_QUOTE, ignore]
        const string json = """
            [[1700000000000,"100.0","101.0","99.0","100.5","1234.5",1700003599999,"123450.0",4567,"700.25","70025.0","0"]]
            """;

        var candles = await Client(json).FetchOhlcvAsync("BTC/USDT", "1h", 1700000000000, 1);

        var c = Assert.Single(candles);
        Assert.Equal(1234.5m, c.Volume);
        Assert.Equal(123450.0m, c.QuoteVolume);
        Assert.Equal(4567L, c.TradeCount);
        Assert.Equal(700.25m, c.TakerBuyVolume);
        Assert.Equal(70025.0m, c.TakerBuyQuoteVolume);
        Assert.True(c.TakerBuyVolume <= c.Volume, "invariante: il volume taker è un sottoinsieme del volume");
    }

    [Fact]
    public async Task Binance_ShortPayload_LeavesExtendedFieldsNull_InsteadOfCrashing()
    {
        // Payload anomalo a 6 campi: la candela resta valida, i campi estesi restano null.
        const string json = """
            [[1700000000000,"100.0","101.0","99.0","100.5","1234.5"]]
            """;

        var candles = await Client(json).FetchOhlcvAsync("BTC/USDT", "1h", 1700000000000, 1);

        var c = Assert.Single(candles);
        Assert.Equal(1234.5m, c.Volume);
        Assert.Null(c.QuoteVolume);
        Assert.Null(c.TradeCount);
        Assert.Null(c.TakerBuyVolume);
        Assert.Null(c.TakerBuyQuoteVolume);
    }
}

/// <summary>
/// La regola di MERGE dell'ingestione sui campi estesi, su DB vero: un update senza campi estesi
/// (fonte che non li espone, es. Bitget) non deve azzerare quelli già scritti.
/// </summary>
[Collection("Postgres")]
public sealed class KlineExtendedFieldsIngestionTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public KlineExtendedFieldsIngestionTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>Client fittizio: restituisce la lista di candele impostata, qualunque sia la richiesta.</summary>
    private sealed class ScriptedExchangeClient(List<Ohlcv> candles) : IExchangeClient
    {
        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)
        {
            // Prima chiamata: consegna le candele; successive: vuoto (fine dei dati).
            var batch = candles.Where(c => c.TimestampMs >= since).ToList();
            candles = [];
            return Task.FromResult(batch);
        }
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ScriptedFactory(IExchangeClient client) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => client;
        public IExchangeClient Create(string exchangeName) => client;
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> DbFactoryAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEnc>();
        _provider = services.BuildServiceProvider();
        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed class PassthroughEnc : ProcioneMGR.Services.Security.IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private static Ohlcv Candle(DateTime ts, decimal close, decimal? quoteVol = null, long? trades = null,
        decimal? takerBase = null, decimal? takerQuote = null)
        => new(ts, close, close, close, close, 1000m, quoteVol, trades, takerBase, takerQuote);

    [Fact]
    public async Task UpdateWithoutExtendedFields_DoesNotEraseThePreviouslyCollectedOnes()
    {
        var dbFactory = await DbFactoryAsync();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 1) Prima ingestione: fonte RICCA (con campi estesi).
        var rich = new ScriptedExchangeClient([Candle(t0, 100m, quoteVol: 100_000m, trades: 500, takerBase: 600m, takerQuote: 60_000m)]);
        var svc1 = new OhlcvIngestionService(new ScriptedFactory(rich), dbFactory, NullLogger<OhlcvIngestionService>.Instance);
        await svc1.IngestHistoricalDataAsync("Binance", "MRG/USDT", "1h", t0, t0.AddHours(1), null, CancellationToken.None);

        // 2) Seconda ingestione della STESSA candela da fonte POVERA (niente campi estesi, close diverso).
        var poor = new ScriptedExchangeClient([Candle(t0, 101m)]);
        var svc2 = new OhlcvIngestionService(new ScriptedFactory(poor), dbFactory, NullLogger<OhlcvIngestionService>.Instance);
        await svc2.IngestHistoricalDataAsync("Binance", "MRG/USDT", "1h", t0, t0.AddHours(1), null, CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.OhlcvData.SingleAsync(o => o.Symbol == "MRG/USDT");

        Assert.Equal(101m, row.Close);                    // l'update dei prezzi è passato
        Assert.Equal(100_000m, row.QuoteVolume);          // ma i campi estesi NON sono stati azzerati
        Assert.Equal(500L, row.TradeCount);
        Assert.Equal(600m, row.TakerBuyVolume);
        Assert.Equal(60_000m, row.TakerBuyQuoteVolume);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
