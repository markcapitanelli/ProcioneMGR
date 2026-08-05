using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Narration;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [G6] Spiegazione dei candidati bocciati dai gate.
///
/// <para>Il contratto che questi test difendono, in ordine di importanza:</para>
/// <list type="number">
///   <item>il riassunto è DETERMINISTICO e non dipende dall'AI (livello 2: AI spenta ⇒ digest
///   identico);</item>
///   <item>l'AI non può far comparire in pagina un candidato che non esiste (le note con chiavi
///   inventate si scartano, contate);</item>
///   <item>il classificatore delle cause resta allineato ai messaggi VERI del motore, o lo dice.</item>
/// </list>
/// </summary>
public class RejectionExplainTests
{
    // ------------------------------------------------------------------ helper

    private static ValidatedCandidate Candidate(
        string strategy = "Composite",
        string symbol = "BTC/USDT",
        string timeframe = "1h",
        bool survived = false,
        string? reject = "Sharpe holdout 0,21 < 0,4",
        decimal holdoutSharpe = 0.21m,
        int holdoutTrades = 30,
        double? dsr = null,
        double? pbo = null,
        double? nullTwin = null) => new()
        {
            StrategyName = strategy,
            Symbol = symbol,
            Timeframe = timeframe,
            Survived = survived,
            RejectReason = reject,
            HoldoutSharpe = holdoutSharpe,
            HoldoutTrades = holdoutTrades,
            DeflatedSharpe = dsr,
            PanelPbo = pbo,
            NullTwinPercentile = nullTwin,
        };

    // ------------------------------------------------------------------ classificatore

