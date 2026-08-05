using ProcioneMGR.Services.Llm.Narration;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [G4] Post-mortem delle operazioni chiuse in perdita.
///
/// <para>Il contratto, in ordine di importanza: (1) dove la causa è ARITMETICA la stabilisce il
/// codice e l'AI non viene nemmeno interpellata; (2) l'AI sceglie SOLO dentro il menù chiuso, e
/// qualunque altra cosa vale come nessuna risposta ⇒ <c>Inconcludente</c>; (3) il testo che
/// raggiunge il comitato è un conteggio di cause, non un'opinione.</para>
/// </summary>
public class PostMortemTests
{
    private static TradeRecord Trade(
        decimal pnlPercent = -2.5m,
        string exitReason = "StopLoss",
        bool liquidated = false,
        decimal entry = 100m,
        decimal exit = 97.5m) => new()
        {
            Id = 42,
            LaneId = 1,
            Symbol = "DOT/USDT",
            StrategyId = "strategia-x",
            Side = OrderSide.Buy,
            EntryPrice = entry,
            ExitPrice = exit,
            PnlPercent = pnlPercent,
            OpenedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            ClosedAtUtc = new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromHours(6),
            ExitReason = exitReason,
            WasLiquidated = liquidated,
            Mode = TradingMode.Paper,
        };

    // ------------------------------------------------------------------ fatti

    [Fact]
    public void Extract_PortaIFattiVeriEStimaIlLordo()
    {
        var facts = PostMortemAnalyzer.Extract(Trade(pnlPercent: -2.5m), feePercent: 0.1m);

        Assert.Equal(42, facts.TradeRecordId);
        Assert.Equal("DOT/USDT", facts.Symbol);
        Assert.Equal(-2.5m, facts.PnlPercent);
        // Lordo = netto + costo andata e ritorno (0,1% × 2), dichiarato come stima.
        Assert.Equal(-2.3m, facts.GrossPnlPercent);
        Assert.Equal(0.2m, facts.FeePercentEstimate);
        Assert.Equal(TimeSpan.FromHours(6), facts.Duration);
    }

    [Fact]
    public void Extract_MotivoDiUscitaAssente_LoDichiara()
    {
        var facts = PostMortemAnalyzer.Extract(Trade(exitReason: ""), feePercent: 0.1m);
        Assert.Equal("(non dichiarato)", facts.ExitReason);
    }

    // ------------------------------------------------------------------ cause calcolabili

    /// <summary>
    /// Lordo positivo e netto negativo ⇒ i costi hanno mangiato il segnale. È aritmetica: pagare
    /// un LLM per confermarla sarebbe spreco, e lasciargli dire altro sarebbe peggio.
    /// </summary>
    [Fact]
    public void DeterministicCause_CostiCheMangianoIlLordo()
    {
        // Netto -0,05% con costi 0,2% ⇒ lordo +0,15%.
        var facts = PostMortemAnalyzer.Extract(Trade(pnlPercent: -0.05m), feePercent: 0.1m);

        Assert.Equal(PostMortemCauses.CostsDominate, PostMortemAnalyzer.DeterministicCause(facts));
    }

    [Fact]
    public void DeterministicCause_LiquidazioneVincesuTutto()
    {
        var facts = PostMortemAnalyzer.Extract(Trade(pnlPercent: -0.05m, liquidated: true), feePercent: 0.1m);

        Assert.Equal(PostMortemCauses.Liquidation, PostMortemAnalyzer.DeterministicCause(facts));
    }

    /// <summary>Perdita vera (anche al lordo): qui il codice non sa, e lo dice restituendo null.</summary>
    [Fact]
    public void DeterministicCause_PerditaVera_LasciaIlDubbio()
    {
        var facts = PostMortemAnalyzer.Extract(Trade(pnlPercent: -5m), feePercent: 0.1m);

        Assert.Null(PostMortemAnalyzer.DeterministicCause(facts));
    }

    // ------------------------------------------------------------------ menù chiuso

