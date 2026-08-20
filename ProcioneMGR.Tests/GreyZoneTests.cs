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
        Assert.True(GreyZone.IsGrey(Candidate(dsr: 0.70)));   // pavimento incluso [F5b: era 0,80]
        Assert.True(GreyZone.IsGrey(Candidate(dsr: 0.773)));  // il MASSIMO davvero osservato in 30 giorni
        Assert.True(GreyZone.IsGrey(Candidate(dsr: 0.9499))); // sotto il tetto
    }

    [Fact]
    public void DsrOutsideBand_IsNotGrey()
    {
        Assert.False(GreyZone.IsGrey(Candidate(dsr: 0.6999))); // sotto il pavimento
        Assert.False(GreyZone.IsGrey(Candidate(dsr: 0.95)));   // il tetto e' la soglia di sopravvivenza
        Assert.False(GreyZone.IsGrey(Candidate(dsr: null)));   // DSR non calcolabile e nessun ContoTrade
    }

    /// <summary>
    /// [F5b, 2026-08-20] Il caso che rende il pavimento una scelta e non un numero: col vecchio 0,80
    /// la porta DSR era MURATA, perché il massimo che il sistema produce è 0,773. Questo test
    /// diventerebbe rosso se qualcuno riportasse il pavimento sopra il tetto reale della macchina,
    /// che è esattamente il modo in cui la banda era morta senza che nessuno se ne accorgesse.
    /// </summary>
    [Fact]
    public void IlPavimento_RestaSottoIlMassimoCheLaMacchinaProduce()
    {
        const double dsrMassimoOsservato = 0.773;   // 30 giorni, 402 candidati arrivati al gate
        Assert.True(GreyZone.DsrFloor <= dsrMassimoOsservato,
            $"pavimento {GreyZone.DsrFloor:F2} sopra il massimo osservato {dsrMassimoOsservato:F3}: la banda DSR sarebbe irraggiungibile");
        Assert.True(GreyZone.DsrFloor < GreyZone.DsrCeiling, "pavimento e tetto invertiti: la banda sarebbe vuota per costruzione");
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
            Candidate(dsr: 0.70),
            Candidate(dsr: 0.773),
            Candidate(dsr: 0.9499),
            Candidate(dsr: 0.95),
            Candidate(dsr: 0.6999),
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
