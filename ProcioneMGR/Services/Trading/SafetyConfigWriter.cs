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

/// <summary>
/// Adapter sottile sul writer generalizzato (<see cref="Config.IAppConfigWriter"/>): la meccanica
/// read-modify-write, il lock sul file e la preservazione dei "_comment" vivono in un posto solo.
/// </summary>
public sealed class SafetyConfigWriter(Config.IAppConfigWriter writer) : ISafetyConfigWriter
{
    public Task SaveAsync(SafetyConfiguration cfg, CancellationToken ct = default)
        => writer.SaveSectionAsync("Trading:Safety", cfg, ct);
}
