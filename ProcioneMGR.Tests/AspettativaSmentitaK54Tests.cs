using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K54, PRD autonomia-piena — Fase 4, 2026-09-02] <b>L'aspettativa si scrive una volta e nessuno la
/// ricontrolla mai più.</b>
///
/// <para><b>Il fatto misurato.</b> La corsia 6 porta <c>expectedSharpe = 1,8754</c> per
/// <c>GridMeanReversion DOGE/USDT 15m</c>, scritto il <b>21 agosto</b>. Da allora la caccia ha
/// rivalutato la stessa identica ipotesi — <i>stessi parametri</i> — altre undici volte, con
/// mediana <b>0,479</b>. Il numero portato è 3,9× tanto, ed è stato prodotto due giorni prima che il
/// motore walk-forward venisse sostituito (confine software del 2026-08-23).</para>
///
/// <para><b>Dove finisce.</b> Non nel ritiro di flotta, che usa una soglia assoluta. In
/// <see cref="StrategyDecayMonitor"/>, <c>ratio = realizzato / atteso</c>, i cui verdetti stanno su
/// <c>/ensemble</c> e sulla scheda della Home: un allarme calcolato contro 1,875 non misura il
/// decadimento della gamba, misura quanto era ottimistica la notte in cui è stata proposta.</para>
///
/// <para><b>I nulli di questa suite sono la metà del valore.</b> Una regola che sostituisse sempre
/// l'aspettativa con «l'ultima misura» passerebbe le prove positive e romperebbe i quattro casi su
/// sette in cui l'evidenza successiva <i>conferma</i> il numero portato — corsia 2 Supertrend con 28
/// rivalutazioni a mediana identica, corsia 5 con 43.</para>
/// </summary>
public class AspettativaSmentitaK54Tests
{
    private static ExpectationEvidence Prova(decimal portato, int misureDopo, decimal? medianaDopo)
        => new(portato, new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc), misureDopo, medianaDopo);

    // ------------------------------------------------------------------ il predicato

    /// <summary>Il caso reale della corsia 6: undici rivalutazioni, mediana 0,479 contro 1,875.</summary>
    [Fact]
    public void ILCASOreale_dellaCORSIA6_eSMENTITO()
    {
        var e = Prova(1.8754m, 11, 0.479m);

        Assert.True(e.Giudicabile);
        Assert.True(e.Contraddetta);
        Assert.Equal(0.479m, e.Corrente);
        Assert.Contains("rivalutata 11 volte", e.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il nullo principale.</b> Corsia 2 Supertrend: 28 rivalutazioni e mediana IDENTICA al
    /// numero portato. Se il predicato marcasse anche questo, la piattaforma direbbe «smentito» su
    /// una misura confermata ventotto volte — cioè griderebbe sempre, che è il difetto in un'altra
    /// forma.
    /// </summary>
    [Fact]
    public void ILNULLO_unNUMEROconfermato28volte_nonESMENTITO()
    {
        var e = Prova(3.195m, 28, 3.195m);

        Assert.True(e.Giudicabile);
        Assert.False(e.Contraddetta);
        Assert.Equal(3.195m, e.Corrente);
        Assert.Contains("confermata da 28", e.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// Poche rivalutazioni non smentiscono niente. Corsia 3: due misure successive, e per quanto
    /// diverse non bastano — due numeri non sono una distribuzione.
    /// </summary>
    [Fact]
    public void POCHEmisure_nonGIUDICANO()
    {
        var e = Prova(3.961m, 2, 40.0m);   // divergenza enorme, ma su due sole misure

        Assert.False(e.Giudicabile);
        Assert.False(e.Contraddetta);
        Assert.Equal(3.961m, e.Corrente);          // si continua a usare il numero portato
        Assert.Contains("2 su 5", e.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// La divergenza <b>non ha un verso privilegiato</b>: un'aspettativa troppo BASSA rende il
    /// giudizio indulgente esattamente come una troppo alta lo rende impossibile. Il caso esiste —
    /// la corsia 3 porta un numero sotto la mediana successiva.
    /// </summary>
    [Fact]
    public void UNaspettativaTROPPObassa_eSMENTITAquantoUNAtroppoALTA()
    {
        var bassa = Prova(1.0m, 10, 2.0m);    // il vero è il doppio
        var alta = Prova(2.0m, 10, 1.0m);     // il vero è la metà

        Assert.True(bassa.Contraddetta);
        Assert.True(alta.Contraddetta);
    }

    /// <summary>
    /// <b>Il nullo della soglia.</b> Le finestre scorrono ogni notte, quindi una variazione
    /// ordinaria è la norma e non deve allarmare: sotto 1,5× non si dice niente. Senza questa
    /// prova, una soglia a 1,05 passerebbe tutte le altre e marcherebbe l'intera flotta.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.2)]     // +20%
    [InlineData(1.0, 0.8)]     // −20%
    [InlineData(1.0, 1.45)]    // appena sotto la soglia
    public void ILNULLOdellaSOGLIA_laVARIABILITAordinaria_nonEunaSMENTITA(double portato, double mediana)
        => Assert.False(Prova((decimal)portato, 10, (decimal)mediana).Contraddetta);

    [Fact]
    public void SENZAmisureSUCCESSIVE_nonSiGIUDICA()
    {
        var e = Prova(1.8754m, 0, null);

        Assert.False(e.Giudicabile);
        Assert.False(e.Contraddetta);
        Assert.Equal(1.8754m, e.Corrente);
    }

    // ------------------------------------------------------------------ la mediana

    /// <summary>
    /// Mediana e non media: una rivalutazione anomala non deve spostare la stima corrente — sarebbe
    /// lo stesso difetto che si sta correggendo, solo dall'altra parte.
    /// </summary>
    [Fact]
    public void LaSTIMAcorrente_eLaMEDIANAnonLaMEDIA()
    {
        // Le undici misure vere della corsia 6.
        decimal[] misure = [0.2329m, 0.4787m, 0.4720m, 0.3212m, 0.3244m, 0.4531m, 0.6743m,
                            0.5902m, 0.5522m, 0.5180m, 0.5901m];

        var mediana = ExpectationEvidenceReader.Mediana(misure);

        Assert.Equal(0.4787m, mediana);
        // Con una singola anomalia enorme la mediana quasi non si muove; la media sì.
        decimal[] conAnomalia = [.. misure, 50m];
        Assert.True(Math.Abs(ExpectationEvidenceReader.Mediana(conAnomalia)!.Value - mediana!.Value) < 0.1m);
    }

    [Fact]
    public void LaMEDIANA_diUNelencoVUOTO_eNULL()
        => Assert.Null(ExpectationEvidenceReader.Mediana([]));

    // ------------------------------------------------------------------ l'effetto sul verdetto

    private static EnsembleStrategy Gamba(decimal atteso) => new()
    {
        StrategyName = "GridMeanReversion",
        DisplayName = "GridMeanReversion DOGE",
        IsActive = true,
        CurrentAllocation = 100m,
        ExpectedSharpe = atteso,
        ExpectedSharpeAtUtc = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Trade sintetici con un rendimento costante: quel che conta qui non è il valore realizzato ma
    /// che <b>a parità di realizzato</b> l'allarme cambi a seconda che si giudichi contro
    /// l'aspettativa d'origine o contro l'evidenza successiva.
    /// </summary>
    private static List<TradeRecord> Trade(string strategyId, int quanti, decimal pnlPercent)
    {
        var basi = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
        return [.. Enumerable.Range(0, quanti).Select(i => new TradeRecord
        {
            StrategyId = strategyId,
            Symbol = "DOGE/USDT",
            Side = i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
            OpenedAtUtc = basi.AddHours(i * 4),
            ClosedAtUtc = basi.AddHours(i * 4 + 1),
            PnlPercent = i % 3 == 0 ? -pnlPercent / 2m : pnlPercent,
            Pnl = i % 3 == 0 ? -pnlPercent / 2m : pnlPercent,
        })];
    }

    /// <summary>
    /// <b>La prova che vale per tutte.</b> Stessi trade, stessa gamba: col solo numero d'origine il
    /// monitor grida al decadimento; con l'evidenza successiva — che dice che quel numero non è mai
    /// stato la realtà — il verdetto cambia. È esattamente la corsia 6.
    /// </summary>
    [Fact]
    public void STESSItrade_ilVERDETTOcambiaSEcSTAlEVIDENZA()
    {
        var monitor = new StrategyDecayMonitor();
        var gamba = Gamba(atteso: 1.8754m);
        var trade = Trade(gamba.StrategyId, 40, 0.9m);
        var opzioni = new DecayMonitorOptions { WindowTradeCount = 20, AlertThresholdRatio = 0.5m };

        var senza = monitor.Analyze(gamba, trade, "15m", opzioni);
        var con = monitor.Analyze(gamba, trade, "15m", opzioni,
            new ExpectationEvidence(1.8754m, gamba.ExpectedSharpeAtUtc!.Value, 11, 0.479m));

        Assert.True(senza.IsMeasurable);
        Assert.Equal(senza.RealizedSharpe, con.RealizedSharpe);       // il realizzato non cambia
        Assert.Equal(senza.SharpeRatio, con.SharpeRatio);             // il rapporto storico resta
        Assert.NotNull(con.SharpeRatioVsEvidence);                    // e ne compare uno nuovo
        // Il rapporto contro la stima corrente è più generoso proprio perché l'asticella è più bassa.
        Assert.True(con.SharpeRatioVsEvidence > senza.SharpeRatio);
        Assert.NotNull(con.Evidence);
    }

    /// <summary>
    /// <b>Il nullo del collegamento.</b> Senza evidenza sufficiente il comportamento dev'essere
    /// IDENTICO a quello di prima: una gamba appena schierata, che non ha ancora rivalutazioni, non
    /// deve comportarsi diversamente da ieri.
    /// </summary>
    [Fact]
    public void ILNULLO_conEVIDENZAinsufficiente_nienteCAMBIA()
    {
        var monitor = new StrategyDecayMonitor();
        var gamba = Gamba(atteso: 1.8754m);
        var trade = Trade(gamba.StrategyId, 40, 0.9m);
        var opzioni = new DecayMonitorOptions { WindowTradeCount = 20 };

        var senza = monitor.Analyze(gamba, trade, "15m", opzioni);
        var con = monitor.Analyze(gamba, trade, "15m", opzioni,
            new ExpectationEvidence(1.8754m, gamba.ExpectedSharpeAtUtc!.Value, 2, 0.4m));

        Assert.Equal(senza.IsAlert, con.IsAlert);
        Assert.Null(con.SharpeRatioVsEvidence);
    }

    /// <summary>
    /// E l'evidenza che <b>conferma</b> non deve spostare il verdetto: sarebbe un cambiamento senza
    /// causa, e renderebbe il monitor imprevedibile su quattro gambe su sette.
    /// </summary>
    [Fact]
    public void UNevidenzaCHEconferma_lasciaTUTTOcomERA()
    {
        var monitor = new StrategyDecayMonitor();
        var gamba = Gamba(atteso: 3.195m);
        var trade = Trade(gamba.StrategyId, 40, 0.9m);
        var opzioni = new DecayMonitorOptions { WindowTradeCount = 20 };

        var senza = monitor.Analyze(gamba, trade, "15m", opzioni);
        var con = monitor.Analyze(gamba, trade, "15m", opzioni,
            new ExpectationEvidence(3.195m, gamba.ExpectedSharpeAtUtc!.Value, 28, 3.195m));

        Assert.Equal(senza.IsAlert, con.IsAlert);
        Assert.Equal(senza.SharpeRatio, con.SharpeRatioVsEvidence);
    }
}
