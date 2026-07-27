using ProcioneMGR.Data;
using ProcioneMGR.Services.ML.Labeling;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C4] Verifica del meta-labeling. Il test che conta è l'ultimo: un **edge piantato** con
/// asimmetria di barriera nota, che la catena deve recuperare. Senza quello, un risultato
/// positivo su dati reali non direbbe se ha funzionato il metodo o il caso — è lo stesso
/// principio della fase `control` di PlatformExpand.
/// </summary>
public class MetaLabelerTests
{
    private static OhlcvData Bar(int i, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = open, High = high, Low = low, Close = close, Volume = 100m,
    };

    private static TripleBarrierConfig Config(int bars = 5)
        => new() { ProfitTakePercent = 2m, StopLossPercent = 1m, VerticalBarrierBars = bars };

    // --- Costruzione dei campioni ----------------------------------------------------------------

    [Fact]
    public void OnlyBarsWithASignal_BecomeSamples()
    {
        var candles = Enumerable.Range(0, 30).Select(i => Bar(i, 100m, 100m, 100m, 100m)).ToList();
        var signals = Enumerable.Repeat(PrimarySignal.None, 30).ToList();
        signals[2] = PrimarySignal.Long;
        signals[7] = PrimarySignal.Long;

        var samples = new MetaLabeler().BuildSamples(candles, signals, Config());

        Assert.Equal(2, samples.Count);
        Assert.Equal([2, 7], samples.Select(s => s.EntryIndex));
    }

    [Fact]
    public void SignalsAreLabelledFromTheirOwnSide()
    {
        // Prezzo che scende: sbagliato per un long, giusto per uno short.
        var candles = Enumerable.Range(0, 30).Select(i => Bar(i, 100m, 100m, 100m, 100m)).ToList();
        candles[3] = Bar(3, 100m, 100m, 97.5m, 98m);

        var longSignals = Enumerable.Repeat(PrimarySignal.None, 30).ToList();
        longSignals[2] = PrimarySignal.Long;
        var shortSignals = Enumerable.Repeat(PrimarySignal.None, 30).ToList();
        shortSignals[2] = PrimarySignal.Short;

        var labeler = new MetaLabeler();
        var asLong = labeler.BuildSamples(candles, longSignals, Config()).Single();
        var asShort = labeler.BuildSamples(candles, shortSignals, Config()).Single();

        Assert.Equal(TripleBarrierOutcome.Stop, asLong.Outcome);
        Assert.False(asLong.WasProfitable);
        Assert.Equal(TripleBarrierOutcome.Profit, asShort.Outcome);
        Assert.True(asShort.WasProfitable);
    }

    [Fact]
    public void ATimeExitIsNotCountedAsSuccess()
    {
        // Uscita a tempo: con costi e slippage un pareggio non e' una vittoria.
        var candles = Enumerable.Range(0, 30).Select(i => Bar(i, 100m, 100m, 100m, 100m)).ToList();
        var signals = Enumerable.Repeat(PrimarySignal.None, 30).ToList();
        signals[1] = PrimarySignal.Long;

        var sample = new MetaLabeler().BuildSamples(candles, signals, Config()).Single();

        Assert.Equal(TripleBarrierOutcome.Vertical, sample.Outcome);
        Assert.False(sample.WasProfitable);
    }

    [Fact]
    public void MisalignedSignals_AreRejectedInsteadOfSilentlyTruncated()
    {
        var candles = Enumerable.Range(0, 30).Select(i => Bar(i, 100m, 100m, 100m, 100m)).ToList();
        var signals = Enumerable.Repeat(PrimarySignal.Long, 10).ToList();

        Assert.Throws<ArgumentException>(() => new MetaLabeler().BuildSamples(candles, signals, Config()));
    }

    // --- Valutazione del filtro -------------------------------------------------------------------

    [Fact]
    public void Evaluate_ReportsPrecisionRecallAndSurvivalTogether()
    {
        var samples = new List<MetaLabelSample>
        {
            Sample(0, true), Sample(1, false), Sample(2, true), Sample(3, false),
        };
        // Il meta-modello scarta esattamente i due perdenti.
        var probabilities = new List<double> { 0.9, 0.1, 0.8, 0.2 };

        var report = new MetaLabeler().Evaluate(samples, probabilities, threshold: 0.5);

        Assert.Equal(4, report.PrimaryCount);
        Assert.Equal(0.5, report.PrimaryPrecision, 10);
        Assert.Equal(2, report.FilteredCount);
        Assert.Equal(1.0, report.FilteredPrecision, 10);
        Assert.Equal(1.0, report.Recall, 10);          // nessun vincente perso
        Assert.Equal(0.5, report.SurvivalRate, 10);
    }

