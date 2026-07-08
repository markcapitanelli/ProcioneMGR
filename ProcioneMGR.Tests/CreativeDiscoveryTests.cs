using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test puri del layer di scoperta creativa: SignalCatalog (normalizzazione causale),
/// le tre meta-strategie (Composite/EventTrigger/RegimeConditional) e i generatori del
/// composer (determinismo, diversità, plausibilità). Nessun DB, dati sintetici seedati.
/// </summary>
public class CreativeDiscoveryTests
{
    private static readonly TechnicalIndicatorsService Svc = new();

    private static OhlcvData Candle(DateTime t, decimal o, decimal h, decimal l, decimal c, decimal v = 100m)
        => new() { Symbol = "X", Timeframe = "1h", TimestampUtc = t, Open = o, High = h, Low = l, Close = c, Volume = v };

    private static List<OhlcvData> SyntheticSeries(int count, int seed)
    {
        var rng = new Random(seed);
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>(count);
        decimal price = 100m;
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = Math.Max(1m, price + (decimal)(rng.NextDouble() * 4 - 2));
            candles.Add(Candle(start.AddHours(i), open,
                Math.Max(open, close) + (decimal)rng.NextDouble(),
                Math.Min(open, close) - (decimal)rng.NextDouble(),
                close, 100m + (decimal)rng.NextDouble() * 100m));
            price = close;
        }
        return candles;
    }

    private static async Task<Signal[]> RunAsync(IStrategy strategy, IReadOnlyList<OhlcvData> candles, Dictionary<string, decimal> pars)
    {
        var closes = candles.Select(c => c.Close).ToList();
        await strategy.InitializeAsync(closes, candles, pars, Svc, CancellationToken.None);
        var signals = new Signal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            signals[i] = strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc);
        }
        return signals;
    }

    // ------------------------------------------------------------- SignalCatalog

    [Fact]
    public void CausalPercentile_IsTruncationInvariant_AndBounded()
    {
        var rng = new Random(3);
        var values = Enumerable.Range(0, 600).Select(_ => (decimal?)(decimal)rng.NextDouble()).ToArray();

        var full = SignalCatalog.CausalPercentile(values, 100);
        var truncated = SignalCatalog.CausalPercentile(values.Take(400).ToArray(), 100);

        // ANTI-LOOK-AHEAD: il valore all'indice i non cambia se la serie viene troncata dopo i.
        for (var i = 0; i < 400; i++)
        {
            Assert.Equal(truncated[i], full[i]);
        }
        foreach (var p in full.Where(p => p.HasValue))
        {
            Assert.InRange(p!.Value, 0m, 100m);
        }
    }

    [Fact]
    public async Task SignalMatrix_IsCachedPerCandleListInstance()
    {
        var candles = SyntheticSeries(400, seed: 7);
        var a = await SignalCatalog.GetMatrixAsync(candles, Svc, CancellationToken.None);
        var b = await SignalCatalog.GetMatrixAsync(candles, Svc, CancellationToken.None);
        Assert.Same(a, b); // stessa istanza di lista -> stessa matrice (cache)
        Assert.Equal(SignalCatalog.SignalCount, a.Length);
    }

    // ------------------------------------------------------------- Composite

    [Fact]
    public async Task Composite_AndLogic_FiresOnlyWhenAllConditionsTrue()
    {
        // Serie che scende a fondo range (RSI basso) con volume alto sull'ultima parte.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        decimal price = 100m;
        for (var i = 0; i < 300; i++)
        {
            var open = price;
            price = i < 260 ? price : price - 1.5m;  // crollo finale -> RSI basso
            var vol = i < 260 ? 100m : 500m;         // volume alto durante il crollo
            candles.Add(Candle(start.AddHours(i), open, Math.Max(open, price) + 0.3m, Math.Min(open, price) - 0.3m, price, vol));
        }

        // RSI<35 AND VolumePct>60 -> Long.
        var pars = new Dictionary<string, decimal>
        {
            ["Logic"] = 0m, ["Direction"] = 0m, ["EntryCount"] = 2m,
            ["EntrySig1"] = 0m, ["EntryOp1"] = 0m, ["EntryThr1"] = 35m,
            ["EntrySig2"] = 4m, ["EntryOp2"] = 1m, ["EntryThr2"] = 60m,
            ["ExitCount"] = 1m, ["ExitSig1"] = 0m, ["ExitOp1"] = 1m, ["ExitThr1"] = 65m,
        };
        var signals = await RunAsync(new CompositeSignalStrategy(), candles, pars);
        Assert.Contains(Signal.Long, signals[260..]);
        Assert.DoesNotContain(Signal.Long, signals[..200]); // nessun falso positivo nella fase neutra
    }

    [Fact]
    public async Task Composite_OrLogic_FiresWhenAnyConditionTrue()
    {
        var candles = SyntheticSeries(400, seed: 11);
        // OR con una condizione sempre-facile (SupertrendDir>−1 impossibile: usare VolumePct>1)
        var pars = new Dictionary<string, decimal>
        {
            ["Logic"] = 1m, ["Direction"] = 0m, ["EntryCount"] = 2m,
            ["EntrySig1"] = 0m, ["EntryOp1"] = 0m, ["EntryThr1"] = 1m,   // RSI<1: quasi mai
            ["EntrySig2"] = 4m, ["EntryOp2"] = 1m, ["EntryThr2"] = 5m,   // VolPct>5: quasi sempre
            ["ExitCount"] = 0m,
        };
        var signals = await RunAsync(new CompositeSignalStrategy(), candles, pars);
        Assert.Contains(Signal.Long, signals); // l'OR con condizione facile deve scattare
    }

    [Fact]
    public async Task Composite_ContradictorySpec_Throws()
    {
        var candles = SyntheticSeries(300, seed: 2);
        // RSI<30 AND RSI>70: assurda per costruzione.
        var pars = new Dictionary<string, decimal>
        {
            ["Logic"] = 0m, ["EntryCount"] = 2m,
            ["EntrySig1"] = 0m, ["EntryOp1"] = 0m, ["EntryThr1"] = 30m,
            ["EntrySig2"] = 0m, ["EntryOp2"] = 1m, ["EntryThr2"] = 70m,
            ["ExitCount"] = 0m,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new CompositeSignalStrategy(), candles, pars));
    }

    [Fact]
    public async Task Composite_IsDeterministic_AndTruncationInvariant()
    {
        var candles = SyntheticSeries(500, seed: 5);
        var pars = new Dictionary<string, decimal>
        {
            ["Logic"] = 0m, ["Direction"] = 0m, ["EntryCount"] = 2m,
            ["EntrySig1"] = 0m, ["EntryOp1"] = 0m, ["EntryThr1"] = 35m,
            ["EntrySig2"] = 6m, ["EntryOp2"] = 1m, ["EntryThr2"] = 65m,
            ["ExitCount"] = 1m, ["ExitSig1"] = 0m, ["ExitOp1"] = 1m, ["ExitThr1"] = 70m,
        };
        var full = await RunAsync(new CompositeSignalStrategy(), candles, pars);
        var again = await RunAsync(new CompositeSignalStrategy(), candles, pars);
        Assert.Equal(full, again); // determinismo

        // ANTI-LOOK-AHEAD end-to-end: il segnale alla barra i è identico su serie troncata a i+1.
        var truncated = await RunAsync(new CompositeSignalStrategy(), candles.Take(350).ToList(), pars);
        for (var i = 0; i < 350; i++)
        {
            Assert.Equal(truncated[i], full[i]);
        }
    }

    // ------------------------------------------------------------- EventTrigger

    [Fact]
    public async Task EventTrigger_PriceShockDown_EntersAndClosesAfterMaxHold()
    {
        // Serie stabile con UN crollo secco: shock-down -> Long (compra il panico), poi
        // chiusura esattamente dopo MaxHoldBars.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        var rng = new Random(9);
        decimal price = 100m;
        for (var i = 0; i < 400; i++)
        {
            var open = price;
            price = i == 300 ? price * 0.93m : Math.Max(1m, price + (decimal)(rng.NextDouble() * 0.6 - 0.3));
            candles.Add(Candle(start.AddHours(i), open, Math.Max(open, price) + 0.2m, Math.Min(open, price) - 0.2m, price));
        }

        var pars = new Dictionary<string, decimal>
        {
            ["EventType"] = 4m,   // PriceShockDown
            ["Direction"] = 0m,
            ["Threshold"] = 95m,
            ["MaxHoldBars"] = 10m,
        };
        var signals = await RunAsync(new EventTriggerStrategy(), candles, pars);

        Assert.Equal(Signal.Long, signals[300]);   // trigger sulla barra dello shock
        Assert.Equal(Signal.Close, signals[310]);  // uscita time-bound dopo 10 barre
        for (var i = 301; i < 310; i++)
        {
            Assert.Equal(Signal.Hold, signals[i]); // una posizione alla volta, in attesa
        }
    }

    [Fact]
    public async Task EventTrigger_InvalidParams_Throws()
    {
        var candles = SyntheticSeries(300, seed: 4);
        var pars = new Dictionary<string, decimal> { ["EventType"] = 9m };
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new EventTriggerStrategy(), candles, pars));
    }

    // ------------------------------------------------------------- RegimeConditional

    [Fact]
    public async Task RegimeConditional_DelegatesByBucket_AndClosesOnSwitch()
    {
        // Uptrend netto poi laterale: Up->Supertrend (deve emettere Long nel trend),
        // Flat->nessuna (deve stare fermo nel laterale), con Close alla transizione.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        decimal price = 100m;
        for (var i = 0; i < 250; i++) { var o = price; price += 1m; candles.Add(Candle(start.AddHours(i), o, price + 0.4m, o - 0.4m, price)); }
        for (var i = 250; i < 500; i++) { var o = price; price += (i % 2 == 0 ? 0.2m : -0.2m); candles.Add(Candle(start.AddHours(i), o, Math.Max(o, price) + 0.4m, Math.Min(o, price) - 0.4m, price)); }

        var pars = new Dictionary<string, decimal>
        {
            ["TrendPeriod"] = 50m,
            ["UpStrategy"] = 7m,   // Supertrend
            ["DownStrategy"] = 0m,
            ["FlatStrategy"] = 0m, // nessuna: in laterale deve tacere
        };
        var signals = await RunAsync(new RegimeConditionalStrategy(), candles, pars);

        Assert.Contains(Signal.Long, signals[..250]);          // trend up -> il Supertrend delegato spara Long
        Assert.Contains(Signal.Close, signals[240..340]);      // transizione di regime -> Close
        Assert.DoesNotContain(Signal.Long, signals[350..]);    // laterale con FlatStrategy=None -> silenzio
        Assert.DoesNotContain(Signal.Short, signals[350..]);
    }

    [Fact]
    public async Task RegimeConditional_AllNone_Throws()
    {
        var candles = SyntheticSeries(200, seed: 6);
        var pars = new Dictionary<string, decimal> { ["UpStrategy"] = 0m, ["DownStrategy"] = 0m, ["FlatStrategy"] = 0m };
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new RegimeConditionalStrategy(), candles, pars));
    }

    // ------------------------------------------------------------- Generators / Composer

    [Fact]
    public void CompositeGenerator_IsDeterministic_Diverse_AndPlausible()
    {
        var generator = new CompositeSignalGenerator();
        var config = new ComposerConfiguration { Seed = 42 };

        var a = generator.Generate(config, 150);
        var b = generator.Generate(config, 150);

        Assert.Equal(a.Select(c => c.Key), b.Select(c => c.Key));       // determinismo
        Assert.Equal(a.Count, a.Select(c => c.Key).Distinct().Count()); // diversità: chiavi uniche
        Assert.True(a.Count is > 0 and <= 150);

        var factory = new StrategyFactory();
        foreach (var candidate in a)
        {
            // Plausibilità: ogni spec deve costruirsi senza eccezioni (niente contraddizioni)
            // e riferire solo parametri esistenti nelle ParameterDefinitions.
            var proto = factory.Create(candidate.StrategyName);
            var validNames = proto.ParameterDefinitions.Select(d => d.Key).ToHashSet();
            foreach (var key in candidate.Parameters.Keys)
            {
                Assert.Contains(key, validNames);
            }
        }
    }

    [Fact]
    public async Task GeneratedCompositeSpecs_AllInitializeWithoutErrors()
    {
        // Ogni spec generata deve inizializzarsi su una serie reale-sintetica: la guardia
        // anti-contraddizione della strategia non deve MAI scattare su spec del generatore.
        var candles = SyntheticSeries(400, seed: 13);
        var specs = new CompositeSignalGenerator().Generate(new ComposerConfiguration { Seed = 1 }, 60);
        foreach (var spec in specs)
        {
            var strategy = new CompositeSignalStrategy();
            var closes = candles.Select(c => c.Close).ToList();
            await strategy.InitializeAsync(closes, candles, spec.Parameters, Svc, CancellationToken.None);
        }
        Assert.True(specs.Count > 0);
    }

    [Fact]
    public void EventAndRegimeGenerators_AreDeterministicAndValid()
    {
        var config = new ComposerConfiguration { Seed = 7 };
        var events1 = new EventTriggerGenerator().Generate(config, 30);
        var events2 = new EventTriggerGenerator().Generate(config, 30);
        Assert.Equal(events1.Select(c => c.Key), events2.Select(c => c.Key));
        Assert.All(events1, c => Assert.Equal("EventTrigger", c.StrategyName));

        var regimes = new RegimeMapGenerator().Generate(config, 30);
        Assert.True(regimes.Count > 0);
        Assert.All(regimes, c =>
        {
            Assert.Equal("RegimeConditional", c.StrategyName);
            // Mai la mappa tutta-none (il generatore la esclude, la strategia la rifiuterebbe).
            Assert.True(c.Parameters["UpStrategy"] + c.Parameters["DownStrategy"] + c.Parameters["FlatStrategy"] > 0m);
        });
    }

    [Fact]
    public void ComposerWindows_CoverTheRangeWithoutOverlap()
    {
        var from = new DateTime(2025, 1, 1);
        var to = new DateTime(2025, 12, 31);
        var windows = StrategyComposer.BuildOosWindows(from, to, 2);

        Assert.True(windows.Count >= 5);
        for (var i = 1; i < windows.Count; i++)
        {
            Assert.Equal(windows[i - 1].To, windows[i].From); // contigue, senza sovrapposizione
        }
        Assert.Equal(from, windows[0].From);
    }
}
