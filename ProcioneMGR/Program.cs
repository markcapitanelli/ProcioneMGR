using Mediator;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using ProcioneMGR.Services.Notifications;
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

// Asset statici quando si gira DA SORGENTE con ASPNETCORE_ENVIRONMENT=Production (run-postgres.ps1):
// il manifest degli Static Web Assets viene caricato in automatico solo in Development, e senza di
// esso MapStaticAssets risponde 500 proprio sugli asset impronta-digitalizzati (blazor.web.js, il
// bundle CSS scoped) mentre i file "semplici" di wwwroot rispondono 200 — l'app parte e sembra sana,
// ma il CSS dei componenti manca e NESSUN circuito interattivo si avvia. È il warning esplicito di
// StaticAssetsInvoker a suggerire questa chiamata. Sull'output PUBBLICATO (immagini Docker) il
// manifest runtime non esiste e la chiamata è un no-op: il pod non cambia comportamento.
builder.WebHost.UseStaticWebAssets();

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
// --- Data Protection: nome applicativo fisso + keyring persistito ---
// Estratto in DataProtectionSetup per essere verificabile da test: il nome applicativo decide
// quali chiavi firmano i cookie, e prima veniva impostato solo quando era configurato un keyring
// su file — cioè mai in sviluppo locale. Vedi il commento della classe.
builder.Services.AddProcioneDataProtection(builder.Configuration);

// Servizio di cifratura (AES-256-GCM) per i segreti a riposo. Singleton: la chiave
// master viene derivata una sola volta. Va registrato PRIMA del DbContext perche'
// l'EncryptedStringConverter ne dipende.
builder.Services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
// Stato della master key (placeholder di sviluppo?): stessa istanza del servizio di cifratura,
// esposta come vista ristretta per i guard fail-fast (startup Production, gate Live del motore).
builder.Services.AddSingleton<IMasterKeyStatus>(sp => (AesGcmEncryptionService)sp.GetRequiredService<IEncryptionService>());
// [Fase 0 PRD-RISANAMENTO] Keyring della rotazione: stessa istanza, vista di classificazione
// (il payload e' sulla chiave corrente?) + servizio di ri-cifratura di massa per il bottone
// "Ri-cifra ora" di /settings/exchanges. Chiude il TODO storico di AesGcmEncryptionService.
builder.Services.AddSingleton<IMasterKeyRing>(sp => (AesGcmEncryptionService)sp.GetRequiredService<IEncryptionService>());
builder.Services.AddSingleton<IMasterKeyRotationService, MasterKeyRotationService>();

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

// [E7] Guardia di freschezza delle serie: SEMPRE nel guscio, con ingestion locale o remota — legge
// solo il DB e notifica la transizione a ferma. È il sync l'imputato: la guardia non può stargli in
// casa (MKR è rimasta ferma dieci mesi con il LogWarning nel posto che nessuno legge).
builder.Services.AddSingleton<ProcioneMGR.Services.Ingestion.SeriesFreshnessWatchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Ingestion.SeriesFreshnessWatchWorker>());

// --- Indicatori tecnici (stateless) ---
builder.Services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();

// [E-04] Catalogo simboli condiviso con cache: sostituisce le sette scansioni Distinct() su
// OhlcvData (~12M righe per ~30 stringhe) che ogni pagina rifaceva per conto proprio.
builder.Services.AddSingleton<ProcioneMGR.Services.MarketData.ISymbolCatalog>(sp =>
    new ProcioneMGR.Services.MarketData.SymbolCatalog(
        sp.GetRequiredService<IDbContextFactory<ProcioneMGR.Data.ApplicationDbContext>>()));

// --- Market regime detection (Fase 7): feature extraction + clustering ---
builder.Services.AddSingleton<IMarketFeatureExtractor, MarketFeatureExtractor>();
builder.Services.AddSingleton<IMarketBreadthCalculator, MarketBreadthCalculator>(); // [3.8a] breadth interna per i regimi
builder.Services.AddSingleton<IRegimeDetector, RegimeDetector>();
builder.Services.AddHostedService<RegimeRetrainingWorker>();

// --- Trading (Fase 8): safety + paper engine ---
// NB: la safety si usa SOLO via SafetyChecker.Evaluate (statico, puro) dentro il TradingEngine:
// nessuna registrazione DI — l'interfaccia istanza era codice morto mai risolto da nessuno.
builder.Services.Configure<SafetyConfiguration>(builder.Configuration.GetSection("Trading:Safety"));
// Writer generalizzato di sezioni appsettings (pannelli /admin/autonomy e /execution):
// read-modify-write con lock sul file; reloadOnChange fa il resto (hot-reload ~1s).
// [E1] Il pannello sicurezza di /trading NON passa più di qui: Trading:Safety la applica il
// MOTORE, quindi si legge e si scrive via IEngineConfigStore (ISafetyConfigWriter rimosso).
builder.Services.AddSingleton<ProcioneMGR.Services.Config.IAppConfigWriter, ProcioneMGR.Services.Config.AppConfigWriter>();

// --- Esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Master switch default-off
//     (Trading:LiveExecution:Enabled). Rif. docs/archive/ROADMAP-QLIB.md §1.2. ---
builder.Services.Configure<LiveExecutionOptions>(builder.Configuration.GetSection("Trading:LiveExecution"));
// ITradingEngine/TradingWorker sono registrati piu' sotto come keyed singleton per corsia
// (vedi blocco "Multi-strategy ensemble + trading: corsie isolate").

