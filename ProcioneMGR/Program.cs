using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Components;
using ProcioneMGR.Components.Account;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

// Npgsql "legacy timestamp behavior": permette a 'timestamp without time zone' di accettare
// DateTime di qualunque Kind (Utc/Unspecified), memorizzandone il valore grezzo. Serve perché il
// codice usa DateTime.UtcNow (Kind=Utc) nelle query, e senza questo switch Npgsql rifiuterebbe di
// scrivere un Kind=Utc su 'timestamp without time zone'. Semantica "naive UTC": nessun cambiamento
// di logica di business (i valori sono gli stessi tick). Va impostato PRIMA di costruire qualunque
// data source Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// --- Data Protection: keyring persistito (Fase 3 microservizi) ---
// È la chiave con cui si firmano/cifrano i cookie di autenticazione. Fuori da un container il
// default di ASP.NET Core la scrive già in una cartella del profilo utente (persistente fra i
// riavvii): in sviluppo locale non serve fare nulla, e infatti senza DataProtection:KeyRingPath
// questo blocco non tocca nulla — comportamento identico a prima.
//
// DENTRO un container è un'altra storia: senza un percorso persistito il keyring vive solo in
// memoria, quindi OGNI riavvio del pod invalida tutti i cookie e disconnette gli utenti. Non è il
// caso di un deploy pianificato (raro, scelto): basta un OOM-kill o una liveness probe fallita, ed
// è silenzioso. In K8s si monta una PVC e si punta qui (vedi infra/k8s/ui/deployment.yaml).
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services.AddDataProtection()
        // Nome esplicito e stabile: il default deriva dal ContentRootPath, che cambiando fra host
        // (sviluppo vs /app nel container) renderebbe indecifrabili le chiavi già scritte.
        .SetApplicationName("ProcioneMGR")
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}

// Servizio di cifratura (AES-256-GCM) per i segreti a riposo. Singleton: la chiave
// master viene derivata una sola volta. Va registrato PRIMA del DbContext perche'
// l'EncryptedStringConverter ne dipende.
builder.Services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
// Stato della master key (placeholder di sviluppo?): stessa istanza del servizio di cifratura,
// esposta come vista ristretta per i guard fail-fast (startup Production, gate Live del motore).
builder.Services.AddSingleton<IMasterKeyStatus>(sp => (AesGcmEncryptionService)sp.GetRequiredService<IEncryptionService>());

// --- Database: PostgreSQL (unico provider) ---
// Le migrazioni vivono nell'assembly ProcioneMGR.Migrations.Postgres e si applicano come passo
// separato (`dotnet ef database update`), non a runtime: l'app NON referenzia quell'assembly per
// evitare un ciclo di progetti. Nessuna IDesignTimeDbContextFactory: EF usa l'host dell'app per
// costruire il context a design-time, così Identity applica correttamente SchemaVersion=Version3
// (una factory custom la bypasserebbe, causando il drop spurio di AspNetUserPasskeys).
// DbContextFactory (per servizi a lunga durata e componenti Blazor interattivi) +
// bridge scoped richiesto da ASP.NET Core Identity. Entrambi condividono lo stesso
// IEncryptionService iniettato nel costruttore del DbContext. La registrazione della factory
// è condivisa con gli host satellite (AddProcioneDatabase, vedi DatabaseServiceCollectionExtensions).
builder.Services.AddProcioneDatabase(builder.Configuration);
builder.Services.AddScoped<ApplicationDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// --- Layer Exchange + ingestione OHLCV (infrastruttura condivisa con ProcioneMGR.Ingestion) ---
// Client exchange + IOhlcvIngestionService: servono sempre (trading, pipeline, dashboard li usano).
builder.Services.AddOhlcvIngestion();

// --- Sincronizzazione watchlist: locale (worker in-process) oppure remota (servizio Ingestion) ---
// Fase 1 microservizi. Il toggle decide UNA SOLA VOLTA a startup quale IMarketDataSyncService
// registrare (richiede riavvio per cambiare, a differenza di MarketData:Enabled che è hot-reload).
// Watchlist.razor inietta sempre l'interfaccia, ignaro di quale implementazione sia attiva.
if (builder.Configuration.GetValue<bool>("MarketData:UseRemoteIngestion"))
{
    // Il worker schedulato NON viene registrato: lo scheduling periodico vive nel servizio remoto,
    // che scrive direttamente sul Postgres condiviso. Il monolite delega solo le sync puntuali.
    builder.Services.AddHttpClient<IMarketDataSyncService, RemoteMarketDataSyncService>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["MarketData:RemoteIngestionUrl"]
            ?? throw new InvalidOperationException(
                "MarketData:RemoteIngestionUrl è obbligatorio quando MarketData:UseRemoteIngestion=true."));
        // Una prima sync con backfill (giorni di candele, paginazione con rate-limit 300ms lato
        // servizio) può superare di molto i 100s di default di HttpClient: timeout largo.
        c.Timeout = TimeSpan.FromMinutes(10);
    });
}
else
{
    builder.Services.AddScoped<IMarketDataSyncService, MarketDataSyncService>();
    builder.Services.AddHostedService<MarketDataSyncWorker>();
}

