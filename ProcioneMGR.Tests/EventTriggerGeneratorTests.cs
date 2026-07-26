using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3 — ROADMAP-RENDIMENTO] Il generatore di event-trigger emetteva varianti di <c>Threshold</c>
/// anche sugli eventi flip del Supertrend, dove quel parametro non lega (sono cambi di segno, non
/// percentili). Il risultato erano DUPLICATI ESATTI: stessa strategia, stesso risultato, contati
/// come tentativi distinti dal DSR e usciti come "confermati" doppi dalla caccia densa del
/// 2026-07-25.
///
/// Questi test difendono due cose: che il generatore non produca più quei duplicati, e che la
/// premessa sia VERA — cioè che sui flip la soglia sia davvero inerte sulla strategia reale.
/// La seconda conta più della prima: se un giorno qualcuno facesse legare Threshold sui flip,
/// il test della premessa fallirebbe e il fix del generatore andrebbe ritirato insieme.
/// </summary>
public class EventTriggerGeneratorTests
{
    [Fact]
    public void FlipEvents_GetASingleThresholdVariant()
    {
        var candidates = new EventTriggerGenerator().Generate(
            new ComposerConfiguration { Seed = 1 }, quota: 1000);

        foreach (var flip in new[] { 2m, 3m })
        {
            var varianti = candidates
                .Where(c => c.Parameters["EventType"] == flip)
                .Select(c => c.Parameters["Threshold"])
                .Distinct()
                .ToList();

            Assert.True(varianti.Count <= 1,
                $"EventType {flip}: {varianti.Count} varianti di Threshold ({string.Join(",", varianti)}) — "
                + "sui flip la soglia non lega, ogni variante è un duplicato che gonfia il conteggio dei trial.");
        }
    }

    [Fact]
    public void NonFlipEvents_KeepTheirThresholdSweep()
    {
        // La correzione deve togliere i duplicati, non la varietà dove il parametro lega davvero.
        var candidates = new EventTriggerGenerator().Generate(
            new ComposerConfiguration { Seed = 1 }, quota: 1000);

        foreach (var evt in new[] { 0m, 1m, 4m, 5m })
        {
            var varianti = candidates
                .Where(c => c.Parameters["EventType"] == evt)
                .Select(c => c.Parameters["Threshold"])
                .Distinct()
                .Count();

            Assert.True(varianti >= 2, $"EventType {evt}: lo sweep di Threshold deve restare (trovate {varianti} varianti).");
        }
    }

    [Fact]
    public void AllGeneratedKeys_AreUnique()
    {
        var candidates = new EventTriggerGenerator().Generate(
            new ComposerConfiguration { Seed = 1 }, quota: 1000);

        Assert.Equal(candidates.Count, candidates.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public async Task ThePremiseIsTrue_ThresholdIsInertOnFlips_OnTheRealStrategy()
    {
        // Se questo test fallisce, Threshold ha COMINCIATO a legare sui flip: il fix del generatore
        // va ritirato, perché a quel punto le varianti non sarebbero più duplicati.
        var rnd = new Random(7);
        var price = 100m;
        var candles = Enumerable.Range(0, 400).Select(i =>
        {
            price *= 1m + (decimal)(rnd.NextDouble() - 0.49) * 0.03m;
            return new ProcioneMGR.Data.OhlcvData
            {
                Symbol = "T/USDT", Timeframe = "1h",
                TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = price, High = price * 1.01m, Low = price * 0.99m, Close = price, Volume = 10m,
            };
        }).ToList();
        var closes = candles.Select(c => c.Close).ToList();
        var indicators = new TechnicalIndicatorsService();

        async Task<List<Signal>> RunAsync(decimal threshold)
        {
            var s = new EventTriggerStrategy();
            await s.InitializeAsync(closes, candles, new Dictionary<string, decimal>
            {
                ["EventType"] = 3m, ["Direction"] = 1m, ["Threshold"] = threshold, ["MaxHoldBars"] = 48m,
            }, indicators, CancellationToken.None);
            return [.. Enumerable.Range(0, candles.Count).Select(i => s.EvaluateSignal(i, closes[i], candles[i].TimestampUtc))];
        }

        Assert.Equal(await RunAsync(60m), await RunAsync(95m));
    }
}
