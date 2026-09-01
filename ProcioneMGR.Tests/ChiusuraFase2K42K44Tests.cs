using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K42, K43, K44 — chiusura della Fase 2, 2026-09-01] Le tre cose che restavano in sospeso.
///
/// <list type="bullet">
/// <item><b>K43</b> — una riga di <c>TradeRecords</c> non è un trade: 367 righe Paper erano
/// <b>301</b> trade logici, e <c>COUNT(DISTINCT PositionId)</c> non poteva vederlo.</item>
/// <item><b>K44</b> — la soglia di ritiro per Sharpe significava <b>quattro cose diverse</b>:
/// confrontava un numero annualizzato sui rendimenti di barra, con un fattore <c>√PeriodsPerYear</c>
/// che vale 46,8 a 4h e 187,2 a 15m.</item>
/// </list>
///
/// (K42, la condanna a metà strada scritta a journal, si prova nel worker: vedi
/// <c>FleetOrchestratorWorker</c>.)
/// </summary>
public class ChiusuraFase2K42K44Tests
{
    // ---------------------------------------------------------------- K43: righe ≠ trade

    private static TradeRecord Riga(int id, DateTime aperta, DateTime chiusa, decimal pnl = 1m, string sid = "leg-a") => new()
    {
        Id = id, LaneId = 4, StrategyId = sid, Symbol = "DOGE/USDT", Side = OrderSide.Sell,
        // Il PositionId è DIVERSO a ogni riga: è esattamente il motivo per cui il controllo
        // precedente era cieco. Viene coniato a ogni esecuzione del motore.
        PositionId = Guid.NewGuid().ToString("N"),
        OpenedAtUtc = aperta, ClosedAtUtc = chiusa, Pnl = pnl, PnlPercent = pnl,
    };

    [Fact]
    public void TreRIGHEdelloStessoTrade_sonoUNtrade()
    {
        // Il caso reale: la stessa posizione DOT/USDT del 2026-06-29 compare con gli Id 222, 227 e
        // 234 — tre run dello stesso backtest persistiti.
        var a = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);
        var c = new DateTime(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc);

        var distinti = TradeDeduplication.Distinti([Riga(222, a, c), Riga(227, a, c), Riga(234, a, c)]);

