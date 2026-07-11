using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using ProcioneMGR.Components;
using ProcioneMGR.Components.Account;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
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
void ConfigureDatabase(DbContextOptionsBuilder options)
{
    var pg = builder.Configuration.GetConnectionString("PostgresConnection")
             ?? throw new InvalidOperationException("Connection string 'PostgresConnection' non trovata.");
    options.UseNpgsql(pg, npgsql => npgsql.MigrationsAssembly("ProcioneMGR.Migrations.Postgres"));
}

// DbContextFactory (per servizi a lunga durata e componenti Blazor interattivi) +
// bridge scoped richiesto da ASP.NET Core Identity. Entrambi condividono lo stesso
// IEncryptionService iniettato nel costruttore del DbContext.
builder.Services.AddDbContextFactory<ApplicationDbContext>(ConfigureDatabase);
builder.Services.AddScoped<ApplicationDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// --- Layer Exchange (Strategy/Factory) ---
// I client sono typed HttpClient: base address e User-Agent centralizzati qui.
builder.Services.AddHttpClient<BinanceClient>(client =>
{
    client.BaseAddress = new Uri("https://api.binance.com");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
});
builder.Services.AddHttpClient<BitgetClient>(client =>
{
    client.BaseAddress = new Uri("https://api.bitget.com");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
});
builder.Services.AddSingleton<IExchangeClientFactory, ExchangeClientFactory>();

// --- Servizio di ingestione OHLCV ---
builder.Services.AddScoped<IOhlcvIngestionService, OhlcvIngestionService>();

// --- Sincronizzazione watchlist (manuale + schedulata in background) ---
builder.Services.AddScoped<IMarketDataSyncService, MarketDataSyncService>();
builder.Services.AddHostedService<MarketDataSyncWorker>();

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
builder.Services.AddSingleton<ISafetyConfigWriter, SafetyConfigWriter>();

// --- Esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Master switch default-off
//     (Trading:LiveExecution:Enabled). Rif. docs/ROADMAP-QLIB.md §1.2. ---
builder.Services.Configure<LiveExecutionOptions>(builder.Configuration.GetSection("Trading:LiveExecution"));
// ITradingEngine/TradingWorker sono registrati piu' sotto come keyed singleton per corsia
// (vedi blocco "Multi-strategy ensemble + trading: corsie isolate").

// --- Backtesting ---
builder.Services.AddSingleton<IStrategyFactory, StrategyFactory>();
builder.Services.AddScoped<IBacktestEngine, BacktestEngine>();

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

var driftOptions = builder.Configuration.GetSection("Drift").Get<ProcioneMGR.Services.Monitoring.Drift.DriftMonitorOptions>()
                   ?? new ProcioneMGR.Services.Monitoring.Drift.DriftMonitorOptions();
builder.Services.AddSingleton(driftOptions);
builder.Services.AddHostedService<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftWorker>();

// --- Observability (Fase 5): meter unico degli eventi di autonomia; export OTLP opzionale sotto. ---
builder.Services.AddSingleton<ProcioneMGR.Services.Observability.ProcioneMetrics>();

// Collettore in-processo dei contatori: alimenta la dashboard /metrics senza backend OTel.
builder.Services.AddSingleton<ProcioneMGR.Services.Observability.MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Observability.MetricsCollector>());

// Export OpenTelemetry OPT-IN (default OFF): senza questo blocco il meter emette a vuoto (costo ~0).
// Con Observability:Enabled=true si esporta via OTLP verso il collector (endpoint da config, default
// localhost:4317). Nessun impatto sul comportamento dell'app, solo telemetria in uscita.
if (builder.Configuration.GetValue<bool>("Observability:Enabled"))
{
    builder.Services.AddOpenTelemetry().WithMetrics(m =>
    {
        m.AddMeter(ProcioneMGR.Services.Observability.ProcioneMetrics.MeterName);
        var otlpEndpoint = builder.Configuration.GetValue<string>("Observability:OtlpEndpoint");
        m.AddOtlpExporter(o =>
        {
            if (!string.IsNullOrWhiteSpace(otlpEndpoint)) o.Endpoint = new Uri(otlpEndpoint);
        });
    });
}

// --- Model registry (Fase 2): ciclo di vita dei modelli ML con gate DSR + ciclo chiuso col drift. ---
var registryOptions = builder.Configuration.GetSection("Registry").Get<ProcioneMGR.Services.Registry.ModelRegistryOptions>()
                      ?? new ProcioneMGR.Services.Registry.ModelRegistryOptions();
builder.Services.AddSingleton(registryOptions);
builder.Services.AddSingleton<ProcioneMGR.Services.Registry.IModelRegistry, ProcioneMGR.Services.Registry.ModelRegistry>();

// --- Backup DB (pagina /admin/backup): pg_dump/pg_restore del database Postgres + cartella backup/ ---
builder.Services.AddSingleton<ProcioneMGR.Services.Admin.DatabaseBackupService>();