    /// <summary>
    /// LE STRINGHE DI QUESTO TEST SONO QUELLE VERE DEL MOTORE (ModelStages.cs e
    /// NullTwinValidationStage.cs). Il classificatore legge prefissi scritti altrove: se qualcuno
    /// cambia un messaggio del motore senza aggiornare <see cref="RejectionDigestBuilder.Classify"/>,
    /// questo test fallisce ed è esattamente il punto — l'alternativa silenziosa sarebbe contare
    /// quei candidati sotto l'etichetta sbagliata.
    /// </summary>
    [Theory]
    [InlineData("Sharpe holdout 0,21 < 0,4", RejectionCauses.SharpeHoldout)]
    [InlineData("Solo 7 trade in holdout (< 20)", RejectionCauses.ContoTrade)]
    [InlineData("DSR 0,812 ≤ 0,95 (probabile overfitting da selezione)", RejectionCauses.DeflatedSharpe)]
    [InlineData("PBO di pannello 62 % ≥ 50 %: selezione inaffidabile", RejectionCauses.PanelPbo)]
    [InlineData("permutation p 0,180 ≥ 0,05 (Sharpe holdout compatibile col rumore)", RejectionCauses.Permutation)]
    [InlineData("Gemello nullo: reale 1,43 al 71° percentile della distribuzione nulla", RejectionCauses.NullTwin)]
    [InlineData("MC RiskFactor95 3,10× > 2×", RejectionCauses.MonteCarlo)]
    [InlineData("Backtest fallito: sequence contains no elements", RejectionCauses.BacktestFailed)]
    [InlineData("Nessun trade nel range di selezione con le varianti stop.", RejectionCauses.NoTrades)]
    public void Classify_RiconosceIMessaggiRealiDelMotore(string reason, string expected)
        => Assert.Equal(expected, RejectionDigestBuilder.Classify(reason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_SenzaMotivo_DichiaraLIgnoranza(string? reason)
        => Assert.Equal(RejectionCauses.Undeclared, RejectionDigestBuilder.Classify(reason));

    /// <summary>Un motivo che il classificatore non conosce finisce in «Other», MAI in una causa a caso.</summary>
    [Fact]
    public void Classify_MotivoSconosciuto_FinisceInOther()
        => Assert.Equal(RejectionCauses.Other, RejectionDigestBuilder.Classify("Bocciato per motivi suoi"));

    [Fact]
    public void Label_CopreOgniCausaProdottaDalClassificatore()
    {
        // Ogni causa che il classificatore può restituire deve avere un'etichetta leggibile:
        // un'etichetta mancante finirebbe in UI come stringa tecnica.
        string[] tutte =
        [
            RejectionCauses.SharpeHoldout, RejectionCauses.ContoTrade, RejectionCauses.DeflatedSharpe,
            RejectionCauses.PanelPbo, RejectionCauses.Permutation, RejectionCauses.NullTwin,
            RejectionCauses.MonteCarlo, RejectionCauses.BacktestFailed, RejectionCauses.NoTrades,
            RejectionCauses.Undeclared, RejectionCauses.Other,
        ];
        foreach (var c in tutte)
        {
            Assert.False(string.IsNullOrWhiteSpace(RejectionCauses.Label(c)), $"Etichetta mancante per {c}");
        }
    }

    // ------------------------------------------------------------------ digest deterministico

    [Fact]
    public void Build_ListaVuota_DigestVuoto()
    {
        Assert.Equal(RunRejectionDigest.Empty, RejectionDigestBuilder.Build([]));
        Assert.Equal(RunRejectionDigest.Empty, RejectionDigestBuilder.Build(null));
    }

    [Fact]
    public void Build_TuttiSopravvissuti_NessunaBocciatura()
    {
        var digest = RejectionDigestBuilder.Build([Candidate(survived: true, reject: null), Candidate(survived: true, reject: null)]);

        Assert.Equal(2, digest.Evaluated);
        Assert.Equal(2, digest.Survived);
        Assert.Equal(0, digest.Rejected);
        Assert.False(digest.HasContent);
        Assert.Empty(digest.Groups);
    }

    [Fact]
    public void Build_RaggruppaPerCausaEContaTutti()
    {
        List<ValidatedCandidate> candidates =
        [
            Candidate(symbol: "BTC/USDT", reject: "Solo 7 trade in holdout (< 20)"),
            Candidate(symbol: "ETH/USDT", reject: "Solo 9 trade in holdout (< 20)"),
            Candidate(symbol: "SOL/USDT", reject: "Solo 3 trade in holdout (< 20)"),
            Candidate(symbol: "DOT/USDT", reject: "DSR 0,812 ≤ 0,95 (probabile overfitting da selezione)"),
            Candidate(symbol: "XRP/USDT", survived: true, reject: null),
        ];

        var digest = RejectionDigestBuilder.Build(candidates);

        Assert.Equal(5, digest.Evaluated);
        Assert.Equal(1, digest.Survived);
        Assert.Equal(4, digest.Rejected);
        // La causa più frequente per prima.
        Assert.Equal(RejectionCauses.ContoTrade, digest.Groups[0].Cause);
        Assert.Equal(3, digest.Groups[0].Count);
        Assert.Equal(RejectionCauses.DeflatedSharpe, digest.Groups[1].Cause);
        Assert.Equal(1, digest.Groups[1].Count);
        // I conteggi coprono TUTTI i bocciati, non solo quelli riportati per esteso.
        Assert.Equal(digest.Rejected, digest.Groups.Sum(g => g.Count));
    }

    /// <summary>topN limita il DETTAGLIO, non i conteggi: è la differenza fra «riassumere» e «nascondere».</summary>
    [Fact]
    public void Build_TopN_LimitaSoloIlDettaglioNonIConteggi()
    {
        var candidates = Enumerable.Range(0, 12)
            .Select(i => Candidate(symbol: $"SYM{i:D2}/USDT", holdoutSharpe: i * 0.1m))
            .ToList();

        var digest = RejectionDigestBuilder.Build(candidates, topN: 3);

        Assert.Equal(12, digest.Rejected);
        Assert.Equal(12, digest.Groups.Sum(g => g.Count));
        Assert.Equal(3, digest.TopRejected.Count);
        // Ordinati per Sharpe holdout decrescente.
        Assert.Equal(1.1m, digest.TopRejected[0].HoldoutSharpe);
        Assert.Equal(1.0m, digest.TopRejected[1].HoldoutSharpe);
        Assert.Equal(0.9m, digest.TopRejected[2].HoldoutSharpe);
    }

    [Fact]
    public void Build_OrdineDeterministico_SuSharpeUguali()
    {
        // Due run identici devono produrre lo stesso ordine: un ordine che balla è rumore in UI.
        List<ValidatedCandidate> candidates =
        [
            Candidate(symbol: "ZZZ/USDT", holdoutSharpe: 0.5m),
            Candidate(symbol: "AAA/USDT", holdoutSharpe: 0.5m),
            Candidate(symbol: "MMM/USDT", holdoutSharpe: 0.5m),
        ];

        var a = RejectionDigestBuilder.Build(candidates);
        var b = RejectionDigestBuilder.Build([.. candidates.AsEnumerable().Reverse()]);

        Assert.Equal(a.TopRejected.Select(c => c.Symbol), b.TopRejected.Select(c => c.Symbol));
    }

    /// <summary>La fascia grigia si conta con LO STESSO filtro del resto della piattaforma, non con uno nuovo.</summary>
    [Fact]
    public void Build_ContaLaFasciaGrigiaCollFiltroCondiviso()
    {
        List<ValidatedCandidate> candidates =
        [
            // Grigio: bocciato per finestra corta, Sharpe holdout positivo, almeno un trade.
            Candidate(symbol: "BTC/USDT", reject: "Solo 7 trade in holdout (< 20)", holdoutSharpe: 1.93m, holdoutTrades: 7),
            // NON grigio: stessa causa ma Sharpe negativo (bocciato nel merito).
            Candidate(symbol: "ETH/USDT", reject: "Solo 5 trade in holdout (< 20)", holdoutSharpe: -0.4m, holdoutTrades: 5),
            // NON grigio: bocciato nel merito.
            Candidate(symbol: "SOL/USDT", reject: "PBO di pannello 62 % ≥ 50 %: selezione inaffidabile", holdoutSharpe: 0.9m),
        ];

        var digest = RejectionDigestBuilder.Build(candidates);

        Assert.Equal(1, digest.GreyCount);
        Assert.True(digest.TopRejected.Single(c => c.Symbol == "BTC/USDT").IsGrey);
        Assert.False(digest.TopRejected.Single(c => c.Symbol == "ETH/USDT").IsGrey);
    }

    [Fact]
    public void Build_PortaINumeriVeriDelVerdetto()
    {
        var digest = RejectionDigestBuilder.Build(
        [
            Candidate(reject: "Gemello nullo: reale 1,43 al 71° percentile", holdoutSharpe: 1.43m,
                holdoutTrades: 42, dsr: 0.87, pbo: 0.31, nullTwin: 71),
        ]);

        var facts = Assert.Single(digest.TopRejected);
        Assert.Equal(1.43m, facts.HoldoutSharpe);
        Assert.Equal(42, facts.HoldoutTrades);
        Assert.Equal(0.87, facts.DeflatedSharpe);
        Assert.Equal(0.31, facts.PanelPbo);
        Assert.Equal(71, facts.NullTwinPercentile);
        Assert.Equal(RejectionCauses.NullTwin, facts.Cause);
        Assert.Contains("Gemello nullo", facts.RejectReason);
    }

    // ------------------------------------------------------------------ prompt

    [Fact]
    public void BuildPrompt_ContieneNumeriVeriEChiavi()
    {
        var digest = RejectionDigestBuilder.Build(
        [
            Candidate(symbol: "DOT/USDT", reject: "Solo 7 trade in holdout (< 20)", holdoutSharpe: 1.93m, holdoutTrades: 7),
            Candidate(symbol: "LTC/USDT", survived: true, reject: null),
        ]);

        var prompt = RejectionNarrator.BuildPrompt(digest);

        Assert.Contains("2 candidati valutati", prompt);
        Assert.Contains("1 sopravvissuti", prompt);
        Assert.Contains("DOT/USDT", prompt);
        Assert.Contains("1.93", prompt);          // i numeri passano in cultura invariante
        Assert.Contains("Solo 7 trade in holdout", prompt); // il verdetto testuale del motore, verbatim
        Assert.Contains(digest.TopRejected[0].Key, prompt); // la chiave che le note dovranno usare
    }

    // ------------------------------------------------------------------ parsing difensivo

    [Fact]
    public void Parse_TieneSoloLeNoteConChiaviInviate()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Composite BTC/USDT 1h" };
        const string raw = """
            {"summary":"Quadro d'insieme.",
             "notes":[{"key":"Composite BTC/USDT 1h","text":"Bocciato dal conteggio trade."},
                      {"key":"Strategia Inventata XYZ 5m","text":"Non esiste."}]}
            """;

        var narration = RejectionNarrator.Parse(raw, allowed);

        var note = Assert.Single(narration.Notes);
        Assert.Equal("Composite BTC/USDT 1h", note.Key);
        Assert.Equal(1, narration.DiscardedNotes); // scartata, e CONTATA: non nascosta
    }

    /// <summary>
    /// La proprietà che conta di più: un modello che inventa TUTTO non riesce a far comparire in
    /// pagina un solo candidato falso. Resta il riassunto, che al più è prosa inutile — mai un
    /// candidato che non è mai esistito accanto a quelli veri.
    /// </summary>
    [Fact]
    public void Parse_ModelloCheInventaTutto_NonProduceNemmenoUnaNota()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Composite BTC/USDT 1h" };
        const string raw = """
            {"summary":"...",
             "notes":[{"key":"Falso 1","text":"a"},{"key":"Falso 2","text":"b"},{"key":"Falso 3","text":"c"}]}
            """;

        var narration = RejectionNarrator.Parse(raw, allowed);

        Assert.Empty(narration.Notes);
        Assert.Equal(3, narration.DiscardedNotes);
    }

