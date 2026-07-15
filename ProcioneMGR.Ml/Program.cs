using ProcioneMGR.Data;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;

// Npgsql "legacy timestamp behavior": identico al monolite/ingestion. Va impostato PRIMA di
// costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

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
