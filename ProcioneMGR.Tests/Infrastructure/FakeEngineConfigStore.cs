using System.Text.Json;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// Store di prova per <see cref="IEngineConfigStore"/>: tiene le sezioni in memoria e registra le
/// scritture. Sostituisce sia il percorso locale che quello gRPC — al chiamante non interessa quale
/// sia, ed è il punto dell'astrazione. Nato in ProtectionsPageRenderTests, estratto qui quando
/// anche il pannello sicurezza di /trading è passato allo store (E1, 2026-07-31).
/// </summary>
public sealed class FakeEngineConfigStore(bool remote = false, bool reachable = true) : IEngineConfigStore
{
    public bool IsRemote => remote;

    /// <summary>
    /// [2026-08-17] Modificabile: serve a simulare la sequenza reale «lettura fallita → il canale si
    /// riapre → scrittura riuscita», che è esattamente il caso in cui il pannello sicurezza poteva
    /// scrivere i default del codice sopra le soglie in vigore.
    /// </summary>
    public bool Reachable { get; set; } = reachable;
    public readonly List<(string Section, object Options)> Saved = [];
    public readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase);
    public string? WarningToReturn { get; set; }

    /// <summary>Se impostata, la prossima scrittura la lancia (motore che rifiuta o non risponde).</summary>
    public Exception? ThrowOnWrite { get; set; }

    public void Seed<T>(string section, T options) =>
        Sections[section] = JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions);

    public Task<EngineConfigSnapshot> ReadAsync(IEnumerable<string>? sections = null, CancellationToken ct = default)
    {
        if (!Reachable)
        {
            return Task.FromResult(new EngineConfigSnapshot([], string.Empty, false, false, "motore non raggiungibile"));
        }
        var views = Sections
            .Select(kv => new EngineConfigSectionView(kv.Key, kv.Value, Writable: true, Source: "appsettings.json"))
            .ToList();
        return Task.FromResult(new EngineConfigSnapshot(views, "/app/appsettings.json", Writable: true));
    }

    public Task<EngineConfigWriteResult> WriteAsync(string section, object options, CancellationToken ct = default)
    {
        if (ThrowOnWrite is not null) return Task.FromException<EngineConfigWriteResult>(ThrowOnWrite);
        Saved.Add((section, options));
        Sections[section] = JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions);
        return Task.FromResult(new EngineConfigWriteResult(Sections[section], WarningToReturn));
    }

    /// <summary>Esito della prova sul motore, pilotabile dal test.</summary>
    public NotificationResult TestNotificationResult { get; set; } = new(NotificationOutcome.Delivered);

    public int TestNotificationCalls { get; private set; }

    public Task<NotificationResult> SendTestNotificationAsync(CancellationToken ct = default)
    {
        TestNotificationCalls++;
        return Task.FromResult(TestNotificationResult);
    }

    /// <summary>[E5] Spia del canale del motore, pilotabile dal test.</summary>
    public EngineNotificationChannelStatus ChannelStatusToReturn { get; set; } =
        new(Reachable: true, ChannelComposed: true, new NotificationChannelStatus(null, null, null, 0));

    public Task<EngineNotificationChannelStatus> GetNotificationChannelStatusAsync(CancellationToken ct = default)
        => Task.FromResult(ChannelStatusToReturn);
}
