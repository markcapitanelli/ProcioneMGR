using ProcioneMGR.Data;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Tests;

/// <summary>
/// Le undici decisioni della revisione algoritmi del 2026-08-20
/// (<c>docs/audit/31_REVISIONE_ALGORITMI_2026-08.md</c>), per la parte che si può inchiodare con
/// un test puro. Ogni caso qui sotto sarebbe VERDE anche col difetto presente se fosse scritto
/// nell'unità sbagliata: è il motivo per cui alcuni asseriscono su un fattore di scala e non su
/// un valore, e per cui M4 e A2 provano il RIFIUTO e non il percorso felice.
/// </summary>
public class RevisioneAlgoritmi2026_08Tests
{
    // ------------------------------------------------------------------ A2: PBO e timeframe misti

    private static PipelineContext ContextWith(params (string Symbol, string Timeframe)[] universe)
    {
        var ctx = new PipelineContext();
        foreach (var (symbol, timeframe) in universe)
        {
            ctx.Universe.Add(new SeriesSpec { Symbol = symbol, Timeframe = timeframe });
        }
        ctx.Candidates.Add(new DiscoveryCandidate
        {
            StrategyName = "EmaCross", Symbol = universe[0].Symbol, Timeframe = universe[0].Timeframe,
        });
        return ctx;
    }

    [Fact]
    public void A2_UniversoMonoTimeframe_Passa()
    {
        var stage = new HoldoutValidationStage(null!, null!);
        Assert.Null(stage.ValidateInput(ContextWith(("BTC/USDT", "1h"), ("ETH/USDT", "1h"))));
    }

    [Fact]
    public void A2_UniversoATimeframeMisti_EScartatoPrimaDiSpendereIlRun()
    {
        // Il PBO di pannello confronta Sharpe PER BARRA su partizioni per INDICE: con granularità
        // diverse «la barra i-esima» è un istante diverso per candidati diversi, e il verdetto è
        // bloccante sull'intero batch. Il rifiuto arriva prima del run, non dopo il numero.
        var stage = new HoldoutValidationStage(null!, null!);
        var motivo = stage.ValidateInput(ContextWith(("BTC/USDT", "1h"), ("ETH/USDT", "4h")));

        Assert.NotNull(motivo);
        Assert.Contains("MISTI", motivo);
        Assert.Contains("1h", motivo);
        Assert.Contains("4h", motivo);
    }

    [Fact]
    public void A2_NessunCandidato_RestaIlPrimoMotivo()
    {
        // La guardia nuova non deve scavalcare quella storica: senza candidati il messaggio giusto
        // resta «nessun candidato», altrimenti si diagnostica il sintomo sbagliato.
        var ctx = new PipelineContext();
        ctx.Universe.Add(new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1h" });
        ctx.Universe.Add(new SeriesSpec { Symbol = "ETH/USDT", Timeframe = "4h" });

        var stage = new HoldoutValidationStage(null!, null!);
        Assert.Contains("Nessun candidato", stage.ValidateInput(ctx)!);
    }

    // -------------------------------------------------- M4: fette contro stop resting sull'exchange

    [Theory]
    [InlineData(TradingMode.Paper, "Twap", true, false)]      // in Paper non si affetta mai
    [InlineData(TradingMode.Testnet, "Immediate", true, false)]
    [InlineData(TradingMode.Testnet, null, true, false)]
    [InlineData(TradingMode.Testnet, "Twap", false, false)]   // esecuzione a fette spenta
    [InlineData(TradingMode.Testnet, "Twap", true, true)]
    [InlineData(TradingMode.Live, "Vwap", true, true)]
    public void M4_WouldSlice_RiconosceLeCondizioni(TradingMode mode, string? algo, bool liveExecEnabled, bool atteso)
        => Assert.Equal(atteso, ExecutionSlicePlanner.WouldSlice(mode, algo, liveExecEnabled));

    [Fact]
    public void M4_ConStopRestingAttivi_IlPianoAFetteNonSiCostruisce()
    {
        // Il bracket sull'exchange si piazza SOLO alla nascita della posizione, sulla quantità di
        // quell'istante: con un piano a fette resterebbe armato sulla sola prima fetta per sempre.
        // Regola 4: fra protezione e riduzione d'impatto vince la protezione.
        Assert.True(ExecutionSlicePlanner.SlicingSuppressedByRestingStops(
            TradingMode.Testnet, MarketType.Futures, "Twap",
            liveExecutionEnabled: true, useExchangeRestingStops: true));
    }

    [Fact]
    public void M4_SenzaStopResting_LeFetteRestanoPermesse()
        => Assert.False(ExecutionSlicePlanner.SlicingSuppressedByRestingStops(
            TradingMode.Testnet, MarketType.Futures, "Twap",
            liveExecutionEnabled: true, useExchangeRestingStops: false));

    [Fact]
    public void M4_SuSpotLaSpuntaNonSopprimeNulla()
    {
        // Il bracket resting esiste SOLO nel percorso Futures: su Spot la spunta non arma alcun
        // trigger, quindi spegnere le fette peggiorerebbe l'esecuzione senza proteggere niente.
        Assert.False(ExecutionSlicePlanner.SlicingSuppressedByRestingStops(
            TradingMode.Testnet, MarketType.Spot, "Twap",
            liveExecutionEnabled: true, useExchangeRestingStops: true));
    }

    [Fact]
    public void M4_InPaperLaSpuntaNonCambiaNulla()
    {
        // In Paper non c'è nessun piano da sopprimere: dichiararlo evita che un domani si legga la
        // soppressione come «gli stop resting spengono qualcosa anche in Paper».
        Assert.False(ExecutionSlicePlanner.SlicingSuppressedByRestingStops(
            TradingMode.Paper, MarketType.Futures, "Twap",
            liveExecutionEnabled: true, useExchangeRestingStops: true));
    }

    // ------------------------------------------------------------------ M2: due scrittori del DSR

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(80, 800, true)]      // esattamente un ordine di grandezza: ammesso
    [InlineData(1, 800, false)]      // /ml (N=1, non deflazionato) contro pipeline: incomparabili
    [InlineData(10, 101, false)]
    [InlineData(null, 800, false)]   // riga storica: N non dichiarato
    [InlineData(800, null, false)]
    [InlineData(0, 800, false)]
    public void M2_DsrComparable(int? a, int? b, bool atteso)
        => Assert.Equal(atteso, SavedMlModel.DsrComparable(a, b));

