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
    IOptionsMonitor<CarryOptions> options,
    ILogger<CarryWorker> logger) : BackgroundService
{
    /// <summary>
    /// Configurazione VIVA passata al motore. È lo stesso oggetto per tutta la vita del worker e le
    /// sue proprietà vengono riscritte a ogni tick da <see cref="CarryOptions"/>: il
    /// <see cref="CarryEngine"/> legge le soglie al momento della decisione, quindi cambiarle da
    /// /admin/autonomy vale dalla valutazione successiva senza perdere lo stato delle posizioni.
    /// </summary>
    private readonly CarryConfiguration _config = new();

    private CarryEngine? _engine;

    /// <summary>Stato per-simbolo del forward test (vuoto finché non c'è stata una valutazione).</summary>
    public IReadOnlyDictionary<string, CarrySymbolState> States =>
        _engine?.States ?? new Dictionary<string, CarrySymbolState>();

    /// <summary>Quando è stata completata l'ultima valutazione (null = mai).</summary>
    public DateTime? LastEvaluationUtc { get; private set; }

    /// <summary>Simboli che nell'ultimo giro non avevano abbastanza storia di funding per decidere.</summary>
    public IReadOnlyList<string> SymbolsWithoutData { get; private set; } = [];

    /// <summary>Modalità EFFETTIVA (mai Live: vedi <see cref="CarryMode"/>).</summary>
    public CarryMode EffectiveMode => ResolveMode(options.CurrentValue.Mode).Mode;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // L'intervallo si fissa all'avvio (PeriodicTimer), come in FeatureDriftWorker/PromotionWorker:
        // cambiarlo richiede riavvio. Enabled invece si rilegge a OGNI tick, così l'interruttore di
        // /admin/autonomy si sente a caldo — prima il worker usciva a startup e restava morto fino
        // al riavvio successivo, il che rendeva muto qualunque interruttore lo avesse comandato.
        var intervalMinutes = Math.Max(5, options.CurrentValue.EvaluationMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        logger.LogInformation("Carry forward-test in ascolto (valutazione ogni {Min} min, stato attuale: {State}).",
            intervalMinutes, options.CurrentValue.Enabled ? "ATTIVO" : "spento");

        do
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Carry: errore nel tick di valutazione."); }
        }
        while (await SafeWaitAsync(timer, ct));
    }

    /// <summary>
    /// Un giro di valutazione su tutti i simboli configurati. Pubblico perché la UI possa forzarlo
    /// ("Esegui ora") sulla STESSA istanza del hosted service — stesso pattern di
    /// <c>SentimentSyncWorker</c> e <c>FeatureDriftWorker</c>. Restituisce quanti simboli sono stati
    /// valutati davvero (esclusi quelli senza storia di funding sufficiente).
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled) return 0;

        var (mode, warning) = ResolveMode(opt.Mode);
        if (warning is not null) logger.LogWarning("{Warning}", warning);

        // Le soglie si rileggono a ogni giro sull'OGGETTO che il motore già tiene: nuove soglie,
        // stesso stato delle posizioni aperte.
        _config.EnterAnnualFundingPercent = opt.EnterAnnualFundingPercent;
        _config.ExitAnnualFundingPercent = opt.ExitAnnualFundingPercent;
        _config.TrailingFundingEvents = opt.TrailingFundingEvents;
        _config.PositionSizePercent = opt.PositionSizePercent;

        using var scope = scopeFactory.CreateScope();
        _engine ??= new CarryEngine(
            new PaperCarryExecutor(scope.ServiceProvider.GetRequiredService<ILogger<PaperCarryExecutor>>()),
            _config,
            scope.ServiceProvider.GetRequiredService<ILogger<CarryEngine>>());
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // Dedupe: il binding delle liste .NET APPENDE al default invece di sostituirlo (default 6 +
        // config 6 = 12 con duplicati). Distinct rende l'insieme corretto qualunque sia la config.
        var symbols = opt.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();

        var evaluated = 0;
        var missing = new List<string>();
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

            if (recent.Count < opt.TrailingFundingEvents) { missing.Add(sym); continue; }
            await _engine.EvaluateAsync(sym + "/USDT", recent, ct);
            evaluated++;
        }

        SymbolsWithoutData = missing;
        LastEvaluationUtc = DateTime.UtcNow;
        logger.LogDebug("Carry [{Mode}]: valutati {N}/{Tot} simboli.", mode, evaluated, symbols.Count);
        return evaluated;
    }

    /// <summary>
    /// Parsing SICURO della modalità: solo Paper/Testnet, mai Live — e Testnet degrada a Paper
    /// finché l'executor Bitget demo non è attivo (follow-up gated dal wallet demo Futures
    /// finanziato). Restituisce anche il motivo, così il chiamante può dirlo una volta sola.
    /// </summary>
    internal static (CarryMode Mode, string? Warning) ResolveMode(string configured)
    {
        if (!Enum.TryParse<CarryMode>(configured, ignoreCase: true, out var mode))
        {
            return (CarryMode.Paper,
                $"Carry: modalità '{configured}' non valida (ammesse solo Paper/Testnet) → uso Paper.");
        }

        if (mode == CarryMode.Testnet)
        {
            return (CarryMode.Paper,
                "Carry: modalità Testnet richiesta ma l'executor Bitget demo non è ancora attivo (wallet demo Futures da finanziare + review). Uso Paper.");
        }

        return (mode, null);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
