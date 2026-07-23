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
    public async Task SaveModel_WithNonReturnTarget_IsRefused_BeforeAnythingElse()
    {
        // La guardia è la PRIMA verifica di SaveModelAsync, indipendente dallo stato di
        // addestramento: con un target non-rendimento il rifiuto deve nominare la semantica
        // ("segnale direzionale"), non un generico "nessun modello addestrato".
        var svc = new MlLabService(null!, null!, null!, null!, null!);
        var cfg = new MlConfigSnapshot(
            ExchangeName.Binance, "BTC/USDT", "1d", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow,
            70, 5, ["Momentum"], [], "LightGbm", [], StackingMode.Average, 16, 8,
            0.005m, -0.005m, 10_000m, 100m, 0.1m,
            MlTargetKind.ForwardRealizedVol);

        var result = await svc.SaveModelAsync(cfg, "vol-model", "user1");

        Assert.True(result.IsError);
        Assert.Contains("direzionale", result.Message);

        // E con il default (rendimento) la guardia NON scatta: l'errore torna quello storico.
        var okCfg = cfg with { TargetKind = MlTargetKind.ForwardReturn };
        var historic = await svc.SaveModelAsync(okCfg, "ret-model", "user1");
        Assert.Contains("Nessun modello addestrato", historic.Message);
    }
}