// --- Indicatori tecnici (stateless) ---
builder.Services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();

// --- Market regime detection (Fase 7): feature extraction + clustering ---
builder.Services.AddSingleton<IMarketFeatureExtractor, MarketFeatureExtractor>();
builder.Services.AddSingleton<IRegimeDetector, RegimeDetector>();
builder.Services.AddHostedService<RegimeRetrainingWorker>();

// --- Trading (Fase 8): safety + paper engine ---
// NB: la safety si usa SOLO via SafetyChecker.Evaluate (statico, puro) dentro il TradingEngine:
// nessuna registrazione DI — l'interfaccia istanza era codice morto mai risolto da nessuno.
builder.Services.Configure<SafetyConfiguration>(builder.Configuration.GetSection("Trading:Safety"));
// Writer generalizzato di sezioni appsettings (pannelli /trading e /admin/autonomy):
// read-modify-write con lock sul file; reloadOnChange fa il resto (hot-reload ~1s).
builder.Services.AddSingleton<ProcioneMGR.Services.Config.IAppConfigWriter, ProcioneMGR.Services.Config.AppConfigWriter>();
builder.Services.AddSingleton<ISafetyConfigWriter, SafetyConfigWriter>();

// --- Esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Master switch default-off
//     (Trading:LiveExecution:Enabled). Rif. docs/ROADMAP-QLIB.md §1.2. ---
builder.Services.Configure<LiveExecutionOptions>(builder.Configuration.GetSection("Trading:LiveExecution"));
// ITradingEngine/TradingWorker sono registrati piu' sotto come keyed singleton per corsia
// (vedi blocco "Multi-strategy ensemble + trading: corsie isolate").

// --- Backtesting ---
builder.Services.AddSingleton<IStrategyFactory, StrategyFactory>();
builder.Services.AddScoped<IBacktestEngine, BacktestEngine>();

// Preset di configurazione pagina + memoria dell'ultima configurazione usata (per utente).
builder.Services.AddScoped<ProcioneMGR.Services.Preferences.IPageConfigStore, ProcioneMGR.Services.Preferences.PageConfigStore>();

// --- Parameter optimization (Grid Search + Walk-Forward) ---
builder.Services.AddScoped<IOptimizationEngine, OptimizationEngine>();
// Ottimizzazione bayesiana (Fase 6): surrogato GP + Expected Improvement, affiancabile al grid.
builder.Services.AddSingleton<ProcioneMGR.Services.Optimization.Bayesian.IHyperparameterOptimizer, ProcioneMGR.Services.Optimization.Bayesian.BayesianOptimizationEngine>();
builder.Services.AddSingleton<ProcioneMGR.Services.Optimization.Bayesian.BayesianSearch>();

// --- Nested decision execution (TWAP/VWAP/Iceberg + simulatore di fill). Additivo: il default
//     "Immediate" riproduce il comportamento odierno. Rif. docs/ROADMAP-QLIB.md §1.2. ---
builder.Services.AddSingleton<ProcioneMGR.Services.Execution.IExecutionAlgorithmFactory, ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory>();
builder.Services.AddSingleton<ProcioneMGR.Services.Execution.IExecutionSimulator, ProcioneMGR.Services.Execution.ExecutionSimulator>();
var executionParams = builder.Configuration.GetSection("Execution").Get<ProcioneMGR.Services.Execution.ExecutionParameters>()
                      ?? new ProcioneMGR.Services.Execution.ExecutionParameters();
builder.Services.AddSingleton(executionParams);

// --- Strategy discovery (sweep strategia × coppia × timeframe) ---
builder.Services.AddScoped<IStrategyDiscovery, StrategyDiscoveryEngine>();

