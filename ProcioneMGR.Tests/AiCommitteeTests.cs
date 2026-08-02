using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Committee;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF3] Il comitato a scelta vincolata. La proprietà REGINA (livello 2 dello standard):
/// provider che rispondono spazzatura al 100% ⇒ comportamento IDENTICO al comitato spento —
/// decide sempre il default deterministico, mai un'eccezione, mai una scelta fuori menù.
/// </summary>
public sealed class AiCommitteeTests
{
    private sealed class ScriptedClient(string? reply, bool configured = true) : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => configured;
        public string Model => "test-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            return reply is null
                ? throw new InvalidOperationException("GROQ HTTP 503: fuori servizio (simulato)")
                : Task.FromResult(reply);
        }
    }

    private sealed class ScriptedResolver(Dictionary<string, ILlmClient> clients) : ILlmClientResolver
    {
        public ILlmClient? Resolve(string provider) => clients.GetValueOrDefault(provider.ToLowerInvariant());
    }

    private static CommitteeQuestion Question(params string[] optionIds) => new(
        "fleet-assignment",
        "Contesto di prova.",
        optionIds.Select(id => new CommitteeOption(id, $"opzione {id}")).ToList(),
        optionIds[0]);

    private static AiCommittee Committee(Dictionary<string, ILlmClient> clients, CommitteeOptions? options = null)
        => new(new ScriptedResolver(clients),
            (options ?? new CommitteeOptions { Enabled = true, MinValidVotes = 2 }).AsMonitor(),
            NullLogger<AiCommittee>.Instance);

    private static string Vote(string choice) => $$"""{"choice":"{{choice}}","confidence":0.8,"reason":"ok"}""";

    // ------------------------------------------------------------------ parse

    [Fact]
    public void Parse_ValidVote_IsCounted()
    {
        var vote = AiCommittee.Parse("nvidia", Vote("b"), Question("a", "b"));
        Assert.True(vote.Valid);
        Assert.Equal("b", vote.OptionId);
    }

    [Fact]
    public void Parse_MarkdownFences_AreTolerated()
    {
        var raw = "Ecco la mia analisi:\n```json\n" + Vote("a") + "\n```\nSpero sia utile!";
        Assert.True(AiCommittee.Parse("groq", raw, Question("a", "b")).Valid);
    }

    [Theory]
    [InlineData("""{"choice":"z","confidence":0.9,"reason":"invento"}""")] // fuori menù
    [InlineData("""{"confidence":0.9,"reason":"senza scelta"}""")]         // choice assente
    [InlineData("non sono un JSON")]
    [InlineData("{ rotto")]
    public void Parse_AnythingOffContract_IsAbstention(string raw)
    {
        var vote = AiCommittee.Parse("gemini", raw, Question("a", "b"));
        Assert.False(vote.Valid);
        Assert.Null(vote.OptionId);
    }

    // ------------------------------------------------------------------ quorum

    [Fact]
    public async Task Majority_Wins()
    {
        var committee = Committee(new()
        {
            ["nvidia"] = new ScriptedClient(Vote("b")),
            ["groq"] = new ScriptedClient(Vote("b")),
            ["gemini"] = new ScriptedClient(Vote("a")),
        });

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.True(verdict.ByQuorum);
        Assert.Equal("b", verdict.ChosenOptionId);
        Assert.Equal(3, verdict.Votes.Count(v => v.Valid));
    }

    [Fact]
    public async Task Tie_FallsBackToTheDefault()
    {
        var committee = Committee(new()
        {
            ["nvidia"] = new ScriptedClient(Vote("b")),
            ["groq"] = new ScriptedClient(Vote("a")),
        });

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.False(verdict.ByQuorum);
        Assert.Equal("a", verdict.ChosenOptionId); // il default, non una moneta
    }

    [Fact]
    public async Task GarbageFromEveryProvider_IsIdenticalToCommitteeOff()
    {
        // LA proprietà: spazzatura totale (fuori menù, JSON rotto, 503) ⇒ default, zero eccezioni.
        var committee = Committee(new()
        {
            ["nvidia"] = new ScriptedClient("""{"choice":"inventata","reason":"x"}"""),
            ["groq"] = new ScriptedClient("BUY BUY BUY!!!"),
            ["gemini"] = new ScriptedClient(null), // lancia (503 simulato)
        });

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.False(verdict.ByQuorum);
        Assert.Equal("a", verdict.ChosenOptionId);
        Assert.Equal(3, verdict.Votes.Count);
        Assert.All(verdict.Votes, v => Assert.False(v.Valid));
    }

    [Fact]
    public async Task BelowQuorum_EvenAUnanimousSingleVote_IsNotAMajority()
    {
        var committee = Committee(new()
        {
            ["nvidia"] = new ScriptedClient(Vote("b")),
            ["groq"] = new ScriptedClient(null),     // astensione
            ["gemini"] = new ScriptedClient(null),   // astensione
        }, new CommitteeOptions { Enabled = true, MinValidVotes = 2 });

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.False(verdict.ByQuorum);
        Assert.Equal("a", verdict.ChosenOptionId);
    }

    [Fact]
    public async Task Disabled_NeverCallsAnyone()
    {
        var nvidia = new ScriptedClient(Vote("b"));
        var committee = Committee(new() { ["nvidia"] = nvidia },
            new CommitteeOptions { Enabled = false });

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.Equal("a", verdict.ChosenOptionId);
        Assert.Equal(0, nvidia.Calls);
    }

    [Fact]
    public async Task ExhaustedBudget_SkipsTheWholeRound()
    {
        var nvidia = new ScriptedClient(Vote("b"));
        var sink = new ExhaustedSink();
        var committee = new AiCommittee(new ScriptedResolver(new() { ["nvidia"] = nvidia }),
            new CommitteeOptions { Enabled = true, MinValidVotes = 1 }.AsMonitor(),
            NullLogger<AiCommittee>.Instance, usageSink: sink);

        var verdict = await committee.AskAsync(Question("a", "b"));

        Assert.Equal("a", verdict.ChosenOptionId);
        Assert.Equal(0, nvidia.Calls); // il comitato triplica le chiamate: il budget si controlla PRIMA
    }

    private sealed class ExhaustedSink : ILlmUsageSink
    {
        public void Record(LlmUsageEvent e) { }
        public LlmBudgetVerdict CheckBudget() => new(true, "budget: test");
        public bool TryMarkExhaustionNotified() => false;
        public LlmUsageSnapshot GetSnapshot() => new(DateTime.UtcNow.Date, [], 0, 0, 0, true);
    }

    [Fact]
    public async Task MenuWithoutTheDefault_IsRejectedLoudly()
    {
        var committee = Committee(new() { ["nvidia"] = new ScriptedClient(Vote("b")) });
        var broken = new CommitteeQuestion("k", "ctx", [new CommitteeOption("b", "b")], DefaultOptionId: "a");

        await Assert.ThrowsAsync<ArgumentException>(() => committee.AskAsync(broken));
    }
}

