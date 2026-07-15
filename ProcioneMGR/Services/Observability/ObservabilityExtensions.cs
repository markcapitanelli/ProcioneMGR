using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace ProcioneMGR.Services.Observability;

/// <summary>
/// Wiring dell'export OpenTelemetry opt-in (flag <c>Observability:Enabled</c>, default OFF).
/// Estratto da Program.cs per essere testabile in isolamento. Con il flag OFF non registra nulla
/// (costo zero); con il flag ON esporta metriche del meter <see cref="ProcioneMetrics"/> e log
/// applicativi via OTLP verso il collector locale (infra/observability/docker-compose.yml).
/// L'exporter OTLP è fire-and-forget con retry in background: nessun collector in ascolto non
/// causa errori né rallenta l'app.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddProcioneObservability(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Observability:Enabled")) return services;

        var otlpEndpoint = configuration.GetValue<string>("Observability:OtlpEndpoint");
        void ConfigureOtlp(OpenTelemetry.Exporter.OtlpExporterOptions o)
        {
            if (!string.IsNullOrWhiteSpace(otlpEndpoint)) o.Endpoint = new Uri(otlpEndpoint);
        }

        services.AddOpenTelemetry()
            // service.name esplicito (default: "unknown_service:..."): in Grafana/Loki distingue
            // il monolite (ProcioneMGR) dai servizi satellite (ProcioneMGR.Ingestion, futuri).
            .ConfigureResource(r => r.AddService(
                serviceName: System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "ProcioneMGR"))
            .WithMetrics(m =>
            {
                m.AddMeter(ProcioneMetrics.MeterName);
                m.AddOtlpExporter(ConfigureOtlp);
            })
            // WithLogging registra da solo l'OpenTelemetryLoggerProvider nel logging pipeline:
            // i log ILogger esistenti escono anche via OTLP (Loki), oltre che su console.
            .WithLogging(l => l.AddOtlpExporter(ConfigureOtlp));

        return services;
    }
}