// --- Creative discovery (composizione sistematica di strategie: generatori deterministici
//     Singleton, composer Scoped perche' dipende dal BacktestEngine scoped) ---
builder.Services.AddSingleton<ICompositeSignalGenerator, CompositeSignalGenerator>();
builder.Services.AddSingleton<IEventTriggerGenerator, EventTriggerGenerator>();
builder.Services.AddSingleton<IRegimeMapGenerator, RegimeMapGenerator>();
builder.Services.AddScoped<IStrategyComposer, StrategyComposer>();

// --- Alpha factor research (libreria fattori + valutazione Information Coefficient) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.IAlphaFactorFactory, ProcioneMGR.Services.Alpha.AlphaFactorFactory>();
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.IFactorEvaluator, ProcioneMGR.Services.Alpha.FactorEvaluator>();

// --- Formulaic alpha mining (programmazione genetica, C# puro). Rif. docs/ROADMAP-QLIB.md §1.7. ---
builder.Services.AddSingleton<ProcioneMGR.Services.AlphaMining.GeneticAlphaMiner>();

// --- Processo ML (dataset da fattori + cross-validation temporale purged/embargoed) ---
// Cache trasparente dei fattori (Fase 4): condivisa fra training (DatasetBuilder) e inferenza
// (MlStrategy via BacktestEngine) così gli stessi input riusano la stessa serie calcolata.
var factorCacheOptions = builder.Configuration.GetSection("FactorCache").Get<ProcioneMGR.Services.Alpha.FactorCacheOptions>()
                         ?? new ProcioneMGR.Services.Alpha.FactorCacheOptions();
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.IFactorCache>(_ => new ProcioneMGR.Services.Alpha.FactorCache(factorCacheOptions));
builder.Services.AddSingleton<ProcioneMGR.Services.ML.IDatasetBuilder, ProcioneMGR.Services.ML.DatasetBuilder>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.IIcFeatureSelector, ProcioneMGR.Services.ML.IcFeatureSelector>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.IPurgedTimeSeriesCv, ProcioneMGR.Services.ML.PurgedTimeSeriesCv>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.IRiskFactorPca, ProcioneMGR.Services.ML.RiskFactorPca>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.IHierarchicalClustering, ProcioneMGR.Services.ML.HierarchicalClustering>();

// --- Time-series: volatilità (GARCH) e statistical arbitrage (cointegrazione/pairs) ---
builder.Services.AddSingleton<ProcioneMGR.Services.TimeSeries.IGarchModel, ProcioneMGR.Services.TimeSeries.GarchModel>();
builder.Services.AddSingleton<ProcioneMGR.Services.TimeSeries.ICointegrationTest, ProcioneMGR.Services.TimeSeries.EngleGrangerCointegrationTest>();
builder.Services.AddSingleton<ProcioneMGR.Services.TimeSeries.PairsSpreadAnalyzer>();
builder.Services.AddSingleton<ProcioneMGR.Services.PairsTrading.IPairsBacktestEngine, ProcioneMGR.Services.PairsTrading.PairsBacktestEngine>();

// --- Alt-data (notizie RSS) + sentiment (Fase D) ---
// Scorer lessicale come fallback testabile: nessuna chiave LLM ancora disponibile, sostituibile
// 1:1 dietro ISentimentScorer quando si deciderà il provider.
builder.Services.AddHttpClient("AltDataRss", c => c.Timeout = TimeSpan.FromSeconds(15));
// ForexFactory (Fase D.2, calendario economico): niente feed RSS pubblico, /calendar è HTML
// server-renderizzato ma dietro Cloudflare — verificato dal vivo che risponde con la pagina
// reale (non una challenge) SOLO con uno User-Agent da browser plausibile.
builder.Services.AddHttpClient("AltDataForexFactory", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
});
// Sentiment retail (Fase D.2): endpoint JSON pubblico di FXSSI, nessun header speciale richiesto.
builder.Services.AddHttpClient("AltDataRetailSentiment", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IEnumerable<ProcioneMGR.Services.AltData.IAltDataSource>>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var sources = ProcioneMGR.Services.AltData.NewsFeeds.KnownFeeds
        .Select(kv => (ProcioneMGR.Services.AltData.IAltDataSource)new ProcioneMGR.Services.AltData.RssNewsSource(kv.Key, kv.Value, httpClientFactory.CreateClient("AltDataRss")))
        .ToList();
    sources.Add(new ProcioneMGR.Services.AltData.ForexFactoryIngestor(httpClientFactory.CreateClient("AltDataForexFactory")));
    sources.Add(new ProcioneMGR.Services.AltData.RetailSentimentIngestor("FXSSI", "fxssi", httpClientFactory.CreateClient("AltDataRetailSentiment")));
    sources.Add(new ProcioneMGR.Services.AltData.RetailSentimentIngestor("MyFxBook", "myfxbook", httpClientFactory.CreateClient("AltDataRetailSentiment")));
    return sources;
});
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.ISentimentScorer, ProcioneMGR.Services.Sentiment.KeywordSentimentScorer>();
builder.Services.AddScoped<ProcioneMGR.Services.AltData.IAltDataSyncService, ProcioneMGR.Services.AltData.AltDataSyncService>();
builder.Services.AddSingleton<ProcioneMGR.Services.AltData.INewsImpactAnalyzer, ProcioneMGR.Services.AltData.NewsImpactAnalyzer>();