    [Fact]
    public void M2_ComparabilitaESimmetrica()
    {
        Assert.Equal(SavedMlModel.DsrComparable(1, 800), SavedMlModel.DsrComparable(800, 1));
        Assert.Equal(SavedMlModel.DsrComparable(80, 800), SavedMlModel.DsrComparable(800, 80));
    }

    // ------------------------------------------------ M6: gambe dell'ensemble ≠ sopravvissuti pieni

    private static PipelineRecommendation Reccomandazione(int candidati, int sopravvissuti, params string?[] verdetti)
    {
        var r = new PipelineRecommendation { CandidatesEvaluated = candidati, Survivors = sopravvissuti };
        foreach (var v in verdetti)
        {
            r.EnsembleLegs.Add(new ProposedLeg { StrategyName = "EmaCross", Symbol = "BTC/USDT", Timeframe = "1h", SourceVerdict = v });
        }
        return r;
    }

    [Fact]
    public void M6_EnsembleDiSoleGambeGrigie_DichiaraZeroSopravvissuti()
    {
        // È il caso per cui la correzione esiste: due gambe schierate e ZERO sopravvissuti pieni,
        // che prima si leggeva «2 sopravvissuti: ensemble schierato», cioè come un successo pieno.
        var testo = CampaignPlanner.DescribeProvenance(Reccomandazione(340, 0, "Grey", "Grey"));

        Assert.Contains("0 sopravvissuti pieni su 340 candidati", testo);
        Assert.Contains("2 dalla FASCIA GRIGIA", testo);
    }

    [Fact]
    public void M6_EnsembleDiSoliSopravvissuti_NonNominaLaFasciaGrigia()
    {
        var testo = CampaignPlanner.DescribeProvenance(Reccomandazione(120, 3, "Survived", "Survived"));

        Assert.Contains("3 sopravvissuti pieni su 120 candidati", testo);
        Assert.DoesNotContain("GRIGIA", testo);
    }

    [Fact]
    public void M6_RaccomandazioneStorica_TaceInveceDiInventareUnoZero()
    {
        // Senza CandidatesEvaluated (JSON precedenti) il dato non è derivabile: meglio non dirlo
        // che dichiarare «0 sopravvissuti su 0 candidati», che è falso e allarmante.
        Assert.Equal(string.Empty, CampaignPlanner.DescribeProvenance(Reccomandazione(0, 0, "Grey")));
        Assert.Equal(string.Empty, CampaignPlanner.DescribeProvenance(null));
    }

    // ------------------------------------------- M5: risk-free lordo contro netto nel decay monitor

