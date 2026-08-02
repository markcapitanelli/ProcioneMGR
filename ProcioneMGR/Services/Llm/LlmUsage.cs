using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Llm;

/// <summary>
/// [AF1] Etichetta di percorso della chiamata LLM in corso, propagata per contesto asincrono: la
/// conosce solo il <see cref="LlmCallGuard"/> (che la riceve come parametro), ma a consumarla è il
/// CLIENT che serve la risposta — e fra i due c'è il failover del <see cref="DelegatingLlmClient"/>,
/// quindi passarla per parametro significherebbe cambiare la firma di <see cref="ILlmClient"/> e
/// ogni fake dei test. Un AsyncLocal attraversa la catena senza toccare nessuna firma.
/// </summary>
public static class LlmCallContext
{
    private static readonly AsyncLocal<string?> Path = new();

    /// <summary>Il path corrente ("advisory" | "veto" | "sentiment" | …), o null fuori dal guard.</summary>
    public static string? CurrentPath => Path.Value;

    public static IDisposable Enter(string path)
    {
        var previous = Path.Value;
        Path.Value = path;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Path.Value = previous;
    }
}

/// <summary>
/// Un consumo dichiarato dal provider nella risposta. <paramref name="Provider"/> è quello del
/// client che ha SERVITO la chiamata (ogni client concreto dichiara se stesso): col failover può
/// non essere il provider attivo, e attribuire i token a quello sbagliato renderebbe il pannello
/// una rassicurazione invece che una misura.
/// </summary>
public readonly record struct LlmUsageEvent(
    string Provider,
    string Model,
    string Path,
    int PromptTokens,
    int CompletionTokens,
    DateTime AtUtc);

/// <summary>Esito del controllo di budget prima di una chiamata.</summary>
public sealed record LlmBudgetVerdict(bool Exhausted, string Reason)
{
    public static readonly LlmBudgetVerdict Allowed = new(false, string.Empty);
}

/// <summary>Consumo aggregato per il pannello.</summary>
public sealed record LlmUsageSnapshot(
    DateTime DayUtc,
    IReadOnlyList<LlmUsageRow> Today,
    long MonthTokens,
    long TodayTokens,
    int TodayCalls,
    bool TrackingEnabled);

public sealed record LlmUsageRow(string Provider, string Model, string Path, int Calls, long PromptTokens, long CompletionTokens);

/// <summary>
/// [AF1] Opzioni di consumo e budget del layer AI, sezione <c>Llm:Budget</c>. TUTTO spento per
/// default (invariante di piattaforma): senza <see cref="TrackingEnabled"/> non si scrive una riga
/// e non si applica alcun tetto — comportamento bit-identico a prima della fase. I limiti a 0
/// significano "nessun tetto". Il budget è il freno al cost runaway: coi free tier di oggi para i
/// loop impazziti, con un domani a pagamento para la bolletta.
/// </summary>
public sealed class LlmBudgetOptions
{
    public bool TrackingEnabled { get; set; }

    /// <summary>Tetto di CHIAMATE al giorno (0 = nessuno). Conta ogni chiamata servita, di ogni path.</summary>
    public int DailyCallLimit { get; set; }

    /// <summary>Tetto di token (prompt+completion) al giorno (0 = nessuno).</summary>
    public int DailyTokenLimit { get; set; }

    /// <summary>Tetto di token nel mese solare UTC (0 = nessuno).</summary>
    public int MonthlyTokenLimit { get; set; }
}

/// <summary>
/// [AF1] Chi raccoglie i consumi e risponde sul budget. Interfaccia stretta di proposito:
/// <see cref="Record"/> non lancia MAI (un contatore non deve poter rompere una chiamata riuscita)
/// e <see cref="CheckBudget"/> è una lettura pura in memoria (il guard la chiama prima di ogni
/// chiamata, non può costare un giro di DB).
/// </summary>
public interface ILlmUsageSink
{
    void Record(LlmUsageEvent e);

    LlmBudgetVerdict CheckBudget();

    /// <summary>
    /// Vero SOLO alla prima chiamata dopo l'esaurimento del budget (una notifica per transizione;
    /// il flag si riarma quando il budget torna disponibile, tipicamente a mezzanotte UTC).
    /// </summary>
    bool TryMarkExhaustionNotified();

    LlmUsageSnapshot GetSnapshot();
}

