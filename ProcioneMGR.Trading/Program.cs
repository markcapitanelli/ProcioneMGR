using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Trading;

// Npgsql "legacy timestamp behavior": identico al monolite/ingestion/ml. Va impostato PRIMA di
// costruire qualunque data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// --- Endpoint: gRPC in h2c su una porta, health HTTP/1.1 su un'altra --------------------------
// gRPC PRETENDE HTTP/2. Su un endpoint in chiaro (niente TLS, quindi niente ALPN) Kestrel col
// default Http1AndHttp2 serve HTTP/1.1 e RIFIUTA le connessioni HTTP/2 con HTTP_1_1_REQUIRED: senza
// Protocols=Http2 esplicito, ogni RPC fallirebbe. Non Ã¨ un dettaglio teorico â€” Ã¨ esattamente ciÃ² che
// succedeva prima di questa riga, e i test con WebApplicationFactory NON lo vedono (TestServer Ã¨
// in-memory e non usa Kestrel): serve provare il servizio davvero in esecuzione.
//
// PerchÃ© due porte e non Protocols=Http2 su tutto: le probe httpGet di Kubernetes parlano HTTP/1.1,
// quindi su un endpoint solo-HTTP/2 readiness e liveness fallirebbero e il pod verrebbe riavviato
// in ciclo. La porta di health resta HTTP/1.1 e non espone nulla di sensibile (solo {status:ok});
// quella dei comandi Ã¨ ristretta dalla NetworkPolicy.
const int GrpcPort = 8080;
const int HealthPort = 8081;
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(GrpcPort, o => o.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(HealthPort, o => o.Protocols = HttpProtocols.Http1);
});

// --- SALTO DI SENSIBILITÃ€ RISPETTO A INGESTION/ML -------------------------------------------
// Ingestion e Ml usano un NoOpEncryptionService che lancia: non toccano colonne cifrate, quindi a
// quei servizi non va distribuita alcuna master key. QUI SERVE LA CHIAVE VERA: IExchangeClientFactory
// deve DECIFRARE le credenziali exchange per firmare le chiamate Testnet/Live. Ãˆ il primo servizio
// satellite che riceve Security:MasterKey (K8s Secret trading-secrets, vedi infra/k8s/README.md) â€”
// motivo per cui Ã¨ anche il primo protetto da una NetworkPolicy.
builder.Services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
// Stessa istanza dietro le due interfacce: IMasterKeyStatus Ã¨ come TradingEngine.StartAsync scopre
// che la chiave Ã¨ ancora il placeholder di sviluppo e rifiuta il Live.
builder.Services.AddSingleton<IMasterKeyStatus>(sp => (AesGcmEncryptionService)sp.GetRequiredService<IEncryptionService>());

// Stesso Postgres condiviso del monolite. Le migrazioni restano di competenza del monolite: qui lo
// schema si usa, non si crea nÃ© si migra.
builder.Services.AddProcioneDatabase(builder.Configuration);

// Client exchange (Binance/Bitget + factory) SENZA IOhlcvIngestionService: questo host firma ordini
// ma non ingerisce candele â€” le legge dal DB, dove le scrive il servizio di ingestione.
builder.Services.AddExchangeClients();

// --- Dipendenze del motore, riusate verbatim dal monolite ------------------------------------
// Tutte stateless o a stato in-memory per-processo: nessuna scrittura concorrente col monolite.
builder.Services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
builder.Services.AddSingleton<IMarketFeatureExtractor, MarketFeatureExtractor>();
builder.Services.AddSingleton<IMarketBreadthCalculator, MarketBreadthCalculator>();
builder.Services.AddSingleton<IRegimeDetector, RegimeDetector>();
builder.Services.AddSingleton<IStrategyFactory, StrategyFactory>();
builder.Services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
builder.Services.AddSingleton<IStrategyDecayMonitor, StrategyDecayMonitor>();
builder.Services.AddSingleton<IExecutionAlgorithmFactory, ExecutionAlgorithmFactory>();

