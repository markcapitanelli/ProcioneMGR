using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K49 / K49b, PRD autonomia-piena — Fase 3, 2026-09-02] Due buchi che si somigliano: uno strumento
/// che c'è e non viene letto, e una guardia messa su tre porte quando le porte sono di più.
///
/// <list type="bullet">
/// <item><b>K49</b> — la guardia contro l'ipotesi doppia non passava dall'auto-apply, che è la porta
/// dell'impronta <i>e</i> di <c>/bot</c>. Il difetto che K33 esiste per impedire rientrava da lì, e
/// per giunta a cavallo dei due territori: l'impronta (0-2) e la flotta (3-7) non si guardano fra
/// loro, quindi nessuno dei due tetti se ne accorgeva.</item>
/// <item><b>K49b</b> — <c>CoversHoldout</c> esisteva, si accendeva a ogni run e nessuno lo leggeva a
/// valle: la famiglia «gate senza strumento» rovesciata. Misurato: <c>MKR/USDT</c> scoperta
/// <b>122 volte su 122</b>, senza una candela da 351 giorni, e comunque nell'universo.</item>
/// </list>
/// </summary>
public class PotaturaEPorteK49Tests
{
    private static PipelineConfiguration Config(params (string Symbol, string Timeframe)[] universo) => new()
    {
        Name = "prova",
        UniverseJson = System.Text.Json.JsonSerializer.Serialize(
            universo.Select(u => new SeriesSpec { Symbol = u.Symbol, Timeframe = u.Timeframe }).ToList()),
    };

    private static IReadOnlySet<(string, string)> Disabilitate(params (string, string)[] s) => s.ToHashSet();

    [Fact]
    public void UnaSERIEsospesaNELLuniverso_vieneDETTA()
    {
        // Il caso reale: MKR/USDT sospesa (BREAK) dal 2026-07-28, rimasta in due configurazioni per
        // 35 giorni, 11 chiavi candidate a zero trade e 424 righe di bocciatura a ogni giro.
        var testo = PipelinePageService.SerieSospese(
            Config(("BTC/USDT", "4h"), ("MKR/USDT", "4h")),
            Disabilitate(("MKR/USDT", "4h")));

        Assert.Equal("MKR/USDT 4h", testo);
    }

    [Fact]
    public void IlNULLO_diK49b_unUNIVERSOsanoNONsegnalaNULLA()
    {
        // Senza questo, un controllo che segnala sempre passerebbe il test qui sopra e riempirebbe
        // la lista di badge — che è il modo in cui un avviso vero smette di essere letto.
        Assert.Null(PipelinePageService.SerieSospese(
            Config(("BTC/USDT", "4h"), ("ETH/USDT", "4h")),
            Disabilitate(("MKR/USDT", "4h"))));
    }

    [Fact]
    public void UnaSERIEdisabilitataSUunALTROtimeframe_nonEquellaDELLuniverso()
    {
        // La chiave è (simbolo, timeframe): MKR/USDT 1d disabilitata non dice nulla su MKR/USDT 4h,
        // e confonderle produrrebbe un avviso su una serie che sta benissimo — o, nel verso
        // peggiore, silenzio su una che è sospesa davvero.
        Assert.Null(PipelinePageService.SerieSospese(
            Config(("MKR/USDT", "4h")),
            Disabilitate(("MKR/USDT", "1d"))));
    }

    [Fact]
    public void PIUserieSOSPESE_siELENCANOtutte()
    {
        var testo = PipelinePageService.SerieSospese(
            Config(("MKR/USDT", "4h"), ("BTC/USDT", "4h"), ("TON/USDT", "1h")),
            Disabilitate(("MKR/USDT", "4h"), ("TON/USDT", "1h")));

        Assert.Equal("MKR/USDT 4h, TON/USDT 1h", testo);
    }

    [Fact]
    public void UnUNIVERSOvuoto_nonEunAVVISO()
    {
        Assert.Null(PipelinePageService.SerieSospese(Config(), Disabilitate(("MKR/USDT", "4h"))));
    }
}