// --- Portfolio optimization (Mean-Variance, Risk Parity, HRP) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.MeanVarianceOptimizer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.RiskParityOptimizer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.HierarchicalRiskParityOptimizer>();

// --- Analisi statistica delle serie (gap/lap, escursioni, ciclicita' - Trombetta cap. 4-5) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.GapLapAnalyzer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.ExcursionAnalyzer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.CyclicalAnalyzer>();

// --- Analisi tecnica classica (candlestick, S/R, pattern, volume - McAllen) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.CandlestickPatternDetector>();
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.SupportResistanceAnalyzer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.ChartPatternDetector>();
builder.Services.AddSingleton<ProcioneMGR.Services.Analysis.VolumeAnalyzer>();

// --- Gestione del rischio (Montecarlo evoluta, Performance/Equity Control - Trombetta cap. 8) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Risk.MonteCarloAnalyzer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Risk.PerformanceControlService>();

// --- Position sizing (Kelly - ML4T cap. 5) e barre non temporali (ML4T cap. 2) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Risk.KellyCalculator>();
builder.Services.AddSingleton<ProcioneMGR.Services.Ingestion.BarBuilder>();

// --- Consulente leva (bootstrap con liquidazione, per capitale piccolo + leverage) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Risk.LeverageAdvisor>();

// --- Monitor di decadimento (realizzato vs atteso dal backtest) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.IStrategyDecayMonitor, ProcioneMGR.Services.Monitoring.StrategyDecayMonitor>();

// --- Concept drift detection (segnale anticipatore: distribuzione delle feature, AFFIANCA il
//     monitor di decadimento reattivo). Rif. docs/ROADMAP-QLIB.md §1.5. ---
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.IFeatureDriftDetector, ProcioneMGR.Services.Monitoring.Drift.PsiDriftDetector>();
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.IFeatureDriftDetector, ProcioneMGR.Services.Monitoring.Drift.KsDriftDetector>();
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.IFeatureDriftDetector, ProcioneMGR.Services.Monitoring.Drift.PageHinkleyDetector>();
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.IFeatureDriftMonitor, ProcioneMGR.Services.Monitoring.Drift.FeatureDriftMonitor>();

// Opzioni via Configure<T> (non POCO singleton): /admin/autonomy le modifica a caldo. Il worker
// è registrato ANCHE come singleton risolvibile, così la UI può chiamare TickAsync ("Esegui ora")
// sulla stessa istanza del hosted service (pattern MetricsCollector più sotto).
builder.Services.Configure<ProcioneMGR.Services.Monitoring.Drift.DriftMonitorOptions>(builder.Configuration.GetSection("Drift"));
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftWorker>());

// --- Observability (Fase 5): meter unico degli eventi di autonomia; export OTLP opzionale sotto. ---
builder.Services.AddSingleton<ProcioneMGR.Services.Observability.ProcioneMetrics>();

// Collettore in-processo dei contatori: alimenta la dashboard /metrics senza backend OTel.
builder.Services.AddSingleton<ProcioneMGR.Services.Observability.MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Observability.MetricsCollector>());

// Export OpenTelemetry OPT-IN (default OFF): senza il flag il meter emette a vuoto (costo ~0).
// Con Observability:Enabled=true si esportano metriche E log via OTLP verso il collector locale
// (endpoint da config, default localhost:4317; stack in infra/observability/docker-compose.yml).
// Nessun impatto sul comportamento dell'app, solo telemetria in uscita.
builder.Services.AddProcioneObservability(builder.Configuration);

// --- Model registry (Fase 2): ciclo di vita dei modelli ML con gate DSR + ciclo chiuso col drift. ---
var registryOptions = builder.Configuration.GetSection("Registry").Get<ProcioneMGR.Services.Registry.ModelRegistryOptions>()
                      ?? new ProcioneMGR.Services.Registry.ModelRegistryOptions();