/// <summary>
/// Implementazione: aggregati in memoria + persistenza periodica su <see cref="LlmUsageRecord"/>
/// (una riga per giorno/provider/modello/path, upsert dal <see cref="LlmUsageFlushWorker"/>).
/// Al riavvio i totali di oggi e del mese si RICARICANO dal database (prima del primo flush):
/// un budget giornaliero che si azzera riavviando il processo non è un budget, è un girotondo.
/// </summary>
public sealed class LlmUsageTracker(
    Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<LlmBudgetOptions> options,
    ILogger<LlmUsageTracker> logger,
    TimeProvider? timeProvider = null) : ILlmUsageSink
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    // Delta non ancora scritti a DB, per chiave (day, provider, model, path).
    private readonly Dictionary<(DateTime Day, string Provider, string Model, string Path), (int Calls, long Prompt, long Completion)> _pending = new();

    // Totali già persistiti (caricati dal DB), per il conteggio del budget.
    private DateTime _loadedDay = DateTime.MinValue;
    private long _persistedTodayTokens;
    private int _persistedTodayCalls;
    private long _persistedMonthTokens;
    private bool _exhaustionNotified;

    private DateTime Today => _time.GetUtcNow().UtcDateTime.Date;

    public void Record(LlmUsageEvent e)
    {
        if (!options.CurrentValue.TrackingEnabled) return;
        try
        {
            lock (_gate)
            {
                RolloverIfNeeded_NoLock();
                var key = (e.AtUtc.Date, e.Provider.ToLowerInvariant(), e.Model, string.IsNullOrEmpty(e.Path) ? "direct" : e.Path);
                var current = _pending.TryGetValue(key, out var v) ? v : (0, 0L, 0L);
                _pending[key] = (current.Item1 + 1, current.Item2 + e.PromptTokens, current.Item3 + e.CompletionTokens);
            }
        }
        catch (Exception ex)
        {
            // Mai propagare: il contatore non deve poter rompere la chiamata che sta contando.
            logger.LogWarning(ex, "Registrazione consumo LLM fallita ({Provider}/{Path}).", e.Provider, e.Path);
        }
    }

    public LlmBudgetVerdict CheckBudget()
    {
        var opt = options.CurrentValue;
        if (!opt.TrackingEnabled) return LlmBudgetVerdict.Allowed;

        lock (_gate)
        {
            RolloverIfNeeded_NoLock();
            var (todayCalls, todayTokens, monthTokens) = Totals_NoLock();

            if (opt.DailyCallLimit > 0 && todayCalls >= opt.DailyCallLimit)
            {
                return new LlmBudgetVerdict(true, $"budget: {todayCalls}/{opt.DailyCallLimit} chiamate oggi");
            }
            if (opt.DailyTokenLimit > 0 && todayTokens >= opt.DailyTokenLimit)
            {
                return new LlmBudgetVerdict(true, $"budget: {todayTokens}/{opt.DailyTokenLimit} token oggi");
            }
            if (opt.MonthlyTokenLimit > 0 && monthTokens >= opt.MonthlyTokenLimit)
            {
                return new LlmBudgetVerdict(true, $"budget: {monthTokens}/{opt.MonthlyTokenLimit} token nel mese");
            }

            _exhaustionNotified = false; // budget disponibile: il prossimo esaurimento rinotifica
            return LlmBudgetVerdict.Allowed;
        }
    }

    public bool TryMarkExhaustionNotified()
    {
        lock (_gate)
        {
            if (_exhaustionNotified) return false;
            _exhaustionNotified = true;
            return true;
        }
    }

    public LlmUsageSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            RolloverIfNeeded_NoLock();
            var (todayCalls, todayTokens, monthTokens) = Totals_NoLock();
            var today = _pending
                .Where(kv => kv.Key.Day == Today)
                .Select(kv => new LlmUsageRow(kv.Key.Provider, kv.Key.Model, kv.Key.Path, kv.Value.Calls, kv.Value.Prompt, kv.Value.Completion))
                .OrderBy(r => r.Provider).ThenBy(r => r.Path)
                .ToList();
            return new LlmUsageSnapshot(Today, today, monthTokens, todayTokens, todayCalls, options.CurrentValue.TrackingEnabled);
        }
    }

    /// <summary>
    /// Carica i totali persistiti di oggi e del mese. Chiamata dal flush worker all'avvio e dopo
    /// ogni rollover di giorno: <see cref="CheckBudget"/> resta una lettura in memoria, e fra
    /// l'avvio del processo e il primo caricamento il budget sottoconta (dichiarato, temporaneo).
    /// </summary>
    public async Task LoadPersistedTotalsAsync(CancellationToken ct = default)
    {
        var today = Today;
        var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var monthRows = await db.LlmUsageRecords.AsNoTracking()
            .Where(r => r.DayUtc >= monthStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                MonthTokens = g.Sum(r => r.PromptTokens + r.CompletionTokens),
                TodayTokens = g.Where(r => r.DayUtc == today).Sum(r => r.PromptTokens + r.CompletionTokens),
                TodayCalls = g.Where(r => r.DayUtc == today).Sum(r => r.Calls),
            })
            .FirstOrDefaultAsync(ct);

        lock (_gate)
        {
            _loadedDay = today;
            _persistedMonthTokens = monthRows?.MonthTokens ?? 0;
            _persistedTodayTokens = monthRows?.TodayTokens ?? 0;
            _persistedTodayCalls = monthRows?.TodayCalls ?? 0;
        }
    }

    /// <summary>Scrive i delta accumulati (upsert per riga-giorno) e li sposta nei persistiti.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<KeyValuePair<(DateTime, string, string, string), (int, long, long)>> toWrite;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            toWrite = _pending.ToList();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var ((day, provider, model, path), (calls, prompt, completion)) in toWrite.Select(kv => (kv.Key, kv.Value)))
        {
            var row = await db.LlmUsageRecords.FirstOrDefaultAsync(
                r => r.DayUtc == day && r.Provider == provider && r.Model == model && r.Path == path, ct);
            if (row is null)
            {
                db.LlmUsageRecords.Add(new LlmUsageRecord
                {
                    DayUtc = day,
                    Provider = provider,
                    Model = model,
                    Path = path,
                    Calls = calls,
                    PromptTokens = prompt,
                    CompletionTokens = completion,
                });
            }
            else
            {
                row.Calls += calls;
                row.PromptTokens += prompt;
                row.CompletionTokens += completion;
            }
        }
        await db.SaveChangesAsync(ct);

        lock (_gate)
        {
            foreach (var (key, (calls, prompt, completion)) in toWrite.Select(kv => (kv.Key, kv.Value)))
            {
                // Sposta nel persistito ciò che è appena stato scritto; nel frattempo Record può
                // aver accumulato altro sulla stessa chiave — si sottrae, non si azzera.
                if (key.Item1 == Today)
                {
                    _persistedTodayCalls += calls;
                    _persistedTodayTokens += prompt + completion;
                }
                if (key.Item1.Year == Today.Year && key.Item1.Month == Today.Month)
                {
                    _persistedMonthTokens += prompt + completion;
                }

                if (_pending.TryGetValue(key, out var current))
                {
                    var remaining = (current.Calls - calls, current.Prompt - prompt, current.Completion - completion);
                    if (remaining.Item1 <= 0 && remaining.Item2 <= 0 && remaining.Item3 <= 0)
                    {
                        _pending.Remove(key);
                    }
                    else
                    {
                        _pending[key] = remaining;
                    }
                }
            }
        }
    }

    private (int TodayCalls, long TodayTokens, long MonthTokens) Totals_NoLock()
    {
        var today = Today;
        var calls = _persistedTodayCalls;
        var todayTokens = _persistedTodayTokens;
        var monthTokens = _persistedMonthTokens;
        foreach (var ((day, _, _, _), (c, p, comp)) in _pending.Select(kv => (kv.Key, kv.Value)))
        {
            if (day == today) { calls += c; todayTokens += p + comp; }
            if (day.Year == today.Year && day.Month == today.Month) { monthTokens += p + comp; }
        }
        return (calls, todayTokens, monthTokens);
    }

    private void RolloverIfNeeded_NoLock()
    {
        if (_loadedDay == Today) return;
        // Giorno nuovo (o primo uso): i contatori del giorno ripartono; quelli del mese si
        // correggono al prossimo LoadPersistedTotalsAsync del worker. Il flag di notifica si
        // riarma: un budget esaurito IERI non deve silenziare l'allarme di oggi.
        if (_loadedDay != DateTime.MinValue)
        {
            _persistedTodayCalls = 0;
            _persistedTodayTokens = 0;
            if (_loadedDay.Month != Today.Month || _loadedDay.Year != Today.Year)
            {
                _persistedMonthTokens = 0;
            }
            _exhaustionNotified = false;
        }
        _loadedDay = Today;
    }
}

/// <summary>
/// [AF1] Flush periodico dei consumi + ricarica dei totali persistiti all'avvio e al cambio di
/// giorno. Cadenza corta (1 minuto) perché il volume è minuscolo (aggregati, non eventi) e un
/// crash non deve perdere più di un minuto di conteggio.
/// </summary>
public sealed class LlmUsageFlushWorker(
    LlmUsageTracker tracker,
    IOptionsMonitor<LlmBudgetOptions> options,
    ILogger<LlmUsageFlushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastLoadedDay = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.TrackingEnabled)
            {
                try
                {
                    if (lastLoadedDay != DateTime.UtcNow.Date)
                    {
                        await tracker.LoadPersistedTotalsAsync(stoppingToken);
                        lastLoadedDay = DateTime.UtcNow.Date;
                    }
                    await tracker.FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Flush consumi LLM fallito: riprovo al prossimo giro.");
                }
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Ultimo flush best-effort allo shutdown: i delta in memoria non si buttano.
        if (options.CurrentValue.TrackingEnabled)
        {
            try { await tracker.FlushAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Flush finale consumi LLM fallito."); }
        }
    }
}
