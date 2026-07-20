using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Persiste le soglie di sicurezza nel file appsettings.json (sezione Trading:Safety).
/// Il provider di configurazione ha reloadOnChange=true, quindi
/// IOptionsMonitor&lt;SafetyConfiguration&gt; vede i nuovi valori entro ~1s senza riavvio.
/// </summary>
public interface ISafetyConfigWriter
{
    Task SaveAsync(SafetyConfiguration cfg, CancellationToken ct = default);
}

public sealed class SafetyConfigWriter(IHostEnvironment env, ILogger<SafetyConfigWriter> logger) : ISafetyConfigWriter
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SaveAsync(SafetyConfiguration cfg, CancellationToken ct = default)
    {
        var path = Path.Combine(env.ContentRootPath, "appsettings.json");
        await _lock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new InvalidOperationException("appsettings.json non valido.");

            // Trading -> Safety (crea i nodi se mancanti).
            if (root["Trading"] is not JsonObject trading)
            {
                trading = new JsonObject();
                root["Trading"] = trading;
            }

            // Serializzazione dell'INTERO oggetto, non un elenco di chiavi scritto a mano: la
            // versione manuale aveva già perso per strada MaxLeverageAllowed/MaintenanceMarginPercent/
            // UseExchangeRestingStops — ogni salvataggio dal pannello li riportava ai default
            // silenziosamente. Così una proprietà nuova non può più essere dimenticata.
            trading["Safety"] = JsonSerializer.SerializeToNode(cfg);

            var output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, output, ct);
            logger.LogInformation("Configurazione di sicurezza salvata in appsettings.json.");
        }
        finally
        {
            _lock.Release();
        }
    }
}