// --- Backtesting ---
builder.Services.AddSingleton<IStrategyFactory, StrategyFactory>();
builder.Services.AddScoped<IBacktestEngine, BacktestEngine>();
// [A1 roadmap integrazione] Giudice del gemello nullo unificato (200 gemelli, 99°): unico punto
// di policy per pipeline (NullTwinValidationStage) e tool CLI. Scoped come il motore che usa.
builder.Services.AddScoped<ProcioneMGR.Services.Validation.INullTwinJudge, ProcioneMGR.Services.Validation.NullTwinJudge>();
// [T0.2] Serie storica dei funding per i backtest futures (da SentimentMetricPoints).
builder.Services.AddScoped<IFundingHistoryProvider, FundingHistoryProvider>();

// Preset di configurazione pagina + memoria dell'ultima configurazione usata (per utente).
builder.Services.AddScoped<ProcioneMGR.Services.Preferences.IPageConfigStore, ProcioneMGR.Services.Preferences.PageConfigStore>();

// --- Parameter optimization (Grid Search + Walk-Forward) ---
builder.Services.AddScoped<IOptimizationEngine, OptimizationEngine>();
// Ottimizzazione bayesiana (Fase 6): surrogato GP + Expected Improvement, affiancabile al grid.
builder.Services.AddSingleton<ProcioneMGR.Services.Optimization.Bayesian.IHyperparameterOptimizer, ProcioneMGR.Services.Optimization.Bayesian.BayesianOptimizationEngine>();
// [E-03, Fase 2 PRD-RISANAMENTO] BayesianSearch NON si registra piu': era un Singleton mai
// risolto da nessuno, e per giunta INCOMPATIBILE col disegno reale — l'unico consumatore
// (OptimizationEngine.cs, ricerca bayesiana) lo costruisce a mano per-run con
// `new BayesianSearch(new BayesianOptimizationEngine(new BayesianOptions { Seed = config.BayesianSeed }))`
// perche' il seed e' PER-ESPERIMENTO: un singleton col seed congelato al boot romperebbe la
// riproducibilita' per-run. La registrazione suggeriva il contrario a chi legge questo file.

// --- Nested decision execution (TWAP/VWAP/Iceberg + simulatore di fill). Additivo: il default
//     "Immediate" riproduce il comportamento odierno. Rif. docs/archive/ROADMAP-QLIB.md §1.2. ---
builder.Services.AddSingleton<ProcioneMGR.Services.Execution.IExecutionAlgorithmFactory, ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory>();
builder.Services.AddSingleton<ProcioneMGR.Services.Execution.IExecutionSimulator, ProcioneMGR.Services.Execution.ExecutionSimulator>();
// Options (non POCO singleton catturato al boot): il pannello "Modello di costo" di /execution li
// modifica a caldo, e senza IOptionsMonitor il confronto fra algoritmi avrebbe continuato a girare
// con i parametri letti all'avvio — cioè un pannello che sembra funzionare e non cambia nulla.
builder.Services.Configure<ProcioneMGR.Services.Execution.ExecutionParameters>(builder.Configuration.GetSection("Execution"));

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
// [D2] Monitor di deriva dei fattori: puro/deterministico come il valutatore, quindi Singleton.
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.IFactorDriftAnalyzer, ProcioneMGR.Services.Alpha.FactorDriftAnalyzer>();
// Fotografia in memoria + job che la aggiorna: senza, l'alert vivrebbe solo per chi apre il
// pannello. Nessuna azione automatica, solo segnalazione (PRD §5e).
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.FactorDriftSnapshot>();
// Storia dell'IC su tabella: il worker la scrive, la Home la ritrova già pronta dopo un riavvio
// del guscio e il pannello può mostrare quando un fattore si è spento senza ricalcolare.
builder.Services.AddSingleton<ProcioneMGR.Services.Alpha.IFactorIcHistoryStore, ProcioneMGR.Services.Alpha.FactorIcHistoryStore>();
// [M1] La sezione FactorDrift governava un worker vivo senza pannello e senza POCO: il worker la
// leggeva con GetValue a tipo inferito, invisibile a entrambi i guardiani di copertura UI.
builder.Services.Configure<ProcioneMGR.Services.Alpha.FactorDriftOptions>(builder.Configuration.GetSection("FactorDrift"));
builder.Services.AddHostedService<ProcioneMGR.Services.Alpha.FactorDriftWorker>();
// [C4] Etichettatura triple-barrier + meta-labeling: puri e deterministici, quindi Singleton.
builder.Services.AddSingleton<ProcioneMGR.Services.ML.Labeling.ITripleBarrierLabeler, ProcioneMGR.Services.ML.Labeling.TripleBarrierLabeler>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.Labeling.IMetaLabeler, ProcioneMGR.Services.ML.Labeling.MetaLabeler>();
builder.Services.AddSingleton<ProcioneMGR.Services.ML.Labeling.IMetaModelTrainer, ProcioneMGR.Services.ML.Labeling.MetaModelTrainer>();
// Il consumo: catena completa (segnali reali -> etichette -> meta-modello) dietro la pagina Backtest.
builder.Services.AddScoped<ProcioneMGR.Services.ML.Labeling.IMetaLabelingAnalysisService, ProcioneMGR.Services.ML.Labeling.MetaLabelingAnalysisService>();

// [D4] Ricerca di pattern per forma (DTW) + misura del valore predittivo col nullo per forma.
builder.Services.AddSingleton<ProcioneMGR.Services.Discovery.Dtw.IDtwMatcher, ProcioneMGR.Services.Discovery.Dtw.DtwMatcher>();
builder.Services.AddSingleton<ProcioneMGR.Services.Discovery.Dtw.IDtwPatternAnalysisService, ProcioneMGR.Services.Discovery.Dtw.DtwPatternAnalysisService>();

