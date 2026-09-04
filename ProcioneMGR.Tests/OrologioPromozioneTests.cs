using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] <b>La promozione giudica con lo stesso orologio del ritiro.</b>
///
/// <para>Il 2026-09-01 il proprietario ha spento <c>AutoPromoteToTestnet</c> dopo aver misurato che
/// la promozione contava l'osservazione come <c>now − StartedAtUtc</c> (8,73 gg sulla corsia 5)
/// mentre il ritiro usa il registro cumulato J8 (6,14 gg): 42 % di differenza sullo stesso oggetto
/// nello stesso istante. <c>StartedAtUtc</c> riparte a ogni riavvio del motore e non sa quando
/// l'IPOTESI è entrata in corsia; il registro sì. Queste prove fissano che, col registro a
/// disposizione, la promozione legge quello — e che senza registro ripiega sull'orologio di sessione
/// invece di tacere.</para>
/// </summary>
[Collection("TradingLanes")]
public sealed class OrologioPromozioneTests : IDisposable
{
    public OrologioPromozioneTests() => TradingLanes.ResetForTests();
    public void Dispose() => TradingLanes.ResetForTests();

    private static readonly DateTime Avvio = DateTime.UtcNow.AddDays(-30);

    /// <summary>Motore che dichiara 30 giorni di sessione: l'orologio ingannevole.</summary>
    private sealed class MotoreTrentaGiorni(int laneId) : ITradingEngine
    {
        public DateTime? UltimaFinestraChiesta { get; private set; }
        public int LaneId => laneId;
        public bool IsRunning => true;
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus
            {
                Mode = TradingMode.Paper, IsRunning = true, Symbol = "UNI/USDT", StartedAtUtc = Avvio,
            });
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
        {
            UltimaFinestraChiesta = from;
            return Task.FromResult(new TradingPerformance());
        }
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Il registro che sa la verità: sei giorni osservati, ancora tre giorni dopo l'avvio di sessione.</summary>
    private sealed class RegistroSeiGiorni : ILaneObservationLedger
    {
        public static readonly DateTime Ancora = Avvio.AddDays(3);
        public int Accrediti { get; private set; }

        public Task<(TimeSpan Observed, DateTime FirstSeenUtc)> AccumulateAsync(
            int laneId, string identity, bool isRunning, DateTime nowUtc, CancellationToken ct = default)
        {
            Accrediti++;
            return Task.FromResult((TimeSpan.FromDays(6), Ancora));
        }

        public Task<(TimeSpan Observed, DateTime FirstSeenUtc)?> ReadAsync(int laneId, string identity, CancellationToken ct = default)
            => Task.FromResult<(TimeSpan, DateTime)?>(identity.StartsWith("UNI/USDT|4h|", StringComparison.Ordinal)
                ? (TimeSpan.FromDays(6), Ancora)
                : null);
    }

    private sealed class DirettorioUnaCorsia : ILaneDirectory
    {
        public Task<IReadOnlyList<LaneSummary>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LaneSummary>>(
                [new LaneSummary(5, "UNI/USDT", "4h", "Paper", true, ActiveStrategyIds: ["s1"])]);
    }

    private static (PromotionEvaluator Evaluator, MotoreTrentaGiorni Motore, RegistroSeiGiorni Registro) Costruisci(bool conRegistro)
    {
        TradingLanes.Configure(8);
        var motore = new MotoreTrentaGiorni(5);
        var registro = new RegistroSeiGiorni();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITradingEngine>(5, (_, _) => motore);
        services.AddSingleton<ILaneDirectory, DirettorioUnaCorsia>();
        var provider = services.BuildServiceProvider();
        var evaluator = new PromotionEvaluator(provider,
            new StaticOptionsMonitor<PromotionEvaluatorOptions>(new PromotionEvaluatorOptions { MinObservationWeeks = 3 }),
            conRegistro ? registro : null);
        return (evaluator, motore, registro);
    }

    /// <summary>Col registro: sei giorni, non trenta — e i trade si ancorano al primo avvistamento, non alla sessione.</summary>
    [Fact]
    public async Task ConIlREGISTRO_laPROMOZIONEleggeSEIgiorni_nonTRENTA()
    {
        var (evaluator, motore, registro) = Costruisci(conRegistro: true);
        var decisione = await evaluator.EvaluateLaneAsync(5);

        Assert.Equal(TimeSpan.FromDays(6), decisione.Metrics.ObservationPeriod);
        Assert.False(decisione.Metrics.MeetsMinWeeks);
        Assert.Equal(RegistroSeiGiorni.Ancora, motore.UltimaFinestraChiesta);
        // La promozione LEGGE, non accredita: l'orologio lo fa scorrere il lettore della flotta, uno solo.
        Assert.Equal(0, registro.Accrediti);
    }

    /// <summary>Senza registro si ripiega sull'orologio di sessione: trenta giorni, e le tre settimane risultano superate.</summary>
    [Fact]
    public async Task SenzaREGISTRO_ripiegaSULLAsessione()
    {
        var (evaluator, motore, _) = Costruisci(conRegistro: false);
        var decisione = await evaluator.EvaluateLaneAsync(5);

        Assert.True(decisione.Metrics.ObservationPeriod >= TimeSpan.FromDays(29));
        Assert.True(decisione.Metrics.MeetsMinWeeks);
        Assert.Equal(Avvio, motore.UltimaFinestraChiesta);
    }
}
