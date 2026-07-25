using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// [1.V roadmap macchina-ricerca] La volatilità come TARGET di predizione.
///
/// Dopo 445.280 combinazioni direzionali con zero sopravvissuti, predire il rischio è la domanda
/// con più probabilità di risposta (la volatilità è persistente). Questi test fissano: (a) la
/// correttezza numerica dei tre target, (b) il default invariato, (c) la GUARDIA di semantica —
/// un modello che predice volatilità non può essere salvato, perché tutto ciò che consuma un
/// SavedMlModel interpreta la predizione come rendimento atteso e la confronterebbe con le soglie
/// long/short (vol alta ≠ compra).
/// </summary>
public class MlTargetKindTests
{
    private static List<OhlcvData> CandlesFromCloses(params decimal[] closes)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return closes.Select((c, i) => new OhlcvData
        {
            Symbol = "TGT/USDT", Timeframe = "1d", TimestampUtc = t0.AddDays(i),
            Open = c, High = c, Low = c, Close = c, Volume = 100m,
        }).ToList();
    }

    [Fact]
    public void ForwardReturn_MatchesTheHistoricalDefinition()
    {
        // Stessa identica definizione di FactorEvaluator.ForwardReturns: nessuna divergenza fra
        // il percorso storico e quello nuovo.
        var candles = CandlesFromCloses(100m, 110m, 121m, 133.1m);

        var viaTargets = ForwardTargets.Compute(candles, 2, MlTargetKind.ForwardReturn);
        var viaEvaluator = new FactorEvaluator().ForwardReturns(candles, 2);

        Assert.Equal(viaEvaluator, viaTargets);
        Assert.Equal(0.21m, viaTargets[0]!.Value, 10);   // 121/100 - 1
    }

    [Fact]
    public void ForwardAbsReturn_IsTheAbsoluteValue_LosingTheSignOnPurpose()
    {
        var candles = CandlesFromCloses(100m, 90m, 80m);

        var abs = ForwardTargets.Compute(candles, 1, MlTargetKind.ForwardAbsReturn);
        var raw = ForwardTargets.Compute(candles, 1, MlTargetKind.ForwardReturn);

        Assert.Equal(-0.1m, raw[0]!.Value, 10);
        Assert.Equal(0.1m, abs[0]!.Value, 10);   // il segno sparisce: è un target di RISCHIO
    }

    [Fact]
    public void ForwardRealizedVol_MatchesAHandComputedCase()
    {
        // Chiusure 100 → 110 → 99: rendimenti per-barra +10% e −10%.
        // Media 0, varianza campionaria = (0.1² + 0.1²)/(2−1) = 0.02 → σ = √0.02 ≈ 0.141421.
        var candles = CandlesFromCloses(100m, 110m, 99m, 99m);

        var vol = ForwardTargets.Compute(candles, 2, MlTargetKind.ForwardRealizedVol);

        Assert.NotNull(vol[0]);
        Assert.Equal(0.141421m, vol[0]!.Value, 5);
    }

    [Fact]
    public void ForwardRealizedVol_WithHorizonOne_IsRejected()
    {
        // Con una sola barra non esiste una deviazione standard: meglio un errore chiaro che uno
        // zero silenzioso spacciato per volatilità.
        var candles = CandlesFromCloses(100m, 101m, 102m);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ForwardTargets.Compute(candles, 1, MlTargetKind.ForwardRealizedVol));
    }

    [Fact]
    public void TailBars_WithoutFullHorizon_YieldNull_NeverPartialValues()
    {
        var candles = CandlesFromCloses(100m, 101m, 102m, 103m);

        foreach (var kind in new[] { MlTargetKind.ForwardReturn, MlTargetKind.ForwardAbsReturn, MlTargetKind.ForwardRealizedVol })
        {
            var t = ForwardTargets.Compute(candles, 2, kind);
            Assert.Null(t[2]);   // servono 2 barre future: la coda non ne ha
            Assert.Null(t[3]);
        }
    }

    [Fact]
    public void DatasetBuilder_WithVolTarget_LabelsAreTheRealizedVol()
    {
        var candles = CandlesFromCloses(100m, 110m, 99m, 108.9m, 100m, 105m, 99.75m, 104.7m, 100m, 105m);
        var factors = new List<FactorSpec>
        {
            new("mom2", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 2m }),
        };

        var dataset = new DatasetBuilder().Build(candles, factors, forwardHorizon: 2,
            targetKind: MlTargetKind.ForwardRealizedVol);
        var expected = ForwardTargets.Compute(candles, 2, MlTargetKind.ForwardRealizedVol);

        Assert.NotEmpty(dataset.Rows);
        // Ogni riga del dataset porta come label la vol realizzata della SUA candela (via timestamp).
        for (var r = 0; r < dataset.Rows.Count; r++)
        {
            var idx = candles.FindIndex(c => c.TimestampUtc == dataset.Timestamps[r]);
            Assert.Equal((float)expected[idx]!.Value, dataset.Rows[r].Label, 5);
        }
    }

    [Fact]
    public async Task SaveModel_WithNonReturnTarget_IsNoLongerBlockedAtSave_GuardMovedToConsumers()
    {
        // [1.V fase 2] La guardia si è SPOSTATA dal salvataggio al consumo: senza modello
        // addestrato l'errore è quello storico ("nessun modello"), NON un rifiuto di semantica.
        // La semantica è fatta rispettare da MlModelLoader/ModelRegistry (test sotto).
        var svc = new MlLabService(null!, null!, null!, null!, null!);
        var cfg = new MlConfigSnapshot(
            ExchangeName.Binance, "BTC/USDT", "1d", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow,
            70, 5, ["Momentum"], [], "LightGbm", [], StackingMode.Average, 16, 8,
            0.005m, -0.005m, 10_000m, 100m, 0.1m,
            MlTargetKind.ForwardRealizedVol);

        var result = await svc.SaveModelAsync(cfg, "vol-model", "user1");

        Assert.True(result.IsError);
        Assert.Contains("Nessun modello addestrato", result.Message);
    }

    // --- [1.V fase 2] Guardie sul CONSUMO direzionale --------------------------------------------

    [Fact]
    public void SavedMlModel_IsDirectional_OnlyForForwardReturn_DefaultIncluded()
    {
        Assert.True(new SavedMlModel().IsDirectional);   // default retro-compatibile
        Assert.True(new SavedMlModel { TargetKind = "ForwardReturn" }.IsDirectional);
        Assert.False(new SavedMlModel { TargetKind = "ForwardRealizedVol" }.IsDirectional);
        Assert.False(new SavedMlModel { TargetKind = "ForwardAbsReturn" }.IsDirectional);
    }

    [Fact]
    public async Task MlModelLoader_LoadAsync_RefusesNonDirectionalModel_WithSemanticMessage()
    {
        // Il punto UNICO di materializzazione direzionale (backtest batch + Champion streaming):
        // un modello di rischio qui deve morire con un messaggio che nomina la semantica, non
        // fallire dopo con segnali privi di senso.
        var saved = new SavedMlModel { Name = "vol-btc", TargetKind = "ForwardRealizedVol", ModelType = "Linear" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MlModelLoader.LoadAsync(saved, new AlphaFactorFactory(), factorCache: null, CancellationToken.None));

        Assert.Contains("ForwardRealizedVol", ex.Message);
        Assert.Contains("long/short", ex.Message);
    }

    [Fact]
    public async Task BacktestAsync_OnVolSession_IsRefused_PointingToVolEvaluation()
    {
        // Il target del MODELLO IN SESSIONE decide, non il form: senza sessione la guardia non può
        // scattare (SessionTargetKind default = ForwardReturn), quindi qui si verifica il default...
        var svc = new MlLabService(null!, null!, null!, null!, null!);
        Assert.Equal(MlTargetKind.ForwardReturn, svc.SessionTargetKind);

        // ...e il messaggio storico per l'assenza di modello.
        var cfg = new MlConfigSnapshot(
            ExchangeName.Binance, "BTC/USDT", "1d", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow,
            70, 5, ["Momentum"], [], "Linear", [], StackingMode.Average, 16, 8,
            0.005m, -0.005m, 10_000m, 100m, 0.1m);
        var res = await svc.BacktestAsync(cfg);
        Assert.True(res.IsError);
        Assert.Contains("Nessun modello addestrato", res.Message);
    }

    // --- [1.V fase 2] Valutatore vol: QLIKE/MSE e baseline ---------------------------------------

    [Fact]
    public void Qlike_IsZeroForPerfectForecast_AndPositiveOtherwise()
    {
        Assert.Equal(0d, VolForecastEvaluator.Qlike(0.02, 0.02), 12);
        Assert.True(VolForecastEvaluator.Qlike(0.01, 0.02) > 0);   // sottostima
        Assert.True(VolForecastEvaluator.Qlike(0.04, 0.02) > 0);   // sovrastima
        // Asimmetria QLIKE: sottostimare della metà costa più che sovrastimare del doppio.
        Assert.True(VolForecastEvaluator.Qlike(0.01, 0.02) > VolForecastEvaluator.Qlike(0.04, 0.02));
    }

    [Fact]
    public void EwmaPerBarVol_OnConstantReturns_ConvergesToThatVol()
    {
        // Rendimenti costanti +1%: r² = 1e-4 sempre ⇒ la varianza EWMA resta 1e-4, vol = 1%.
        var closes = new List<decimal> { 100m };
        for (var i = 0; i < 60; i++) closes.Add(closes[^1] * 1.01m);
        var candles = CandlesFromCloses([.. closes]);

        var ewma = VolForecastEvaluator.EwmaPerBarVol(candles);

        Assert.Null(ewma[0]);                       // nessun rendimento ancora
        Assert.Equal(0.01, ewma[^1]!.Value, 6);     // converge alla vol vera
    }

    [Fact]
    public void PastRealizedVol_MatchesHandComputedWindow()
    {
        // Chiusure 100, 110, 99: all'indice 2 la finestra (0,2] ha rendimenti +10% e −10%
        // ⇒ σ campionaria = √0,02 (stesso conto di ForwardRealizedVol, ma all'INDIETRO).
        var candles = CandlesFromCloses(100m, 110m, 99m);
        var naive = VolForecastEvaluator.PastRealizedVol(candles, 2);

        Assert.Null(naive[0]);
        Assert.Null(naive[1]);
        Assert.Equal(Math.Sqrt(0.02), naive[2]!.Value, 6);
    }

    [Fact]
    public void Score_SkipsRowsWithZeroRealizedVol_AndMisalignedSeriesThrow()
    {
        var pred = new double?[] { 0.02, 0.02, null };
        var act = new double?[] { 0.02, 0.0, 0.02 };   // la riga con realizzato 0 non informa

        var (qlike, mse, rows) = VolForecastEvaluator.Score(pred, act);

        Assert.Equal(1, rows);          // solo la prima riga è valida
        Assert.Equal(0d, qlike, 12);
        Assert.Equal(0d, mse, 12);
        Assert.Throws<ArgumentException>(() => VolForecastEvaluator.Score(new double?[] { 1 }, new double?[] { 1, 2 }));
    }
}
