using ProcioneMGR.Services.ML.Labeling;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C4, chiusura] Verifica del meta-modello. Il test che decide se il pezzo vale qualcosa è
/// <see cref="LearnableSignal_IsRecoveredOutOfFold"/> accoppiato a
/// <see cref="PureNoise_DoesNotProduceAFakeImprovement"/>: il primo pretende che un segnale
/// imparabile venga trovato, il secondo che il rumore NON produca un miglioramento. Solo insieme
/// dicono che il modello impara invece di adattarsi.
/// </summary>
public class MetaModelTrainerTests
{
    private static MetaLabelSample Sample(int i, bool profitable, double weight = 1.0) => new(
        i, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        PrimarySignal.Long, profitable,
        profitable ? TripleBarrierOutcome.Profit : TripleBarrierOutcome.Stop,
        profitable ? 2m : -1m, weight);

    private static TripleBarrierConfig Barrier(int bars = 5)
        => new() { ProfitTakePercent = 2m, StopLossPercent = 1m, VerticalBarrierBars = bars };

    // --- Il segnale imparabile ---------------------------------------------------------------------

    [Fact]
    public void LearnableSignal_IsRecoveredOutOfFold()
    {
        // La prima feature dice la verita' sull'esito; le altre due sono rumore. Un meta-modello
        // che funziona deve trovarla e separare le classi su dati che non ha visto.
        const int n = 600;
        var rnd = new Random(5);
        var samples = new List<MetaLabelSample>(n);
        var features = new List<float[]>(n);

        for (var i = 0; i < n; i++)
        {
            var profitable = rnd.NextDouble() < 0.5;
            samples.Add(Sample(i, profitable));
            features.Add([
                profitable ? 1f : 0f,
                (float)rnd.NextDouble(),
                (float)rnd.NextDouble(),
            ]);
        }

        var result = new MetaModelTrainer().TrainOutOfFold(samples, features, Barrier());

        Assert.True(result.SamplesScored > n / 2, $"attesi molti campioni valutati, ottenuti {result.SamplesScored}");

        var report = new MetaLabeler().Evaluate(samples, result.OutOfFoldProbabilities, threshold: 0.5);
        Assert.True(report.IsImprovement,
            $"precision {report.PrimaryPrecision:F3} -> {report.FilteredPrecision:F3} su {report.FilteredCount} operazioni");
        Assert.True(report.FilteredPrecision > 0.9,
            $"con una feature che dice la verita' la precision dovrebbe essere quasi 1, misurata {report.FilteredPrecision:F3}");
    }

    // --- Il rumore, che è il controllo che rende credibile il precedente ---------------------------