// Il ribilanciamento dell'ensemble (EnsembleManager) risolve IBacktestEngine da uno scope.
builder.Services.AddScoped<IBacktestEngine, BacktestEngine>();

var factorCacheOptions = builder.Configuration.GetSection("FactorCache").Get<FactorCacheOptions>() ?? new FactorCacheOptions();
builder.Services.AddSingleton<IFactorCache>(_ => new FactorCache(factorCacheOptions));

// Registry dei modelli: il TradingEngine risolve il Champion per la MlStrategy.
builder.Services.AddSingleton(builder.Configuration.GetSection("Registry").Get<ModelRegistryOptions>() ?? new ModelRegistryOptions());
builder.Services.AddSingleton<IModelRegistry, ModelRegistry>();

builder.Services.AddSingleton<ProcioneMetrics>();

// --- Configurazione di sicurezza -------------------------------------------------------------
// STESSE sezioni del monolite. Il monolite le SCRIVE (AppConfigWriter â†’ appsettings.json nel
// ContentRootPath); qui si LEGGONO soltanto, con reloadOnChange (default di CreateBuilder) che
// propaga la modifica in ~1s via IOptionsMonitor. PerchÃ© i due si vedano davvero, il file deve
// essere LO STESSO: in K8s un PVC ReadWriteMany montato sullo stesso path in entrambi i pod, in
// locale lo stesso file su disco. Se i due file divergono, questo servizio applica limiti di
// sicurezza diversi da quelli mostrati in /trading â€” vedi infra/k8s/README.md.
builder.Services.Configure<SafetyConfiguration>(builder.Configuration.GetSection("Trading:Safety"));
builder.Services.Configure<LiveExecutionOptions>(builder.Configuration.GetSection("Trading:LiveExecution"));

// --- Le corsie ------------------------------------------------------------------------------
// isTradingServiceHost: true â†’ (1) ignora Trading:UseRemoteTrading e registra SEMPRE il ramo locale
// (motore reale + TradingWorker + ExecutionWorker): questo processo Ãˆ il servizio di trading, e una
// config condivisa col monolite (stesso file via PVC) lo farebbe altrimenti puntare a se stesso;
// (2) NON registra EnsembleRebalanceWorker: il ribilanciamento SCRIVE i pesi delle strategie e resta
// del solo monolite â€” averlo in entrambi significherebbe due processi che ribilanciano la stessa
// corsia. L'IEnsembleManager invece c'Ã¨ (il motore lo richiede), ma qui Ã¨ solo in lettura.
builder.Services.AddTradingLanes(builder.Configuration, isTradingServiceHost: true);

// Canale di notifica (Fase 4, PRD Autonomia): in questo host il producer Ã¨ il
// LaneInvariantWatchdog (quarantena corsie). Stessa sezione di config del monolite.
builder.Services.AddProcioneNotifications(builder.Configuration);

builder.Services.AddProcioneObservability(builder.Configuration);

// P1-6 (audit consolidamento 2026-07-17): SharedSecretAuthInterceptor applica un'autorizzazione
// applicativa a OGNI rpc di questo servizio, in aggiunta alla NetworkPolicy K8s (confine di rete,
// non applicativo). Registrato globalmente su AddGrpc: un solo servizio gRPC in questo host
// (TradingCommandServiceImpl), non serve applicarlo per-metodo.
builder.Services.AddGrpc(options => options.Interceptors.Add<SharedSecretAuthInterceptor>());

var app = builder.Build();

// Nessun RequireHost: a separare i due Ã¨ giÃ  il protocollo dell'endpoint (gRPC parla solo con la
// porta HTTP/2, le probe solo con quella HTTP/1.1). Vincolare l'host romperebbe i test con
// WebApplicationFactory, il cui TestServer non ha una porta nel base address.
app.MapGrpcService<TradingCommandServiceImpl>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Esposto per i test di integrazione (WebApplicationFactory + GrpcChannel).
public partial class Program;