/// <summary>[AF5.4] Lo scheduling del digest: un orario, una volta al giorno, mai a raffica.</summary>
public sealed class DigestScheduleTests
{
    private static readonly DateTime Day = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Local);

    [Fact]
    public void BeforeTheHour_NotDue()
        => Assert.False(DigestSchedule.IsDue(Day.AddHours(7).AddMinutes(29), 7, 30, null));

    [Fact]
    public void AtTheHour_Due()
        => Assert.True(DigestSchedule.IsDue(Day.AddHours(7).AddMinutes(30), 7, 30, null));

    [Fact]
    public void AlreadySentToday_NotDueAgain()
        => Assert.False(DigestSchedule.IsDue(Day.AddHours(9), 7, 30, DateOnly.FromDateTime(Day)));

    [Fact]
    public void SentYesterday_DueToday()
        => Assert.True(DigestSchedule.IsDue(Day.AddHours(7).AddMinutes(31), 7, 30, DateOnly.FromDateTime(Day.AddDays(-1))));

    [Fact]
    public void LateStart_StillSendsTheSameDay()
        // App partita alle 15: il digest delle 07:30 non è "perso", è dovuto.
        => Assert.True(DigestSchedule.IsDue(Day.AddHours(15), 7, 30, null));

    [Fact]
    public void Composer_AlwaysEndsWithTheDeadMansSwitchLine()
    {
        var text = DailyDigestComposer.Compose(
            new DigestData([], [], [], null, null, []), new DateTime(2026, 8, 3, 7, 30, 0));
        Assert.Contains("Se domani questo messaggio non arriva", text);
        Assert.Contains("CORSIE", text);
    }
}
