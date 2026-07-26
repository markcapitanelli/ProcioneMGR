using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 2 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Prima di questo guard tutti i limiti di
/// rischio a runtime erano scalari e ciechi alla correlazione: tetto sulla singola posizione, tetto
/// sull'esposizione della corsia, numero massimo di posizioni. Tre corsie long su tre altcoin che
/// si muovono con BTC contavano quindi come tre scommesse indipendenti, mentre erano una sola
/// scommessa di taglia tripla — ed è nei crash, quando le correlazioni crypto tendono a 1, che la
/// differenza si paga tutta insieme.
///
/// I due comportamenti che questi test difendono, perché sono quelli che si sbagliano per primi:
/// <b>il segno</b> (una copertura genuina non deve essere punita come se aggiungesse rischio) e
/// <b>il fail-safe verso il permesso</b> (senza storico non si blocca al buio).
/// </summary>
[Collection("Postgres")]
public sealed class CorrelatedExposureTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public CorrelatedExposureTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> DbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static CorrelatedExposureGuard Guard(
        IDbContextFactory<ApplicationDbContext> db, CorrelatedExposureOptions options) =>
        new(db, options.AsMonitor(), NullLogger<CorrelatedExposureGuard>.Instance);

    private static CorrelatedExposureOptions Enabled(decimal maxPercent = 50m) => new()
    {
        Enabled = true,
        MaxCorrelatedExposurePercent = maxPercent,
        Timeframe = "1h",
        LookbackBars = 720,
        MinOverlappingBars = 100,
        MinCorrelationToCount = 0.5d,
    };

    /// <summary>
    /// Semina le serie di prezzo dei simboli dati con la correlazione voluta: a ogni barra una
    /// passeggiata comune (il "mercato") più un rumore idiosincratico per simbolo, dosati da
    /// <paramref name="commonWeight"/> (1 = simboli gemelli, 0 = indipendenti). Tutti i simboli si
    /// generano in un solo passaggio, sulle STESSE barre: seminarne uno due volte violerebbe
    /// l'indice unico (Symbol, Timeframe, TimestampUtc).
    /// </summary>
    private static async Task SeedCorrelatedSeriesAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, double commonWeight,
        params string[] symbols)
    {
        const int bars = 400;
        var rng = new Random(20260725);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var prices = symbols.ToDictionary(s => s, _ => 100m);
        var rows = new List<OhlcvData>(bars * symbols.Length);

        for (var i = 0; i < bars; i++)
        {
            var common = Gaussian(rng);
            var t = start.AddHours(i);
            foreach (var symbol in symbols)
            {
                var ret = commonWeight * common + (1 - commonWeight) * Gaussian(rng);
                prices[symbol] *= (decimal)Math.Exp(ret * 0.01);
                rows.Add(Candle(symbol, t, prices[symbol]));
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.OhlcvData.AddRange(rows);
        await db.SaveChangesAsync();

        static double Gaussian(Random r) =>
            Math.Sqrt(-2.0 * Math.Log(1.0 - r.NextDouble())) * Math.Cos(2.0 * Math.PI * r.NextDouble());

        static OhlcvData Candle(string symbol, DateTime t, decimal close) => new()
        {
            Symbol = symbol, Timeframe = "1h", TimestampUtc = t,
            Open = close, High = close, Low = close, Close = close, Volume = 1m,
        };
    }

    private static async Task SeedLaneAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int laneId, decimal capital,
        string? positionSymbol = null, OrderSide side = OrderSide.Buy, decimal notional = 0m)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TradingEngineStates.Add(new TradingEngineState
        {
            LaneId = laneId, Mode = TradingMode.Testnet, TotalCapital = capital,
            AvailableCapital = capital, Symbol = positionSymbol ?? "BTC/USDT", ExchangeName = "Binance",
        });

        if (positionSymbol is not null && notional > 0m)
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = laneId, Symbol = positionSymbol, Side = side,
                EntryPrice = 100m, CurrentPrice = 100m, Quantity = notional / 100m,
                OpenedAtUtc = DateTime.UtcNow, OpenedInMode = TradingMode.Testnet,
            });
        }
        await db.SaveChangesAsync();
    }

    // --- Il caso del PDF: più corsie, stessa scommessa -----------------------------------------

    [Fact]
    public async Task ThreeCorrelatedLongs_AggregateBeyondLimit_AreCaught()
    {
        // Due corsie già long su simboli fortemente correlati col candidato, 30.000 ciascuna su un
        // capitale aggregato di 90.000 (limite 50% = 45.000). Ognuna sta dentro OGNI limite scalare
        // esistente; insieme sono una scommessa da ~90.000 sullo stesso fattore.
        var db = await DbAsync();
        await SeedCorrelatedSeriesAsync(db, commonWeight: 0.95, "ATOM/USDT", "DOGE/USDT", "SHIB/USDT");
        await SeedLaneAsync(db, laneId: 0, capital: 30_000m, positionSymbol: "DOGE/USDT", notional: 30_000m);
        await SeedLaneAsync(db, laneId: 1, capital: 30_000m, positionSymbol: "SHIB/USDT", notional: 30_000m);
        await SeedLaneAsync(db, laneId: 2, capital: 30_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 2, "ATOM/USDT", OrderSide.Buy, candidateNotional: 30_000m, TradingMode.Testnet);

        Assert.True(assessment.IsMeasurable);
        Assert.Equal(45_000m, assessment.LimitNotional);
        Assert.True(assessment.Exceeds,
            $"esposizione correlata {assessment.CorrelatedNotional:N0} doveva superare {assessment.LimitNotional:N0}");
        Assert.Equal(2, assessment.Contributions.Count);
        Assert.All(assessment.Contributions, c => Assert.True(c.Correlation > 0.5));
    }

    [Fact]
    public async Task UncorrelatedPositions_DoNotBlock()
    {
        // Stessi nozionali del test sopra, ma i simboli non si muovono insieme: sono davvero
        // scommesse diverse e il limite non deve intervenire.
        var db = await DbAsync();
        await SeedCorrelatedSeriesAsync(db, commonWeight: 0.0, "ATOM/USDT", "XMR/USDT");
        await SeedLaneAsync(db, laneId: 0, capital: 45_000m, positionSymbol: "XMR/USDT", notional: 30_000m);
        await SeedLaneAsync(db, laneId: 1, capital: 45_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 1, "ATOM/USDT", OrderSide.Buy, candidateNotional: 30_000m, TradingMode.Testnet);

        Assert.True(assessment.IsMeasurable);
        Assert.False(assessment.Exceeds);
        Assert.Empty(assessment.Contributions);   // sotto la soglia di correlazione: indipendenti
    }

    // --- Il segno: una copertura non è un rischio ----------------------------------------------

    [Fact]
    public async Task CorrelatedShort_OffsetsInsteadOfAdding()
    {
        // LA TRAPPOLA: sommando i nozionali in valore assoluto, un long e uno short su asset
        // gemelli risulterebbero il DOPPIO del rischio mentre sono, in sostanza, una copertura.
        // Un limite che punisce le coperture spinge verso il portafoglio più rischioso: è peggio
        // che non avere limite.
        var db = await DbAsync();
        await SeedCorrelatedSeriesAsync(db, commonWeight: 0.95, "ATOM/USDT", "DOGE/USDT");
        await SeedLaneAsync(db, laneId: 0, capital: 30_000m,
            positionSymbol: "DOGE/USDT", side: OrderSide.Sell, notional: 30_000m);
        await SeedLaneAsync(db, laneId: 1, capital: 30_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 1, "ATOM/USDT", OrderSide.Buy, candidateNotional: 30_000m, TradingMode.Testnet);

        Assert.True(assessment.IsMeasurable);
        Assert.False(assessment.Exceeds);
        Assert.True(Math.Abs(assessment.CorrelatedNotional) < 30_000m,
            "lo short correlato deve ridurre l'esposizione netta, non aumentarla");
    }

    [Fact]
    public async Task SamePositionSymbol_CountsFully_WithoutEstimating()
    {
        // Stesso simbolo = ρ 1 per definizione: nessuno storico richiesto, nessuna stima possibile
        // da sbagliare. 40k già aperti + 40k candidati contro un limite di 40k (50% di 80k).
        var db = await DbAsync();
        await SeedLaneAsync(db, laneId: 0, capital: 40_000m, positionSymbol: "BTC/USDT", notional: 40_000m);
        await SeedLaneAsync(db, laneId: 1, capital: 40_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 1, "BTC/USDT", OrderSide.Buy, candidateNotional: 40_000m, TradingMode.Testnet);

        Assert.True(assessment.Exceeds);
        Assert.Equal(80_000m, assessment.CorrelatedNotional);
        Assert.Equal(1d, Assert.Single(assessment.Contributions).Correlation);
    }

    // --- Fail-safe e isolamento ----------------------------------------------------------------

    [Fact]
    public async Task WithoutPriceHistory_DoesNotBlock()
    {
        // Nessuna candela: la correlazione non è stimabile. Bloccare al buio fermerebbe
        // l'operatività per un buco di dati — un guasto peggiore del rischio che si evita.
        var db = await DbAsync();
        await SeedLaneAsync(db, laneId: 0, capital: 50_000m, positionSymbol: "DOGE/USDT", notional: 40_000m);
        await SeedLaneAsync(db, laneId: 1, capital: 50_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 1, "ATOM/USDT", OrderSide.Buy, candidateNotional: 40_000m, TradingMode.Testnet);

        Assert.True(assessment.IsMeasurable);      // capitale noto ⇒ la misura esiste...
        Assert.Empty(assessment.Contributions);    // ...ma la posizione non stimabile è esclusa
        Assert.False(assessment.Exceeds);
    }

    [Fact]
    public async Task Disabled_IsInert()
    {
        var db = await DbAsync();
        await SeedLaneAsync(db, laneId: 0, capital: 40_000m, positionSymbol: "BTC/USDT", notional: 40_000m);

        var assessment = await Guard(db, new CorrelatedExposureOptions()).AssessAsync(
            laneId: 1, "BTC/USDT", OrderSide.Buy, 40_000m, TradingMode.Testnet);

        Assert.False(assessment.IsMeasurable);
        Assert.False(assessment.Exceeds);
    }

    [Fact]
    public async Task PaperPositions_DoNotConstrainTestnet()
    {
        // Una posizione simulata non è un'esposizione reale: stesso discriminatore anti-mescolamento
        // già usato al caricamento delle corsie.
        var db = await DbAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.TradingEngineStates.Add(new TradingEngineState
            {
                LaneId = 0, Mode = TradingMode.Paper, TotalCapital = 1_000_000m,
                Symbol = "BTC/USDT", ExchangeName = "Binance",
            });
            ctx.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0, Symbol = "BTC/USDT", Side = OrderSide.Buy, EntryPrice = 100m,
                CurrentPrice = 100m, Quantity = 9_000m, OpenedAtUtc = DateTime.UtcNow,
                OpenedInMode = TradingMode.Paper,
            });
            await ctx.SaveChangesAsync();
        }
        await SeedLaneAsync(db, laneId: 1, capital: 40_000m);

        var assessment = await Guard(db, Enabled()).AssessAsync(
            laneId: 1, "BTC/USDT", OrderSide.Buy, candidateNotional: 10_000m, TradingMode.Testnet);

        Assert.True(assessment.IsMeasurable);
        Assert.Empty(assessment.Contributions);
        Assert.Equal(40_000m, assessment.AggregateCapital);   // il capitale Paper non entra
        Assert.False(assessment.Exceeds);
    }

    [Fact]
    public void AlignedLogReturns_JoinOnTimestamp_NotOnPosition()
    {
        // Due serie con buchi diversi: accostarle per posizione correlerebbe istanti diversi,
        // producendo un numero che sembra una misura e non lo è.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new List<(DateTime, decimal)>
        {
            (t0, 100m), (t0.AddHours(1), 110m), (t0.AddHours(2), 121m), (t0.AddHours(3), 133.1m),
        };
        var b = new List<(DateTime, decimal)>
        {
            (t0, 50m), (t0.AddHours(2), 60.5m), (t0.AddHours(3), 66.55m),   // manca la barra 1
        };

        var (x, y) = CorrelatedExposureGuard.AlignedLogReturns(a, b);

        // Barre in comune: 0, 2, 3 ⇒ due rendimenti, non tre.
        Assert.Equal(2, x.Count);
        Assert.Equal(2, y.Count);
        Assert.All(x, v => Assert.True(v > 0));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