    [Fact]
    public void APrecisionGainOnACollapsedSampleIsNotCountedAsImprovement()
    {
        // IL PUNTO ONESTO DELL'ITEM: buttare via il 99% dei segnali fa salire la precision di un
        // campione che non misura piu' nulla. Il verdetto deve dire di no.
        var samples = Enumerable.Range(0, 200).Select(i => Sample(i, i % 2 == 0)).ToList();
        var probabilities = samples.Select((s, i) => i < 3 && s.WasProfitable ? 0.99 : 0.01).ToList();

        var report = new MetaLabeler().Evaluate(samples, probabilities, threshold: 0.5);

        Assert.True(report.FilteredPrecision > report.PrimaryPrecision, "la precision sale...");
        Assert.False(report.IsImprovement, "...ma su un campione crollato non e' un miglioramento");
    }

    [Fact]
    public void ARealImprovementIsRecognised()
    {
        var samples = Enumerable.Range(0, 200).Select(i => Sample(i, i % 2 == 0)).ToList();
        // Tiene tutti i vincenti e meta' dei perdenti: precision 0,5 -> 0,67 su 150 operazioni.
        var probabilities = samples.Select((s, i) => s.WasProfitable || i % 4 == 1 ? 0.9 : 0.1).ToList();

        var report = new MetaLabeler().Evaluate(samples, probabilities, threshold: 0.5);

        Assert.True(report.IsImprovement);
        Assert.True(report.FilteredPrecision > report.PrimaryPrecision);
        Assert.True(report.SurvivalRate >= 0.2);
        Assert.Equal(1.0, report.Recall, 10);
    }

    [Fact]
    public void MisalignedProbabilities_AreRejected()
    {
        var samples = new List<MetaLabelSample> { Sample(0, true) };
        Assert.Throws<ArgumentException>(() => new MetaLabeler().Evaluate(samples, [0.5, 0.5], 0.5));
    }

    // --- L'esperimento di controllo: edge piantato -------------------------------------------------

    [Fact]
    public void PlantedEdge_IsRecoveredByTheChain()
    {
        // Si pianta un'asimmetria NOTA: quando la barra precedente ha volume alto il prezzo sale
        // abbastanza da toccare il take profit; altrimenti scende fino allo stop. Il primario
        // segnala long OVUNQUE (precision attesa ~50%); un meta-modello che guardasse il volume
        // dovrebbe isolare i vincenti. Qui il "meta-modello" e' la regola vera, per verificare che
        // la CATENA di etichettatura e valutazione recuperi l'edge piantato.
        const int n = 600;
        var rnd = new Random(11);
        var winner = new bool[n];
        for (var i = 0; i < n; i++) winner[i] = rnd.NextDouble() < 0.5;

        // Ogni barra CHIUDE a 100, così ogni ingresso parte dallo stesso livello e le due barriere
        // sono sempre a +2% e −1%. La barra j incorpora nel suo range l'esito deciso per l'ingresso
        // fatto alla barra j−1: sale a 103 (tocca il profitto) o scende a 98 (tocca lo stop).
        var candles = new List<OhlcvData> { Bar(0, 100m, 100m, 100m, 100m) };
        for (var j = 1; j < n; j++)
        {
            var w = winner[j - 1];
            candles.Add(Bar(j, 100m, w ? 103m : 100m, w ? 100m : 98m, 100m));
        }

        // Il segnale primario e' long su ogni barra tranne la coda non risolvibile.
        var signals = Enumerable.Repeat(PrimarySignal.Long, n).ToList();
        var labeler = new MetaLabeler();
        var samples = labeler.BuildSamples(candles, signals, new TripleBarrierConfig
        {
            ProfitTakePercent = 2m,
            StopLossPercent = 1m,
            VerticalBarrierBars = 1,   // l'esito si decide alla barra successiva
        });

        Assert.True(samples.Count > 400, $"campioni attesi molti, ottenuti {samples.Count}");

        // Il primario da solo deve stare vicino al 50%: l'edge c'e' ma lui non lo sfrutta.
        var primaryPrecision = samples.Count(s => s.WasProfitable) / (double)samples.Count;
        Assert.InRange(primaryPrecision, 0.40, 0.60);

        // Il meta-modello "oracolo" conosce la regola piantata: l'esito dell'ingresso alla barra i
        // e' winner[i], realizzato dal range della barra i+1.
        var probabilities = samples.Select(s => winner[s.EntryIndex] ? 0.9 : 0.1).ToList();
        var report = labeler.Evaluate(samples, probabilities, threshold: 0.5);

        Assert.True(report.IsImprovement, "la catena deve riconoscere l'edge piantato come miglioramento");
        Assert.Equal(1.0, report.FilteredPrecision, 6);   // l'oracolo isola tutti e soli i vincenti
        Assert.Equal(1.0, report.Recall, 6);
        Assert.True(report.FilteredMeanReturnPercent > report.PrimaryMeanReturnPercent);
    }

    private static MetaLabelSample Sample(int index, bool profitable) => new(
        index, DateTime.UnixEpoch, PrimarySignal.Long, profitable,
        profitable ? TripleBarrierOutcome.Profit : TripleBarrierOutcome.Stop,
        profitable ? 2m : -1m, 1.0);
}
