namespace ProcioneMGR.Services.Experiments;

/// <summary>
/// Logger sperimentale generalizzato (un piccolo MLflow interno). Ogni tipo di esecuzione di
/// ricerca apre un run all'inizio, ne registra le metriche/artefatti, e lo chiude a fine —
/// così backtest, sweep, training e discovery finiscono nella STESSA tabella comparabile,
/// invece di vivere solo nella UI del momento e poi perdersi. Rif. <c>docs/ROADMAP-QLIB.md §1.3</c>.
///
/// Additivo per costruzione: non modifica il comportamento degli engine, aggiunge solo
/// osservabilità. Idempotente rispetto agli errori del chiamante: un fallimento del logging non
/// deve mai far cadere il calcolo che lo ospita (gli engine sono la fonte di verità, il tracker
/// è un osservatore).
/// </summary>
public interface IExperimentTracker
{
    /// <summary>
    /// Apre un run in stato "Running" e ne restituisce l'Id. <paramref name="parameters"/> viene
    /// serializzato in JSON e ne viene calcolato l'hash (config identiche ⇒ hash identico).
    /// </summary>
    Task<Guid> StartRunAsync(
        string kind,
        string name,
        object? parameters,
        string? symbol = null,
        string? timeframe = null,
        string? createdBy = null,
        CancellationToken ct = default);

    /// <summary>Registra/aggiorna le metriche finali del run (merge nel dizionario esistente).</summary>
    Task LogMetricsAsync(Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default);

    /// <summary>Allega un artefatto voluminoso (equity curve, importanze, ...) al run.</summary>
    Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default);

    /// <summary>Chiude il run con lo stato finale ("Completed" | "Failed") ed eventuale log d'errore.</summary>
    Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default);
}
