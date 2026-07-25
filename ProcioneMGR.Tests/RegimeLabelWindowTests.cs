using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione di un difetto SILENZIOSO trovato guardando un run reale della pipeline il 2026-07-25:
/// la configurazione swing giornaliera riportava <c>Regime: sconosciuto</c> a ogni esecuzione,
/// anche subito dopo aver riaddestrato il modello nello stesso run.
///
/// La causa: la finestra di etichettatura è espressa in <b>giorni</b> (<c>labelLookbackDays</c>,
/// default 30) mentre il warmup dell'estrattore di feature è in <b>barre</b> (50, la finestra più
/// lunga che usa). Su 1h trenta giorni fanno 720 barre e tutto funziona; su <b>1d ne fanno 30</b>,
/// cioè sotto il warmup — l'estrattore restituiva zero feature, nessuna candela veniva etichettata,
/// e il regime usciva "sconosciuto" senza che niente segnalasse il perché.
///
/// È il tipo di guasto che non rompe niente e non compare nei log: produce un valore plausibile
/// ("sconosciuto" è una risposta legittima quando manca un modello) per una ragione sbagliata.
/// </summary>
public class RegimeLabelWindowTests
{
    /// <summary>Warmup dell'estrattore: 50 barre, la finestra più lunga (SMA/regressione a 50).</summary>
    private const int ExtractorWarmupBars = 50;

    private static double HoursPerBar(string tf) => tf switch
    {
        "15m" => 0.25, "1h" => 1, "4h" => 4, "1d" => 24, "1w" => 168, _ => 1,
    };

    private static int BarsIn(int days, string tf) => (int)(days * 24 / HoursPerBar(tf));

    [Theory]
    [InlineData("15m")]
    [InlineData("1h")]
    [InlineData("4h")]
    [InlineData("1d")]
    [InlineData("1w")]
    public void EveryTimeframe_GetsEnoughBarsToClearTheWarmup(string timeframe)
    {
        var days = RegimeAnalysisStage.MinLabelDaysForTests(timeframe);

        Assert.True(BarsIn(days, timeframe) > ExtractorWarmupBars,
            $"{timeframe}: {days} giorni danno {BarsIn(days, timeframe)} barre, sotto il warmup di {ExtractorWarmupBars} — "
            + "l'estrattore restituirebbe zero feature e il regime resterebbe 'sconosciuto' per sempre.");
    }

    [Fact]
    public void TheDailyCase_IsTheOneThatUsedToBreak()
    {
        // Il caso concreto osservato: 1d con la finestra di default.
        Assert.True(BarsIn(30, "1d") <= ExtractorWarmupBars,
            "premessa del test: 30 giorni su 1d danno 30 barre, sotto il warmup — è ciò che rompeva");

        var corretto = RegimeAnalysisStage.MinLabelDaysForTests("1d");
        Assert.True(corretto >= 120, $"servono almeno 120 giorni su 1d, calcolati {corretto}");
    }

    [Fact]
    public void TheHourlyCase_IsUnchanged()
    {
        // Il minimo non deve allargare la finestra dove già bastava: su 1h la richiesta dell'utente
        // (30 giorni) resta quella che comanda, altrimenti si leggerebbero dati inutili a ogni run.
        var minimo = RegimeAnalysisStage.MinLabelDaysForTests("1h");

        Assert.True(minimo < 30, $"su 1h il minimo ({minimo}) non deve superare la finestra di default");
        Assert.Equal(30, Math.Max(30, minimo));
    }

    [Fact]
    public void ExtractorReallyReturnsNothingBelowWarmup()
    {
        // Non si dà per buona la premessa: la si verifica sull'estrattore VERO.
        // dbFactory null: ComputeFeatures è il percorso PURO in memoria e non tocca il database.
        var extractor = new MarketFeatureExtractor(null!, new ProcioneMGR.Services.Indicators.TechnicalIndicatorsService());
        var poche = Enumerable.Range(0, ExtractorWarmupBars).Select(i => new ProcioneMGR.Data.OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1d",
            TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            Open = 100m, High = 101m, Low = 99m, Close = 100m + i, Volume = 10m,
        }).ToList();

        Assert.Empty(extractor.ComputeFeatures(poche, "1d"));

        var abbastanza = poche.Concat(Enumerable.Range(ExtractorWarmupBars, 80).Select(i => new ProcioneMGR.Data.OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1d",
            TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            Open = 100m, High = 101m, Low = 99m, Close = 100m + i, Volume = 10m,
        })).ToList();

        Assert.NotEmpty(extractor.ComputeFeatures(abbastanza, "1d"));
    }
}
