using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T2.7 roadmap macchina-ricerca] Event-study rigoroso + rilevatore di eventi di mercato.
///
/// Il criterio di validazione dell'item, dichiarato nella roadmap: (a) un effetto PIANTATO viene
/// recuperato (CAAR post positiva, placebo significativo); (b) il PLACEBO su rumore puro non
/// produce significatività (le date a caso non "reagiscono"); (c) tutto deterministico a parità
/// di seme — la stessa disciplina di T1.5, perché il placebo È una randomizzazione temporale.
/// </summary>
public class EventStudyTests
{
    private static List<OhlcvData> RandomWalk(int n, int seed, double vol = 0.01)
    {
        var rng = new Random(seed);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            // Box-Muller: rumore gaussiano senza dipendenze esterne.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            price *= (decimal)Math.Exp(vol * z - vol * vol / 2);
            candles.Add(new OhlcvData
            {
                Symbol = "EVT/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = price, High = price, Low = price, Close = price, Volume = 100m,
            });
        }
        return candles;
    }

    // ------------------------------------------------------------------ EventStudy

    [Fact]
    public void PlantedEffect_IsRecovered_WithSignificantPlacebo()
    {
        // Effetto piantato: dopo ognuna delle 25 date-evento, le successive 10 barre ricevono
        // +50 bp di drift extra. L'event-study deve vederlo nella CAAR post e il placebo deve
        // dichiararlo non-caso.
        var candles = RandomWalk(3000, seed: 7, vol: 0.005);
        var eventIdx = Enumerable.Range(0, 25).Select(k => 200 + k * 100).ToList();
        foreach (var i in eventIdx)
        {
            for (var k = 0; k <= 10 && i + k < candles.Count; k++)
            {
                var boost = (decimal)Math.Pow(1.005, k + 1);
                var c = candles[i + k];
                c.Close *= boost; c.Open *= boost; c.High *= boost; c.Low *= boost;
                // Le barre DOPO la finestra restano rialzate: l'effetto è permanente, non un glitch.
            }
            for (var j = i + 11; j < candles.Count; j++)
            {
                var boost = (decimal)Math.Pow(1.005, 11);
                var c = candles[j];
                c.Close *= boost; c.Open *= boost; c.High *= boost; c.Low *= boost;
            }
        }
        var times = eventIdx.Select(i => candles[i].TimestampUtc).ToList();

        var result = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 300, Seed: 11));

        Assert.Equal(25, result.EventsUsable);
        Assert.True(result.CaarPost > 0.03, $"CAAR post attesa >3% (11 barre × ~50bp), trovata {result.CaarPost:P2}");
        Assert.True(result.PlaceboPValue < 0.05, $"placebo p atteso <0.05, trovato {result.PlaceboPValue:F3}");
        Assert.True(result.TStatPost > 2, $"t atteso >2, trovato {result.TStatPost:F2}");
        // E la finestra PRE resta pulita: nessuna anticipazione nell'effetto piantato.
        Assert.True(Math.Abs(result.CaarPre) < 0.01, $"CAAR pre attesa ~0, trovata {result.CaarPre:P2}");
    }

    [Fact]
    public void PureNoise_RandomDates_AreNotSignificant()
    {
        var candles = RandomWalk(3000, seed: 21);
        var rng = new Random(5);
        var times = Enumerable.Range(0, 25).Select(_ => candles[rng.Next(200, 2900)].TimestampUtc).ToList();

        var result = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 300, Seed: 12));

        Assert.True(result.PlaceboPValue > 0.05,
            $"su rumore puro il placebo non deve essere estremo: p={result.PlaceboPValue:F3}");
    }

    [Fact]
    public void SameSeed_SameResult_Deterministic()
    {
        var candles = RandomWalk(1500, seed: 3);
        var times = new List<DateTime> { candles[400].TimestampUtc, candles[700].TimestampUtc, candles[1000].TimestampUtc };

        var a = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 100, Seed: 9));
        var b = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 100, Seed: 9));

        Assert.Equal(a.PlaceboPValue, b.PlaceboPValue);
        Assert.Equal(a.CaarPost, b.CaarPost);
        Assert.Equal(a.Aar, b.Aar);
    }

    [Fact]
    public void EventsTooCloseToBoundaries_AreExcluded_NotSilentlyMangled()
    {
        var candles = RandomWalk(300, seed: 4);
        var times = new List<DateTime>
        {
            candles[5].TimestampUtc,     // troppo presto: niente finestra di stima
            candles[295].TimestampUtc,   // troppo tardi: niente finestra post
            candles[150].TimestampUtc,   // valido
        };

        var result = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 50, Seed: 1));

        Assert.Equal(3, result.EventsSupplied);
        Assert.Equal(1, result.EventsUsable);
    }

    [Fact]
    public void AbnormalReturn_SubtractsBaselineDrift()
    {
        // Serie con drift costante +1% a barra: qualunque finestra post-evento sale del 10% in 10
        // barre, ma l'abnormal return DEVE essere ~0 perché la baseline sale uguale. È la differenza
        // fra questo studio e le medie semplici di NewsImpactAnalyzer.
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        var price = 100m;
        for (var i = 0; i < 500; i++)
        {
            price *= 1.01m;
            candles.Add(new OhlcvData
            {
                Symbol = "DRIFT/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = price, High = price, Low = price, Close = price, Volume = 100m,
            });
        }
        var times = new List<DateTime> { candles[200].TimestampUtc, candles[300].TimestampUtc };

        var result = EventStudy.Run(candles, times, new EventStudyConfig(PlaceboSamples: 50, Seed: 2));

        Assert.Equal(2, result.EventsUsable);
        Assert.True(Math.Abs(result.CaarPost) < 1e-6, $"drift di baseline non sottratto: CAAR={result.CaarPost:E2}");
    }

    // ------------------------------------------------------------------ MarketEventDetector

    [Fact]
    public void Detector_FindsPlantedCrash_AndCooldownDedupesTheCluster()
    {
        var candles = RandomWalk(500, seed: 8, vol: 0.005);
        // Crash piantato: tre barre consecutive a -10% ciascuna (un cluster = UN episodio).
        foreach (var i in new[] { 300, 301, 302 })
        {
            var factor = 0.90m;
            var c = candles[i];
            c.Close = candles[i - 1].Close * factor;
            c.Open = c.Close; c.High = c.Close; c.Low = c.Close;
            // Propaga il livello alle barre successive (random walk riparte dal nuovo prezzo).
            for (var j = i + 1; j < candles.Count; j++)
            {
                var cj = candles[j];
                cj.Close *= factor; cj.Open *= factor; cj.High *= factor; cj.Low *= factor;
            }
        }

        var events = MarketEventDetector.Detect(candles);

        var crashes = events.Where(e => e.Kind == MarketEventKind.Crash).ToList();
        Assert.Single(crashes);   // cooldown: il cluster di 3 barre è UN evento
        Assert.Equal(candles[300].TimestampUtc, crashes[0].TimestampUtc);
        Assert.True(crashes[0].Magnitude >= 1.0);
    }

    [Fact]
    public void Detector_FindsVolumeBlowout_AgainstRollingMedian()
    {
        var candles = RandomWalk(300, seed: 15, vol: 0.005);
        candles[200].Volume = 5000m;   // 50× la mediana (100)

        var events = MarketEventDetector.Detect(candles);

        var blowouts = events.Where(e => e.Kind == MarketEventKind.VolumeBlowout).ToList();
        Assert.Contains(blowouts, e => e.TimestampUtc == candles[200].TimestampUtc);
    }

    // ------------------------------------------------------------------ Segnali F3 (catalogo 12-13)

    [Fact]
    public async Task PostCrashSignal_Is100AtTheEvent_DecaysLinearly_AndZeroWithoutEvents()
    {
        var candles = RandomWalk(400, seed: 8, vol: 0.005);
        // Stesso crash piantato del test del detector (cluster a 300-302 → UN evento a 300).
        foreach (var i in new[] { 300, 301, 302 })
        {
            var factor = 0.90m;
            var c = candles[i];
            c.Close = candles[i - 1].Close * factor;
            c.Open = c.Close; c.High = c.Close; c.Low = c.Close;
            for (var j = i + 1; j < candles.Count; j++)
            {
                var cj = candles[j];
                cj.Close *= factor; cj.Open *= factor; cj.High *= factor; cj.Low *= factor;
            }
        }

        var matrix = await ProcioneMGR.Services.Backtesting.SignalCatalog.GetMatrixAsync(
            candles, new ProcioneMGR.Services.Indicators.TechnicalIndicatorsService(), CancellationToken.None);

        var postCrash = matrix[12];
        Assert.Null(postCrash[10]);                 // warm-up del rilevatore
        Assert.Equal(0m, postCrash[200]);           // nessun evento recente = 0, non null
        Assert.Equal(100m, postCrash[300]);         // barra dell'evento
        Assert.Equal(95m, postCrash[301]);          // decadimento lineare su 20 barre
        Assert.Equal(0m, postCrash[300 + ProcioneMGR.Services.Backtesting.SignalCatalog.EventDecayBars]);
        // E il segnale gemello (Surge) resta a zero: il crash non lo accende.
        Assert.Equal(0m, matrix[13][301]);
    }

    [Fact]
    public void Detector_IsCausal_FutureBarsDoNotChangePastEvents()
    {
        var candles = RandomWalk(400, seed: 30, vol: 0.005);
        candles[250].Volume = 5000m;

        var full = MarketEventDetector.Detect(candles);
        var truncated = MarketEventDetector.Detect(candles.Take(260).ToList());

        // Gli eventi fino alla barra 259 devono essere IDENTICI: il futuro non riscrive il passato.
        var fullUpTo = full.Where(e => e.TimestampUtc <= candles[259].TimestampUtc).ToList();
        Assert.Equal(fullUpTo.Count, truncated.Count);
        for (var i = 0; i < truncated.Count; i++)
        {
            Assert.Equal(fullUpTo[i].TimestampUtc, truncated[i].TimestampUtc);
            Assert.Equal(fullUpTo[i].Kind, truncated[i].Kind);
        }
    }
}
