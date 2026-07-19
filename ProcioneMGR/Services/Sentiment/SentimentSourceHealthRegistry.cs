using System.Collections.Concurrent;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Fotografia della salute di una fonte dati (news o metriche) per la UI.</summary>
public sealed record SourceHealth(
    string Name,
    DateTime? LastSuccessUtc,
    int LastCount,
    DateTime? LastErrorUtc,
    string? LastError);

/// <summary>
/// Registro in-memory della salute delle fonti di sentiment (RSS, calendario, retail, Fear &amp;
/// Greed, derivati Binance): ultima sync riuscita, quanti elementi, ultimo errore. Una fonte che
/// fallisce viene già SALTATA senza far fallire il batch — questo registro rende il fallimento
/// VISIBILE (/sentiment) invece che sepolto nei log. Process-local di proposito: dopo un riavvio
/// è vuoto fino al primo tick, e va benissimo così (niente tabella per uno stato diagnostico).
/// </summary>
public sealed class SentimentSourceHealthRegistry
{
    private readonly ConcurrentDictionary<string, SourceHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    public void ReportSuccess(string name, int count)
        => _health.AddOrUpdate(name,
            _ => new SourceHealth(name, DateTime.UtcNow, count, null, null),
            (_, prev) => prev with { LastSuccessUtc = DateTime.UtcNow, LastCount = count });

    public void ReportError(string name, string message)
        => _health.AddOrUpdate(name,
            _ => new SourceHealth(name, null, 0, DateTime.UtcNow, message),
            (_, prev) => prev with { LastErrorUtc = DateTime.UtcNow, LastError = message });

    public IReadOnlyList<SourceHealth> Snapshot()
        => _health.Values.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
