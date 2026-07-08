namespace ProcioneMGR.Services.Experiments;

/// <summary>
/// Un <b>run sperimentale</b>: la registrazione osservabile e confrontabile di UN'esecuzione di
/// ricerca (backtest, sweep di ottimizzazione, training ML, campagna di discovery, pipeline...).
///
/// Generalizza il tracking che finora esisteva SOLO per il Pipeline a 15 stadi
/// (<c>PipelineRun</c>/<c>PipelineArtifact</c>): stesso pattern a colonne JSON (schema stabile
/// mentre parametri/metriche evolvono), ma disaccoppiato da un singolo consumatore. Non sostituisce
/// <c>PipelineRun</c> (il cui checkpoint per-stadio è un bisogno diverso): il Pipeline può SCRIVERE
/// in aggiunta un <see cref="ExperimentRun"/> di kind "Pipeline" per comparire nella stessa tabella
/// comparativa degli altri (comporre, non sostituire). Rif. <c>docs/ROADMAP-QLIB.md §1.3</c>.
/// </summary>
public class ExperimentRun
{
    public Guid Id { get; set; }

    /// <summary>"Backtest" | "Optimization" | "MlTraining" | "Discovery" | "Pipeline" | "AlphaMining".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Etichetta leggibile scelta dal chiamante (es. "LightGBM · BTCUSDT · 50 fattori").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"Running" | "Completed" | "Failed".</summary>
    public string Status { get; set; } = "Running";

    /// <summary>Id dell'utente che ha avviato il run (vuoto per run automatici/di sistema).</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Symbol principale del run, denormalizzato per il filtro della UI (nullable).</summary>
    public string? Symbol { get; set; }

    /// <summary>Timeframe principale del run, denormalizzato per il filtro della UI (nullable).</summary>
    public string? Timeframe { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>JSON dei parametri/configurazione del run (shape libera decisa dal chiamante).</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>
    /// Hash SHA-256 (hex) di <see cref="ParametersJson"/>: versioning "git-like" leggero per
    /// riconoscere run con configurazione identica. NON è un content-addressable store completo
    /// (scelta dichiarata: complessità non giustificata qui, vedi ROADMAP-QLIB §1.3).
    /// </summary>
    public string ParametersHash { get; set; } = string.Empty;

    /// <summary>JSON: dizionario nome→valore (decimal) delle metriche finali del run.</summary>
    public string MetricsJson { get; set; } = "{}";

    public string? ErrorLog { get; set; }
}

/// <summary>
/// Artefatto voluminoso associato a un run (equity curve, lista trade, importanze feature, ...),
/// tenuto FUORI dalla riga del run così la tabella storica resta veloce da interrogare — stesso
/// principio di <c>PipelineArtifact</c>.
/// </summary>
public class ExperimentArtifact
{
    public int Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>Etichetta del tipo di artefatto ("EquityCurve" | "FeatureImportance" | ...).</summary>
    public string KindTag { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