    [Fact]
    public void PureNoise_RarelyProducesAFakeImprovement()
    {
        // Nessuna feature informa. Un meta-modello che qui "migliorasse" starebbe solo adattandosi:
        // e' il fallimento piu' pericoloso perche' out-of-fold sembra credibile.
        //
        // Il test gira su MOLTI SEMI di proposito. La prima versione ne usava uno solo e falliva:
        // su quell'estrazione la precision saliva da 0,477 a 0,529 e il verdetto — che allora
        // confrontava due stime puntuali — diceva "miglioramento". Una singola estrazione non
        // distingue un filtro che sceglie da uno che tira a sorte; quello che si puo' pretendere e'
        // che il verdetto scatti RARAMENTE sul rumore. Con la soglia a 1,96 errori standard il
        // tasso atteso e' ~2,5%: qui si concede fino al 15% per non rendere il test fragile.
        const int n = 600;
        const int seeds = 20;
        var falsePositives = 0;
        var trainer = new MetaModelTrainer();
        var labeler = new MetaLabeler();

        for (var seed = 1; seed <= seeds; seed++)
        {
            var rnd = new Random(seed);
            var samples = new List<MetaLabelSample>(n);
            var features = new List<float[]>(n);
            for (var i = 0; i < n; i++)
            {
                samples.Add(Sample(i, rnd.NextDouble() < 0.5));
                features.Add([(float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble()]);
            }

            var result = trainer.TrainOutOfFold(samples, features, Barrier());
            if (labeler.Evaluate(samples, result.OutOfFoldProbabilities, threshold: 0.5).IsImprovement) falsePositives++;
        }

        Assert.True(falsePositives <= 3,
            $"sul puro rumore il verdetto e' scattato {falsePositives} volte su {seeds}: troppo spesso per fidarsene");
    }

    [Fact]
    public void SelectionZScore_IsZeroWhenTheFilterPicksAtRandom()
    {
        // Un filtro che tiene esattamente la stessa proporzione di vincenti del campione completo
        // non ha scelto nulla: lo z deve dirlo.
        var samples = Enumerable.Range(0, 200).Select(i => Sample(i, i % 2 == 0)).ToList();
        // Tiene 100 campioni con 50 vincenti: identica proporzione dell'insieme (50%).
        var probabilities = samples.Select((s, i) => i < 100 ? 0.9 : 0.1).ToList();

        var report = new MetaLabeler().Evaluate(samples, probabilities, threshold: 0.5);

        Assert.Equal(report.PrimaryPrecision, report.FilteredPrecision, 10);
        Assert.Equal(0.0, report.SelectionZScore, 6);
        Assert.False(report.IsImprovement);
    }

    // --- I vincoli strutturali --------------------------------------------------------------------

    [Fact]
    public void PurgeWindow_IsNeverShorterThanTheLabelHorizon()
    {
        // L'etichetta si risolve fino a i+orizzonte: una purga piu' corta lascerebbe il modello
        // vedere il futuro del fold di test.
        var samples = Enumerable.Range(0, 200).Select(i => Sample(i, i % 2 == 0)).ToList();
        var features = samples.Select(s => new[] { s.WasProfitable ? 1f : 0f }).ToList();

        var result = new MetaModelTrainer().TrainOutOfFold(
            samples, features, Barrier(bars: 20), new MetaModelConfig { PurgeWindow = 3 });

        Assert.Equal(20, result.PurgeWindowUsed);
    }

    [Fact]
    public void SampleWeightsAreHonouredByTheTrainer()
    {
        // Stessi dati, pesi diversi: se i pesi non arrivassero al trainer le due uscite sarebbero
        // identiche. Non si pretende una direzione, solo che facciano differenza.
        //
        // La feature e' volutamente RUMOROSA (concorda con l'esito nel 70% dei casi). Con una
        // feature perfettamente separante — come nella prima stesura di questo test — il modello
        // azzecca tutto comunque e i pesi non possono cambiare nulla: il test falliva misurando
        // la propria costruzione, non il codice.
        const int n = 400;
        var rnd = new Random(3);
        var samples = new List<MetaLabelSample>(n);
        var features = new List<float[]>(n);
        for (var i = 0; i < n; i++)
        {
            var profitable = i % 2 == 0;
            samples.Add(Sample(i, profitable));
            var agrees = rnd.NextDouble() < 0.7;
            features.Add([(float)rnd.NextDouble(), (agrees == profitable) ? 1f : 0f]);
        }

        var trainer = new MetaModelTrainer();
        var flat = trainer.TrainOutOfFold(samples, features, Barrier());
        var skewed = trainer.TrainOutOfFold(
            samples.Select((s, i) => s with { Weight = i % 2 == 0 ? 0.01 : 1.0 }).ToList(),
            features, Barrier());

        Assert.NotEqual(flat.OutOfFoldProbabilities, skewed.OutOfFoldProbabilities);
    }

    [Fact]
    public void TooFewSamples_AreDeclaredInsteadOfScoredInSample()
    {
        var samples = Enumerable.Range(0, 6).Select(i => Sample(i, i % 2 == 0)).ToList();
        var features = samples.Select(_ => new[] { 1f }).ToList();

        var result = new MetaModelTrainer().TrainOutOfFold(samples, features, Barrier());

        Assert.Equal(0, result.SamplesScored);
        Assert.All(result.OutOfFoldProbabilities, p => Assert.Equal(-1.0, p));
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void SingleClassSamples_ProduceNoModel()
    {
        // Tutti vincenti: non c'e' niente da separare, e inventare un modello sarebbe peggio che dirlo.
        var samples = Enumerable.Range(0, 200).Select(i => Sample(i, true)).ToList();
        var features = samples.Select(_ => new[] { 1f, 2f }).ToList();

        var result = new MetaModelTrainer().TrainOutOfFold(samples, features, Barrier());

        Assert.Equal(0, result.SamplesScored);
    }

    [Fact]
    public void MisalignedFeatures_AreRejected()
    {
        var samples = Enumerable.Range(0, 50).Select(i => Sample(i, true)).ToList();
        Assert.Throws<ArgumentException>(() =>
            new MetaModelTrainer().TrainOutOfFold(samples, [[1f]], Barrier()));
    }

    [Fact]
    public void TrainingIsReproducible()
    {
        const int n = 300;
        var rnd = new Random(21);
        var samples = new List<MetaLabelSample>(n);
        var features = new List<float[]>(n);
        for (var i = 0; i < n; i++)
        {
            var profitable = rnd.NextDouble() < 0.5;
            samples.Add(Sample(i, profitable));
            features.Add([profitable ? 1f : 0f, (float)rnd.NextDouble()]);
        }

        var trainer = new MetaModelTrainer();
        var a = trainer.TrainOutOfFold(samples, features, Barrier());
        var b = trainer.TrainOutOfFold(samples, features, Barrier());

        Assert.Equal(a.OutOfFoldProbabilities, b.OutOfFoldProbabilities);
    }
}