// --- Formulaic alpha mining (programmazione genetica, C# puro). Rif. docs/archive/ROADMAP-QLIB.md §1.7. ---
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
    // Si inietta la IHttpClientFactory stessa (è lei, non i client che produce, il tipo pensato per
    // essere trattenuto a lungo termine): ogni fonte chiama CreateClient() per fetch, non qui una
    // volta sola a startup — vedi il commento in RssNewsSource.FetchLatestAsync.
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var sources = ProcioneMGR.Services.AltData.NewsFeeds.KnownFeeds
        .Select(kv => (ProcioneMGR.Services.AltData.IAltDataSource)new ProcioneMGR.Services.AltData.RssNewsSource(kv.Key, kv.Value, httpClientFactory))
        .ToList();
    sources.Add(new ProcioneMGR.Services.AltData.ForexFactoryIngestor(httpClientFactory));
    sources.Add(new ProcioneMGR.Services.AltData.RetailSentimentIngestor("FXSSI", "fxssi", httpClientFactory));
    sources.Add(new ProcioneMGR.Services.AltData.RetailSentimentIngestor("MyFxBook", "myfxbook", httpClientFactory));
    return sources;
});
// [Fase B/pilota ONNX] Tre scorer concreti + delegante hot-reload (stesso pattern del layer LLM):
// Keyword = default storico a costo zero; Llm = provider AI attivo (opt-in dal pannello);
// Onnx = inferenza locale del pilota. Ognuno dei non-lessicali ripiega DA SOLO sul lessico.
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.KeywordSentimentScorer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.LlmSentimentScorer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.OnnxSentimentScorer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.ISentimentScorer, ProcioneMGR.Services.Sentiment.DelegatingSentimentScorer>();
builder.Services.AddScoped<ProcioneMGR.Services.Sentiment.OnnxSentimentPilotService>();
builder.Services.AddScoped<ProcioneMGR.Services.Sentiment.SentimentScorerComparisonService>();
builder.Services.AddScoped<ProcioneMGR.Services.AltData.IAltDataSyncService, ProcioneMGR.Services.AltData.AltDataSyncService>();
builder.Services.AddSingleton<ProcioneMGR.Services.AltData.INewsImpactAnalyzer, ProcioneMGR.Services.AltData.NewsImpactAnalyzer>();

// --- Sentiment 2.0: serie di market mood (Fear & Greed + derivati Binance, API senza chiave) ---
builder.Services.Configure<ProcioneMGR.Services.Sentiment.SentimentOptions>(builder.Configuration.GetSection("Sentiment"));
builder.Services.AddHttpClient("SentimentFearGreed", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("SentimentBinanceFutures", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
});
builder.Services.AddSingleton<IEnumerable<ProcioneMGR.Services.Sentiment.Metrics.ISentimentMetricSource>>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProcioneMGR.Services.Sentiment.SentimentOptions>>();
    return new List<ProcioneMGR.Services.Sentiment.Metrics.ISentimentMetricSource>
    {
        new ProcioneMGR.Services.Sentiment.Metrics.FearGreedClient(httpClientFactory),
        new ProcioneMGR.Services.Sentiment.Metrics.BinanceFuturesSentimentClient(options.CurrentValue.Symbols, httpClientFactory),
    };
});
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentSourceHealthRegistry>();
builder.Services.AddScoped<ProcioneMGR.Services.Sentiment.Metrics.ISentimentMetricSyncService, ProcioneMGR.Services.Sentiment.Metrics.SentimentMetricSyncService>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentSnapshotCache>();
builder.Services.AddScoped<ProcioneMGR.Services.Sentiment.ISentimentSnapshotService, ProcioneMGR.Services.Sentiment.SentimentSnapshotService>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.ISentimentNewsProvider, ProcioneMGR.Services.Sentiment.SentimentNewsProvider>();
// Worker anche singleton risolvibile: "Esegui ora" dalla UI usa la stessa istanza del hosted service.
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Sentiment.SentimentSyncWorker>());
// Guardiano di profondità delle serie-patrimonio (funding perso due volte in silenzio, 2026-08-11):
// misura min/count contro le soglie di Sentiment:HeritageGuard, fotografia letta da Home e /sentiment.
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentHeritageSnapshot>();
builder.Services.AddSingleton<ProcioneMGR.Services.Sentiment.SentimentHeritageGuardWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Sentiment.SentimentHeritageGuardWorker>());

// --- [E3] Forward test del carry delta-neutro (default OFF, mai Live per costruzione) ---
// [B3 core caldo] Il CarryWorker NON è più registrato qui: vive nell'host del MOTORE (AddTradingLanes,
// ramo !useRemote) — è operatività che deve sopravvivere ai riavvii del guscio. Con
// Trading:UseRemoteTrading=false resta in questo processo come sempre; con true gira dentro
// procionemgr-trading. NB: il funding che il carry legge (SentimentMetricPoints) lo scrive il
// SentimentSyncWorker, che RESTA nel guscio per scelta (PRD §2): a guscio giù il carry conserva lo
// stato e riprende a decidere quando il funding torna fresco.

