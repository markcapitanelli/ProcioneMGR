using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J19/J21, PRD autonomia-operativa 2026-08-25] Le bugie del motore rese visibili o impossibili.
/// J19: la corsia 0 è rimasta SETTE SETTIMANE «in corsa» con timeframe vuoto — nessuna candela
/// poteva raggiungerla, e lo gridava solo nei log del pod che ruotano in ~10h. J21: cinque
/// TradeRecords con ClosedAtUtc PRIMA di OpenedAtUtc (scarti 18-29 giorni), chiusure protettive
/// valutate su candele storiche riconsegnate da un recupero dati.
/// </summary>
public class MotoreOnestaJ19J21Tests
{
    private static TradingEngineState Stato(string symbol = "ADA/USDT", string timeframe = "4h", bool running = true) => new()
    {
        LaneId = 0,
        Symbol = symbol,
        Timeframe = timeframe,
        IsRunning = running,
        TotalCapital = 10_000m,
        AvailableCapital = 10_000m,
        Leverage = 1,
    };

    private static readonly LaneInvariantOptions Opzioni = new();

    // ------------------------------------------------------------------ J19

    [Fact]
    public void CorsiaInCorsaSenzaTimeframe_EViolazione()
    {
        // Il caso vero della corsia 0: IsRunning=true, Symbol vuoto, ultimo trade 2026-07-05.
        var violations = LaneInvariantChecker.Check(Stato(symbol: "", timeframe: ""), [], Opzioni);
        Assert.Contains(violations, v => v.Contains("non alimentabile"));
    }

    [Theory]
    [InlineData("", "4h")]
    [InlineData("ADA/USDT", "")]
    public void BastaUnoDeiDueVuoti_PerLaViolazione(string symbol, string timeframe)
    {
        var violations = LaneInvariantChecker.Check(Stato(symbol, timeframe), [], Opzioni);
        Assert.Contains(violations, v => v.Contains("non alimentabile"));
    }

    [Fact]
    public void CorsiaFermaSenzaConfig_NonEViolazione()
    {
        // Una corsia FERMA e non configurata è uno stato legittimo (mai schierata): l'allarme
        // esiste per il semaforo verde sul binario morto, non per il binario vuoto.
        var violations = LaneInvariantChecker.Check(Stato(symbol: "", timeframe: "", running: false), [], Opzioni);
        Assert.DoesNotContain(violations, v => v.Contains("non alimentabile"));
    }

    [Fact]
    public void CorsiaConfigurataInCorsa_NessunaViolazioneNuova()
    {
        var violations = LaneInvariantChecker.Check(Stato(), [], Opzioni);
        Assert.DoesNotContain(violations, v => v.Contains("non alimentabile"));
    }

    // ------------------------------------------------------------------ J21

    private static readonly DateTime Apertura = new(2026, 7, 21, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ChiusuraNormale_TimestampIntatto()
    {
        var ts = Apertura.AddHours(6);
        Assert.Equal(ts, PositionCloser.ClampCloseTimestamp(Apertura, ts, NullLogger.Instance));
    }

    [Fact]
    public void ChiusuraPrimaDellApertura_BloccataAllApertura()
    {
        // Il caso vero della riga 248: aperta il 21/07, «chiusa» il 02/07 su una candela storica.
        var candelaVecchia = Apertura.AddDays(-18);
        Assert.Equal(Apertura, PositionCloser.ClampCloseTimestamp(Apertura, candelaVecchia, NullLogger.Instance));
    }

    [Fact]
    public void ChiusuraNellIstanteDellApertura_Ammessa()
    {
        // Il confine esatto non è un'inversione: si persiste com'è.
        Assert.Equal(Apertura, PositionCloser.ClampCloseTimestamp(Apertura, Apertura, NullLogger.Instance));
    }
}