        var solo = Assert.Single(distinti);
        Assert.Equal(222, solo.Id);   // vince la PRIMA scritta
    }

    [Fact]
    public void IlNULLO_diK43_dueTradeDIVERSInonSiFONDONO()
    {
        // Senza questo, una deduplica che collassa tutto passerebbe il test qui sopra e
        // cancellerebbe metà della storia. Stessa gamba, stesso simbolo, aperture diverse.
        var a = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

        var distinti = TradeDeduplication.Distinti(
        [
            Riga(1, a, a.AddHours(4)),
            Riga(2, a.AddHours(8), a.AddHours(12)),
        ]);

        Assert.Equal(2, distinti.Count);
    }

    [Fact]
    public void RepliceCONpnlDIVERSO_vinceLaPRIMAscritta_edEunaSCELTAdichiarata()
    {
        // Misurato: 25 gruppi su 301 hanno repliche con Pnl diverso — un replay su una finestra di
        // dati diversa dà un numero diverso per lo stesso trade. La prima è quella prodotta quando
        // l'operazione è avvenuta; scegliere «l'ultima» farebbe riscrivere la storia a un riavvio.
        var a = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);
        var c = a.AddHours(4);

        var distinti = TradeDeduplication.Distinti([Riga(50, a, c, pnl: 7m), Riga(10, a, c, pnl: -3m)]);

        var solo = Assert.Single(distinti);
        Assert.Equal(10, solo.Id);
        Assert.Equal(-3m, solo.Pnl);
    }

    [Fact]
    public void GambeDIVERSEsulloStessoIstante_restanoDUE()
    {
        // Due gambe della stessa corsia possono aprire e chiudere sulla stessa candela: è un
        // ensemble, non un duplicato. Lo StrategyId fa parte della chiave apposta.
        var a = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);
        var c = a.AddHours(4);

        Assert.Equal(2, TradeDeduplication.Distinti([Riga(1, a, c, sid: "leg-a"), Riga(2, a, c, sid: "leg-b")]).Count);
    }

    [Fact]
    public void LeRepliche_siCONTANO_perPoterleDICHIARARE()
    {
        var a = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);
        var righe = new[] { Riga(1, a, a.AddHours(4)), Riga(2, a, a.AddHours(4)), Riga(3, a.AddDays(1), a.AddDays(1).AddHours(4)) };
        var distinti = TradeDeduplication.Distinti(righe);

        Assert.Equal(2, distinti.Count);
        Assert.Equal(1, TradeDeduplication.Repliche(righe, distinti));
    }

    // ---------------------------------------------------------------- K44: l'unità della soglia

    private static FleetLaneState Corsia(
        string timeframe, decimal sharpeAnnualizzato, decimal? sharpePerTrade, int trade = 25) =>
        new(4, IsRunning: true, "Paper", IsConfigured: true, Quarantined: false, CampaignOwned: false,
            EmergencyStopped: false, RealizedSharpe: sharpeAnnualizzato, TradeCount: trade,
            Observation: TimeSpan.FromDays(30), Symbol: "DOGE/USDT", Timeframe: timeframe,
            ExpectedTradesPerMonth: 3.8m, GreySourced: true, Unreadable: false,
            RealizedSharpePerTrade: sharpePerTrade);

    private static FleetState Stato(params FleetLaneState[] lanes) => new()
    {
        Lanes = lanes, Candidates = [], FootprintLanes = 3, ExposureGuardEnabled = true,
        NowUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static readonly FleetOptions Opt = new()
    {
        Enabled = true, RetireSharpeThreshold = -0.5m, RetireMinTrades = 20, RetireMinWeeks = 3,
    };

    [Fact]
    public void SiGIUDICAsulloSharpePERtrade_nonSuQuelloANNUALIZZATO()
    {
        // Lo Sharpe annualizzato è ampiamente sopra la soglia; quello per trade è sotto. Prima si
        // sarebbe assolta la corsia guardando il numero sbagliato.
        var piano = FleetOrchestrator.Decide(
            Stato(Corsia("15m", sharpeAnnualizzato: 3.0m, sharpePerTrade: -0.9m)), Opt);

        var ritiro = Assert.Single(piano.Actions.OfType<StopAndFreeLane>());
        Assert.Contains("Sharpe per trade -0,900", ritiro.Reason.Replace('.', ','));
        Assert.Contains("dipende dal timeframe", ritiro.Reason);
    }

    [Fact]
    public void IlNULLO_diK44_sopraLaSoglia_nonSiRITIRA()
    {
        // Senza, un criterio che ritira sempre passerebbe il test qui sopra.
        var piano = FleetOrchestrator.Decide(
            Stato(Corsia("15m", sharpeAnnualizzato: -3.0m, sharpePerTrade: 0.2m)), Opt);

        Assert.Empty(piano.Actions.OfType<StopAndFreeLane>());
    }

    [Fact]
    public void LoSTESSOnumeroPERtrade_daLoSTESSOverdettoSU4hEsu15m()
    {
        // IL PUNTO DI K44. Con lo Sharpe annualizzato le due corsie avrebbero numeri diversi per lo
        // stesso comportamento — √2190 = 46,8 contro √35040 = 187,2, un fattore 4,0 — e la stessa
        // soglia avrebbe dato verdetti opposti. Sul numero per trade il verdetto è uno solo.
        var a4h = FleetOrchestrator.Decide(Stato(Corsia("4h", 0.5m, -0.9m)), Opt);
        var a15m = FleetOrchestrator.Decide(Stato(Corsia("15m", 2.0m, -0.9m)), Opt);

        Assert.Single(a4h.Actions.OfType<StopAndFreeLane>());
        Assert.Single(a15m.Actions.OfType<StopAndFreeLane>());
    }

    [Fact]
    public void SharpePERtradeNONdisponibile_NONsiGIUDICA()
    {
        // Il verso fail-closed, e il caso reale: un motore con un'immagine precedente al campo
        // risponde zero campioni. Leggere quello zero come un verdetto contro una soglia negativa
        // assolverebbe per ignoranza; contro una soglia a zero condannerebbe per ignoranza. Né
        // l'uno né l'altro: non si giudica.
        var piano = FleetOrchestrator.Decide(
            Stato(Corsia("15m", sharpeAnnualizzato: -9.0m, sharpePerTrade: null)), Opt);

        Assert.Empty(piano.Actions.OfType<StopAndFreeLane>());
    }

    [Fact]
    public void SenzaSTORIAsufficiente_nonSiGIUDICAcomunque()
    {
        // Il cancello di storia resta quello di prima: K44 cambia l'unità, non i prerequisiti.
        var piano = FleetOrchestrator.Decide(
            Stato(Corsia("15m", -9.0m, -9.0m, trade: 5)), Opt);

        Assert.Empty(piano.Actions.OfType<StopAndFreeLane>());
    }
}
