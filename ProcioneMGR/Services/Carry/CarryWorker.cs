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

    /// <summary>[2026-09-05] Gli episodi aperti sono stati riletti dal registro (una volta per vita del processo).</summary>
    private bool _restored;

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
        var ledger = new CarryLedger(dbFactory, scope.ServiceProvider.GetRequiredService<ILogger<CarryLedger>>());
        var modeName = mode.ToString();

        // [2026-09-05] IL REGISTRO PRIMA DI TUTTO. Fino a oggi lo stato viveva solo in memoria e ogni
        // rischieramento del pod (uno per merge) «riapriva» i sei carry azzerando il funding incassato:
        // il forward test dell'unica classe di edge misurata positiva non lasciava alcuna misura.
        // Il ripristino è fail-closed: se il registro non risponde NON si valuta, perché valutare
        // senza sapere cosa è aperto significherebbe scrivere aperture doppie.
        if (!_restored)
        {
            try
            {
                await ledger.RestoreAsync(_engine, modeName, ct);
                _restored = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Carry: registro non leggibile, salto la valutazione di questo giro (riprovo al prossimo).");
                return 0;
            }
        }

        // Dedupe: il binding delle liste .NET APPENDE al default invece di sostituirlo (default 6 +
        // config 6 = 12 con duplicati). Distinct rende l'insieme corretto qualunque sia la config.
        var symbols = opt.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();

        var evaluated = 0;
        var missing = new List<string>();
        var costPercent = CarryLedgerMath.RoundTripCostPercent(_config);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var sym in symbols)
        {
            // Ultimi funding del simbolo (più recente in coda), per la finestra di decisione.
            var recentPoints = await db.SentimentMetricPoints.AsNoTracking()
                .Where(p => p.Metric == SentimentMetrics.FundingRate && p.Symbol == sym)
                .OrderByDescending(p => p.TimestampUtc)
                .Take(opt.TrailingFundingEvents)
                .Select(p => new { p.TimestampUtc, p.Value })
                .ToListAsync(ct);
            recentPoints.Reverse();   // ordine cronologico crescente (ultimo = più recente)

            if (recentPoints.Count < opt.TrailingFundingEvents) { missing.Add(sym); continue; }
            var recent = recentPoints.Select(p => p.Value).ToList();
            var latest = recentPoints[^1];
            var symbol = sym + "/USDT";
            var action = await _engine.EvaluateAsync(symbol, recent, ct);
            evaluated++;

            // Il registro segue la decisione, mai il contrario: un errore qui non cambia ciò che il
            // motore ha deciso, ma va detto — è la misura che manca, non un dettaglio.
            try
            {
                var annualized = _engine.LastAnnualized(symbol) ?? 0m;
                var state = _engine.States.GetValueOrDefault(symbol);
                switch (action)
                {
                    case CarryAction.Open when state is not null:
                        await ledger.OpenAsync(symbol, modeName, state.NotionalQuote, annualized, costPercent,
                            latest.TimestampUtc, DateTime.UtcNow, ct);
                        break;
                    case CarryAction.Close:
                        await ledger.AccrueAsync(symbol, modeName, latest.TimestampUtc, latest.Value, ct);
                        await ledger.CloseAsync(symbol, modeName, annualized, DateTime.UtcNow,
                            $"funding annualizzato {annualized:F1}% sotto {_config.ExitAnnualFundingPercent:F1}%", ct);
                        break;
                    default:
                        if (state is { InPosition: true })
                        {
                            var totale = await ledger.AccrueAsync(symbol, modeName, latest.TimestampUtc, latest.Value, ct);
                            if (totale is decimal t) state.FundingCollectedPercent = t;
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Carry [{Mode}] {Sym}: registro non aggiornato dopo {Action}.", modeName, symbol, action);
            }
        }

        SymbolsWithoutData = missing;
        LastEvaluationUtc = DateTime.UtcNow;

        // [K8, PRD autonomia-piena 2026-08-31] Il battito PERSISTITO del carry.
        //
        // Fino a oggi il testimone di vitalità era questo campo in-process, e il guardiano che lo
        // legge (FleetOrchestratorWorker.WatchCarryAsync) vive nel GUSCIO mentre questo worker vive
        // nel POD: con Trading:UseRemoteTrading=true quel guardiano faceva GetService<CarryWorker>()
        // e otteneva SEMPRE null. Risultato: Fleet:CarrySilenceAlertHours era una manopola
        // amministrabile che non poteva scattare mai, sull'unica classe con edge positivo MISURATO
        // (5-12% netto annuo) — cioè la sorveglianza mancava esattamente dove serviva di più. Se il
        // carry nel pod smetteva di decidere lo si scopriva solo nei log del pod, ritenzione ~10h.
        //
        // Il battito passa dalla tabella che esiste già per questo (HostHeartbeats, AF5.1): una
        // riga per ruolo, upsert, nessuna migrazione. Un guasto qui NON deve fermare il carry: la
        // valutazione è già avvenuta, e perdere il battito è perdere la spia, non il lavoro.
        try
        {
            var row = await db.HostHeartbeats.FirstOrDefaultAsync(h => h.Host == HostHeartbeat.CarryRole, ct);
            if (row is null)
            {
                db.HostHeartbeats.Add(new HostHeartbeat
                {
                    Host = HostHeartbeat.CarryRole,
                    LastUtc = LastEvaluationUtc.Value,
                    Version = $"{mode} · {evaluated}/{symbols.Count} simboli",
                });
            }
            else
            {
                row.LastUtc = LastEvaluationUtc.Value;
                row.Version = $"{mode} · {evaluated}/{symbols.Count} simboli";
            }
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Carry: battito non persistito (il guardiano lo leggerà come silenzio).");
        }

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