builder.Services.AddSingleton(registryOptions);
builder.Services.AddSingleton<ProcioneMGR.Services.Registry.IModelRegistry, ProcioneMGR.Services.Registry.ModelRegistry>();

// --- Dual-read ML (Fase 2a): confronto OSSERVATIVO col servizio remoto procionemgr-ml. ---
// Ml:Enabled è hot-reload (letto a ogni candela); Ml:RemoteUrl richiede riavvio (il canale gRPC è
// creato una sola volta). Se RemoteUrl è vuoto il client NON viene registrato: il TradingEngine
// riceve null e il confronto è staticamente spento (zero overhead, nessun impatto sul trading).
builder.Services.Configure<ProcioneMGR.Services.ML.MlComparisonOptions>(builder.Configuration.GetSection("Ml"));
var mlRemoteUrl = builder.Configuration["Ml:RemoteUrl"];
if (!string.IsNullOrWhiteSpace(mlRemoteUrl))
{
    builder.Services.AddGrpcClient<ProcioneMGR.Contracts.Ml.V1.InferenceService.InferenceServiceClient>(o =>
        o.Address = new Uri(mlRemoteUrl));
    builder.Services.AddSingleton<ProcioneMGR.Services.ML.IMlComparisonClient, ProcioneMGR.Services.ML.MlComparisonClient>();
}

// --- Backup DB (pagina /admin/backup): pg_dump/pg_restore del database Postgres + cartella backup/ ---
builder.Services.AddSingleton<ProcioneMGR.Services.Admin.DatabaseBackupService>();

// --- Multi-strategy ensemble + trading: corsie isolate (LaneId 0..LaneCount-1) ---
// Corsie fisse e in numero limitato invece di un orchestratore dinamico con entita' Ensemble
// per-Id: la sessione Paper reale gia' in corso sulla corsia 0 non deve subire discontinuita',
// e l'isolamento dati e' garantito dalla colonna discriminante LaneId (TradingEntities/
// EnsembleState) invece che da DbContext separati. Ogni corsia ha la propria istanza keyed
// di IEnsembleManager/ITradingEngine + il proprio TradingWorker/EnsembleRebalanceWorker.
//
// La composizione vive in AddTradingLanes (condivisa verbatim con l'host ProcioneMGR.Trading,
// Fase 2b): è lì che il toggle Trading:UseRemoteTrading commuta fra motore locale e client
// remoto, garantendo per costruzione che i due non siano mai attivi insieme sulla stessa corsia.
builder.Services.AddTradingLanes(builder.Configuration);

// --- Autonomous Pipeline (orchestratore end-to-end: dati -> feature -> discovery -> holdout -> raccomandazione) ---
// Gli stage sono transient e risolti nello scope del run (dipendono da servizi scoped come
// IBacktestEngine); catalogo ed engine sono singleton (un run alla volta, stato live condiviso).
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineRulesProvider, ProcioneMGR.Services.Pipeline.PipelineRulesProvider>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineStageCatalog, ProcioneMGR.Services.Pipeline.PipelineStageCatalog>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineEngine, ProcioneMGR.Services.Pipeline.PipelineEngine>();
builder.Services.AddHostedService<ProcioneMGR.Services.Pipeline.PipelineSchedulerWorker>();

// --- Experiment tracking generalizzato (osservabilità confrontabile di ogni run di ricerca) ---
// Singleton: usa IDbContextFactory (context a vita breve per operazione), additivo, nessuna
// modifica agli engine. Rif. docs/ROADMAP-QLIB.md §1.3.
builder.Services.AddSingleton<ProcioneMGR.Services.Experiments.IExperimentTracker, ProcioneMGR.Services.Experiments.ExperimentTracker>();

// --- Layer AI di supervisione del ciclo di ricerca (SOLO advisory) ---
// Confine di sicurezza: questi servizi leggono i run e scrivono un advisory; NON avviano trading,
// NON passano in Live, NON toccano SafetyChecker (nessun servizio di esecuzione iniettato). Inattivo
// per default: il worker si spegne subito se Llm:Enabled=false o se manca la env ANTHROPIC_API_KEY.
// Opzioni via Configure<T> (hot-reload da /admin/autonomy); worker anche singleton risolvibile
// per il bottone "Esegui supervisione ora" (stessa istanza del hosted service).
builder.Services.Configure<ProcioneMGR.Services.Llm.LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmClient, ProcioneMGR.Services.Llm.AnthropicLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.IPipelineSupervisor, ProcioneMGR.Services.Llm.PipelineSupervisor>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.LlmSupervisorWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Pipeline.LlmSupervisorWorker>());