    [Fact]
    public void Parse_ChiaveRipetuta_UnaSolaVolta()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "K1" };
        const string raw = """{"summary":"s","notes":[{"key":"K1","text":"primo"},{"key":"K1","text":"secondo"}]}""";

        var narration = RejectionNarrator.Parse(raw, allowed);

        Assert.Single(narration.Notes);
        Assert.Equal("primo", narration.Notes[0].Text);
        Assert.Equal(1, narration.DiscardedNotes);
    }

    [Fact]
    public void Parse_NoteVuoteScartate()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "K1", "K2" };
        const string raw = """{"summary":"s","notes":[{"key":"K1","text":"  "},{"key":"","text":"x"},{"key":"K2","text":"buona"}]}""";

        var narration = RejectionNarrator.Parse(raw, allowed);

        Assert.Single(narration.Notes);
        Assert.Equal("K2", narration.Notes[0].Key);
        Assert.Equal(2, narration.DiscardedNotes);
    }

    [Fact]
    public void Parse_SopravviveAiFencesMarkdown()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "K1" };
        const string raw = """
            Ecco l'analisi:
            ```json
            {"summary":"Riassunto.","notes":[{"key":"K1","text":"nota"}]}
            ```
            """;

        var narration = RejectionNarrator.Parse(raw, allowed);

        Assert.Equal("Riassunto.", narration.Summary);
        Assert.Single(narration.Notes);
    }

    [Theory]
    [InlineData("nessun json qui")]
    [InlineData("")]
    public void Parse_RispostaSenzaJson_Lancia(string raw)
        => Assert.ThrowsAny<Exception>(() => RejectionNarrator.Parse(raw, new HashSet<string>()));

    // ------------------------------------------------------------------ narratore (fallback)

    private sealed class FakeLlm(string response = "{}") : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured { get; set; } = true;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }

    /// <summary>Guard che restituisce sempre lo stesso esito, per provare i rami di fallback.</summary>
    private sealed class ScriptedGuard(LlmCallOutcome outcome, string? text = null) : ILlmCallGuard
    {
        public int Calls { get; private set; }

        public async Task<LlmCallResult> ExecuteAsync(string path, Func<CancellationToken, Task<string>> call,
            TimeSpan? timeout = null, bool forceProbe = false, CancellationToken ct = default)
        {
            Calls++;
            if (outcome != LlmCallOutcome.Ok)
            {
                return new LlmCallResult { Outcome = outcome, Cause = "test" };
            }
            var produced = text ?? await call(ct);
            return new LlmCallResult { Outcome = LlmCallOutcome.Ok, Text = produced };
        }

        public LlmGuardStatus GetStatus() =>
            new(false, 0, null, null, null, null, null);
    }

    private static RejectionNarrator Narrator(ILlmClient llm, ILlmCallGuard guard) =>
        new(llm, guard, NullLogger<RejectionNarrator>.Instance);

    private static RunRejectionDigest SampleDigest() =>
        RejectionDigestBuilder.Build([Candidate(symbol: "DOT/USDT", reject: "Solo 7 trade in holdout (< 20)", holdoutSharpe: 1.93m, holdoutTrades: 7)]);

    /// <summary>Digest vuoto ⇒ nessuna chiamata: una chiamata a vuoto la si paga comunque.</summary>
    [Fact]
    public async Task Narrate_DigestVuoto_NonChiamaLAi()
    {
        var llm = new FakeLlm();
        var guard = new ScriptedGuard(LlmCallOutcome.Ok);

        var result = await Narrator(llm, guard).NarrateAsync(RunRejectionDigest.Empty);

        Assert.Null(result);
        Assert.Equal(0, guard.Calls);
        Assert.Equal(0, llm.Calls);
    }

    /// <summary>
    /// LIVELLO 2 — la proprietà regina: qualunque modo l'AI abbia di non funzionare produce
    /// «nessuna prosa», mai un'eccezione e mai un digest alterato.
    /// </summary>
    [Theory]
    [InlineData(LlmCallOutcome.SkippedNotConfigured)]
    [InlineData(LlmCallOutcome.SkippedBreakerOpen)]
    [InlineData(LlmCallOutcome.SkippedBudgetExhausted)]
    [InlineData(LlmCallOutcome.FailedRetryable)]
    [InlineData(LlmCallOutcome.FailedPermanent)]
    public async Task Narrate_AiNonDisponibile_RestituisceNullSenzaEccezioni(LlmCallOutcome outcome)
    {
        var digest = SampleDigest();
        var result = await Narrator(new FakeLlm(), new ScriptedGuard(outcome)).NarrateAsync(digest);

        Assert.Null(result);
        // Il digest è un record immutabile: resta quello di prima, per costruzione.
        Assert.Equal(1, digest.Rejected);
        Assert.Single(digest.TopRejected);
    }

    [Fact]
    public async Task Narrate_RispostaIllegibile_RestituisceNull()
    {
        var result = await Narrator(new FakeLlm(), new ScriptedGuard(LlmCallOutcome.Ok, "non è json"))
            .NarrateAsync(SampleDigest());

        Assert.Null(result);
    }

    [Fact]
    public async Task Narrate_RispostaBuona_PortaModelloENote()
    {
        var digest = SampleDigest();
        var key = digest.TopRejected[0].Key;
        var payload = $$"""{"summary":"Un run senza sopravvissuti.","notes":[{"key":"{{key}}","text":"Solo 7 trade."}]}""";

        var result = await Narrator(new FakeLlm(), new ScriptedGuard(LlmCallOutcome.Ok, payload)).NarrateAsync(digest);

        Assert.NotNull(result);
        Assert.Equal("Un run senza sopravvissuti.", result.Summary);
        Assert.Equal("fake-model", result.ModelUsed);
        Assert.Equal(key, Assert.Single(result.Notes).Key);
        Assert.Equal(0, result.DiscardedNotes);
        Assert.NotEqual(default, result.CreatedAtUtc);
    }

    /// <summary>Il path del guard è quello dichiarato: budget e metriche di AF1 contano su questa etichetta.</summary>
    [Fact]
    public async Task Narrate_UsaIlPathDichiarato()
    {
        string? seen = null;
        var guard = new PathCapturingGuard(p => seen = p);

        await Narrator(new FakeLlm(), guard).NarrateAsync(SampleDigest());

        Assert.Equal(RejectionNarrator.GuardPath, seen);
    }

    private sealed class PathCapturingGuard(Action<string> onPath) : ILlmCallGuard
    {
        public Task<LlmCallResult> ExecuteAsync(string path, Func<CancellationToken, Task<string>> call,
            TimeSpan? timeout = null, bool forceProbe = false, CancellationToken ct = default)
        {
            onPath(path);
            return Task.FromResult(new LlmCallResult { Outcome = LlmCallOutcome.SkippedNotConfigured, Cause = "test" });
        }

        public LlmGuardStatus GetStatus() => new(false, 0, null, null, null, null, null);
    }

    // ------------------------------------------------------------------ configurazione

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    [InlineData(-3, false)]
    public void AdminConfigRules_ValidaIlTettoDeiBocciatiRiportati(int topN, bool valid)
    {
        var options = new LlmOptions { ExplainRejectionsTopN = topN };
        var error = ProcioneMGR.Services.Config.AdminConfigRules.Validate(options);

        if (valid) Assert.Null(error);
        else Assert.NotNull(error);
    }

    [Fact]
    public void LlmOptions_SpiegazioneSpentaPerDefault()
    {
        // Default-off: l'invariante di ogni fase del Filone G.
        Assert.False(new LlmOptions().ExplainRejections);
        Assert.Equal(RejectionDigestBuilder.DefaultTopN, new LlmOptions().ExplainRejectionsTopN);
    }
}
