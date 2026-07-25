using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Carry;

/// <summary>Configurazione del forward-test del carry (sezione "Carry").</summary>
public sealed class CarryOptions
{
    /// <summary>
    /// Default OFF: il carry è un edge nuovo in forward test, si accende deliberatamente. Anche
    /// acceso, di default gira in PAPER (nessun ordine reale) — vedi <see cref="Mode"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// "Paper" (default, simulazione) o "Testnet". Live NON è un valore accettato: il parsing lo
    /// rifiuta e resta Paper. Il carry non può mai operare con denaro reale.
    /// </summary>
    public string Mode { get; set; } = "Paper";

    /// <summary>Simboli (ticker base) da sorvegliare per il carry.</summary>
    public List<string> Symbols { get; set; } = ["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"];

    /// <summary>Minuti fra due valutazioni (il funding cambia ogni 8h: un'ora è ampiamente sufficiente).</summary>
    public int EvaluationMinutes { get; set; } = 60;

    public decimal EnterAnnualFundingPercent { get; set; } = 5m;
    public decimal ExitAnnualFundingPercent { get; set; }
    public int TrailingFundingEvents { get; set; } = 9;
    public decimal PositionSizePercent { get; set; } = 50m;
}

/// <summary>
/// [E3] Forward test del carry delta-neutro. Legge il funding recente dei simboli configurati dal DB
/// (serie <see cref="SentimentMetrics.FundingRate"/>, la stessa che alimenta backtest e sentiment) e
/// fa girare il <see cref="CarryEngine"/> con la stessa regola del backtest. In PAPER registra le
/// decisioni senza toccare alcun exchange: è il modo sicuro di vedere, dal vivo, quando il carry
/// aprirebbe/chiuderebbe, PRIMA di dargli ordini reali su Bitget demo.
///
/// <para>SICUREZZA: <see cref="CarryMode"/> non ha il valore Live; il parsing di
/// <see cref="CarryOptions.Mode"/> accetta solo Paper/Testnet e ripiega su Paper per qualsiasi altro
/// valore. Il carry non può operare con denaro reale, per costruzione.</para>
/// </summary>
public sealed class CarryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CarryOptions> options,
    ILogger<CarryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            logger.LogInformation("Carry forward-test disabilitato (Carry:Enabled=false).");
            return;
        }

        // Parsing SICURO della modalità: solo Paper/Testnet, mai Live.
        var mode = Enum.TryParse<CarryMode>(opt.Mode, ignoreCase: true, out var m) ? m : CarryMode.Paper;
        if (!opt.Mode.Equals(mode.ToString(), StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("Carry: modalità '{Requested}' non valida (ammesse solo Paper/Testnet) → uso {Mode}.", opt.Mode, mode);

        if (mode == CarryMode.Testnet)
        {
            // Il percorso Testnet reale (ordini Bitget demo) è un follow-up gated dal wallet demo
            // Futures finanziato: finché non esiste un executor Testnet verificato, si resta in Paper.
            logger.LogWarning("Carry: modalità Testnet richiesta ma l'executor Bitget demo non è ancora attivo (wallet demo Futures da finanziare + review). Uso Paper.");
            mode = CarryMode.Paper;
        }

        var cfg = new CarryConfiguration
        {
            EnterAnnualFundingPercent = opt.EnterAnnualFundingPercent,
            ExitAnnualFundingPercent = opt.ExitAnnualFundingPercent,
            TrailingFundingEvents = opt.TrailingFundingEvents,
            PositionSizePercent = opt.PositionSizePercent,
        };

        using var scope = scopeFactory.CreateScope();
        var paperExecutor = new PaperCarryExecutor(
            scope.ServiceProvider.GetRequiredService<ILogger<PaperCarryExecutor>>());
        var engine = new CarryEngine(paperExecutor, cfg,
            scope.ServiceProvider.GetRequiredService<ILogger<CarryEngine>>());
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // Dedupe: il binding delle liste .NET APPENDE al default invece di sostituirlo (default 6 +
        // config 6 = 12 con duplicati). Distinct rende l'insieme corretto qualunque sia la config.
        var symbols = opt.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();

        logger.LogInformation("Carry forward-test AVVIATO ({Mode}) su {N} simboli, valutazione ogni {Min} min.",
            mode, symbols.Count, opt.EvaluationMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, opt.EvaluationMinutes)));
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                foreach (var sym in symbols)
                {
                    // Ultimi funding del simbolo (più recente in coda), per la finestra di decisione.
                    var recent = await db.SentimentMetricPoints.AsNoTracking()
                        .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == sym)
                        .OrderByDescending(p => p.TimestampUtc)
                        .Take(opt.TrailingFundingEvents)
                        .Select(p => p.Value)
                        .ToListAsync(ct);
                    recent.Reverse();   // ordine cronologico crescente (ultimo = più recente)

                    if (recent.Count < opt.TrailingFundingEvents) continue;
                    await engine.EvaluateAsync(sym + "/USDT", recent, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Carry: errore nel tick di valutazione."); }
        }
        while (await SafeWaitAsync(timer, ct));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