// --- [F4] Accumulo liquidazioni (stream pubblico Binance futures, keyless) ---
// Default ON: il dato non è ricostruibile a posteriori, ogni giorno spento è storia persa.
// TryAdd: la factory WebSocket è la stessa del feed real-time R1 (registrata lì quando attivo).
builder.Services.Configure<ProcioneMGR.Services.MarketData.LiquidationsOptions>(builder.Configuration.GetSection("Liquidations"));
builder.Services.TryAddSingleton<ProcioneMGR.Services.MarketData.IWebSocketTransportFactory, ProcioneMGR.Services.MarketData.ClientWebSocketTransportFactory>();
// Anche singleton risolvibile: il pannello /admin/autonomy legge stato di connessione e messaggi
// ricevuti dalla STESSA istanza del hosted service (pattern MetricsCollector/SentimentSyncWorker).
builder.Services.AddSingleton<ProcioneMGR.Services.MarketData.LiquidationSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.MarketData.LiquidationSyncWorker>());
// [2026-08-24] Lo STESSO singleton visto come diagnostica: il guardiano delle serie-patrimonio deve
// poter LEGGERE perche' la serie liquidazioni e' vuota, invece di asserirne la causa a commento.
builder.Services.AddSingleton<ProcioneMGR.Services.MarketData.ILiquidationFeedDiagnostics>(
    sp => sp.GetRequiredService<ProcioneMGR.Services.MarketData.LiquidationSyncWorker>());

// --- Portfolio optimization (Mean-Variance, Risk Parity, HRP) ---
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.MeanVarianceOptimizer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.RiskParityOptimizer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.HierarchicalRiskParityOptimizer>();
// [2.8 PRD-RISANAMENTO, chiude C-05] Le stesse TRE istanze anche come IPortfolioOptimizer:
// l'EnsembleAssemblyStage risolve per nome (parametro di stage 'portfolioOptimizer', default
// HRP = pesi identici allo storico). Prima l'allocatore era un tipo concreto cablato nello
// stage e /portfolio confrontava allocazioni che nessuno poteva applicare.
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.IPortfolioOptimizer>(sp => sp.GetRequiredService<ProcioneMGR.Services.Portfolio.HierarchicalRiskParityOptimizer>());
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.IPortfolioOptimizer>(sp => sp.GetRequiredService<ProcioneMGR.Services.Portfolio.MeanVarianceOptimizer>());
builder.Services.AddSingleton<ProcioneMGR.Services.Portfolio.IPortfolioOptimizer>(sp => sp.GetRequiredService<ProcioneMGR.Services.Portfolio.RiskParityOptimizer>());

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
//     monitor di decadimento reattivo). Rif. docs/archive/ROADMAP-QLIB.md §1.5. ---
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
// [I6] Fotografia per la Home, sullo stesso impianto della deriva dei FATTORI (D2.a/D2.b): scritta
// a fine tick e RICOSTRUITA all'avvio dall'ultimo tick registrato. L'idratazione non è una cache: il
// guscio si riavvia di continuo, e senza di essa l'allarme mancherebbe proprio nei minuti in cui uno
// guarda la Home, comparendo solo dopo il primo tick — che con cadenza di ore può essere lontano.
builder.Services.AddSingleton<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftSnapshot>();
builder.Services.AddHostedService<ProcioneMGR.Services.Monitoring.Drift.FeatureDriftHydrationWorker>();

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

// --- Backup DB (pagina /admin/backup): pg_dump/pg_restore del database Postgres ---
// La sezione Backup e' la FONTE UNICA della destinazione notturna: la legge questo servizio e la
// legge scripts/db-backup.ps1 dall'appsettings.json del repo principale. Prima del 2026-08-23 la
// pagina conosceva solo backup/ sotto la content root e mostrava l'ultimo file del 2026-07-09
// mentre il dump di stanotte stava sano in %USERPROFILE%\ProcioneMGR-Backup.
builder.Services.Configure<ProcioneMGR.Services.Admin.BackupOptions>(
    builder.Configuration.GetSection(ProcioneMGR.Services.Admin.BackupOptions.SectionName));
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

// --- CQRS/Mediator (Fase 1, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4) ---
// Solo lato Blazor: TradingCommandServiceImpl (ProcioneMGR.Trading, gRPC standalone) non
// referenzia questo pacchetto e continua a chiamare ITradingEngine direttamente (§4.2/§4.3).
// Un solo IMediator globale, non keyed per corsia: il routing per corsia avviene per dato
// (ogni comando/query porta LaneId), non per istanza di servizio.
builder.Services.AddMediator(o =>
{
    o.ServiceLifetime = ServiceLifetime.Singleton;
    o.PipelineBehaviors = [typeof(ProcioneMGR.Services.Trading.Behaviors.LoggingBehavior<,>)];
});

// --- Autonomous Pipeline (orchestratore end-to-end: dati -> feature -> discovery -> holdout -> raccomandazione) ---
// Gli stage sono transient e risolti nello scope del run (dipendono da servizi scoped come
// IBacktestEngine); catalogo ed engine sono singleton (un run alla volta, stato live condiviso).
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineRulesProvider, ProcioneMGR.Services.Pipeline.PipelineRulesProvider>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineStageCatalog, ProcioneMGR.Services.Pipeline.PipelineStageCatalog>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IPipelineEngine, ProcioneMGR.Services.Pipeline.PipelineEngine>();
builder.Services.AddHostedService<ProcioneMGR.Services.Pipeline.PipelineSchedulerWorker>();

// Canale di notifica (Fase 4, PRD Autonomia §7): default OFF, provider Logging/Telegram.
// Registrato PRIMA dei producer (watchdog, planner, engine, promozioni) che lo ricevono opzionale.
builder.Services.AddProcioneNotifications(builder.Configuration);

// Campaign Planner (Fase 1, PRD Autonomia): la politica di reazione agli esiti dei run.
// Gate Campaign:Enabled default OFF — senza attivazione esplicita il worker gira a vuoto.
// L'evaluator è la catena valuta-e-applica CONDIVISA con la ri-applica dello scheduler
// (supervisore con veto + isteresi + applier): una sola istanza, un solo gate di atomicità.
builder.Services.Configure<ProcioneMGR.Services.Pipeline.CampaignOptions>(builder.Configuration.GetSection("Campaign"));
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IRunApplyEvaluator, ProcioneMGR.Services.Pipeline.RunApplyEvaluator>();
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.ICampaignPlanner, ProcioneMGR.Services.Pipeline.CampaignPlanner>();
builder.Services.AddHostedService<ProcioneMGR.Services.Pipeline.CampaignPlannerWorker>();

