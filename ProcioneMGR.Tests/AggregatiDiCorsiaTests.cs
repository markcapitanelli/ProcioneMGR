using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C1/C2/C3, 2026-08-21] I derivati che impediscono a una pagina di dire una cosa per un'altra.
///
/// <para>Nascono dalla notte intraday: la flotta aveva otto corsie riassegnate più volte fra simboli
/// diversi, e le superfici che le riassumono non dichiaravano quale finestra stessero guardando.
/// Il caso misurato è la corsia 0, con <b>159 operazioni in totale e ZERO sulla coppia che ha
/// configurata</b>: <c>/trading</c> aveva ricevuto la correzione il 2026-08-03, <c>/bot</c> no.</para>
/// </summary>
public class AggregatiDiCorsiaTests
{
    private static PipelineDateRanges Finestra(DateTime holdoutTo, int ampiezzaGiorni = 120) => new()
    {
        SelectionFrom = holdoutTo.AddDays(-ampiezzaGiorni - 400),
        SelectionTo = holdoutTo.AddDays(-ampiezzaGiorni),
        HoldoutFrom = holdoutTo.AddDays(-ampiezzaGiorni),
        HoldoutTo = holdoutTo,
    };

    // ------------------------------------------------- [C3] eta' della finestra, non sua ampiezza

    [Fact]
    public void EtaEAmpiezzaSonoDueCoseDiverse()
    {
        // È la confusione che la colonna «Aggiornata» alimentava: quella mostra l'ora dell'ultimo
        // SALVATAGGIO, che non dice nulla né dell'ampiezza né dell'età della finestra.
        var adesso = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var r = Finestra(new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), ampiezzaGiorni: 148);

        Assert.Equal(25.5, r.HoldoutAgeDays(adesso), precision: 1);   // vecchia di 25 giorni
        Assert.Equal(4.9m, r.HoldoutMonths()!.Value, precision: 1);   // ma larga quasi cinque mesi
    }

    [Fact]
    public void FinestraChiusaOggi_EtaQuasiZero()
    {
        var adesso = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.InRange(Finestra(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc)).HoldoutAgeDays(adesso), 0, 1);
    }

    [Fact]
    public void FinestraCheSiChiudeNelFuturo_DaUnNumeroNEGATIVO_NonZero()
    {
        // Anche «la finestra non è ancora chiusa» è un fatto da vedere: azzerarlo lo nasconderebbe.
        var adesso = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Finestra(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)).HoldoutAgeDays(adesso) < 0);
    }

    // --------------------------------------------------------- [C2] universi a timeframe misti

    private static PipelineConfiguration Config(params (string Symbol, string Timeframe)[] universo) => new()
    {
        Name = "prova",
        UniverseJson = System.Text.Json.JsonSerializer.Serialize(
            universo.Select(u => new SeriesSpec { Symbol = u.Symbol, Timeframe = u.Timeframe }).ToList()),
        DateRangesJson = System.Text.Json.JsonSerializer.Serialize(
            Finestra(new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc))),
    };

    [Fact]
    public void UniversoOmogeneo_NessunAvviso()
        => Assert.Null(PipelinePageService.MixedTimeframes(Config(("BTC/USDT", "1h"), ("ETH/USDT", "1h"))));

    [Fact]
    public void UniversoMisto_ELoDICE_ConIDueTimeframe()
    {
        // È lo stato in cui si trovava la configurazione 8, SCHEDULATA ogni notte: 46.000
        // combinazioni esplorate per non produrre nulla, perché la validazione holdout si rifiuta.
        var avviso = PipelinePageService.MixedTimeframes(Config(("BTC/USDT", "1h"), ("ETH/USDT", "4h")));

        Assert.NotNull(avviso);
        Assert.Contains("1h", avviso);
        Assert.Contains("4h", avviso);
    }

    [Fact]
    public void UniversoMisto_LOrdineNonDipendeDaComeEScrittoLUniverso()
    {
        // Due configurazioni con le stesse serie in ordine diverso devono dire la stessa cosa,
        // altrimenti il badge cambia testo senza che nulla sia cambiato.
        Assert.Equal(
            PipelinePageService.MixedTimeframes(Config(("BTC/USDT", "4h"), ("ETH/USDT", "1h"))),
            PipelinePageService.MixedTimeframes(Config(("ETH/USDT", "1h"), ("BTC/USDT", "4h"))));
    }

    [Fact]
    public void UniversoVuoto_NonEMisto()
        => Assert.Null(PipelinePageService.MixedTimeframes(Config()));

    [Fact]
    public void UniversoConTreTimeframe_LiElencaTutti()
    {
        var avviso = PipelinePageService.MixedTimeframes(Config(("BTC/USDT", "5m"), ("ETH/USDT", "15m"), ("SOL/USDT", "1h")));
        Assert.NotNull(avviso);
        Assert.Equal(2, avviso!.Count(ch => ch == '+'));
    }

    [Fact]
    public void FinestraNonLeggibile_NonFaEsplodereLaPagina()
    {
        // Regola 4 sulla diagnostica: un JSON corrotto deve degradare, non impedire di vedere la lista.
        var rotta = new PipelineConfiguration { Name = "rotta", UniverseJson = "[]", DateRangesJson = "{non-json" };
        Assert.Null(PipelinePageService.DateRangesOf(rotta));
    }
}