    [Fact]
    public void M5_IlDivarioDiRiskFreeEDichiarato_EValeRfSuSigma()
    {
        // Il realizzato è LORDO, l'atteso è netto di rf: il divario sistematico è rf/σ_annualizzata.
        // Serie con σ per bucket nota, così il numero è verificabile a mano.
        var returns = new decimal[100];
        for (var i = 0; i < returns.Length; i++) returns[i] = i % 2 == 0 ? 0.01m : -0.01m;   // σ = 0,01

        const decimal bucketsPerYear = 8760m;   // 1h
        var bias = StrategyDecayMonitor.RiskFreeBias(returns, bucketsPerYear, riskFreeRateAnnual: 0.02m);

        Assert.NotNull(bias);
        var sigmaAnnua = 0.01 * Math.Sqrt(8760);            // ≈ 0,9359
        Assert.Equal(0.02 / sigmaAnnua, (double)bias!.Value, precision: 6);
    }

    [Fact]
    public void M5_SenzaRiskFreeNonCEDivario()
        => Assert.Null(StrategyDecayMonitor.RiskFreeBias([0.01m, -0.01m, 0.01m], 8760m, riskFreeRateAnnual: 0m));

    [Fact]
    public void M5_SerieDegenere_NonProduceUnNumeroInventato()
    {
        Assert.Null(StrategyDecayMonitor.RiskFreeBias([0.01m, 0.01m, 0.01m], 8760m, 0.02m));  // σ = 0
        Assert.Null(StrategyDecayMonitor.RiskFreeBias([0.01m], 8760m, 0.02m));                // un solo punto
    }

    // --------------------------------------------------- A1/A5: la volatilità torna per-candela

    [Theory]
    // Fattore di conversione da varianza giornaliera a varianza per candela: minuti_tf / 1440.
    // Su σ il fattore è la radice — è il numero per cui il trigger contestuale sbagliava.
    [InlineData("1h", 4.898979)]
    [InlineData("4h", 2.449490)]
    [InlineData("15m", 9.797959)]
    [InlineData("1d", 1.0)]
    public void A1_IlRiscalamentoAlTimeframe_EQuelloDichiarato(string timeframe, double fattoreAtteso)
    {
        var minuti = Timeframes.Supported[timeframe].TotalMinutes;
        const double rvGiornaliera = 0.0009;                       // σ giornaliero = 3%

        var sigmaGiornaliero = Math.Sqrt(rvGiornaliera);
        var sigmaPerCandela = Math.Sqrt(rvGiornaliera * (minuti / 1440.0));

        Assert.Equal(fattoreAtteso, sigmaGiornaliero / sigmaPerCandela, precision: 5);
    }

    [Fact]
    public void A1_IlRatioNonCambia_QuindiLevelESizingRestanoIdentici()
    {
        // Il motivo per cui la correzione NON tocca il gate C3 né il dosaggio: il ratio
        // forecast/lungo-periodo è invariante di scala, perché entrambi si riscalano insieme.
        const double forecastGiornaliero = 0.0009, longRunGiornaliero = 0.0016;
        var perCandela = 60.0 / 1440.0;

        var ratioPrima = Math.Sqrt(forecastGiornaliero) / Math.Sqrt(longRunGiornaliero);
        var ratioDopo = Math.Sqrt(forecastGiornaliero * perCandela) / Math.Sqrt(longRunGiornaliero * perCandela);

        Assert.Equal(ratioPrima, ratioDopo, precision: 12);
    }

    [Fact]
    public void A5_ConLaScalaSbagliata_IlRamoCompressioneEraVeroPerAritmetica()
    {
        // La prova del difetto, non della correzione: con un forecast GIORNALIERO contro una
        // realizzata PER CANDELA su 1h, r/f vale ~0,20 e la banda 1,5 dichiara «compressione»
        // qualunque cosa faccia il mercato. Con le due misure sulla stessa base, non scatta.
        const double sigmaGiornaliero = 0.03;
        var sigmaOrario = sigmaGiornaliero / Math.Sqrt(24);   // stessa volatilità, altra base

        var prima = ProcioneMGR.Services.Pipeline.RegimeChangeDetector.Evaluate(
            baselineRegime: 1, currentRegime: 1,
            forecastVol: sigmaGiornaliero, realizedVol: sigmaOrario,
            volBandMultiple: 1.5, baselineRunId: Guid.NewGuid(), forecastSource: "har-log-rv");

        var dopo = ProcioneMGR.Services.Pipeline.RegimeChangeDetector.Evaluate(
            baselineRegime: 1, currentRegime: 1,
            forecastVol: sigmaOrario, realizedVol: sigmaOrario,
            volBandMultiple: 1.5, baselineRunId: Guid.NewGuid(), forecastSource: "har-log-rv");

        Assert.True(prima.Triggered);
        Assert.Contains("compressione", prima.Reason);
        Assert.Contains("har-log-rv", prima.Reason);   // e la motivazione non dice più «GARCH» a caso
        Assert.False(dopo.Triggered);
    }
}
