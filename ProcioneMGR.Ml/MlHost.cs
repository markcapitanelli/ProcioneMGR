using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Ml;

/// <summary>
/// Costruzione dell'host del microservizio di inferenza, estratta da Program.cs per poterla far
/// partire su Kestrel VERO da un test (vedi MlKestrelTransportTests): con WebApplicationFactory il
/// TestServer sostituisce Kestrel e la configurazione degli endpoint non viene mai esercitata.
/// </summary>
public static class MlHost
{
    /// <summary>Porta gRPC (h2c) di default: quella dichiarata nel Deployment K8s.</summary>
    public const int DefaultGrpcPort = 8080;

    /// <summary>Porta health (HTTP/1.1) di default: quella a cui puntano le probe K8s.</summary>
    public const int DefaultHealthPort = 8081;

    /// <param name="configureBuilder">
    /// Invocato subito prima di <c>builder.Build()</c>. Serve ai test per sostituire le dipendenze
    /// (es. IModelRegistry) senza toccare il resto del wiring reale; in produzione è null.
    /// </param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Endpoint: gRPC in h2c su una porta, health HTTP/1.1 su un'altra.
        // gRPC richiede HTTP/2. Su un endpoint in chiaro non c'è TLS, quindi non c'è ALPN con cui
        // negoziare il protocollo: col default Http1AndHttp2 Kestrel serve HTTP/1.1 e chiude le
        // connessioni HTTP/2 con HTTP_1_1_REQUIRED (0xd) — nessuna chiamata gRPC passa. Per l'h2c
        // l'endpoint DEVE essere Http2 esplicito.
        // Servono due porte perché le probe httpGet di Kubernetes parlano HTTP/1.1: su un endpoint
        // solo-HTTP/2 fallirebbero e manderebbero il pod in CrashLoop.
        // NB: queste Listen* hanno la precedenza su ASPNETCORE_URLS (che infatti il Dockerfile non
        // imposta per questo target). Nessun RequireHost sulle route: a separare i due endpoint
        // basta il protocollo, e RequireHost romperebbe i test con WebApplicationFactory (il
        // TestServer non ha porta nel base address).
        // Le porte sono configurabili (Endpoints__GrpcPort / Endpoints__HealthPort) solo perché i
        // test possano chiedere porte effimere (0) ed evitare collisioni; in K8s valgono i default.
        var grpcPort = builder.Configuration.GetValue("Endpoints:GrpcPort", DefaultGrpcPort);
        var healthPort = builder.Configuration.GetValue("Endpoints:HealthPort", DefaultHealthPort);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
            options.ListenAnyIP(healthPort, o => o.Protocols = HttpProtocols.Http1);
        });

        // IEncryptionService no-op che lancia: ApplicationDbContext lo richiede nel costruttore, ma il
        // path di inferenza legge solo SavedMlModels (sola lettura, nessuna colonna cifrata). Nessuna
        // master key va distribuita a questo servizio. Vedi NoOpEncryptionService.
        builder.Services.AddSingleton<IEncryptionService, NoOpEncryptionService>();

        // Stesso Postgres condiviso del monolite (il servizio legge i modelli salvati). Registrazione
        // condivisa AddProcioneDatabase (fail-fast a startup se la connection string manca).
        builder.Services.AddProcioneDatabase(builder.Configuration);

        // Registry dei modelli riusato verbatim dal monolite: risolve il Champion per (symbol, timeframe).
        builder.Services.AddSingleton(builder.Configuration.GetSection("Registry").Get<ModelRegistryOptions>() ?? new ModelRegistryOptions());
        builder.Services.AddSingleton<IModelRegistry, ModelRegistry>();

        // Telemetria opt-in verso lo stack observability (stesso wiring del monolite/ingestion).
        builder.Services.AddProcioneObservability(builder.Configuration);

        builder.Services.AddGrpc();

        configureBuilder?.Invoke(builder);

        var app = builder.Build();

        app.MapGrpcService<InferenceServiceImpl>();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        return app;
    }
}
