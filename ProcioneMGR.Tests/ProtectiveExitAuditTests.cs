using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-06] Il controllo che avrebbe dovuto accorgersi al posto del proprietario.
///
/// <para>Il caso vero che lo motiva: short ETC/USDT a 7,07 con take profit a <b>6,378554</b>; la
/// barra 4h del 06/08 08:00 ha segnato minimo <b>6,31</b> e la posizione è rimasta aperta. Il
/// primo test riproduce esattamente quei numeri.</para>
/// </summary>
public class ProtectiveExitAuditTests
{
    private static readonly DateTime Apertura = new(2026, 7, 26, 16, 0, 0, DateTimeKind.Utc);

    private static OpenPosition Short(decimal entry, decimal? sl, decimal? tp, string symbol = "ETC/USDT") => new()
    {
        LaneId = 3, PositionId = "b1e549ad", Symbol = symbol, Side = OrderSide.Sell,
        EntryPrice = entry, Quantity = 113.15417m, StopLoss = sl, TakeProfit = tp,
        OpenedAtUtc = Apertura,
    };

    private static OpenPosition Long(decimal entry, decimal? sl, decimal? tp, string symbol = "ETC/USDT") => new()
    {
        LaneId = 3, PositionId = "long-1", Symbol = symbol, Side = OrderSide.Buy,
        EntryPrice = entry, Quantity = 10m, StopLoss = sl, TakeProfit = tp,
        OpenedAtUtc = Apertura,
    };

    private static OhlcvData Barra(DateTime quando, decimal low, decimal high, string symbol = "ETC/USDT") => new()
    {
        Symbol = symbol, Timeframe = "4h", TimestampUtc = quando,
        Open = (low + high) / 2m, High = high, Low = low, Close = (low + high) / 2m, Volume = 1m,
    };

    // ------------------------------------------------------------------ il caso reale

    [Fact]
    public void ShortColTargetToccato_EUnAnomalia_ColSuoPrimoIstante()
    {
        var barre = new[]
        {
            Barra(new DateTime(2026, 8, 6, 4, 0, 0, DateTimeKind.Utc), 6.50m, 6.57m),
            Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 6.31m, 6.54m),   // tocca
            Barra(new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc), 6.35m, 6.43m),  // tocca
        };

        var a = Assert.Single(ProtectiveExitAudit.Find([Short(7.07m, 7.33159m, 6.378554m)], barre));

        Assert.Equal("take profit", a.Kind);
        Assert.Equal(6.378554m, a.Level);
        Assert.Equal(6.31m, a.ReachedPrice);
        Assert.Equal(2, a.BarsTouched);
        Assert.Equal(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), a.FirstTouchUtc);
    }

    /// <summary>Il controllo deve TACERE quando tutto è a posto: un allarme sempre acceso non è un allarme.</summary>
    [Fact]
    public void ProtezioniMaiToccate_NessunaAnomalia()
        => Assert.Empty(ProtectiveExitAudit.Find(
            [Short(7.07m, 7.33159m, 6.378554m)],
            [Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 6.40m, 6.60m)]));

    // ------------------------------------------------------------------ il verso di long e short

    [Fact]
    public void PerUnLong_IlTargetLoToccaIlMassimo_ELoStopIlMinimo()
    {
        var barre = new[] { Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 5.90m, 7.50m) };

        var esiti = ProtectiveExitAudit.Find([Long(6.50m, sl: 6.00m, tp: 7.00m)], barre);

        Assert.Equal(2, esiti.Count);
        var target = esiti.Single(e => e.Kind == "take profit");
        var stop = esiti.Single(e => e.Kind == "stop loss");
        Assert.Equal(7.50m, target.ReachedPrice);   // il MASSIMO
        Assert.Equal(5.90m, stop.ReachedPrice);     // il MINIMO
    }

    /// <summary>
    /// Il verso invertito è il modo più facile di scrivere un controllo che non trova mai niente:
    /// per uno short il target sta SOTTO. Con gli stessi prezzi del test precedente e le stesse
    /// soglie, uno short deve dare l'esito opposto su ciascuna protezione.
    /// </summary>
    [Fact]
    public void PerUnoShort_IlVersoEOpposto()
    {
        var barre = new[] { Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 5.90m, 7.50m) };

        var esiti = ProtectiveExitAudit.Find([Short(6.50m, sl: 7.00m, tp: 6.00m)], barre);

        Assert.Equal(5.90m, esiti.Single(e => e.Kind == "take profit").ReachedPrice);
        Assert.Equal(7.50m, esiti.Single(e => e.Kind == "stop loss").ReachedPrice);
    }

    // ------------------------------------------------------------------ i confini

    /// <summary>
    /// Il prezzo toccato PRIMA che la posizione esistesse non dice nulla su di essa: contarlo
    /// produrrebbe un allarme su ogni posizione appena aperta.
    /// </summary>
    [Fact]
    public void LeBarrePrecedentiAllAperturaNonContano()
        => Assert.Empty(ProtectiveExitAudit.Find(
            [Short(7.07m, 7.33159m, 6.378554m)],
            [Barra(Apertura.AddHours(-4), 6.00m, 6.10m)]));

    /// <summary>La barra esattamente sull'apertura conta: è quella in cui la posizione è nata.</summary>
    [Fact]
    public void LaBarraDellAperturaConta()
        => Assert.Single(ProtectiveExitAudit.Find(
            [Short(7.07m, 7.33159m, 6.378554m)],
            [Barra(Apertura, 6.00m, 7.10m)]));

    [Fact]
    public void SenzaProtezioniImpostate_NienteDaControllare()
        => Assert.Empty(ProtectiveExitAudit.Find(
            [Short(7.07m, sl: null, tp: null)],
            [Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 1m, 99m)]));

    /// <summary>Una protezione a zero è «non impostata», non «target a prezzo zero» — che sarebbe sempre toccato.</summary>
    [Fact]
    public void ProtezioneAZero_NonEUnLivello()
        => Assert.Empty(ProtectiveExitAudit.Find(
            [Short(7.07m, sl: 0m, tp: 0m)],
            [Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 1m, 99m)]));

    /// <summary>
    /// Le barre di un ALTRO simbolo non toccano questa posizione. Non è teoria: la corsia 3 ha
    /// tenuto aperto uno short ETC mentre operava su DOT, e DOT quotava 0,81 — contro un target
    /// ETC di 6,38 ogni barra DOT sembrerebbe un tocco.
    /// </summary>
    [Fact]
    public void LeBarreDiUnAltroSimboloNonContano()
        => Assert.Empty(ProtectiveExitAudit.Find(
            [Short(7.07m, 7.33159m, 6.378554m)],
            [Barra(new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc), 0.80m, 0.83m, "DOT/USDT")]));
}