// Trigger contestuale (Fase 2, PRD Autonomia): regime K-means / banda vol GARCH → wake del
// planner (mai lancio diretto di run). Inerte senza Campaign:Enabled.
builder.Services.Configure<ProcioneMGR.Services.Pipeline.RegimeTriggerOptions>(builder.Configuration.GetSection("RegimeTrigger"));
builder.Services.AddSingleton<ProcioneMGR.Services.Pipeline.IRegimeChangeDetector, ProcioneMGR.Services.Pipeline.RegimeChangeDetector>();
builder.Services.AddHostedService<ProcioneMGR.Services.Pipeline.RegimeChangeTriggerWorker>();

// --- Experiment tracking generalizzato (osservabilità confrontabile di ogni run di ricerca) ---
// Singleton: usa IDbContextFactory (context a vita breve per operazione), additivo, nessuna
// modifica agli engine. Rif. docs/archive/ROADMAP-QLIB.md §1.3.
builder.Services.AddSingleton<ProcioneMGR.Services.Experiments.IExperimentTracker, ProcioneMGR.Services.Experiments.ExperimentTracker>();

// --- Layer AI di supervisione del ciclo di ricerca (SOLO advisory) ---
// Confine di sicurezza: questi servizi leggono i run e scrivono un advisory; NON avviano trading,
// NON passano in Live, NON toccano SafetyChecker (nessun servizio di esecuzione iniettato). Inattivo
// per default: il worker si spegne subito se Llm:Enabled=false o se manca la env ANTHROPIC_API_KEY.
// Opzioni via Configure<T> (hot-reload da /admin/autonomy); worker anche singleton risolvibile
// per il bottone "Esegui supervisione ora" (stessa istanza del hosted service).
builder.Services.Configure<ProcioneMGR.Services.Llm.LlmOptions>(builder.Configuration.GetSection("Llm"));
// [AF1] Consumo e budget del layer AI. Il tracker è il sink dei client (ognuno dichiara il
// consumo con sé stesso come provider: col failover conta chi ha SERVITO) e il tetto del guard
// (budget esaurito = SkippedBudgetExhausted, nessuna chiamata, breaker fermo). Default off:
// senza Llm:Budget:TrackingEnabled non si scrive una riga e non si applica alcun tetto.
builder.Services.Configure<ProcioneMGR.Services.Llm.LlmBudgetOptions>(builder.Configuration.GetSection("Llm:Budget"));
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.LlmUsageTracker>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmUsageSink>(sp => sp.GetRequiredService<ProcioneMGR.Services.Llm.LlmUsageTracker>());
builder.Services.AddHostedService<ProcioneMGR.Services.Llm.LlmUsageFlushWorker>();
// Multi-provider (2026-08-01): le chiavi vivono cifrate a DB (AiCredentials, pannello
// /admin/ai-supervisor) con fallback env; l'ILlmClient registrato è il delegante, che instrada
// OGNI chiamata sul Provider corrente (hot-reload) — supervisore/guard/worker restano ignari.
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.IAiKeyStore, ProcioneMGR.Services.Llm.AiKeyStore>();
builder.Services.AddHttpClient(ProcioneMGR.Services.Llm.OpenAiCompatibleLlmClient.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(120)); // i modelli con reasoning sono lenti; il taglio operativo resta al LlmCallGuard
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.AnthropicLlmClient>();
// [Fase D] I provider OpenAI-compatible: stessa base, cinque righe a testa (principio §1.2 del PRD).
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.NvidiaLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.GeminiLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.GroqLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.HuggingFaceLlmClient>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmClient, ProcioneMGR.Services.Llm.DelegatingLlmClient>();
// [Fase C/D] Risolve un client per NOME: il secondo parere parla con un provider specifico, e il
// delegante instrada su TUTTI i provider noti attraverso lo stesso resolver.
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmClientResolver, ProcioneMGR.Services.Llm.LlmClientResolver>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.ILlmCallGuard, ProcioneMGR.Services.Llm.LlmCallGuard>();
// [AF3] Comitato a scelta vincolata: i provider votano in parallelo su menù chiusi preparati dal
// codice; risposta invalida = astensione, quorum mancato = default deterministico. Nessun potere
// fuori dal menù, nessun breaker condiviso (un'ecatombe del comitato non sospende advisory/veto).
// Default OFF (Committee:Enabled + Fleet:UseCommittee, entrambi necessari).
builder.Services.Configure<ProcioneMGR.Services.Llm.Committee.CommitteeOptions>(builder.Configuration.GetSection("Committee"));
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.Committee.IAiCommittee, ProcioneMGR.Services.Llm.Committee.AiCommittee>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.IPipelineSupervisor, ProcioneMGR.Services.Llm.PipelineSupervisor>();
// [G6] Spiegazione dei candidati bocciati. Il RIASSUNTO è deterministico e vive senza AI (il
// servizio si registra comunque: la pagina lo usa per il digest anche a Llm:ExplainRejections=false);
// il narratore aggiunge solo la prosa, ed è quello che l'interruttore accende.
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.Narration.IRejectionNarrator, ProcioneMGR.Services.Llm.Narration.RejectionNarrator>();
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.Narration.IRejectionExplainService, ProcioneMGR.Services.Llm.Narration.RejectionExplainService>();
// [G9] Narrativa di sintesi in cima al digest giornaliero. Additiva: col narratore assente o muto
// il digest esce identico a prima (Notifications:Digest:NarrativeEnabled, default off).
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.Narration.IDigestNarrator, ProcioneMGR.Services.Llm.Narration.DigestNarrator>();
// [G4] Post-mortem delle operazioni in perdita: fatti dal codice, causa dal menù chiuso, prosa
// dall'AI solo se serve. Sezione PostMortem, default SPENTO; il contesto che ne deriva raggiunge
// il comitato AF3 come informazione in più, mai come scorciatoia fuori dal menù.
builder.Services.Configure<ProcioneMGR.Services.Llm.Narration.PostMortemOptions>(builder.Configuration.GetSection("PostMortem"));
builder.Services.AddSingleton<ProcioneMGR.Services.Llm.Narration.IPostMortemService, ProcioneMGR.Services.Llm.Narration.PostMortemService>();
builder.Services.AddHostedService<ProcioneMGR.Services.Llm.Narration.PostMortemWorker>();
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

