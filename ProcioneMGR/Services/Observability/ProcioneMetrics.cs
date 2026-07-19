using System.Diagnostics.Metrics;

namespace ProcioneMGR.Services.Observability;

/// <summary>
/// Punto unico di strumentazione (Fase 5): un <see cref="Meter"/> del BCL con i contatori/istogrammi
/// degli eventi che contano per un sistema autonomo 24/7 — promozioni di corsia, drift, ritiri di
/// modelli, run di pipeline, trade, esecuzioni. È basato SOLO su <c>System.Diagnostics.Metrics</c>
/// (nessuna dipendenza esterna): l'export (OpenTelemetry/OTLP) è un layer opzionale sopra, wired in
/// <c>Program.cs</c> e spento di default. Senza un listener/exporter, registrare una metrica costa
/// quasi nulla, quindi la strumentazione resta sempre attiva senza penalità.
/// </summary>
public sealed class ProcioneMetrics : IDisposable
{
    /// <summary>Nome del meter: <c>OpenTelemetry.AddMeter(MeterName)</c> lo aggancia per l'export.</summary>
    public const string MeterName = "ProcioneMGR";

    private readonly Meter _meter;
    private readonly Counter<long> _lanePromotions;
    private readonly Counter<long> _driftAlerts;
    private readonly Counter<long> _modelsRetired;
    private readonly Counter<long> _pipelineRuns;
    private readonly Counter<long> _tradesExecuted;
    private readonly Counter<long> _executionJobs;
    private readonly Histogram<double> _executionSlippageBps;
    private readonly Counter<long> _mlComparisons;
    private readonly Counter<long> _llmCalls;
    private readonly Counter<long> _llmAdvisories;
    private readonly Counter<long> _llmVetoes;
    private readonly Counter<long> _sentimentSyncs;

    public ProcioneMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _lanePromotions = _meter.CreateCounter<long>("procione.lane.promotions", unit: "{promotion}",
            description: "Promozioni/retrocessioni di corsia (Paper↔Testnet).");
        _driftAlerts = _meter.CreateCounter<long>("procione.drift.alerts", unit: "{alert}",
            description: "Feature in drift Alert rilevate sui modelli.");
        _modelsRetired = _meter.CreateCounter<long>("procione.models.retired", unit: "{model}",
            description: "Modelli ritirati dal registry (superati o degradati per drift).");
        _pipelineRuns = _meter.CreateCounter<long>("procione.pipeline.runs", unit: "{run}",
            description: "Run di pipeline autonoma per esito.");
        _tradesExecuted = _meter.CreateCounter<long>("procione.trades.executed", unit: "{trade}",
            description: "Trade eseguiti dal motore, per modalità e lato.");
        _executionJobs = _meter.CreateCounter<long>("procione.execution.jobs", unit: "{job}",
            description: "Job di esecuzione a fette (TWAP/VWAP/Iceberg) per esito.");
        _executionSlippageBps = _meter.CreateHistogram<double>("procione.execution.slippage_bps", unit: "bps",
            description: "Implementation shortfall degli ordini eseguiti a fette.");
        _mlComparisons = _meter.CreateCounter<long>("procione.ml.comparisons", unit: "{comparison}",
            description: "Confronti locale/remoto del segnale ml (dual-read Fase 2a), per esito.");
        _llmCalls = _meter.CreateCounter<long>("procione.llm.calls", unit: "{call}",
            description: "Chiamate al modello Claude per path (advisory|veto) ed esito.");
        _llmAdvisories = _meter.CreateCounter<long>("procione.llm.advisories", unit: "{advisory}",
            description: "Advisory di supervisione AI persistite, per esito.");
        _llmVetoes = _meter.CreateCounter<long>("procione.llm.vetoes", unit: "{veto}",
            description: "Veti posti dal supervisore AI sulla ri-applica.");
        _sentimentSyncs = _meter.CreateCounter<long>("procione.sentiment.sync", unit: "{sync}",
            description: "Sync delle fonti di sentiment (news e metriche di market mood), per fonte ed esito.");
    }

    public void RecordLanePromotion(int laneId, string newMode) =>
        _lanePromotions.Add(1, new KeyValuePair<string, object?>("lane", laneId), new KeyValuePair<string, object?>("mode", newMode));

    public void RecordDriftAlerts(string symbol, string timeframe, int alertCount) =>
        _driftAlerts.Add(Math.Max(1, alertCount), new KeyValuePair<string, object?>("symbol", symbol), new KeyValuePair<string, object?>("timeframe", timeframe));

    public void RecordModelRetired(string symbol, string timeframe) =>
        _modelsRetired.Add(1, new KeyValuePair<string, object?>("symbol", symbol), new KeyValuePair<string, object?>("timeframe", timeframe));

    public void RecordPipelineRun(string status) =>
        _pipelineRuns.Add(1, new KeyValuePair<string, object?>("status", status));

    public void RecordTradeExecuted(string mode, string side, string action = "Open") =>
        _tradesExecuted.Add(1,
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("side", side),
            new KeyValuePair<string, object?>("action", action));

    public void RecordExecutionJob(string algorithm, string status) =>
        _executionJobs.Add(1, new KeyValuePair<string, object?>("algorithm", algorithm), new KeyValuePair<string, object?>("status", status));

    public void RecordExecutionSlippage(double bps, string algorithm) =>
        _executionSlippageBps.Record(bps, new KeyValuePair<string, object?>("algorithm", algorithm));

    /// <summary>Esito di un confronto dual-read: match | mismatch | timeout | error.</summary>
    public void RecordMlComparison(string outcome) =>
        _mlComparisons.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Chiamata Claude: path advisory|veto, result ok|error|skipped_breaker|skipped_unconfigured.</summary>
    public void RecordLlmCall(string path, string result) =>
        _llmCalls.Add(1, new KeyValuePair<string, object?>("path", path), new KeyValuePair<string, object?>("result", result));

    public void RecordLlmAdvisory(bool isError) =>
        _llmAdvisories.Add(1, new KeyValuePair<string, object?>("esito", isError ? "error" : "ok"));

    public void RecordLlmVeto() => _llmVetoes.Add(1);

    /// <summary>Sync di una fonte sentiment: esito ok | error.</summary>
    public void RecordSentimentSync(string source, string esito) =>
        _sentimentSyncs.Add(1, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("esito", esito));

    public void Dispose() => _meter.Dispose();
}
