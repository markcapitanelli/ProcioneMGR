using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Experiments;

/// <summary>
/// Implementazione di <see cref="IExperimentTracker"/> su EF Core. Usa
/// <see cref="IDbContextFactory{TContext}"/> (context a vita breve per operazione) così è sicuro
/// come Singleton e utilizzabile da servizi/worker a lunga durata e da componenti Blazor.
///
/// Disciplina anti-regressione: ogni metodo apre e chiude il proprio context; nessuno stato
/// condiviso. Il tracker NON lancia mai eccezioni verso i calcoli che lo ospitano quando è usato
/// tramite gli helper "best-effort" (vedi <see cref="ExperimentTrackerExtensions"/>).
/// </summary>
public sealed class ExperimentTracker : IExperimentTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public ExperimentTracker(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Guid> StartRunAsync(
        string kind,
        string name,
        object? parameters,
        string? symbol = null,
        string? timeframe = null,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        var parametersJson = parameters is null ? "{}" : JsonSerializer.Serialize(parameters, JsonOptions);

        var run = new ExperimentRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Status = "Running",
            CreatedBy = createdBy ?? string.Empty,
            Symbol = symbol,
            Timeframe = timeframe,
            StartedAt = DateTime.UtcNow,
            ParametersJson = parametersJson,
            ParametersHash = Sha256Hex(parametersJson),
            MetricsJson = "{}",
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ExperimentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task LogMetricsAsync(Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var run = await db.ExperimentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        var existing = Deserialize(run.MetricsJson);
        foreach (var (k, v) in metrics) existing[k] = v;
        run.MetricsJson = JsonSerializer.Serialize(existing, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    public async Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ExperimentArtifacts.Add(new ExperimentArtifact
        {
            RunId = runId,
            KindTag = kindTag,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var run = await db.ExperimentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(errorLog)) run.ErrorLog = errorLog;
        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, decimal> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, JsonOptions) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