// --- Multi-strategy ensemble + trading: corsie isolate (LaneId 0..LaneCount-1) ---
// Corsie fisse e in numero limitato invece di un orchestratore dinamico con entita' Ensemble
// per-Id: la sessione Paper reale gia' in corso sulla corsia 0 non deve subire discontinuita',
// e l'isolamento dati e' garantito dalla colonna discriminante LaneId (TradingEntities/
// EnsembleState) invece che da DbContext separati. Ogni corsia ha la propria istanza keyed
// di IEnsembleManager/ITradingEngine + il proprio TradingWorker/EnsembleRebalanceWorker.
for (var lane = 0; lane < TradingLanes.Count; lane++)
{
    var laneId = lane;

    builder.Services.AddKeyedSingleton<IEnsembleManager>(laneId, (sp, _) => new EnsembleManager(
        laneId,
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IRegimeDetector>(),
        sp.GetRequiredService<IMarketFeatureExtractor>(),
        sp.GetRequiredService<ProcioneMGR.Services.Monitoring.IStrategyDecayMonitor>(),
        sp.GetRequiredService<ILogger<EnsembleManager>>()));

    builder.Services.AddKeyedSingleton<ITradingEngine>(laneId, (sp, _) => new TradingEngine(
        laneId,
        sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
        sp.GetRequiredService<IStrategyFactory>(),
        sp.GetRequiredService<ITechnicalIndicatorsService>(),
        sp.GetRequiredService<IExchangeClientFactory>(),
        sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
        sp.GetRequiredService<IOptionsMonitor<SafetyConfiguration>>(),
        sp.GetRequiredService<IOptionsMonitor<LiveExecutionOptions>>(),
        sp.GetRequiredService<ProcioneMGR.Services.Execution.IExecutionAlgorithmFactory>(),
        sp.GetRequiredService<ILogger<TradingEngine>>(),
        sp.GetRequiredService<ProcioneMGR.Services.Observability.ProcioneMetrics>(),
        sp.GetRequiredService<ProcioneMGR.Services.Registry.IModelRegistry>(),
        sp.GetRequiredService<ProcioneMGR.Services.Alpha.IAlphaFactorFactory>(),
        sp.GetRequiredService<ProcioneMGR.Services.Alpha.IFactorCache>(),
        sp.GetRequiredService<IMasterKeyStatus>()));

    builder.Services.AddSingleton<IHostedService>(sp => new TradingWorker(
        sp.GetRequiredKeyedService<ITradingEngine>(laneId),
        sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
        sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
        sp.GetRequiredService<ILogger<TradingWorker>>()));

    builder.Services.AddSingleton<IHostedService>(sp => new ExecutionWorker(
        sp.GetRequiredKeyedService<ITradingEngine>(laneId),
        sp.GetRequiredService<IOptionsMonitor<LiveExecutionOptions>>(),
        sp.GetRequiredService<ILogger<ExecutionWorker>>()));

    builder.Services.AddSingleton<IHostedService>(sp => new EnsembleRebalanceWorker(
        sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
        sp.GetRequiredService<ILogger<EnsembleRebalanceWorker>>()));
}

// Fallback non-keyed: risolve sempre la corsia 0. Serve ai consumer non ancora aggiornati con
// un selettore di corsia esplicito (dashboard, retraining regime, applicazione raccomandazioni
// pipeline) - comportamento identico a prima dell'introduzione delle corsie multiple.
builder.Services.AddSingleton<IEnsembleManager>(sp => sp.GetRequiredKeyedService<IEnsembleManager>(0));
builder.Services.AddSingleton<ITradingEngine>(sp => sp.GetRequiredKeyedService<ITradingEngine>(0));

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
var llmOptions = builder.Configuration.GetSection("Llm").Get<ProcioneMGR.Services.Llm.LlmOptions>()
                 ?? new ProcioneMGR.Services.Llm.LlmOptions();
builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmClient, ProcioneMGR.Services.Llm.AnthropicLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.IPipelineSupervisor, ProcioneMGR.Services.Llm.PipelineSupervisor>();
builder.Services.AddHostedService<ProcioneMGR.Services.Pipeline.LlmSupervisorWorker>();

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

var autoReapplyOptions = builder.Configuration.GetSection("AutoReapply").Get<ProcioneMGR.Services.Pipeline.AutoReapplyOptions>()
                         ?? new ProcioneMGR.Services.Pipeline.AutoReapplyOptions();
builder.Services.AddSingleton(autoReapplyOptions);

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
var promotionOptions = builder.Configuration.GetSection("PromotionEvaluator").Get<PromotionEvaluatorOptions>()
                       ?? new PromotionEvaluatorOptions();
builder.Services.AddSingleton(promotionOptions);
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

// Applica le migrazioni pendenti e crea i ruoli (Admin/Manager/User) all'avvio.
// Saltato sotto i tool di design-time (dotnet ef): non deve tentare di connettersi/migrare il DB
// mentre si generano migrazioni (es. verso un PostgreSQL non ancora creato).
if (!EF.IsDesignTime)
{
    await DbInitializer.InitializeAsync(app.Services);
}

app.Run();