// Provider del supervisore-veto scelto PER CHIAMATA (hot-reload da /admin/autonomy), non al boot:
// entrambe le implementazioni sono registrate, il delegating agent instrada su Provider corrente.
builder.Services.Configure<ProcioneMGR.Services.Agents.SupervisorAgentOptions>(builder.Configuration.GetSection("PipelineSupervisor"));
builder.Services.AddSingleton<ProcioneMGR.Services.Agents.LoggingSupervisorAgent>();
builder.Services.AddSingleton<ProcioneMGR.Services.Agents.ClaudeSupervisorAgent>();
builder.Services.AddSingleton<ProcioneMGR.Services.Agents.IPipelineSupervisorAgent, ProcioneMGR.Services.Agents.DelegatingSupervisorAgent>();

// --- [AF5.4] Digest giornaliero: SOLO nel monolite (è lui che vede corsie, journal e consumi).
// Default OFF. La sua ASSENZA all'ora attesa è il dead-man's-switch percepibile dall'umano.
builder.Services.Configure<ProcioneMGR.Services.Notifications.DigestOptions>(builder.Configuration.GetSection("Notifications:Digest"));
builder.Services.AddHostedService<ProcioneMGR.Services.Notifications.DailyDigestWorker>();

// --- [AF2] Orchestratore di flotta (Queen Bee): SOLO nel monolite, come planner e scheduler ---
// Core deterministico puro + reader in sola lettura + worker con journal. Default OFF, e anche
// acceso parte in DryRun: in AF2a non esiste il braccio esecutivo (arriva con AF2b). Non tocca
// MAI l'impronta storica (corsie 0..2), le corsie Live/Testnet, le quarantene o le campagne.
builder.Services.Configure<ProcioneMGR.Services.Fleet.FleetOptions>(builder.Configuration.GetSection("Fleet"));
// [J8] Il registro dell'osservazione cumulata: dichiarato PRIMA del reader che lo consuma.
builder.Services.AddSingleton<ProcioneMGR.Services.Fleet.ILaneObservationLedger, ProcioneMGR.Services.Fleet.LaneObservationLedger>();
// [J9] La ricostruzione delle frequenze attese mancanti (azione amministrativa, /admin/autonomy).
builder.Services.AddSingleton<ProcioneMGR.Services.Fleet.ExpectedFrequencyBackfill>();
builder.Services.AddSingleton<ProcioneMGR.Services.Fleet.IFleetStateReader, ProcioneMGR.Services.Fleet.FleetStateReader>();
// [F5] Il click umano sui candidati grigi: scrive la config su una corsia di flotta libera e
// (se richiesto) la avvia in Paper. Solo grigi, solo flotta, solo Paper: non è una porta di servizio.
builder.Services.AddSingleton<ProcioneMGR.Services.Fleet.IGreyDeployer, ProcioneMGR.Services.Fleet.GreyDeployer>();
builder.Services.AddSingleton<ProcioneMGR.Services.Fleet.FleetOrchestratorWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.Fleet.FleetOrchestratorWorker>());

// --- [I1] Sonda dello stato degli agenti autonomi ---
// Registrata QUI perché è il primo punto in cui tutti e quattro i suoi soggetti esistono (planner,
// flotta, comitato, drift). Dice all'avvio quali agenti sono ACCESI e quali possono davvero agire:
// sono due cose diverse, e non distinguerle il 2026-08-18 ha prodotto un piano di lavoro costruito
// sulla premessa sbagliata che tre agenti vivi fossero spenti. Non notifica di proposito — vedi il
// doc-comment: lo stato è una condizione, non un evento, e il budget notifiche è condiviso.
// Singleton risolvibile oltre che hosted: la card di /admin/autonomy rilegge la stessa istanza.
builder.Services.AddSingleton<ProcioneMGR.Services.Health.AgentStateProbe>();
builder.Services.AddHostedService<ProcioneMGR.Services.Health.AgentStateProbeWorker>();
// [J3] La sonda «la ricerca è viva»: letta dalla Home a ogni caricamento (query leggere), nessun worker.
builder.Services.AddSingleton<ProcioneMGR.Services.Health.ResearchLivenessProbe>();

// --- Autonomia: auto-promozione Paper→Testnet (MAI a Live) ---
// L'evaluator decide (logica pura, testabile), il promoter agisce (stop→restart della corsia),
// il worker rivaluta ogni N ore. Confine non negoziabile: nessuna promozione automatica a Live.
builder.Services.Configure<PromotionEvaluatorOptions>(builder.Configuration.GetSection("PromotionEvaluator"));
builder.Services.AddSingleton<IPromotionEvaluator, PromotionEvaluator>();
builder.Services.AddSingleton<ILanePromoter, LanePromoter>();
builder.Services.AddHostedService<PromotionWorker>();

