using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Narration;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Tests;

/// <summary>
/// [G9] Narrativa di sintesi in cima al digest giornaliero.
///
/// <para>Il contratto: il digest è il <b>dead-man's-switch</b> del proprietario — se non arriva,
/// la piattaforma è muta. Una funzione di comodo come questa non deve poterlo toccare in alcun
/// modo. Da qui la proprietà che questi test difendono per prima: <b>senza narrativa il messaggio
/// è identico carattere per carattere</b> a quello di prima della funzione.</para>
/// </summary>
public class DigestNarrativeTests
{
    private static DigestData Sample() => new(
        Lanes: ["corsia 0 AAVE/USDT [Paper]: Sharpe 0,42, 3 trade, DD 2,1%, 8gg"],
        FleetDecisions: ["ProposeGrey corsia 4 [dry-run]: Composite DOT/USDT 1h"],
        Attention: ["corsia 1: PRONTA per Testnet (auto-promozione spenta)"],
        AiUsage: "oggi 12 chiamate / 8400 token · mese 210000 token",
        Carry: "ultima valutazione 2026-08-05 06:00:00Z, 2 posizioni",
        Heartbeats: ["guscio: ultimo battito 1 min fa"]);

    // ------------------------------------------------------------------ additività

    /// <summary>
    /// LA PROPRIETÀ REGINA: narrativa assente ⇒ messaggio IDENTICO a quello che il compositore
    /// produceva prima che G9 esistesse. Non "equivalente": identico.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Compose_SenzaNarrativa_MessaggioIdenticoAllaVersioneSenzaParametro(string? narrative)
    {
        var now = new DateTime(2026, 8, 5, 7, 30, 0, DateTimeKind.Local);

        var senzaParametro = DailyDigestComposer.Compose(Sample(), now);
        var conNarrativaVuota = DailyDigestComposer.Compose(Sample(), now, narrative);

        Assert.Equal(senzaParametro, conNarrativaVuota);
    }

    [Fact]
    public void Compose_ConNarrativa_LaMetteSopraSenzaToccareIDati()
    {
        var now = new DateTime(2026, 8, 5, 7, 30, 0, DateTimeKind.Local);
        const string narrativa = "Giornata tranquilla: una sola proposta della flotta, nessuna promozione.";

        var baseline = DailyDigestComposer.Compose(Sample(), now);
        var conNarrativa = DailyDigestComposer.Compose(Sample(), now, narrativa);

        Assert.Contains(narrativa, conNarrativa);
        // I dati strutturati restano tutti, parola per parola: la sintesi si AGGIUNGE, non sostituisce.
        foreach (var riga in baseline.Split('\n').Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            Assert.Contains(riga, conNarrativa);
        }
        // E la chiusura col dead-man's-switch resta l'ultima riga.
        Assert.EndsWith("guarda il watchdog e scripts/bringup.ps1.", conNarrativa.TrimEnd());
    }

    [Fact]
    public void Compose_NarrativaSopraICorsie()
    {
        var now = new DateTime(2026, 8, 5, 7, 30, 0, DateTimeKind.Local);
        var testo = DailyDigestComposer.Compose(Sample(), now, "Sintesi.");

        Assert.True(testo.IndexOf("Sintesi.", StringComparison.Ordinal) < testo.IndexOf("CORSIE", StringComparison.Ordinal),
            "La sintesi deve stare SOPRA i dati: il lettore ha la fonte accanto al riassunto.");
    }

    [Fact]
    public void DigestOptions_NarrativaSpentaPerDefault()
        => Assert.False(new DigestOptions().NarrativeEnabled);

    // ------------------------------------------------------------------ prompt

    [Fact]
    public void BuildPrompt_ContieneLeStesseRigheDelMessaggio()
    {
        var prompt = DigestNarrator.BuildPrompt(Sample());

        Assert.Contains("corsia 0 AAVE/USDT", prompt);
        Assert.Contains("ProposeGrey corsia 4", prompt);
        Assert.Contains("PRONTA per Testnet", prompt);
        Assert.Contains("oggi 12 chiamate", prompt);
        Assert.Contains("2 posizioni", prompt);
        Assert.Contains("ultimo battito 1 min fa", prompt);
    }

    [Fact]
    public void BuildPrompt_DatiVuoti_DichiaraIlVuoto()
    {
        var prompt = DigestNarrator.BuildPrompt(new DigestData([], [], [], null, null, []));

        Assert.Contains("(nessuna corsia leggibile)", prompt);
        Assert.Contains("nessuna decisione", prompt);
    }

