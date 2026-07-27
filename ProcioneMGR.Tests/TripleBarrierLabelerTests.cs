using ProcioneMGR.Data;
using ProcioneMGR.Services.ML.Labeling;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C4] Verifica dell'etichettatura triple-barrier. Come per gli altri strumenti di misura della
/// piattaforma, i test costruiscono serie in cui la risposta giusta è NOTA per costruzione — un
/// percorso che tocca solo il profitto, uno che tocca solo lo stop, uno che non tocca niente, e
/// il caso ambiguo in cui li tocca entrambi nella stessa barra.
/// </summary>
public class TripleBarrierLabelerTests
{
    private static OhlcvData Bar(int i, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 100m,
    };

    /// <summary>Serie piatta a 100, su cui i test iniettano il percorso che vogliono misurare.</summary>
    private static List<OhlcvData> Flat(int n, decimal price = 100m)
        => Enumerable.Range(0, n).Select(i => Bar(i, price, price, price, price)).ToList();

    private static TripleBarrierConfig Config(decimal tp = 2m, decimal sl = 1m, int bars = 5, OrderSide side = OrderSide.Buy)
        => new() { ProfitTakePercent = tp, StopLossPercent = sl, VerticalBarrierBars = bars, Side = side };

    // --- I tre esiti -----------------------------------------------------------------------------

    [Fact]
    public void PriceReachingUpperBarrier_IsLabelledProfit()
    {
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 102.5m, 100m, 102m);   // tocca +2% alla barra 1

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();

