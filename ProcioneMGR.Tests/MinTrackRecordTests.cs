using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [F4 PRD Valore] Il power check MinTRL: la formula (Bailey-López de Prado 2012/2014) e lo stage
/// che la dichiara in testa al run. I numeri di ancoraggio non sono inventati: sono l'aritmetica
/// che la piattaforma ha già incontrato empiricamente (huntdense 2026-07-31: su un holdout di mesi
/// il puro caso arriva a Sharpe 2,2-2,9 al 99° con centinaia di tentativi, e nessun candidato
/// reale lo supera). Qui quella esperienza diventa un teorema verificato, non un ricordo.
/// </summary>
public class MinTrackRecordTests
{
    // ------------------------------------------------------------------ formula

    [Fact]
    public void MinTrl_IsInfinite_WhenObservedDoesNotBeatBenchmark()
    {
        Assert.Equal(double.PositiveInfinity, MinTrackRecord.MinTrl(0.05, 0.05));
        Assert.Equal(double.PositiveInfinity, MinTrackRecord.MinTrl(0.04, 0.05));
    }

    [Fact]
    public void MinTrl_QuartersWhenTheGapDoubles_GaussianCase()
    {
        // Identità strutturale della formula: a parità di SR osservato (quindi di correzione per
        // skew/curtosi), T−1 scala come 1/(SR−SR*)² — raddoppiare il margine divide per quattro.
        const double sr = 0.10;
        var t1 = MinTrackRecord.MinTrl(sr, benchmarkSr: sr - 0.02) - 1;
        var t2 = MinTrackRecord.MinTrl(sr, benchmarkSr: sr - 0.04) - 1;
        Assert.Equal(4.0, t1 / t2, precision: 6);
    }

    [Fact]
    public void MinDetectableSharpe_IsTheInverseOfMinTrl()
    {
        const int observations = 1_000;
        const double benchmark = 0.05;
        var sr = MinTrackRecord.MinDetectableSharpe(observations, benchmark);
        var backT = MinTrackRecord.MinTrl(sr, benchmark);
        Assert.InRange(backT, observations - 1, observations + 1);
    }

    [Fact]
    public void Anchor_AnnualizedSharpeOne_NeedsAlmostThreeYears_AgainstZeroBenchmark()
    {
        // L'ancora "semplice" senza multiple testing: SR annualizzato 1,0 su 1h (ppy 8760),
        // contro soglia zero al 95%, richiede ~(1.645)² anni ≈ 2,7 anni di osservazioni.
        // (Il "6,2 anni" dell'esperienza interna nasce quando la soglia NON è zero ma l'E[max]
        // dei tentativi — vedi il test dell'aritmetica huntdense qui sotto.)
        const int ppy = 8760;
        var perPeriod = MinTrackRecord.AnnualizedToPerPeriod(1.0, ppy);
        var t = MinTrackRecord.MinTrl(perPeriod, benchmarkSr: 0.0);
        var years = t / ppy;
        Assert.InRange(years, 2.4, 3.0);
    }

    [Fact]
    public void Anchor_FourMonthHoldout_With300Trials_DemandsImplausibleSharpe()
    {
        // L'aritmetica che ha prodotto dieci «0 sopravvissuti»: su ~4 mesi di holdout a 1h con
        // centinaia di tentativi, il minimo rilevabile supera Sharpe 2 annualizzato — cioè oltre
        // il plausibile per la classe direzionale-tecnica. È il numero che huntdense ha toccato
        // con mano (caso a 2,2-2,9) e che questo stage ora dichiara PRIMA di girare.
        const int ppy = 8760;
        var observations = (int)(ppy * (4.0 / 12.0));
        var nullBenchmark = MinTrackRecord.ExpectedMaxSharpeUnderNull(300, observations);
        var minAnnualized = MinTrackRecord.PerPeriodToAnnualized(
            MinTrackRecord.MinDetectableSharpe(observations, nullBenchmark), ppy);
        Assert.True(minAnnualized > 2.0, $"atteso >2, ottenuto {minAnnualized:F2}");
    }