    // ------------------------------------------------------------------ pulizia della risposta

    [Fact]
    public void Clean_TogliMarkdownEPortaSuUnaRiga()
    {
        var pulito = DigestNarrator.Clean("```\n**Giornata** tranquilla.\n\nNessuna   promozione.\n```");

        Assert.Equal("Giornata tranquilla. Nessuna promozione.", pulito);
    }

    [Fact]
    public void Clean_TagliaIModelliProlissi()
    {
        var lungo = string.Join(" ", Enumerable.Repeat("parola", 400));

        var pulito = DigestNarrator.Clean(lungo);

        Assert.True(pulito.Length <= DigestNarrator.MaxChars + 1, $"Lunghezza {pulito.Length} oltre il tetto.");
        Assert.EndsWith("…", pulito);   // il taglio è dichiarato, non nascosto
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clean_VuotoRestaVuoto(string? raw)
        => Assert.Equal(string.Empty, DigestNarrator.Clean(raw));

    // ------------------------------------------------------------------ narratore

    private sealed class FakeLlm(string response) : ILlmClient
    {
        public bool IsConfigured => true;
        public string Model => "fake";
        public Task<string> CompleteAsync(string s, string u, CancellationToken ct) => Task.FromResult(response);
    }

    private sealed class ScriptedGuard(LlmCallOutcome outcome, string? text = null) : ILlmCallGuard
    {
        public string? LastPath { get; private set; }

        public Task<LlmCallResult> ExecuteAsync(string path, Func<CancellationToken, Task<string>> call,
            TimeSpan? timeout = null, bool forceProbe = false, CancellationToken ct = default)
        {
            LastPath = path;
            return Task.FromResult(outcome == LlmCallOutcome.Ok
                ? new LlmCallResult { Outcome = LlmCallOutcome.Ok, Text = text }
                : new LlmCallResult { Outcome = outcome, Cause = "test" });
        }

        public LlmGuardStatus GetStatus() => new(false, 0, null, null, null, null, null);
    }

    /// <summary>
    /// LIVELLO 2: ogni modo di non funzionare dell'AI produce «nessuna narrativa» — mai
    /// un'eccezione che risalga fino al worker e impedisca il digest.
    /// </summary>
    [Theory]
    [InlineData(LlmCallOutcome.SkippedNotConfigured)]
    [InlineData(LlmCallOutcome.SkippedBreakerOpen)]
    [InlineData(LlmCallOutcome.SkippedBudgetExhausted)]
    [InlineData(LlmCallOutcome.FailedRetryable)]
    [InlineData(LlmCallOutcome.FailedPermanent)]
    public async Task Narrate_AiNonDisponibile_RestituisceNull(LlmCallOutcome outcome)
    {
        var narrator = new DigestNarrator(new FakeLlm(""), new ScriptedGuard(outcome), NullLogger<DigestNarrator>.Instance);

        Assert.Null(await narrator.NarrateAsync(Sample()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```\n```")]
    public async Task Narrate_RispostaVuota_RestituisceNullNonUnaRigaVuota(string response)
    {
        var narrator = new DigestNarrator(new FakeLlm(response),
            new ScriptedGuard(LlmCallOutcome.Ok, response), NullLogger<DigestNarrator>.Instance);

        Assert.Null(await narrator.NarrateAsync(Sample()));
    }

    [Fact]
    public async Task Narrate_RispostaBuona_TornaPulita()
    {
        var narrator = new DigestNarrator(new FakeLlm("x"),
            new ScriptedGuard(LlmCallOutcome.Ok, "**Giornata** tranquilla.\nNessuna promozione."),
            NullLogger<DigestNarrator>.Instance);

        Assert.Equal("Giornata tranquilla. Nessuna promozione.", await narrator.NarrateAsync(Sample()));
    }

    [Fact]
    public async Task Narrate_UsaIlPathDichiarato()
    {
        var guard = new ScriptedGuard(LlmCallOutcome.SkippedNotConfigured);
        await new DigestNarrator(new FakeLlm(""), guard, NullLogger<DigestNarrator>.Instance).NarrateAsync(Sample());

        // Budget e metriche AF1 contano su questa etichetta.
        Assert.Equal(DigestNarrator.GuardPath, guard.LastPath);
    }
}
