using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProcioneMGR.Data;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;

// Npgsql "legacy timestamp behavior": identico al monolite/ingestion. Va impostato PRIMA di
// costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Endpoint: gRPC in h2c su una porta, health HTTP/1.1 su un'altra.
// gRPC richiede HTTP/2. Su un endpoint in chiaro non c'è TLS, quindi non c'è ALPN con cui
// negoziare il protocollo: col default Http1AndHttp2 Kestrel serve HTTP/1.1 e chiude le
// connessioni HTTP/2 con HTTP_1_1_REQUIRED (0xd) — nessuna chiamata gRPC passa. Per l'h2c
// l'endpoint DEVE essere Http2 esplicito.
// Servono due porte perché le probe httpGet di Kubernetes parlano HTTP/1.1: su un endpoint
// solo-HTTP/2 fallirebbero e manderebbero il pod in CrashLoop.
// NB: queste Listen* hanno la precedenza su ASPNETCORE_URLS (che infatti il Dockerfile non
// imposta per questo target). Nessun RequireHost sulle route: a separare i due endpoint basta
// il protocollo, e RequireHost romperebbe i test con WebApplicationFactory (il TestServer non
// ha porta nel base address).
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, o => o.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(8081, o => o.Protocols = HttpProtocols.Http1);
});

// IEncryptionService no-op che lancia: ApplicationDbContext lo richiede nel costruttore, ma il path
// di inferenza legge solo SavedMlModels (sola lettura, nessuna colonna cifrata). Nessuna master key
// va distribuita a questo servizio. Vedi NoOpEncryptionService.
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

var app = builder.Build();

app.MapGrpcService<InferenceServiceImpl>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Esposto per i test di integrazione (WebApplicationFactory + GrpcChannel).
public partial class Program;
