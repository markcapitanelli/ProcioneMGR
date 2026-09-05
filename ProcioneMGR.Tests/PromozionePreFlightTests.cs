using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] <b>La promozione a Testnet controlla le credenziali PRIMA di toccare la corsia, e
/// verifica il flatten invece di presumerlo.</b>
///
/// <para>Misurato sul database vivo: una sola credenziale testnet (Bitget) e sei corsie su Binance.
/// La sequenza chiudi→ferma→riavvia caricava le credenziali solo nel riavvio: con la promozione
/// automatica accesa, ogni corsia Binance matura sarebbe stata fermata, la sua sessione Paper
/// sovrascritta (PnL, picco, data di avvio) e poi lasciata ferma in Testnet per un errore che si
/// poteva prevedere leggendo una tabella. E <c>CloseAllPositionsAsync</c> è best-effort: una
/// chiusura rifiutata dall'exchange non lancia, e il cambio di modalità che seguiva cancellava la
/// riga di una posizione reale senza alcun ordine.</para>
/// </summary>
public sealed class PromozionePreFlightTests
{
    private sealed class FakeEngine : ITradingEngine
    {
        public int LaneId => 3;
        public TradingMode Mode { get; set; } = TradingMode.Paper;
        public string ExchangeName { get; set; } = "Binance";
        public List<OpenPosition> Residue { get; set; } = [];
        public List<string> Calls { get; } = [];

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { Mode = Mode, IsRunning = true, Symbol = "DOT/USDT", ExchangeName = ExchangeName });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) { Calls.Add($"Start:{mode}"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Calls.Add("Stop"); return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) { Calls.Add("Emergency"); return Task.CompletedTask; }
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(Residue);
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) { Calls.Add("CloseAll"); return Task.CompletedTask; }
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCredentials(params DecryptedExchangeCredential[] rows) : IExchangeCredentialReader
    {
        public Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DecryptedExchangeCredential>>(rows);
        public Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)
            => Task.FromResult(rows.FirstOrDefault(r => r.ExchangeName == exchange && r.IsTestnet == testnet));
        public Task<(int Total, int Unreadable)> CountUnreadableAsync(CancellationToken ct = default) => Task.FromResult((rows.Length, 0));
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("il database non deve essere toccato in questi test");
    }

    private sealed class StaticOptions(PromotionEvaluatorOptions value) : IOptionsMonitor<PromotionEvaluatorOptions>
    {
        public PromotionEvaluatorOptions CurrentValue { get; } = value;
        public PromotionEvaluatorOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PromotionEvaluatorOptions, string> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private static DecryptedExchangeCredential Credential(ExchangeName exchange, bool testnet, bool decryptable = true)
        => new(1, exchange, $"{exchange} {(testnet ? "Test" : "Main")}", testnet, DateTime.UtcNow, decryptable, decryptable ? "k" : null, decryptable ? "s" : null, null);

    private static (LanePromoter Promoter, FakeEngine Engine) Build(FakeEngine engine, IExchangeCredentialReader? credentials)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITradingEngine>(3, engine);
        if (credentials is not null) services.AddSingleton(credentials);
        var promoter = new LanePromoter(
            services.BuildServiceProvider(), new ThrowingDbFactory(),
            new StaticOptions(new PromotionEvaluatorOptions { NotifyOnPromotion = false }),
            NullLogger<LanePromoter>.Instance);
        return (promoter, engine);
    }

    [Fact]
    public async Task SenzaCredenzialiTestnet_LaCorsiaNonVieneToccata()
    {
        var (promoter, engine) = Build(new FakeEngine(), new FakeCredentials(Credential(ExchangeName.Bitget, testnet: true)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => promoter.PromoteLaneAsync(3, TradingMode.Testnet, "test"));

        Assert.Contains("PRIMA di toccare", ex.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task CredenzialeIndecifrabile_LaCorsiaNonVieneToccata()
    {
        var (promoter, engine) = Build(new FakeEngine(), new FakeCredentials(Credential(ExchangeName.Binance, testnet: true, decryptable: false)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => promoter.PromoteLaneAsync(3, TradingMode.Testnet, "test"));

        Assert.Contains("non si decifra", ex.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task FlattenIncompleto_NessunCambioDiModalita()
    {
        var engine = new FakeEngine { Mode = TradingMode.Testnet, Residue = [new OpenPosition { Symbol = "DOT/USDT" }] };
        var (promoter, _) = Build(engine, new FakeCredentials(Credential(ExchangeName.Binance, testnet: true)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => promoter.PromoteLaneAsync(3, TradingMode.Paper, "retrocessione"));

        Assert.Contains("ancora aperte dopo il flatten", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["CloseAll"], engine.Calls);
    }

    [Fact]
    public async Task ConCredenzialiEFlattenPulito_ChiudeFermaERiavvia()
    {
        var (promoter, engine) = Build(new FakeEngine(), new FakeCredentials(Credential(ExchangeName.Binance, testnet: true)));

        await promoter.PromoteLaneAsync(3, TradingMode.Testnet, "matura");

        Assert.Equal(["CloseAll", "Stop", "Start:Testnet"], engine.Calls);
    }

    /// <summary>Senza il lettore registrato (vecchi harness) il pre-flight si salta e vale la sequenza storica: nessun nuovo requisito nascosto.</summary>
    [Fact]
    public async Task SenzaLettoreDiCredenziali_LaSequenzaStoricaResta()
    {
        var (promoter, engine) = Build(new FakeEngine(), credentials: null);

        await promoter.PromoteLaneAsync(3, TradingMode.Testnet, "test");

        Assert.Equal(["CloseAll", "Stop", "Start:Testnet"], engine.Calls);
    }

    [Fact]
    public async Task LiveRestaVietato_PrimaDiQualunqueControllo()
    {
        var (promoter, engine) = Build(new FakeEngine(), new FakeCredentials());

        await Assert.ThrowsAsync<InvalidOperationException>(() => promoter.PromoteLaneAsync(3, TradingMode.Live, "mai"));
        Assert.Empty(engine.Calls);
    }
}
