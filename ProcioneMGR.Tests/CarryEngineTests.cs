using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Carry;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E3 roadmap profitto-intraday] Orchestrazione live del carry. Questi test fissano: (a) la regola
/// di decisione è la STESSA del backtest (CarryDecider — un solo punto di verità); (b) il motore
/// apre due gambe quando il funding è alto e chiude quando scende, tramite l'executor; (c) la
/// finestra non piena → Hold; (d) il failsafe strutturale: CarryMode non ha il valore Live, quindi
/// operare con denaro reale è IRRAPPRESENTABILE.
/// </summary>
public class CarryEngineTests
{
    /// <summary>Executor spia: registra le aperture/chiusure senza toccare alcun exchange.</summary>
    private sealed class RecordingExecutor : ICarryExecutor
    {
        public CarryMode Mode => CarryMode.Paper;
        public List<(string Sym, decimal Notional)> Opens { get; } = [];
        public List<(string Sym, decimal Notional)> Closes { get; } = [];
        public bool OpenShouldFail { get; set; }

        public Task<CarryExecutionResult> OpenAsync(string symbol, decimal notionalQuote, CancellationToken ct)
        {
            if (OpenShouldFail) return Task.FromResult(new CarryExecutionResult(false, "gamba perp rifiutata"));
            Opens.Add((symbol, notionalQuote));
            return Task.FromResult(new CarryExecutionResult(true, "ok"));
        }

        public Task<CarryExecutionResult> CloseAsync(string symbol, decimal notionalQuote, CancellationToken ct)
        {
            Closes.Add((symbol, notionalQuote));
            return Task.FromResult(new CarryExecutionResult(true, "ok"));
        }
    }

    private static CarryConfiguration Cfg() => new()
    {
        InitialCapital = 10_000m, PositionSizePercent = 50m,
        EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 0m,
        TrailingFundingEvents = 3, FundingEventsPerDay = 3,
    };

    private static CarryEngine Engine(RecordingExecutor ex) =>
        new(ex, Cfg(), NullLogger<CarryEngine>.Instance);

    // Funding annualizzato = media·3·365. Per superare 5%/anno servono ~0,0046%/8h; 0,03%/8h = 32,85%.
    private static List<decimal> High => [0.03m, 0.03m, 0.03m];
    // Sotto la soglia di uscita 0 in modo NON ambiguo (funding leggermente negativo: "sparito").
    private static List<decimal> Low => [-0.002m, -0.002m, -0.002m];

    [Fact]
    public async Task HighFunding_OpensBothLegs_AtConfiguredNotional()
    {
        var ex = new RecordingExecutor();
        var action = await Engine(ex).EvaluateAsync("BTC/USDT", High, CancellationToken.None);

        Assert.Equal(CarryAction.Open, action);
        var open = Assert.Single(ex.Opens);
        Assert.Equal("BTC/USDT", open.Sym);
        Assert.Equal(5_000m, open.Notional);   // 50% di 10.000: stesso nozionale per gamba
    }

    [Fact]
    public async Task FundingDrops_WhenInPosition_Closes()
    {
        var ex = new RecordingExecutor();
        var engine = Engine(ex);

        await engine.EvaluateAsync("BTC/USDT", High, CancellationToken.None);   // apre
        Assert.True(engine.States["BTC/USDT"].InPosition);

        var action = await engine.EvaluateAsync("BTC/USDT", Low, CancellationToken.None);   // esce
        Assert.Equal(CarryAction.Close, action);
        Assert.Single(ex.Closes);
        Assert.False(engine.States["BTC/USDT"].InPosition);
    }

    [Fact]
    public async Task WindowNotFull_Holds_NoExecution()
    {
        var ex = new RecordingExecutor();
        // Solo 2 punti ma trailing=3: la finestra non è piena → non si decide.
        var action = await Engine(ex).EvaluateAsync("BTC/USDT", [0.03m, 0.03m], CancellationToken.None);

        Assert.Equal(CarryAction.Hold, action);
        Assert.Empty(ex.Opens);
    }

    [Fact]
    public async Task OpenFailure_LeavesFlat_DoesNotMarkInPosition()
    {
        var ex = new RecordingExecutor { OpenShouldFail = true };
        var engine = Engine(ex);

        var action = await engine.EvaluateAsync("BTC/USDT", High, CancellationToken.None);

        Assert.Equal(CarryAction.Hold, action);   // l'apertura fallita degrada a Hold
        Assert.False(engine.States.TryGetValue("BTC/USDT", out var s) && s.InPosition);
    }

    [Fact]
    public async Task AlreadyInPosition_HighFunding_HoldsWithoutReopening()
    {
        var ex = new RecordingExecutor();
        var engine = Engine(ex);

        await engine.EvaluateAsync("BTC/USDT", High, CancellationToken.None);   // apre
        var action = await engine.EvaluateAsync("BTC/USDT", High, CancellationToken.None);   // già dentro

        Assert.Equal(CarryAction.Hold, action);
        Assert.Single(ex.Opens);   // nessuna riapertura
    }

    [Fact]
    public void CarryMode_HasNoLiveValue_LiveIsUnrepresentable()
    {
        // Il failsafe strutturale: l'enum contiene SOLO Paper e Testnet. Nessun percorso può
        // portare il carry a Live perché il valore non esiste.
        var values = Enum.GetNames<CarryMode>();
        Assert.Equal(2, values.Length);
        Assert.Contains("Paper", values);
        Assert.Contains("Testnet", values);
        Assert.DoesNotContain("Live", values);
    }

    [Fact]
    public void PaperExecutor_IsAlwaysPaperMode_AndSucceedsWithoutExchange()
    {
        var paper = new PaperCarryExecutor(NullLogger<PaperCarryExecutor>.Instance);
        Assert.Equal(CarryMode.Paper, paper.Mode);
        var r = paper.OpenAsync("BTC/USDT", 5_000m, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(r.Success);
    }
}