        Assert.Equal(TripleBarrierOutcome.Profit, label.Outcome);
        Assert.Equal(1, label.ExitIndex);
        Assert.Equal(2m, label.ReturnPercent, 6);   // esce ESATTAMENTE alla barriera, non al close
    }

    [Fact]
    public void PriceReachingLowerBarrier_IsLabelledStop()
    {
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 100m, 98.5m, 99m);     // tocca −1% alla barra 1

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();

        Assert.Equal(TripleBarrierOutcome.Stop, label.Outcome);
        Assert.Equal(-1m, label.ReturnPercent, 6);
    }

    [Fact]
    public void PriceTouchingNeitherBarrier_IsLabelledVertical()
    {
        var candles = Flat(10);   // prezzo immobile: nessuna barriera raggiunta

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();

        Assert.Equal(TripleBarrierOutcome.Vertical, label.Outcome);
        Assert.Equal(5, label.ExitIndex);          // esce alla barriera verticale
        Assert.Equal(0m, label.ReturnPercent, 6);
    }

    // --- L'ambiguità intra-barra ------------------------------------------------------------------

    [Fact]
    public void WhenBothBarriersAreTouchedInTheSameBar_TheStopWins()
    {
        // L'OHLC non dice quale sia arrivato prima. La scelta pessimistica e' l'unica che non
        // produce un backtest piu' bello della realta'.
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 103m, 98m, 100m);   // tocca sia +2% sia −1%

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();

        Assert.Equal(TripleBarrierOutcome.Stop, label.Outcome);
        Assert.Equal(-1m, label.ReturnPercent, 6);
    }

    // --- Look-ahead -------------------------------------------------------------------------------

    [Fact]
    public void LabelIgnoresTheEntryBarItself_OnlyTheFutureCounts()
    {
        // La barra di ingresso sfonda entrambe le barriere, ma l'ingresso e' al suo CLOSE: quel
        // movimento e' gia' passato e non puo' etichettare l'ingresso.
        var candles = Flat(10);
        candles[0] = Bar(0, 100m, 130m, 70m, 100m);

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();

        Assert.Equal(TripleBarrierOutcome.Vertical, label.Outcome);
    }

    [Fact]
    public void TailBarsWithoutEnoughFuture_AreLeftUnlabelled()
    {
        var candles = Flat(10);

        var labels = new TripleBarrierLabeler().Label(candles, Config(bars: 5));

        // 10 barre, orizzonte 5 -> ingressi risolvibili solo da 0 a 4.
        Assert.Equal(5, labels.Count);
        Assert.Equal(4, labels[^1].EntryIndex);
        Assert.All(labels, l => Assert.True(l.ExitIndex < candles.Count));
    }

    // --- Lato corto -------------------------------------------------------------------------------

    [Fact]
    public void ForAShort_ADroppingPriceIsProfit()
    {
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 100m, 97.5m, 98m);   // −2%: per uno short e' il take profit

        var label = new TripleBarrierLabeler().Label(candles, Config(side: OrderSide.Sell)).First();

        Assert.Equal(TripleBarrierOutcome.Profit, label.Outcome);
        Assert.Equal(2m, label.ReturnPercent, 6);   // rendimento POSITIVO per lo short
    }

    [Fact]
    public void ForAShort_ARisingPriceIsStop()
    {
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 101.5m, 100m, 101m);

        var label = new TripleBarrierLabeler().Label(candles, Config(side: OrderSide.Sell)).First();

        Assert.Equal(TripleBarrierOutcome.Stop, label.Outcome);
        Assert.Equal(-1m, label.ReturnPercent, 6);
    }

    // --- Il confronto che giustifica l'intero item ------------------------------------------------

    [Fact]
    public void TripleBarrier_DisagreesWithFixedHorizon_WhenThePathHitsTheStopFirst()
    {
        // IL CASO CHE MOTIVA C4: il prezzo crolla sotto lo stop e poi risale, chiudendo in utile.
        // L'etichetta a orizzonte fisso dice "vincente"; la realta' di un ordine con bracket dice
        // "stoppato in perdita". Se questo test fallisse, il triple-barrier non aggiungerebbe nulla
        // rispetto a cio' che la piattaforma gia' faceva.
        var candles = Flat(10);
        candles[1] = Bar(1, 100m, 100m, 97m, 98m);       // sfonda lo stop a −1%
        candles[2] = Bar(2, 98m, 101m, 98m, 101m);       // ...poi risale
        candles[3] = Bar(3, 101m, 101m, 101m, 101m);
        candles[4] = Bar(4, 101m, 101m, 101m, 101m);
        candles[5] = Bar(5, 101m, 101m, 101m, 101m);

        var label = new TripleBarrierLabeler().Label(candles, Config()).First();
        var fixedHorizonReturn = (candles[5].Close - candles[0].Close) / candles[0].Close * 100m;

        Assert.Equal(TripleBarrierOutcome.Stop, label.Outcome);
        Assert.Equal(-1m, label.ReturnPercent, 6);
        Assert.True(fixedHorizonReturn > 0m,
            "l'orizzonte fisso vede un guadagno: e' esattamente l'illusione che il triple-barrier toglie");
    }

    // --- Pesi di campione -------------------------------------------------------------------------

    [Fact]
    public void NonOverlappingLabels_AllHaveFullUniqueness()
    {
        var labeler = new TripleBarrierLabeler();
        var labels = new List<TripleBarrierLabel>
        {
            new(0, DateTime.UnixEpoch, 1, DateTime.UnixEpoch, TripleBarrierOutcome.Vertical, 0m, 100m, 100m),
            new(2, DateTime.UnixEpoch, 3, DateTime.UnixEpoch, TripleBarrierOutcome.Vertical, 0m, 100m, 100m),
        };

        var weights = labeler.AverageUniqueness(labels, barCount: 4);

        Assert.All(weights, w => Assert.Equal(1.0, w, 10));
    }

    [Fact]
    public void FullyOverlappingLabels_ShareTheirWeight()
    {
        // Due etichette sulla stessa identica finestra: ciascuna vale meta'.
        var labeler = new TripleBarrierLabeler();
        var labels = new List<TripleBarrierLabel>
        {
            new(0, DateTime.UnixEpoch, 3, DateTime.UnixEpoch, TripleBarrierOutcome.Vertical, 0m, 100m, 100m),
            new(0, DateTime.UnixEpoch, 3, DateTime.UnixEpoch, TripleBarrierOutcome.Vertical, 0m, 100m, 100m),
        };

        var weights = labeler.AverageUniqueness(labels, barCount: 4);

        Assert.All(weights, w => Assert.Equal(0.5, w, 10));
    }

    [Fact]
    public void OverlappingLabelsFromRealSeries_GetWeightsBelowOne()
    {
        // Col triple-barrier le etichette si sovrappongono quasi sempre: se i pesi risultassero
        // tutti 1 il calcolo non starebbe facendo il suo lavoro.
        var candles = Flat(60);
        var labeler = new TripleBarrierLabeler();
        var labels = labeler.Label(candles, Config(bars: 10));

        var weights = labeler.AverageUniqueness(labels, candles.Count);

        Assert.Equal(labels.Count, weights.Count);
        Assert.All(weights, w => Assert.InRange(w, 0.0, 1.0));
        Assert.True(weights.Average() < 0.5,
            $"con orizzonte 10 e ingressi a ogni barra la sovrapposizione e' forte: media attesa bassa, misurata {weights.Average():F3}");
    }

    // --- Barriere derivate dai dati ---------------------------------------------------------------

    [Fact]
    public void SuggestConfig_DerivesBarriersFromTheSeriesExcursions()
    {
        // Serie con un minimo di dinamica, altrimenti le escursioni sono tutte nulle.
        var rnd = new Random(7);
        var price = 100m;
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 400; i++)
        {
            var drift = (decimal)((rnd.NextDouble() - 0.5) * 0.02);
            var next = price * (1m + drift);
            candles.Add(Bar(i, price, Math.Max(price, next) * 1.002m, Math.Min(price, next) * 0.998m, next));
            price = next;
        }

        var config = new TripleBarrierLabeler().SuggestConfig(candles, OrderSide.Buy, verticalBarrierBars: 10);

        Assert.Equal(10, config.VerticalBarrierBars);
        Assert.Equal(OrderSide.Buy, config.Side);
        Assert.True(config.ProfitTakePercent > 0m, "il take profit deve venire dai percentili MFE reali");
        Assert.True(config.StopLossPercent > 0m, "lo stop deve venire dai percentili MAE reali");
    }

    [Fact]
    public void Labelling_IsDeterministic()
    {
        var candles = Flat(40);
        candles[3] = Bar(3, 100m, 103m, 100m, 102m);
        var labeler = new TripleBarrierLabeler();

        var a = labeler.Label(candles, Config());
        var b = labeler.Label(candles, Config());

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Outcome, b[i].Outcome);
            Assert.Equal(a[i].ReturnPercent, b[i].ReturnPercent);
        }
    }
}