    [Fact]
    public void ExpectedMaxUnderNull_GrowsWithTrials_ShrinksWithObservations()
    {
        var few = MinTrackRecord.ExpectedMaxSharpeUnderNull(10, 1_000);
        var many = MinTrackRecord.ExpectedMaxSharpeUnderNull(300, 1_000);
        Assert.True(many > few, "più tentativi ⇒ il caso arriva più in alto");

        var shortT = MinTrackRecord.ExpectedMaxSharpeUnderNull(100, 500);
        var longT = MinTrackRecord.ExpectedMaxSharpeUnderNull(100, 5_000);
        Assert.True(shortT > longT, "più osservazioni ⇒ stimatore più preciso ⇒ E[max] più basso");

        Assert.Equal(0.0, MinTrackRecord.ExpectedMaxSharpeUnderNull(1, 1_000));
    }

    // ------------------------------------------------------------------ stage

    private static (PipelineContext Ctx, List<string> Log) Context(double holdoutMonths, string timeframe = "1h")
    {
        var log = new List<string>();
        var to = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);
        var ctx = new PipelineContext
        {
            Universe = [new SeriesSpec { Symbol = "BTC/USDT", Timeframe = timeframe }],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = to.AddMonths(-12),
                SelectionTo = to.AddDays(-(int)(holdoutMonths * 30.44)),
                HoldoutFrom = to.AddDays(-(int)(holdoutMonths * 30.44)),
                HoldoutTo = to,
            },
            Log = log.Add,
        };
        return (ctx, log);
    }

    private static StageConfig Config(params (string Key, string Value)[] overrides)
    {
        var stage = new PowerCheckStage();
        var parameters = stage.ParameterDefinitions.ToDictionary(d => d.Key, d => d.DefaultValue);
        foreach (var (key, value) in overrides) parameters[key] = value;
        return new StageConfig { Type = stage.Name, Order = stage.DefaultOrder, Enabled = true, Parameters = parameters };
    }

    [Fact]
    public async Task Stage_DeclaresUnderpoweredRun_UpFront_WithoutBlockingByDefault()
    {
        var (ctx, log) = Context(holdoutMonths: 4);

        await new PowerCheckStage().ExecuteAsync(ctx, Config(), CancellationToken.None);

        Assert.NotNull(ctx.Power);
        Assert.True(ctx.Power!.Underpowered, "4 mesi × 300 tentativi: l'esito «0 promossi» è aritmetico");
        Assert.Contains(log, l => l.Contains("SOTTO POTENZA"));
        Assert.Contains(log, l => l.Contains("per passare serve Sharpe"));
    }

    [Fact]
    public async Task Stage_Enforce_StopsTheRun_WithTheExplanation()
    {
        var (ctx, _) = Context(holdoutMonths: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PowerCheckStage().ExecuteAsync(ctx, Config(("enforce", "true")), CancellationToken.None));

        Assert.Contains("SOTTO POTENZA", ex.Message);
        Assert.Contains("forward test Paper", ex.Message); // il rimedio F5, indicato nel messaggio
    }

    [Fact]
    public async Task Stage_LongHoldoutFewTrials_HasPower()
    {
        // 3 anni di holdout con 10 tentativi: il minimo rilevabile scende sotto il tetto
        // plausibile — la potenza non è un muro, è una funzione della finestra e della disciplina.
        var (ctx, _) = Context(holdoutMonths: 36);

        await new PowerCheckStage().ExecuteAsync(ctx, Config(("expectedTrials", "10")), CancellationToken.None);

        Assert.NotNull(ctx.Power);
        Assert.False(ctx.Power!.Underpowered);
        Assert.True(ctx.Power.WorstMinDetectableAnnualizedSharpe < 2.0);
    }

    [Fact]
    public async Task Stage_MixedUniverse_DeclaresPartialPower_InsteadOfContradictingItself()
    {
        // [J17] Il difetto riprodotto: giudizio con All (tutte le serie sotto potenza) ma riepilogo
        // col Max (il peggiore). Su un universo 1h+4h con holdout lungo e pochi tentativi, l'1h ha
        // potere e il 4h no: prima usciva «Potenza OK: minimo rilevabile 8,91» — una frase che si
        // contraddice da sola. Ora il caso misto è uno stato dichiarato, coi timeframe deboli nominati.
        var log = new List<string>();
        var to = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);
        var ctx = new PipelineContext
        {
            Universe =
            [
                new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1h" },
                new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1d" },
                new SeriesSpec { Symbol = "ETH/USDT", Timeframe = "1d" },
            ],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = to.AddMonths(-12),
                SelectionTo = to.AddMonths(-6),
                HoldoutFrom = to.AddMonths(-6),
                HoldoutTo = to,
            },
            Log = log.Add,
        };
        var stage = new PowerCheckStage();

        // Primo passaggio: si leggono i minimi rilevabili VERI dei due gruppi. L'annualizzazione
        // rende i minimi quasi indipendenti dal timeframe (entrambi ~ z·sqrt(365/giorni)): la
        // differenza fra 1h e 1d viene solo dall'inflazione a T piccolo, quindi il tetto del caso
        // misto va messo NEL MEZZO dei due numeri reali, non scelto a occhio.
        await stage.ExecuteAsync(ctx, Config(("expectedTrials", "100")), CancellationToken.None);
        var min1h = ctx.Power!.Series.Where(s => s.Timeframe == "1h").Min(s => s.MinDetectableAnnualizedSharpe);
        var min1d = ctx.Power!.Series.Where(s => s.Timeframe == "1d").Min(s => s.MinDetectableAnnualizedSharpe);
        Assert.True(min1d > min1h, "il gruppo a T piccolo (1d) deve avere il minimo rilevabile più alto");
        var cap = (min1h + min1d) / 2.0;

        // Secondo passaggio, col tetto fra i due: 1h ha potere, 1d no — il caso misto vero.
        log.Clear();
        await stage.ExecuteAsync(ctx, Config(
            ("expectedTrials", "100"),
            ("maxPlausibleSharpe", cap.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))), CancellationToken.None);

        Assert.False(ctx.Power!.Underpowered, "il verdetto complessivo non è SOTTO POTENZA: un gruppo ha potere");
        Assert.Contains("1d", ctx.Power.UnderpoweredTimeframes());
        Assert.DoesNotContain("1h", ctx.Power.UnderpoweredTimeframes());

        var summary = stage.Summarize(ctx);
        Assert.Contains("POTENZA PARZIALE", summary.Text);
        Assert.Contains("1d", summary.Text);
        Assert.DoesNotContain("Potenza OK", summary.Text);
        Assert.Contains(log, l => l.Contains("POTENZA PARZIALE"));

        // Il log è per GRUPPO, non per serie: le due serie 1d producono UNA riga di misura, non due
        // righe identiche che sembrano due misure indipendenti.
        Assert.Single(log.Where(l => l.Contains("1d (2 serie")));
    }

    [Fact]
    public void Stage_Summary_CarriesTheHeadlineNumbers()
    {
        var stage = new PowerCheckStage();
        var (ctx, _) = Context(holdoutMonths: 4);
        stage.ExecuteAsync(ctx, Config(), CancellationToken.None).GetAwaiter().GetResult();

        var summary = stage.Summarize(ctx);

        Assert.Equal("PowerCheck", summary.StageName);
        Assert.Contains("SOTTO POTENZA", summary.Text);
        Assert.True(summary.Metrics["SharpeMinRilevabileAnn"] > 2.0m);
        Assert.Equal(300m, summary.Metrics["TentativiAssunti"]);
    }
}
