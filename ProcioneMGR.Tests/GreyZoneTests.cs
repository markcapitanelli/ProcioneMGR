using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [PRD memoria-caccia 2026-08-14] Il giudice della fascia grigia è UNO (<see cref="GreyZone"/>),
/// promosso da FleetStateReader quando i consumatori sono diventati tre. Questi test fissano:
/// (1) la definizione esatta, caso per caso e sui bordi delle soglie;
/// (2) il contratto di delega — FleetStateReader.IsGrey e GreyZone.IsGrey NON possono divergere
///     su nessun candidato (due soglie in due posti = due verdetti sulla stessa riga, il difetto
///     già pagato con la doppia regola dell'ampiezza finestra in D2.b).
/// </summary>
public sealed class GreyZoneTests
{
    private static ValidatedCandidate Candidate(
        bool survived = false,
        decimal holdoutSharpe = 1.0m,
        int trades = 10,
        string? reject = null,
        double? dsr = null) => new()
    {
        StrategyName = "RsiOversold",
        Symbol = "DOT/USDT",
        Timeframe = "15m",
        Survived = survived,
        HoldoutSharpe = holdoutSharpe,
        HoldoutTrades = trades,
        RejectReason = reject,
        DeflatedSharpe = dsr,
    };

    // ------------------------------------------------------------------ definizione

    [Fact]
    public void ShortWindowReject_WithPositiveSharpe_IsGrey() =>
        Assert.True(GreyZone.IsGrey(Candidate(reject: "Solo 8 trade in holdout (< 10)")));

    [Fact]
    public void DsrInBand_IsGrey()
    {
        Assert.True(GreyZone.IsGrey(Candidate(dsr: 0.80)));   // pavimento incluso
        Assert.True(GreyZone.IsGrey(Candidate(dsr: 0.9499))); // sotto il tetto
    }

    [Fact]
    public void DsrOutsideBand_IsNotGrey()
    {
        Assert.False(GreyZone.IsGrey(Candidate(dsr: 0.7999))); // sotto il pavimento
        Assert.False(GreyZone.IsGrey(Candidate(dsr: 0.95)));   // il tetto e' la soglia di sopravvivenza
        Assert.False(GreyZone.IsGrey(Candidate(dsr: null)));   // DSR non calcolabile e nessun ContoTrade
    }

    [Fact]
    public void LosingCandidate_IsNeverGrey()
    {
        // «Un grigio che perde non e' grigio, e' bocciato nel merito» — anche con la bocciatura
        // per sola finestra corta, anche con DSR in fascia.
        Assert.False(GreyZone.IsGrey(Candidate(holdoutSharpe: -0.5m, reject: "Solo 8 trade in holdout (< 10)")));
        Assert.False(GreyZone.IsGrey(Candidate(holdoutSharpe: 0m, dsr: 0.90)));
    }

    [Fact]
    public void ZeroTrades_IsNotGrey() =>
        Assert.False(GreyZone.IsGrey(Candidate(trades: 0, reject: "Solo 0 trade in holdout (< 10)")));

    [Fact]
    public void Survivor_IsNotGrey() =>
        Assert.False(GreyZone.IsGrey(Candidate(survived: true, dsr: 0.90)));

    [Fact]
    public void MeritReject_IsNotGrey() =>
        Assert.False(GreyZone.IsGrey(Candidate(reject: "Sharpe holdout -1,50 < 0,30")));

    // ------------------------------------------------------------------ contratto di delega

    [Fact]
    public void FleetStateReader_Delegates_NeverDiverges()
    {
        // Matrice che copre ogni ramo del predicato: se qualcuno un giorno duplicasse la soglia
        // in FleetStateReader invece di delegare, almeno un caso qui divergerebbe.
        var matrix = new[]
        {
            Candidate(reject: "Solo 8 trade in holdout (< 10)"),
            Candidate(dsr: 0.80),
            Candidate(dsr: 0.9499),
            Candidate(dsr: 0.95),
            Candidate(dsr: 0.7999),
            Candidate(holdoutSharpe: -0.5m, reject: "Solo 8 trade in holdout (< 10)"),
            Candidate(trades: 0, reject: "Solo 0 trade in holdout (< 10)"),
            Candidate(survived: true, dsr: 0.90),
            Candidate(reject: "Sharpe holdout -1,50 < 0,30"),
            Candidate(reject: null, dsr: null),
        };
        foreach (var c in matrix)
        {
            Assert.Equal(GreyZone.IsGrey(c), FleetStateReader.IsGrey(c));
        }
    }
}
