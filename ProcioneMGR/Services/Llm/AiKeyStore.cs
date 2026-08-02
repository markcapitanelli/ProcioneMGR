using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Llm;

/// <summary>Nomi canonici dei provider AI. Stringhe (non enum): un provider nuovo non deve toccare lo schema.</summary>
public static class AiProviders
{
    public const string Anthropic = "Anthropic";
    public const string Nvidia = "Nvidia";
    public const string Gemini = "Gemini";
    public const string Groq = "Groq";
    public const string HuggingFace = "HuggingFace";

    /// <summary>
    /// I provider noti alla UI di configurazione, nell'ordine di presentazione. Anthropic in coda
    /// dal 2026-08-02 (scelta del proprietario: credito esaurito, si sfruttano le altre — resta
    /// disponibile per quando/se il credito tornerà).
    /// </summary>
    public static readonly IReadOnlyList<string> Known = [Nvidia, Groq, Gemini, HuggingFace, Anthropic];

    /// <summary>Variabile d'ambiente di fallback per il provider ("ANTHROPIC_API_KEY", "NVIDIA_API_KEY", "GEMINI_API_KEY", "GROQ_API_KEY", "HUGGINGFACE_API_KEY").</summary>
    public static string EnvVarFor(string provider) => provider.ToUpperInvariant() + "_API_KEY";
}

/// <summary>Da dove viene la chiave che il provider userebbe ORA (per la UI: mai il valore, solo la fonte).</summary>
public enum AiKeySource { None, Environment, Database }

/// <summary>
/// Fonte unica delle chiavi API dei provider AI: prima la riga cifrata a database (inserita da
/// /admin/ai-supervisor), poi la variabile d'ambiente come fallback — così il comportamento
/// storico (solo env) resta valido per chi non tocca il pannello.
/// </summary>
public interface IAiKeyStore
{
    /// <summary>Chiave effettiva per il provider (DB → env), o null se assente. Carica la cache al primo uso.</summary>
    Task<string?> GetKeyAsync(string provider, CancellationToken ct = default);

    /// <summary>Lettura sincrona per gli IsConfigured: cache (se già caricata) → env. Mai I/O.</summary>
    string? GetCachedKey(string provider);

    /// <summary>Fonte corrente della chiave, per la UI.</summary>
    AiKeySource GetCachedSource(string provider);

    /// <summary>Inserisce o sostituisce la chiave del provider (cifrata a riposo) e aggiorna la cache.</summary>
    Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default);

    /// <summary>Rimuove la chiave a database (l'eventuale env torna a valere).</summary>
    Task RemoveKeyAsync(string provider, CancellationToken ct = default);

    /// <summary>Ricarica la cache dal database (usata dalla UI e dal worker all'avvio).</summary>
    Task ReloadAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementazione con cache in memoria (ConcurrentDictionary) caricata pigramente: i percorsi
/// sincroni (IsConfigured dei client) non fanno mai I/O. Se una riga non si decifra (master key
/// cambiata), il caricamento lo dice a voce alta e si prosegue col solo fallback env — la lezione
/// B2: mai un errore crypto silenzioso che sembra "chiave assente".
/// </summary>
public sealed class AiKeyStore(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AiKeyStore> logger) : IAiKeyStore
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loaded;

    public async Task<string?> GetKeyAsync(string provider, CancellationToken ct = default)
    {
        if (!_loaded) await ReloadAsync(ct);
        return GetCachedKey(provider);
    }

    public string? GetCachedKey(string provider)
    {
        if (_cache.TryGetValue(provider, out var fromDb) && !string.IsNullOrWhiteSpace(fromDb))
        {
            return fromDb;
        }
        var env = Environment.GetEnvironmentVariable(AiProviders.EnvVarFor(provider));
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public AiKeySource GetCachedSource(string provider)
    {
        if (_cache.TryGetValue(provider, out var fromDb) && !string.IsNullOrWhiteSpace(fromDb))
        {
            return AiKeySource.Database;
        }
        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AiProviders.EnvVarFor(provider)))
            ? AiKeySource.None
            : AiKeySource.Environment;
    }

    public async Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AiCredentials.FirstOrDefaultAsync(c => c.Provider == provider, ct);
        if (row is null)
        {
            db.AiCredentials.Add(new AiCredential { Provider = provider, ApiKey = apiKey, UpdatedAtUtc = DateTime.UtcNow });
        }
        else
        {
            row.ApiKey = apiKey;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        _cache[provider] = apiKey;
        logger.LogInformation("Chiave AI del provider {Provider} salvata (cifrata a riposo).", provider);
    }

    public async Task RemoveKeyAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AiCredentials.Where(c => c.Provider == provider).ExecuteDeleteAsync(ct);
        _cache.TryRemove(provider, out _);
        logger.LogInformation("Chiave AI del provider {Provider} rimossa dal database (resta l'eventuale fallback env).", provider);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                var rows = await db.AiCredentials.AsNoTracking().ToListAsync(ct);
                _cache.Clear();
                foreach (var row in rows)
                {
                    _cache[row.Provider] = row.ApiKey;
                }
            }
            catch (Exception ex) when (IsDecryptFailure(ex))
            {
                // Master key diversa da quella che cifrò le righe: dichiararlo, non fingere
                // "nessuna chiave". Si prosegue col solo fallback env finché le chiavi non
                // vengono reinserite dal pannello.
                _cache.Clear();
                logger.LogCritical(ex,
                    "Chiavi AI a database NON decifrabili con la master key corrente: reinseriscile in " +
                    "/admin/ai-supervisor (o ripristina la Security:MasterKey originale). Attivo il solo fallback env.");
            }
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>EF può propagare l'errore del converter diretto o wrappato: si scorre la catena (stesso helper di TradingEngine).</summary>
    private static bool IsDecryptFailure(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Cryptography.CryptographicException or FormatException)
            {
                return true;
            }
        }
        return false;
    }
}
