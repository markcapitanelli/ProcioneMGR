using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2.6 PRD-RISANAMENTO] Il ponte che rende raggiungibile l'IncrementalIcGate dalla selezione
/// fattori (C-03/G-16: «il gate c'è, chi lo chiama no»). Il metodo è quello dell'edge piantato
/// (docs/STANDARD-VERIFICA.md): si costruisce una serie dove la ridondanza è VERA per costruzione
/// — il rendimento dipende da due componenti indipendenti x e y, un fattore legge x, il suo echo
/// rilegge x, un terzo legge y — e si pretende che il filtro tenga x e y e scarti l'echo.
/// </summary>
public class IncrementalFactorFilterTests
{
    // ------------------------------------------------------------------ dati piantati

    private static double Gauss(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// n candele il cui rendimento forward a 1 barra è 0,6x + 0,6y (+ poco rumore), con x ⊥ y.
    /// Il forward return alla barra i è (close[i+1]−close[i])/close[i] — la STESSA convenzione di
    /// FactorEvaluator.ForwardReturns — quindi close[i+1] = close[i]·(1+r[i]) pianta r esattamente.
    /// </summary>
    private static (List<OhlcvData> Candles, double[] X, double[] Y) PlantedSeries(int n, int seed)
    {
        var rnd = new Random(seed);
        var x = new double[n];
        var y = new double[n];
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = Gauss(rnd);
            y[i] = Gauss(rnd);
            r[i] = 0.01 * (0.6 * x[i] + 0.6 * y[i]) + 0.002 * Gauss(rnd);
        }

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var close = price;
            candles.Add(new OhlcvData
            {
                Symbol = "AAA/USDT",
                Timeframe = "1h",
                TimestampUtc = start.AddHours(i),
                Open = close,
                High = close * 1.001m,
                Low = close * 0.999m,
                Close = close,
                Volume = 100m,
            });
            price = close * (1m + (decimal)r[i]);
        }
        return (candles, x, y);
    }

    /// <summary>Fattore con valori decisi dal test: il valore alla barra i è values[i].</summary>
    private sealed class PlantedFactor(string name, double[] values) : IAlphaFactor
    {
        public string Name { get; } = name;
        public string DisplayName => Name;
        public FactorCategory Category => FactorCategory.Technical;
        public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } = [];

        public IReadOnlyList<decimal?> Compute(
            IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> parameters)
            => values.Take(candles.Count).Select(v => (decimal?)(decimal)v).ToList();
    }

    private static FactorSpec Spec(string name, double[] values)
        => new(name, new PlantedFactor(name, values), new Dictionary<string, decimal>());

    private static double[] Echo(double[] source, int seed, double noise = 0.05)
    {
        var rnd = new Random(seed);
        return source.Select(v => v + noise * Gauss(rnd)).ToArray();
    }

    // ------------------------------------------------------------------ filtro puro

    [Fact]
    public void EchoDelCapostipite_Scartato_IndipendenteTenuto()
    {
        var (candles, x, y) = PlantedSeries(1200, seed: 7);
        var specs = new List<FactorSpec>
        {
            Spec("Base", x),            // capostipite: si tiene per definizione
            Spec("EchoDiBase", Echo(x, seed: 8)),  // stessa informazione: da scartare
            Spec("Indipendente", y),    // componente ortogonale: aggiunge, si tiene
        };

        var result = IncrementalFactorFilter.Apply(specs, candles, forwardHorizon: 1);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(["Base", "EchoDiBase", "Indipendente"], result.Entries.Select(e => e.Spec.FeatureName));

        Assert.True(result.Entries[0].Kept, "il capostipite si tiene sempre");
        Assert.Null(result.Entries[0].Outcome);

        Assert.False(result.Entries[1].Kept, "l'echo non aggiunge informazione: va scartato");
        var echoOutcome = result.Entries[1].Outcome!;
        Assert.True(Math.Abs(echoOutcome.RawIc) > 0.15,
            $"l'IC grezzo dell'echo deve essere alto (eredita quello di Base): {echoOutcome.RawIc:F4}");
        Assert.True(Math.Abs(echoOutcome.PartialIc) < 0.06,
            $"l'IC parziale dell'echo deve crollare: {echoOutcome.PartialIc:F4}");

        Assert.True(result.Entries[2].Kept, "la componente indipendente aggiunge: si tiene");
        Assert.True(Math.Abs(result.Entries[2].Outcome!.PartialIc) > 0.15,
            $"l'IC parziale dell'indipendente deve restare alto: {result.Entries[2].Outcome!.PartialIc:F4}");

        Assert.Equal(["Base", "Indipendente"], result.Kept.Select(k => k.FeatureName));
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void ITenutiDiventanoControlli_UnEchoDelSecondoTenuto_VieneScartato()
    {
        // L'echo di y arriva DOPO che y è stato tenuto: se il filtro confrontasse solo col
        // capostipite (x ⊥ y, quindi l'echo di y sembrerebbe nuovo) lo terrebbe per errore.
        var (candles, x, y) = PlantedSeries(1200, seed: 19);
        var specs = new List<FactorSpec>
        {
            Spec("Base", x),
            Spec("Indipendente", y),
            Spec("EchoDellIndipendente", Echo(y, seed: 20)),
        };

        var result = IncrementalFactorFilter.Apply(specs, candles, forwardHorizon: 1);

        Assert.Equal(["Base", "Indipendente"], result.Kept.Select(k => k.FeatureName));
        Assert.False(result.Entries[2].Kept,
            "l'echo del secondo tenuto va scartato: i controlli devono crescere coi tenuti");
    }

    [Fact]
    public void FattoreSingolo_TenutoSenzaVerdetto_EListaVuotaResiste()
    {
        var (candles, x, _) = PlantedSeries(400, seed: 3);

        var single = IncrementalFactorFilter.Apply([Spec("Unico", x)], candles, forwardHorizon: 1);
        Assert.Single(single.Entries);
        Assert.True(single.Entries[0].Kept);
        Assert.Null(single.Entries[0].Outcome);
        Assert.Equal(0, single.DroppedCount);

        var empty = IncrementalFactorFilter.Apply([], candles, forwardHorizon: 1);
        Assert.Empty(empty.Entries);
        Assert.Empty(empty.Kept);
    }

    [Fact]
    public void Deterministico_StessiInput_StessoVerdetto()
    {
        var (candles, x, y) = PlantedSeries(800, seed: 42);
        var build = () => new List<FactorSpec>
        {
            Spec("Base", x), Spec("EchoDiBase", Echo(x, seed: 43)), Spec("Indipendente", y),
        };

        var first = IncrementalFactorFilter.Apply(build(), candles, forwardHorizon: 1);
        var second = IncrementalFactorFilter.Apply(build(), candles, forwardHorizon: 1);

        Assert.Equal(first.Kept.Select(k => k.FeatureName), second.Kept.Select(k => k.FeatureName));
        Assert.Equal(
            first.Entries.Select(e => e.Outcome?.PartialIc),
            second.Entries.Select(e => e.Outcome?.PartialIc));
    }

    // ------------------------------------------------------------------ stage della pipeline

    private sealed class PlantedFactory(params IAlphaFactor[] factors) : IAlphaFactorFactory
    {
        public IReadOnlyList<IAlphaFactor> Prototypes { get; } = factors;
        public IAlphaFactor Create(string factorName) => Prototypes.First(f => f.Name == factorName);
    }

    private sealed class SingleSeriesCache(List<OhlcvData> candles) : IPipelineCandleCache
    {
        public Task<IReadOnlyList<OhlcvData>> GetAsync(
            string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)
        {
            IReadOnlyList<OhlcvData> filtered =
                candles.Where(c => c.TimestampUtc >= from && c.TimestampUtc <= to).ToList();
            return Task.FromResult(filtered);
        }
    }

    private static PipelineContext StageContext(List<OhlcvData> candles)
    {
        var from = candles[0].TimestampUtc;
        var to = candles[^1].TimestampUtc;
        return new PipelineContext
        {
            ExchangeName = "Binance",
            Universe = [new SeriesSpec { Symbol = "AAA/USDT", Timeframe = "1h" }],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = from,
                SelectionTo = to,
                HoldoutFrom = to,
                HoldoutTo = to.AddHours(1),
            },
            Candles = new SingleSeriesCache(candles),
            Seed = 1,
        };
    }

    [Fact]
    public async Task Stage_GateSpentoDiDefault_SelezionaAncheIlRidondante()
    {
        var (candles, x, y) = PlantedSeries(1200, seed: 31);
        var factory = new PlantedFactory(
            new PlantedFactor("Base", x),
            new PlantedFactor("EchoDiBase", Echo(x, seed: 32)),
            new PlantedFactor("Indipendente", y));
        var stage = new FeatureEngineeringStage(factory, new FactorEvaluator());
        var ctx = StageContext(candles);
        // Il default di incrementalIcGate è false: NON lo si passa, così il test rompe se qualcuno
        // cambia il default di nascosto.
        var config = new StageConfig { Parameters = new() { ["topK"] = "3", ["minAbsIc"] = "0.02" } };

        await stage.ExecuteAsync(ctx, config, CancellationToken.None);

        Assert.NotNull(ctx.Features);
        Assert.Equal(3, ctx.Features!.SelectedFactorNames.Count);
        Assert.Contains("EchoDiBase", ctx.Features.SelectedFactorNames);
    }

    [Fact]
    public async Task Stage_GateAcceso_ScartaIlRidondante_ELoDiceNeiLog()
    {
        var (candles, x, y) = PlantedSeries(1200, seed: 31);
        var factory = new PlantedFactory(
            new PlantedFactor("Base", x),
            new PlantedFactor("EchoDiBase", Echo(x, seed: 32)),
            new PlantedFactor("Indipendente", y));
        var stage = new FeatureEngineeringStage(factory, new FactorEvaluator());
        var ctx = StageContext(candles);
        var lines = new List<string>();
        ctx.Log = lines.Add;
        var config = new StageConfig
        {
            Parameters = new() { ["topK"] = "3", ["minAbsIc"] = "0.02", ["incrementalIcGate"] = "true" },
        };

        await stage.ExecuteAsync(ctx, config, CancellationToken.None);

        // Base e il suo echo si contendono il posto (chi ha |IC| più alto fa da capostipite):
        // ne sopravvive UNO solo, più la componente indipendente.
        var selected = ctx.Features!.SelectedFactorNames;
        Assert.Equal(2, selected.Count);
        Assert.Contains("Indipendente", selected);
        Assert.Single(selected, n => n is "Base" or "EchoDiBase");
        Assert.Contains(lines, l => l.Contains("Gate incrementale"));
    }
}
