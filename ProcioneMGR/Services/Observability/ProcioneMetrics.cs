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
    }

    public void RecordLanePromotion(int laneId, string newMode) =>
        _lanePromotions.Add(1, new KeyValuePair<string, object?>("lane", laneId), new KeyValuePair<string, object?>("mode", newMode));

    public void RecordDriftAlerts(string symbol, string timeframe, int alertCount) =>
        _driftAlerts.Add(Math.Max(1, alertCount), new KeyValuePair<string, object?>("symbol", symbol), new KeyValuePair<string, object?>("timeframe", timeframe));

    public void RecordModelRetired(string symbol, string timeframe) =>
        _modelsRetired.Add(1, new KeyValuePair<string, object?>("symbol", symbol), new KeyValuePair<string, object?>("timeframe", timeframe));

    public void RecordPipelineRun(string status) =>
        _pipelineRuns.Add(1, new KeyValuePair<string, object?>("status", status));

    public void RecordTradeExecuted(string mode, string side) =>
        _tradesExecuted.Add(1, new KeyValuePair<string, object?>("mode", mode), new KeyValuePair<string, object?>("side", side));

    public void RecordExecutionJob(string algorithm, string status) =>
        _executionJobs.Add(1, new KeyValuePair<string, object?>("algorithm", algorithm), new KeyValuePair<string, object?>("status", status));

    public void RecordExecutionSlippage(double bps, string algorithm) =>
        _executionSlippageBps.Record(bps, new KeyValuePair<string, object?>("algorithm", algorithm));

    public void Dispose() => _meter.Dispose();
}