// --- Autonomia: ri-applica automatica dell'ensemble + supervisore AI del ciclo di ri-applica ---
// Il PipelineApplier estrae la logica di "Applica al Trading" (una sola implementazione, usata sia
// dalla UI che dallo scheduler). Il comparatore decide oggettivamente (con hysteresis) se un nuovo
// ensemble è meglio del corrente; il supervisore (Logging di default, Claude opzionale) può solo
// porre un veto. Tutto scrive SOLO configurazione: nessun trading avviato, mai Live, mai SafetyChecker.
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineApplier, ProcioneMGR.Services.Pipeline.PipelineApplier>();

var comparatorOptions = builder.Configuration.GetSection("EnsembleComparator").Get<EnsembleComparatorOptions>()
                        ?? new EnsembleComparatorOptions();
builder.Services.AddSingleton(comparatorOptions);
builder.Services.AddSingleton<IEnsembleComparator, EnsembleComparator>();

builder.Services.Configure<ProcioneMGR.Services.Pipeline.AutoReapplyOptions>(builder.Configuration.GetSection("AutoReapply"));

var supervisorAgentOptions = builder.Configuration.GetSection("PipelineSupervisor").Get<ProcioneMGR.Services.Agents.SupervisorAgentOptions>()
                             ?? new ProcioneMGR.Services.Agents.SupervisorAgentOptions();
builder.Services.AddSingleton(supervisorAgentOptions);
if (string.Equals(supervisorAgentOptions.Provider, "Claude", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ProcioneMGR.Services.Agents.IPipelineSupervisorAgent, ProcioneMGR.Services.Agents.ClaudeSupervisorAgent>();
}
else
{
    builder.Services.AddSingleton<ProcioneMGR.Services.Agents.IPipelineSupervisorAgent, ProcioneMGR.Services.Agents.LoggingSupervisorAgent>();
}

// --- Autonomia: auto-promozione Paper→Testnet (MAI a Live) ---
// L'evaluator decide (logica pura, testabile), il promoter agisce (stop→restart della corsia),
// il worker rivaluta ogni N ore. Confine non negoziabile: nessuna promozione automatica a Live.
builder.Services.Configure<PromotionEvaluatorOptions>(builder.Configuration.GetSection("PromotionEvaluator"));
builder.Services.AddSingleton<IPromotionEvaluator, PromotionEvaluator>();
builder.Services.AddSingleton<ILanePromoter, LanePromoter>();
builder.Services.AddHostedService<PromotionWorker>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Fase 1: nessun server email reale (IdentityNoOpEmailSender), quindi
        // disattiviamo la conferma account per permettere login immediato post-registrazione.
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

// Fail-fast: in Production non si parte MAI con la master key placeholder del template — con
// quella chiave (pubblica su git) le credenziali exchange "cifrate" sono in chiaro di fatto.
// In Development resta permessa (comodo per il primo avvio); il trading LIVE è comunque
// bloccato dal gate equivalente in TradingEngine.StartAsync qualunque sia l'ambiente.
if (app.Environment.IsProduction()
    && app.Services.GetRequiredService<IMasterKeyStatus>().IsDefaultDevKey)
{
    throw new InvalidOperationException(
        "Security:MasterKey è ancora il placeholder di sviluppo del template: genera una chiave " +
        "reale (base64 di 32 byte) e impostala via variabile d'ambiente PROCIONE_MGR_MASTER_KEY " +
        "o User Secrets prima di avviare in produzione.");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// Liveness/readiness per Kubernetes (Fase 3): stesso endpoint anonimo già esposto da
// ingestion/ml/trading — il monolite era l'unico dei quattro a non averlo. Le probe non possono
// puntare a "/" (redirect di login, negoziazione del circuito Blazor): serve un endpoint che
// risponda 200 e basta. Nessun dato esposto, nessuna autorizzazione richiesta di proposito.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Applica le migrazioni pendenti e crea i ruoli (Admin/Manager/User) all'avvio.
// Saltato sotto i tool di design-time (dotnet ef): non deve tentare di connettersi/migrare il DB
// mentre si generano migrazioni (es. verso un PostgreSQL non ancora creato).
if (!EF.IsDesignTime)
{
    await DbInitializer.InitializeAsync(app.Services);
}

app.Run();
