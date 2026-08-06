using System.Text.Json;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF2/F5] Il verdetto di un run come candidato di flotta. Il caso che ha originato il fix
/// (2026-08-03, primo journal vuoto): un run con ZERO sopravvissuti non ha gambe nella
/// raccomandazione, e il lettore derivava i trade/mese dalle gambe — quindi la fascia grigia,
/// che vive per definizione nei run a zero sopravvissuti, non entrava MAI in coda.
/// </summary>
public sealed class FleetReaderEvaluateTests
{
    private static readonly string Ranges = JsonSerializer.Serialize(new PipelineDateRanges
    {
        SelectionFrom = new DateTime(2026, 1, 1),
        SelectionTo = new DateTime(2026, 6, 1),
        HoldoutFrom = new DateTime(2026, 6, 1),
        HoldoutTo = new DateTime(2026, 8, 1), // ~2 mesi di holdout
    });

    private static ValidatedCandidate Candidate(bool survived, decimal holdoutSharpe, int trades,
        string? reject = null, double? dsr = null, string strategy = "RsiOversold", string symbol = "DOT/USDT")
        => new()
        {
            StrategyName = strategy, Symbol = symbol, Timeframe = "15m",
            Survived = survived, HoldoutSharpe = holdoutSharpe, HoldoutTrades = trades,
            RejectReason = reject, DeflatedSharpe = dsr,
        };

    private static PipelineRecommendation Empty() => new() { CandidatesEvaluated = 58 };

    [Fact]
    public void ZeroSurvivors_WithContoTradeRejection_IsGrey_WithItsOwnFrequency()
    {
        // IL caso del fix: 0 sopravvissuti, bocciato "Solo 8 trade in holdout (< 10)" ma con
        // Sharpe positivo → grigio, con frequenza derivata dai SUOI trade (8 in ~2 mesi ≈ 4/mese).
        var verdict = FleetStateReader.Evaluate(Empty(),
            [Candidate(false, 1.2m, 8, reject: "Solo 8 trade in holdout (< 10)")], Ranges);

        Assert.NotNull(verdict);
        Assert.Equal("grey", verdict.Value.Band);
        Assert.InRange(verdict.Value.TradesPerMonth, 3.5m, 4.5m);
        Assert.Contains("RsiOversold DOT/USDT", verdict.Value.Summary);
        // [2026-08-06] Formattato come lo formatta la produzione, NON con la virgola scritta a
        // mano: `:F2` segue la cultura del processo, quindi l'asserzione letterale passava sul
        // Windows italiano dello sviluppatore e falliva sul runner Linux della CI — che è la
        // ragione per cui la CI era rossa anche su master dal 2026-08-03. Il difetto vero non è
        // qui: nessuna cultura è fissata da nessuna parte, quindi il guscio scrive «1,20» e il
        // motore in container scrive «1.20» nello STESSO journal. Vedi la nota in ROADMAP.
        Assert.Contains($"Sharpe holdout {1.2m:F2}", verdict.Value.Summary);
    }

    [Fact]
    public void GreyDsrBand_AlsoQualifies()
    {
        var verdict = FleetStateReader.Evaluate(Empty(),
            [Candidate(false, 0.9m, 20, reject: "DSR 0,88 ≤ 0,95 dopo 64 tentativi", dsr: 0.88)], Ranges);

        Assert.Equal("grey", verdict!.Value.Band);
    }

    [Fact]
    public void MeritRejections_AreNotGrey()
    {
        // Bocciati nel merito: Sharpe negativo (perde davvero) o DSR fuori fascia. Nessun candidato.
        Assert.Null(FleetStateReader.Evaluate(Empty(),
            [Candidate(false, -1.5m, 40, reject: "Sharpe holdout -1,50 < 0,30")], Ranges));
        Assert.Null(FleetStateReader.Evaluate(Empty(),
            [Candidate(false, 0.4m, 50, reject: "DSR 0,20 ≤ 0,95 dopo 64 tentativi", dsr: 0.20)], Ranges));
    }

    [Fact]
    public void LosingGrey_IsNotGrey()
    {
        // "Solo N trade" ma Sharpe holdout NEGATIVO: la finestra corta non salva chi perde.
        Assert.Null(FleetStateReader.Evaluate(Empty(),
            [Candidate(false, -0.3m, 5, reject: "Solo 5 trade in holdout (< 10)")], Ranges));
    }

    [Fact]
    public void BestGreyLeads_AndTheOthersAreCounted()
    {
        var verdict = FleetStateReader.Evaluate(Empty(),
        [
            Candidate(false, 0.8m, 6, reject: "Solo 6 trade in holdout (< 10)", strategy: "Macd"),
            Candidate(false, 1.9m, 9, reject: "Solo 9 trade in holdout (< 10)", strategy: "Supertrend", symbol: "XLM/USDT"),
        ], Ranges);

        Assert.Contains("Supertrend XLM/USDT", verdict!.Value.Summary); // vince lo Sharpe più alto
        Assert.Contains("+1 altri", verdict.Value.Summary);
    }

    [Fact]
    public void Survivors_ArePass_WithTheThinnestLegFrequency()
    {
        var recommendation = new PipelineRecommendation
        {
            BestCandidate = "RsiOversold DOT/USDT 15m",
            CandidatesEvaluated = 58,
            EnsembleLegs =
            [
                new ProposedLeg { Symbol = "DOT/USDT", Timeframe = "15m", HoldoutTrades = 40 },
                new ProposedLeg { Symbol = "XLM/USDT", Timeframe = "1h", HoldoutTrades = 12 },
            ],
        };

        var verdict = FleetStateReader.Evaluate(recommendation, [Candidate(true, 1.5m, 40)], Ranges);

        Assert.Equal("pass", verdict!.Value.Band);
        Assert.InRange(verdict.Value.TradesPerMonth, 5.5m, 6.5m); // 12 trade / ~2 mesi: la gamba più RADA
    }

    [Fact]
    public void UnusableWindow_MeansNoCandidate()
    {
        var grey = new List<ValidatedCandidate> { Candidate(false, 1.2m, 8, reject: "Solo 8 trade in holdout (< 10)") };
        Assert.Null(FleetStateReader.Evaluate(Empty(), grey, null));
        Assert.Null(FleetStateReader.Evaluate(Empty(), grey, "{ rotto"));
    }
}
