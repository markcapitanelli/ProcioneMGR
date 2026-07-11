using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProcioneMGR.Services.Config;

/// <summary>
/// Persiste una sezione di configurazione in <c>appsettings.json</c>. Il provider JSON dell'host ha
/// <c>reloadOnChange=true</c>, quindi chi legge via <c>IOptionsMonitor&lt;T&gt;</c> (o
/// <c>IConfiguration</c> live) vede i nuovi valori entro ~1s senza riavvio — è lo stesso meccanismo
/// del pannello sicurezza di /trading, generalizzato per /admin/autonomy. Niente tabella DB, niente
/// provider custom: il file resta l'unica fonte di verità della configurazione.
/// </summary>
public interface IAppConfigWriter
{
    /// <summary>
    /// Serializza <paramref name="options"/> e lo scrive alla sezione <paramref name="sectionPath"/>
    /// (segmenti separati da <c>:</c>, es. <c>"Trading:Safety"</c>; i nodi mancanti vengono creati).
    /// </summary>
    Task SaveSectionAsync<T>(string sectionPath, T options, CancellationToken ct = default);
}

public sealed class AppConfigWriter(IHostEnvironment env, ILogger<AppConfigWriter> logger) : IAppConfigWriter
{
    // Un solo lock per il FILE (non per sezione): due salvataggi concorrenti da pannelli diversi
    // farebbero read-modify-write incrociati perdendo una delle due scritture.
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task SaveSectionAsync<T>(string sectionPath, T options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        ArgumentNullException.ThrowIfNull(options);

        var path = Path.Combine(env.ContentRootPath, "appsettings.json");
        await FileLock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new InvalidOperationException("appsettings.json non valido.");

            // Naviga (creando i nodi mancanti) fino al PADRE della sezione da scrivere.
            var segments = sectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parent = root;
            foreach (var segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject child)
                {
                    child = new JsonObject();
                    parent[segment] = child;
                }
                parent = child;
            }

            // Serializzazione dell'INTERO oggetto, non un elenco di chiavi scritto a mano: una
            // proprietà nuova non può essere dimenticata (è il fix H1 del SafetyConfigWriter,
            // qui per costruzione). Le chiavi di documentazione "_comment*" della sezione
            // esistente vengono preservate: sono per il lettore umano del file, non per il binder.
            var leaf = segments[^1];
            var serialized = JsonSerializer.SerializeToNode(options)?.AsObject()
                             ?? throw new InvalidOperationException($"Serializzazione della sezione '{sectionPath}' fallita.");
            if (parent[leaf] is JsonObject existing)
            {
                foreach (var (key, value) in existing.Where(kv => kv.Key.StartsWith('_')))
                {
                    serialized[key] = value?.DeepClone();
                }
            }
            parent[leaf] = serialized;

            var output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, output, ct);
            logger.LogInformation("Sezione '{Section}' salvata in appsettings.json.", sectionPath);
        }
        finally
        {
            FileLock.Release();
        }
    }
}
