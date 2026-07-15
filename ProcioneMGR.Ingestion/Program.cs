using ProcioneMGR.Data;
using ProcioneMGR.Ingestion;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Security;

// Npgsql "legacy timestamp behavior": identico al monolite (Program.cs). Va impostato PRIMA di
// costruire qualunque data source Npgsql, altrimenti la scrittura di OhlcvData.TimestampUtc
// (Kind=Utc su 'timestamp without time zone') verrebbe rifiutata.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// IEncryptionService no-op che lancia: ApplicationDbContext lo richiede nel costruttore per
// l'EncryptedStringConverter, ma il path di ingestione non tocca mai colonne cifrate. Nessuna
// master key va distribuita a questo servizio. Vedi NoOpEncryptionService per il perché.
builder.Services.AddSingleton<IEncryptionService, NoOpEncryptionService>();

// Database: stesso Postgres condiviso del monolite (il servizio scrive su OhlcvData, che tutti i
// consumer leggono dal DB), con la stessa registrazione condivisa AddProcioneDatabase (fail-fast
// a startup se la connection string manca). Le migrazioni restano di competenza del monolite:
// qui non si crea nè migra lo schema, si usa e basta.
builder.Services.AddProcioneDatabase(builder.Configuration);

// Infrastruttura di ingestione condivisa (client exchange + IOhlcvIngestionService) riusata verbatim
// dal monolite, più il servizio di sync locale e il worker schedulato (questo host POSSIEDE lo
// scheduling periodico, a differenza del monolite quando delega a questo servizio).
builder.Services.AddOhlcvIngestion();
builder.Services.AddScoped<IMarketDataSyncService, MarketDataSyncService>();
builder.Services.AddHostedService<MarketDataSyncWorker>();

// Telemetria verso lo stack observability di Fase 0 (stesso wiring opt-in del monolite:
// Observability:Enabled default OFF, log+metriche via OTLP quando acceso). Un microservizio
// senza telemetria centralizzata sarebbe cieco proprio dove serve di più.
builder.Services.AddProcioneObservability(builder.Configuration);

var app = builder.Build();

// Sincronizzazione puntuale di una serie tracciata (id int del dominio, non un simbolo bare:
// un simbolo da solo è ambiguo tra exchange/timeframe diversi). Chiamato dal monolite via
// RemoteMarketDataSyncService quando MarketData:UseRemoteIngestion=true.
app.MapPost("/sync/{trackedSeriesId:int}", async (
    int trackedSeriesId, IMarketDataSyncService sync, CancellationToken ct) =>
{
    var candlesProcessed = await sync.SyncSeriesAsync(trackedSeriesId, ct);
    return Results.Ok(new { candlesProcessed });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Esposto per i test di integrazione (WebApplicationFactory), come da convenzione minimal API.
public partial class Program;