    [Fact]
    public void Menu_OgniVoceHaUnEtichetta()
    {
        foreach (var c in PostMortemCauses.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(PostMortemCauses.Label(c)), $"Etichetta mancante per {c}");
        }
    }

    [Fact]
    public void Menu_LeCauseCalcolabiliNonSonoOffrbileAllAi()
    {
        // Costi e liquidazione le stabilisce il codice: offrirle all'AI sarebbe darle la
        // possibilità di contraddire un'aritmetica.
        Assert.DoesNotContain(PostMortemCauses.CostsDominate, PostMortemCauses.AiSelectable);
        Assert.DoesNotContain(PostMortemCauses.Liquidation, PostMortemCauses.AiSelectable);
        Assert.Contains(PostMortemCauses.Inconclusive, PostMortemCauses.AiSelectable);
    }

    [Fact]
    public void BuildPrompt_ContieneIFattiEIlMenu()
    {
        var prompt = PostMortemService.BuildPrompt(PostMortemAnalyzer.Extract(Trade(), feePercent: 0.1m));

        Assert.Contains("DOT/USDT", prompt);
        Assert.Contains("StopLoss", prompt);
        Assert.Contains("-2.50%", prompt);
        foreach (var c in PostMortemCauses.AiSelectable)
        {
            Assert.Contains(c, prompt);
        }
    }

    // ------------------------------------------------------------------ verdetto difensivo

    [Fact]
    public void ParseVerdict_CausaDelMenu_Accettata()
    {
        var (cause, text) = PostMortemService.ParseVerdict(
            $$"""{"cause":"{{PostMortemCauses.AdverseRegime}}","text":"Il regime era cambiato."}""");

        Assert.Equal(PostMortemCauses.AdverseRegime, cause);
        Assert.Equal("Il regime era cambiato.", text);
    }

    /// <summary>
    /// LA PROPRIETÀ CHE CONTA: una causa inventata vale come NESSUNA risposta. Nessun tentativo di
    /// avvicinarla a una voce vera — indovinare qui significa attribuire una causa che nessuno ha
    /// scelto.
    /// </summary>
    [Theory]
    [InlineData("CausaInventata")]
    [InlineData("regimeavverso")]       // maiuscole sbagliate: fuori menù
    [InlineData("RegimeAvverso ma non troppo")]
    [InlineData("CostiDominanti")]      // esiste, ma NON è offribile all'AI (la calcola il codice)
    [InlineData("")]
    [InlineData(null)]
    public void ParseVerdict_FuoriMenu_ValeComeNessunaRisposta(string? cause)
    {
        var json = cause is null ? """{"text":"x"}""" : $$"""{"cause":"{{cause}}","text":"x"}""";

        var (parsed, text) = PostMortemService.ParseVerdict(json);

        Assert.Null(parsed);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ParseVerdict_SopravviveAiFencesMarkdown()
    {
        var (cause, _) = PostMortemService.ParseVerdict(
            $"```json\n{{\"cause\":\"{PostMortemCauses.NormalNoise}\",\"text\":\"ok\"}}\n```");

        Assert.Equal(PostMortemCauses.NormalNoise, cause);
    }

    [Theory]
    [InlineData("niente json")]
    [InlineData("")]
    public void ParseVerdict_SenzaJson_Lancia(string raw)
        => Assert.ThrowsAny<Exception>(() => PostMortemService.ParseVerdict(raw));

    // ------------------------------------------------------------------ contesto per il comitato

    [Fact]
    public void Summarize_NessunPostMortem_StringaVuota()
    {
        // Mai una frase che finge di sapere: se non c'è nulla, il comitato non riceve nulla.
        Assert.Equal(string.Empty, PostMortemService.Summarize([]));
    }

    [Fact]
    public void Summarize_ContaLeCauseInOrdineDiFrequenza()
    {
        var testo = PostMortemService.Summarize(
        [
            PostMortemCauses.AdverseRegime,
            PostMortemCauses.AdverseRegime,
            PostMortemCauses.AdverseRegime,
            PostMortemCauses.TightStop,
            PostMortemCauses.CostsDominate,
        ]);

        Assert.Contains("ultime 5 operazioni in perdita", testo);
        Assert.Contains("3× regime di mercato avverso", testo);
        Assert.Contains("1× stop troppo stretto", testo);
        // La più frequente per prima.
        Assert.True(testo.IndexOf("3×", StringComparison.Ordinal) < testo.IndexOf("1×", StringComparison.Ordinal));
    }

    [Fact]
    public void Summarize_Deterministico_APariMerito()
    {
        var a = PostMortemService.Summarize([PostMortemCauses.TightStop, PostMortemCauses.AdverseRegime]);
        var b = PostMortemService.Summarize([PostMortemCauses.AdverseRegime, PostMortemCauses.TightStop]);

        Assert.Equal(a, b);   // stesso insieme, stessa frase: niente rumore nel prompt
    }

    // ------------------------------------------------------------------ configurazione

    [Fact]
    public void Opzioni_SpentePerDefault()
    {
        var o = new PostMortemOptions();

        Assert.False(o.Enabled);
        Assert.False(o.UseAi);   // due interruttori distinti: scrivere i post-mortem e pagarli
        Assert.True(o.LossThresholdPercent > 0m);
        Assert.True(o.MaxPerRun > 0);
    }
}
