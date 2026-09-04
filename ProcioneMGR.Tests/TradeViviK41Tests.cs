using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K41 chiuso, revisione 2026-09-04] <b>Un trade è vivo se è stato scritto quando è avvenuto.</b>
///
/// <para>La colonna <c>RecordedAtUtc</c> esisteva da K41 e non aveva lettori: una corsia Paper
/// fermata per giorni e riavviata con la stessa gamba rigioca trenta giorni di candele, e le righe
/// dei giorni in cui era ferma — senza un originale da cui essere dedotte, con tempi di candela
/// dopo l'àncora — entravano nel ritiro e nel decadimento come trade veri.</para>
/// </summary>
public class TradeViviK41Tests
{
    private static TradeRecord Riga(int id, DateTime chiusa, DateTime? scritta) => new()
    {
        Id = id, LaneId = 4, StrategyId = "s", Symbol = "DOGE/USDT", Side = OrderSide.Buy,
        OpenedAtUtc = chiusa.AddHours(-2), ClosedAtUtc = chiusa, RecordedAtUtc = scritta, Pnl = 1m, PnlPercent = 0.5m,
    };

    private static readonly DateTime T = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UNtradeSCRITTOentroTREbarre_eVIVO()
    {
        // 15m: tolleranza 3 × 15′ + 30′ = 75′. Scritto 40 minuti dopo la candela: vivo.
        var vivi = TradeDeduplication.Vivi([Riga(1, T, T.AddMinutes(40))], "15m");
        Assert.Single(vivi);
    }

    [Fact]
    public void UNtradeSCRITTOgiorniDOPO_eREPLAY()
    {
        var righe = new[] { Riga(1, T, T.AddDays(5)), Riga(2, T.AddHours(1), T.AddHours(1).AddMinutes(10)) };
        var vivi = TradeDeduplication.Vivi(righe, "15m");

        Assert.Single(vivi);
        Assert.Equal(2, vivi[0].Id);
        Assert.Equal(1, TradeDeduplication.Replay(righe, vivi));
    }

    /// <summary>
    /// <b>Il nullo che protegge la storia.</b> Le 371 righe precedenti a K41 non hanno l'ora di
    /// parete: non si può giudicare ciò che non è stato misurato, e scartarle azzererebbe le corsie
    /// d'impronta. Restano, dichiarate.
    /// </summary>
    [Fact]
    public void SENZAoraDIparete_nonSiGIUDICA_eRESTA()
    {
        var vivi = TradeDeduplication.Vivi([Riga(1, T, null)], "1h");
        Assert.Single(vivi);
    }

    /// <summary>La tolleranza segue il timeframe: a 1d tre barre sono tre giorni, a 5m un quarto d'ora.</summary>
    [Fact]
    public void LaTOLLERANZA_segueILtimeframe()
    {
        Assert.Equal(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(30), TradeDeduplication.TolleranzaDiScrittura("1d"));
        Assert.Equal(TimeSpan.FromMinutes(15 + 30), TradeDeduplication.TolleranzaDiScrittura("5m"));
        // Un trade a 1d scritto due giorni dopo la candela è ancora vivo; a 5m no.
        Assert.Single(TradeDeduplication.Vivi([Riga(1, T, T.AddDays(2))], "1d"));
        Assert.Empty(TradeDeduplication.Vivi([Riga(1, T, T.AddDays(2))], "5m"));
    }
}