// P1-5 (audit consolidamento 2026-07-17): orchestrazione di Trading.razor estratta in un service
// testabile senza Blazor — vedi il doc-comment della classe. Scoped: uno scope Blazor Server = un
// circuito, quindi un'istanza per sessione utente, come il componente che la consuma.
builder.Services.AddScoped<ProcioneMGR.Services.Trading.TradingPageService>();

// [B3] Diagnostica delle uscite protettive: confronti d'ombra, posizioni orfane, misura del
// ritardo su richiesta. Registrata SEMPRE, anche col trading remoto — sono letture su Postgres e
// non toccano il motore, quindi restano disponibili proprio quando il core non risponde, che è il
// momento in cui uno vuole sapere cosa è successo. L'analizzatore è senza stato: singleton.
builder.Services.AddSingleton<ProcioneMGR.Services.Trading.ProtectiveExitLagAnalyzer>();
builder.Services.AddScoped<ProcioneMGR.Services.Trading.ProtectiveExitDiagnosticsService>();

// [R3] Modalità Semplice (/bot): stessa granularità Scoped delle altre page service.
builder.Services.AddScoped<ProcioneMGR.Services.Risk.BotPageService>();
builder.Services.AddScoped<ProcioneMGR.Services.Pipeline.CampaignPageService>();

// Orchestrazione di MlLab.razor estratta in un service testabile (P1-5, PRD §3.3). Scoped come sopra.
builder.Services.AddScoped<ProcioneMGR.Services.ML.MlLabService>();

// Orchestrazione di Backtest.razor estratta in un service testabile (P1-5, PRD §3.3). Scoped come sopra.
builder.Services.AddScoped<ProcioneMGR.Services.Backtesting.BacktestPageService>();

// Orchestrazione di Optimization.razor estratta in un service testabile (P1-5, PRD §3.3). Scoped come sopra.
builder.Services.AddScoped<ProcioneMGR.Services.Optimization.OptimizationPageService>();

// Orchestrazione di Ensemble.razor estratta in un service testabile (P1-5, PRD §3.3). Scoped come sopra.
builder.Services.AddScoped<ProcioneMGR.Services.Ensemble.EnsemblePageService>();

// Orchestrazione di Pipeline.razor estratta in un service testabile (P1-5, PRD §3.3). Scoped come sopra.
builder.Services.AddScoped<ProcioneMGR.Services.Pipeline.PipelinePageService>();

// [R1+R2, PRD memoria-caccia 2026-08-14] Archivio candidati: indexer (tabella derivata dagli
// artifact, ricostruibile — senza stato: singleton) + orchestrazione di Research.razor (Scoped
// come le altre page service).
builder.Services.AddSingleton<ProcioneMGR.Services.Research.IResearchCandidateIndexer, ProcioneMGR.Services.Research.ResearchCandidateIndexer>();
// [I14] L'indice a righe degli artefatti "PairScreen": gli 86 blob dello screening coppie erano in
// database dal 2026-07 e nessuna query li aveva mai riletti. Singleton come il gemello: porta un
// semaforo interno, e due indicizzazioni concorrenti nello stesso processo non hanno senso.
builder.Services.AddSingleton<ProcioneMGR.Services.PairsTrading.IPairCandidateIndexer, ProcioneMGR.Services.PairsTrading.PairCandidateIndexer>();
// [J7] L'indicizzazione AUTOMATICA: l'indice era costruito e mai azionato (0 righe contro 174
// artefatti) — il braccio che lo aziona da solo, coi pulsanti della pagina per il manuale.
builder.Services.AddHostedService<ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker>();
// [I14c] La storia dello spread delle coppie sorvegliate. Lo STORE e' sempre registrato — la pagina
// deve poter leggere una storia gia' scritta anche col worker spento, che e' lo stato di fabbrica.
builder.Services.Configure<ProcioneMGR.Services.PairsTrading.PairsWatchOptions>(builder.Configuration.GetSection("PairsWatch"));
builder.Services.AddSingleton<ProcioneMGR.Services.PairsTrading.IPairSpreadHistoryStore, ProcioneMGR.Services.PairsTrading.PairSpreadHistoryStore>();
// Il WORKER e' l'unico dell'ondata che scrive in permanenza sul Postgres condiviso: si registra
// sempre ma nasce inerte (PairsWatch:Enabled=false e nessuna coppia elencata).
builder.Services.AddSingleton<ProcioneMGR.Services.PairsTrading.PairSpreadWatchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcioneMGR.Services.PairsTrading.PairSpreadWatchWorker>());
builder.Services.AddScoped<ProcioneMGR.Services.Research.ResearchPageService>();
// [K10 2026-08-31] Il gemello di J7 per l'archivio della ricerca. Fino a oggi l'indicizzatore era
// iniettato SOLO in ResearchPageService: l'indice cresceva quando un umano apriva /research, e il
// 2026-08-30 si era fermato al 25/08 con 34 run completati e non indicizzati dietro.
builder.Services.AddHostedService<ProcioneMGR.Services.Research.ResearchIndexSyncWorker>();
// [K3] La sonda di quiete: risponde a «posso fermarti adesso?» sull'endpoint /health/quiet.
builder.Services.AddSingleton<ProcioneMGR.Services.Health.ShellQuietProbe>();
// [K7/K8 — superficie UI] Il quadro dei battiti in Home: erano quattro righe che non si vedevano
// da nessuna parte in app.
builder.Services.AddSingleton<ProcioneMGR.Services.Health.HeartbeatBoardProbe>();

// [2026-08-15, revisione post-incidente 122 serie ferme] Orchestrazione di Watchlist.razor:
// timbro del ciclo di sync, freschezza per-serie sull'indice, verifica stato simboli su exchange.
// Il conteggio candele sta in una cache SINGLETON: è lo stesso numero per tutti i circuiti e
// costa secondi di database — calcolarlo per circuito, come faceva la prima versione, moltiplicava
// il costo per il numero di schede aperte (misurato nella verifica browser del 2026-08-16).
builder.Services.AddSingleton<ProcioneMGR.Services.Ingestion.SeriesCandleCountCache>();
builder.Services.AddScoped<ProcioneMGR.Services.Ingestion.WatchlistPageService>();

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

// Redirect HTTPS disattivabile via config (Fase 3): dentro il cluster il pod ui parla solo HTTP in
// chiaro dietro port-forward/Ingress. Oggi il middleware lì è di fatto inerte (nessun listener
// https configurato => logga un warning e lascia passare), ma il giorno in cui un Ingress
// terminasse TLS e inoltrasse in chiaro, il redirect incondizionato produrrebbe un loop. Meglio un
// interruttore esplicito ora (ui-config.env lo spegne nel pod) che una sorpresa dietro l'Ingress
// futuro. Default false = comportamento locale identico a prima.
if (!builder.Configuration.GetValue<bool>("Http:DisableHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

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
// [K1 2026-08-31] Insieme allo stato viaggia la REVISIONE con cui questo processo è stato
// compilato. È l'unico modo perché un sorvegliante esterno (la plancia) sappia quale codice sta
// davvero girando: leggere il binario su disco direbbe cosa è stato compilato per ultimo, non cosa
// è vivo, e i due hanno divergito per giorni. Non è un segreto — è un identificatore di build, non
// una credenziale — e l'endpoint resta anonimo per la stessa ragione per cui lo era: le probe di
// Kubernetes non possono autenticarsi. Null quando il timbro manca: mai una stringa inventata.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    revision = ProcioneMGR.Services.Health.BuildRevision.Sha,
}));

// [K3 2026-08-31] «È un buon momento per fermarmi?» — la domanda che l'aggiornamento automatico
// del guscio deve poter fare. Endpoint SEPARATO da /health di proposito: questo interroga il
// database, e la liveness non deve mai dipendere dal database — se Postgres cade, /health deve
// continuare a rispondere 200, altrimenti Kubernetes riavvia un processo sano per un guasto che sta
// altrove e il watchdog dichiara giù un guscio che sta benissimo. Anonimo per la stessa ragione di
// /health: chi lo interroga è la plancia, che non ha una sessione.
app.MapGet("/health/quiet", async (ProcioneMGR.Services.Health.ShellQuietProbe probe, CancellationToken ct) =>
{
    var v = await probe.ProbeAsync(ct);
    return Results.Ok(new { quiet = v.Quiet, reason = v.Reason });
});

// Crea i ruoli applicativi (Admin/Manager/User) all'avvio. NON applica le migrazioni, nonostante
// quanto diceva questo commento fino alla Fase 3: lo schema si applica come passo separato
// (`dotnet ef database update`, pattern migrate-on-deploy) perché l'app non referenzia l'assembly
// delle migrazioni. Vedi DbInitializer, che lo dichiara esplicitamente. Distinzione tutt'altro che
// accademica in K8s: con lo schema mancante il pod va in crash-loop su `relation "AspNetRoles" does
// not exist` e si riprende da solo appena il DB è migrato — vedi infra/k8s/README.md (Fase 3).
// Saltato sotto i tool di design-time (dotnet ef): non deve tentare di connettersi al DB mentre si
// generano migrazioni (es. verso un PostgreSQL non ancora creato).
if (!EF.IsDesignTime)
{
    // [2026-08-05] Migrate-on-startup, prima dei ruoli (che scrivono su tabelle che devono esistere).
    // Non fallisce mai l'avvio da solo: se le migrazioni non sono applicabili da questo host, o il
    // lock è occupato, lo dice a log e prosegue — il comportamento storico (migrate-on-deploy) resta
    // valido e possibile. Vedi DatabaseMigrator per il perché del lock e del caricamento per nome.
    var migrationOptions = app.Configuration.GetSection("Database").Get<DatabaseMigrationOptions>()
                           ?? new DatabaseMigrationOptions();
    var migrationLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigrator");
    try
    {
        await DatabaseMigrator.MigrateAsync(app.Services, migrationOptions, migrationLogger);
    }
    catch (Exception ex)
    {
        // Uno schema non allineato è un guasto grave, ma va DETTO, non nascosto dietro un crash
        // opaco: il passo successivo (i ruoli) fallirà con un messaggio chiaro se la causa è quella.
        migrationLogger.LogCritical(ex, "Migrazione automatica fallita. Applica lo schema a mano: dotnet ef database update.");
    }

    await DbInitializer.InitializeAsync(app.Services);

    // [Fase B] Warm-up della cache chiavi AI. `IsConfigured` è sincrono per contratto (mai I/O)
    // e la cache si caricava solo aprendo /admin/ai-supervisor: dopo OGNI riavvio l'intero layer
    // AI risultava «non configurato» — advisory in attesa e scorer LLM sul fallback — finché
    // qualcuno non apriva il pannello. Trovato dal vivo al collaudo della Fase B (batch «non
    // configurato» con la chiave NVIDIA regolarmente a database). Best-effort: un DB lento non
    // deve bloccare l'avvio, e ReloadAsync dichiara già da solo le chiavi non decifrabili.
    _ = Task.Run(async () =>
    {
        try
        {
            await app.Services.GetRequiredService<ProcioneMGR.Services.Llm.IAiKeyStore>().ReloadAsync();
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
                .LogWarning(ex, "Warm-up della cache chiavi AI fallito: il layer si configurerà alla prima apertura del pannello.");
        }
    });
}

app.Run();
