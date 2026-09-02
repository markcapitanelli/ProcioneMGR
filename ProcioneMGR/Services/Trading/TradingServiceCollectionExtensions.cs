using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Composizione DI delle corsie di trading (LaneId 0..<see cref="TradingLanes.Count"/>-1).
/// Estratta da Program.cs per essere riusata verbatim dal servizio standalone
/// <c>ProcioneMGR.Trading</c> (Fase 2b microservizi).
///
/// È QUI che vive la garanzia di sicurezza centrale della Fase 2b: il vincolo "mai due esecuzioni
/// simultanee sulla stessa corsia" non è retto da un lock distribuito ma dalla REGISTRAZIONE
/// CONDIZIONALE — con <c>Trading:UseRemoteTrading=true</c> il monolite non registra alcun
/// <see cref="TradingWorker"/>/<see cref="ExecutionWorker"/>/<see cref="TradingEngine"/> locale, e
/// l'unico processo che esegue ordini è <c>procionemgr-trading</c> (replicas:1 + Recreate, tutte
/// e 3 le lane in-process: il <see cref="System.Threading.SemaphoreSlim"/> per-istanza del motore
/// resta quindi sufficiente). I due insiemi sono mutuamente esclusivi per costruzione, non per
/// convenzione — vedi TradingServiceCollectionExtensionsTests.
///
/// Lo stesso ragionamento vale per ogni componente che SCRIVE: l'<see cref="EnsembleRebalanceWorker"/>
/// resta del solo monolite (vedi <c>isTradingServiceHost</c>). La regola generale di questa fase è
/// che ogni scrittore ha esattamente un host; ciò che è in sola lettura può stare in entrambi.
/// </summary>
public static class TradingServiceCollectionExtensions
{
    /// <summary>
    /// [2026-08-17] Le rpc di sola LETTURA del servizio di trading: prendono la deadline stretta
    /// (vedi <see cref="ProcioneMGR.Contracts.Grpc.DeadlineClientInterceptor"/>). Tutto ciò che non
    /// è qui dentro è un comando e prende quella generosa — l'elenco è per DIFETTO conservativo:
    /// una rpc nuova dimenticata qui viene trattata da comando, cioè con più tempo, mai con meno.
    /// </summary>
    private static readonly IReadOnlySet<string> RemoteReadMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetLaneStatus",
        "GetOpenPositions",
        "GetPerformance",
        "GetEngineConfig",
    };

    /// <summary>
    /// Registra, per ogni corsia: <see cref="IEnsembleManager"/> (sempre) e
    /// <see cref="ITradingEngine"/> — locale (motore reale + worker) o remoto
    /// (<see cref="RemoteTradingEngineClient"/> verso <c>procionemgr-trading</c>) a seconda di
    /// <c>Trading:UseRemoteTrading</c> (default <c>false</c> = comportamento storico) — più
    /// <see cref="EnsembleRebalanceWorker"/>, che appartiene solo all'host monolite.
    /// </summary>
    /// <param name="isTradingServiceHost">
    /// <c>true</c> solo per l'host che <em>è</em> il servizio di trading (<c>ProcioneMGR.Trading</c>).
    /// Cambia due cose, entrambe per evitare un doppio scrittore:
    /// <list type="bullet">
    /// <item>ignora <c>Trading:UseRemoteTrading</c> e registra sempre il ramo locale — questo processo
    /// è il locale, e una config condivisa col monolite (stesso file via PVC) lo farebbe altrimenti
    /// puntare a se stesso;</item>
    /// <item>NON registra <see cref="EnsembleRebalanceWorker"/> — il ribilanciamento resta del
    /// monolite (vedi sotto).</item>
    /// </list>
    /// </param>
    public static IServiceCollection AddTradingLanes(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isTradingServiceHost = false)
    {
        // Numero di corsie: letto QUI, prima di ogni registrazione keyed, perché la prima lettura di
        // TradingLanes.Count congela il valore e tutto ciò che viene dopo (motori, worker, UI,
        // validatore gRPC) vi si aggancia. Entrambi gli host chiamano questo metodo, quindi entrambi
        // vedono lo stesso numero — che è ciò che impedisce al monolite e al servizio di trading di
        // avere idee diverse su quante corsie esistano.
        TradingLanes.Configure(configuration.GetValue("Trading:LaneCount", TradingLanes.DefaultCount));

        // Lettura resiliente delle credenziali (bug B2): serve al TradingEngine (avvio Testnet/Live
        // con errore chiaro invece di AuthenticationTagMismatchException grezza) e alla pagina
        // /settings/exchanges. Registrata QUI perché è la composizione condivisa da entrambi gli
        // host che decifrano credenziali (monolite e procionemgr-trading). TryAdd: i test possono
        // sostituirla registrando prima la propria.
        services.TryAddSingleton<IExchangeCredentialReader, ExchangeCredentialReader>();

        // Fase 3-C2 (PRD Autonomia): all'avvio, se la master key non decifra le credenziali
        // esistenti lo si dichiara A VOCE ALTA (LogCritical + notifica + banner UI) invece di
        // scoprirlo da un 500 o da un avvio Testnet fallito. Sola lettura: vive in ogni host.
        services.TryAddSingleton<IMasterKeyProbe, MasterKeyProbe>();
        services.AddHostedService<MasterKeyProbeWorker>();

        // Quarantena corsie (Fase 0-A3): lo store serve a ENTRAMBI gli host (la UI del monolite
        // legge/rimuove anche in modalità remota; il watchdog scrive dove gira il motore locale).
        services.TryAddSingleton<ILaneQuarantineStore, LaneQuarantineStore>();

        // [B0 PRD core-caldo] Lease di esecuzione per corsia (advisory lock Postgres): il vincolo
        // "mai due esecutori sulla stessa corsia" — finora retto SOLO dalla registrazione
        // condizionale qui sotto e dalla disciplina di deploy — diventa invariante applicata dal
        // database. TryAdd: i test possono sostituirlo con un fake senza rete. La configuration è
        // quella PASSATA a questo metodo (come per ogni altra lettura qui dentro), non risolta dal
        // contenitore: gli host di test non registrano IConfiguration come servizio.
        services.TryAddSingleton<ILaneLeaseFactory>(sp => new NpgsqlLaneLeaseFactory(
            configuration,
            sp.GetRequiredService<ILogger<NpgsqlLaneLeaseFactory>>()));

        // Elenco corsie per il selettore della UI: sola lettura, serve a entrambe le pagine che
        // permettono di cambiare corsia (Trading, Ensemble).
        services.TryAddSingleton<ILaneDirectory, LaneDirectory>();
        services.Configure<LaneInvariantOptions>(configuration.GetSection("Trading:LaneInvariants"));

        // [Fase 2] Esposizione correlata FRA corsie: singolo servizio condiviso (il limite è
        // trasversale per definizione, e la cache delle correlazioni va condivisa). Default SPENTO
        // nelle sue opzioni: senza configurazione il comportamento è quello di prima.
        services.Configure<Risk.CorrelatedExposureOptions>(configuration.GetSection("Trading:CorrelatedExposure"));
        services.TryAddSingleton<Risk.ICorrelatedExposureGuard, Risk.CorrelatedExposureGuard>();

        // [Fase 4] Router di regime: filtra quali strategie operano nel regime corrente. Default
        // SPENTO nelle sue opzioni. Singleton condiviso: la classificazione dipende dalla serie,
        // non dalla corsia.
        services.Configure<Regime.RegimeRoutingOptions>(configuration.GetSection("Trading:RegimeRouting"));
        services.TryAddSingleton<Regime.ILaneRegimeRouter, Regime.LaneRegimeRouter>();

        var useRemote = !isTradingServiceHost && configuration.GetValue<bool>("Trading:UseRemoteTrading");


        if (useRemote)
        {
            // Un solo canale gRPC condiviso dalle 3 istanze keyed: le lane si distinguono per il
            // laneId passato in ogni request, non per connessione. Fail-fast a startup se l'URL
            // manca (stesso patto di MarketData:RemoteIngestionUrl in Fase 1): meglio non partire
            // che partire con un trading muto.
            var remoteUrl = configuration["Trading:RemoteUrl"];
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                throw new InvalidOperationException(
                    "Trading:RemoteUrl è obbligatorio quando Trading:UseRemoteTrading=true.");
            }

            // P1-6: stesso fail-fast di RemoteUrl. Il segreto deve combaciare con quello letto da
            // SharedSecretAuthInterceptor lato procionemgr-trading (K8s: STESSO Secret montato in
            // entrambi i pod, come già avviene per Security:MasterKey — vedi infra/k8s/README.md).
            var sharedSecret = configuration["Trading:GrpcSharedSecret"];
            if (string.IsNullOrWhiteSpace(sharedSecret))
            {
                throw new InvalidOperationException(
                    "Trading:GrpcSharedSecret è obbligatorio quando Trading:UseRemoteTrading=true.");
            }

            services.AddGrpcClient<Proto.TradingCommandService.TradingCommandServiceClient>(o =>
                    o.Address = new Uri(remoteUrl))
                // P3-12 (2026-07-17): il default del client (4MB) tornava insufficiente per
                // GetPerformance quando `trades` portava l'intero storico da `from` in poi — su una
                // lane Paper che gira da mesi, decine di migliaia di TradeRecord, da qui il
                // MaxReceiveMessageSize a 64MB che stava prima di questa riga. Non serve più:
                // TradingEngine.GetPerformanceAsync ora tronca `trades` ai 500 più recenti (il
                // conteggio vero resta in total_trades), quindi il payload non cresce più con l'età
                // della lane e il default gRPC basta di nuovo.
                .AddInterceptor(() => new ProcioneMGR.Contracts.Grpc.SharedSecretClientInterceptor(sharedSecret))
                // [2026-08-17] DEADLINE su ogni rpc. Senza, una chiamata poteva restare appesa
                // all'infinito (la gRPC client factory disabilita apposta il timeout dell'HttpClient,
                // perché il limite dovrebbe essere la deadline): il polling di /trading si bloccava
                // per intero e — non essendoci alcuna eccezione — il banner di dati non aggiornati
                // non compariva mai, lasciando a schermo numeri di ore prima spacciati per attuali.
                // Amministrabili da UI come ogni altra chiave (mandato 2026-08-09): pannello
                // "Motore di trading" in /admin/autonomy.
                .AddInterceptor(() => new ProcioneMGR.Contracts.Grpc.DeadlineClientInterceptor(
                    TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int>("Trading:RemoteReadTimeoutSeconds", 10))),
                    TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int>("Trading:RemoteCommandTimeoutSeconds", 60))),
                    RemoteReadMethods));

            // [2026-07-29] La configurazione ospitata dal motore si chiede AL MOTORE: il suo file
            // non è il nostro. Vedi IEngineConfigStore.
            services.TryAddSingleton<IEngineConfigStore, RemoteEngineConfigStore>();

            // [AF0] Il numero di corsie è topologia DUPLICATA nei due host (questo file e la
            // ConfigMap del motore), ognuno la congela alla prima lettura, e un disallineamento si
            // manifestava solo al primo comando fallito su una corsia alta. La sonda lo dice
            // all'avvio, a voce alta. Solo qui nel ramo remoto: in-process i due numeri escono
            // dallo stesso file per costruzione.
            services.TryAddSingleton<LaneCountCoherenceProbe>();
            services.AddHostedService<LaneCountCoherenceProbeWorker>();
        }
        else
        {
            // Motore in-process: il file di questo processo è quello che il motore legge, quindi
            // si scrive direttamente — nessuna rete, nessun caso di irraggiungibilità.
            // Il writer si registra QUI e non solo in Program.cs: chi compone le corsie in locale
            // ne ha bisogno per forza, e lasciarlo al chiamante è una dipendenza implicita che
            // esplode al primo resolve invece che alla composizione.
            services.TryAddSingleton<Config.IAppConfigWriter, Config.AppConfigWriter>();
            services.TryAddSingleton<EngineConfigService>();
            services.TryAddSingleton<IEngineConfigStore, LocalEngineConfigStore>();
        }

        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var laneId = lane;

            // Ogni host ha la PROPRIA istanza di IEnsembleManager, perché il TradingEngine la
            // richiede nel costruttore. Due istanze vive non sono un problema finché entrambe
            // LEGGONO: il motore la usa una tantum a StartAsync (GetConfigurationAsync).
            services.AddKeyedSingleton<IEnsembleManager>(laneId, (sp, _) => new EnsembleManager(
                laneId,
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IRegimeDetector>(),
                sp.GetRequiredService<IMarketFeatureExtractor>(),
                sp.GetRequiredService<Monitoring.IStrategyDecayMonitor>(),
                sp.GetRequiredService<ILogger<EnsembleManager>>(),
                // [G3, audit 2026-07-31] I backtest interni del ribilanciamento usano la fee VIVA
                // del motore (hot-reload), non più 0,1% fisso: i pesi si calcolano sui costi che
                // si pagano davvero. Composto qui perché Ensemble non importa Trading (vedi il
                // commento sul parametro).
                () => sp.GetRequiredService<IOptionsMonitor<SafetyConfiguration>>().CurrentValue.FeePercent,
                // [K54] L'evidenza successiva all'aspettativa. Opzionale nel costruttore, ma qui
                // c'è sempre: senza, il monitor di decadimento continuerebbe a giudicare contro un
                // numero che la ricerca ha già smentito undici volte.
                sp.GetService<Fleet.IExpectationEvidenceReader>()));

            if (useRemote)
            {
                // Nessun TradingWorker/ExecutionWorker qui: lo scheduling delle candele e delle
                // fette di esecuzione vive DENTRO procionemgr-trading. Registrarli anche qui
                // significherebbe due processi che aprono ordini sulla stessa corsia.
                services.AddKeyedSingleton<ITradingEngine>(laneId, (sp, _) => new RemoteTradingEngineClient(
                    laneId,
                    sp.GetRequiredService<Proto.TradingCommandService.TradingCommandServiceClient>(),
                    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                    sp.GetRequiredService<ILogger<RemoteTradingEngineClient>>()));
            }
            else
            {
                // [R3] Soglie di sicurezza EFFETTIVE della corsia: profilo di rischio sovrapposto
                // alla configurazione globale. Una istanza PER CORSIA (è dove vive il profilo);
                // implementa IOptionsMonitor<SafetyConfiguration>, quindi entra al posto del
                // monitor globale in tutti i punti di lettura senza toccarne nessuno.
                services.AddKeyedSingleton(laneId, (sp, _) =>
                    new LaneSafetyMonitor(sp.GetRequiredService<IOptionsMonitor<SafetyConfiguration>>()));

                services.AddKeyedSingleton<ITradingEngine>(laneId, (sp, _) => new TradingEngine(
                    laneId,
                    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                    sp.GetRequiredService<IStrategyFactory>(),
                    sp.GetRequiredService<ITechnicalIndicatorsService>(),
                    sp.GetRequiredService<IExchangeClientFactory>(),
                    sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
                    sp.GetRequiredKeyedService<LaneSafetyMonitor>(laneId),
                    sp.GetRequiredService<IOptionsMonitor<LiveExecutionOptions>>(),
                    sp.GetRequiredService<Execution.IExecutionAlgorithmFactory>(),
                    sp.GetRequiredService<ILogger<TradingEngine>>(),
                    sp.GetRequiredService<Observability.ProcioneMetrics>(),
                    sp.GetRequiredService<Registry.IModelRegistry>(),
                    sp.GetRequiredService<Alpha.IAlphaFactorFactory>(),
                    sp.GetRequiredService<Alpha.IFactorCache>(),
                    sp.GetRequiredService<IMasterKeyStatus>(),
                    // Dual-read ML (Fase 2a): opzionali. GetService (non Required): null se Ml:RemoteUrl non è
                    // configurato → confronto spento, comportamento identico a prima.
                    sp.GetService<ML.IMlComparisonClient>(),
                    sp.GetService<IOptionsMonitor<ML.MlComparisonOptions>>(),
                    sp.GetRequiredService<IExchangeCredentialReader>(),
                    // Lo STESSO oggetto passato come monitor delle soglie: il motore vi deposita il
                    // profilo letto dalla configurazione della corsia, e da lì in poi ogni lettura
                    // delle soglie — ovunque nella cascata — vede quelle effettive.
                    sp.GetRequiredKeyedService<LaneSafetyMonitor>(laneId),
                    // [Fase 2] Singleton condiviso fra le corsie: il limite è per definizione
                    // trasversale, e la cache delle correlazioni va condivisa, non replicata.
                    sp.GetRequiredService<Risk.ICorrelatedExposureGuard>(),
                    sp.GetRequiredService<Regime.ILaneRegimeRouter>(),
                    // [B3, sentinella] Le opzioni del feed dicono al motore se il tick DECIDE o
                    // OSSERVA. Registrate solo qui, accanto al motore locale: dove il motore non
                    // vive, il tick non arriva.
                    sp.GetRequiredService<IOptionsMonitor<MarketData.RealtimeFeedOptions>>(),
                    sp.GetRequiredService<IProtectiveExitShadowRecorder>()));

                services.AddSingleton<IHostedService>(sp => new TradingWorker(
                    sp.GetRequiredKeyedService<ITradingEngine>(laneId),
                    sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
                    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                    sp.GetRequiredService<ILogger<TradingWorker>>(),
                    sp.GetRequiredService<ILaneLeaseFactory>()));

                services.AddSingleton<IHostedService>(sp => new ExecutionWorker(
                    sp.GetRequiredKeyedService<ITradingEngine>(laneId),
                    sp.GetRequiredService<IOptionsMonitor<LiveExecutionOptions>>(),
                    sp.GetRequiredService<ILogger<ExecutionWorker>>()));
            }

            // Il RIBILANCIAMENTO dell'ensemble appartiene al solo monolite, mai al servizio di
            // trading: a differenza dell'IEnsembleManager qui sopra (che il motore usa in sola
            // lettura), questo worker SCRIVE — RebalanceAsync ricalcola e salva i pesi delle
            // strategie. Registrarlo in entrambi gli host significherebbe due processi che
            // ribilanciano la stessa corsia sullo stesso Postgres, con race sull'ultima scrittura.
            // Lasciarlo qui tiene Ensemble.razor e il ribilanciamento pienamente funzionanti anche
            // in modalità remota, dove il monolite non esegue più ordini ma resta padrone
            // dell'ensemble. NB: registrato in fondo al ciclo per conservare l'ordine di avvio
            // storico degli IHostedService (TradingWorker → ExecutionWorker → rebalance).
            if (!isTradingServiceHost)
            {
                services.AddSingleton<IHostedService>(sp => new EnsembleRebalanceWorker(
                    sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
                    sp.GetRequiredService<ILogger<EnsembleRebalanceWorker>>()));
            }
        }

        // Watchdog degli invarianti contabili (Fase 0-A3): UNO per flotta, non per corsia, e SOLO
        // nell'host dove il motore è locale — è uno scrittore (quarantena + StopAsync), e la regola
        // della Fase 2b è che ogni scrittore ha esattamente un host. In modalità remota vive quindi
        // dentro procionemgr-trading, mai nel monolite.
        // [R1] Feed di prezzo real-time. Vive nello STESSO host del motore, mai nel monolite quando
        // il trading è remoto: i tick non devono attraversare gRPC (una chiamata di rete per tick
        // reintrodurrebbe dal lato sbagliato proprio la latenza che il feed serve a togliere).
        // Stessa regola "un scrittore, un host" del watchdog qui sotto.
        //
        // La registrazione è incondizionata ma il worker si autospegne se
        // MarketData:Realtime:Enabled è false (default): a feature spenta non apre alcuna
        // connessione e la piattaforma resta sul solo percorso a candele REST.
        // Il BINDING delle opzioni del feed e della sentinella è incondizionato, la loro ESECUZIONE
        // no (vedi sotto). Motivo: col trading remoto il monolite non ospita il feed ma ne ospita
        // ancora il pannello di configurazione (/admin/protections), e un pannello che legge i
        // default invece del file mostrerebbe all'operatore uno stato che non è quello vero.
        services.Configure<MarketData.RealtimeFeedOptions>(
            configuration.GetSection(MarketData.RealtimeFeedOptions.SectionName));
        services.Configure<ProtectiveExitShadowOptions>(configuration.GetSection("Trading:ProtectiveExitShadow"));
        // Stesso motivo per il carry: il forward test gira dove gira il motore, il suo interruttore
        // sta nel guscio (/admin/autonomy).
        services.Configure<Carry.CarryOptions>(configuration.GetSection("Carry"));

        if (!useRemote)
        {
            services.TryAddSingleton<MarketData.IWebSocketTransportFactory, MarketData.ClientWebSocketTransportFactory>();
            // [B3] Sentinella d'ombra: vive dove vive il motore, come il feed che la alimenta.
            services.TryAddSingleton<IProtectiveExitShadowRecorder, ProtectiveExitShadowRecorder>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<MarketData.IExchangeStreamMapper, MarketData.BinanceStreamMapper>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<MarketData.IExchangeStreamMapper, MarketData.BitgetStreamMapper>());
            services.AddHostedService<MarketData.RealtimePriceWorker>();
        }

        if (!useRemote)
        {
            services.AddSingleton<IHostedService>(sp => new LaneInvariantWatchdog(
                sp,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<ILaneQuarantineStore>(),
                sp.GetRequiredService<IOptionsMonitor<LaneInvariantOptions>>(),
                sp.GetRequiredService<ILogger<LaneInvariantWatchdog>>(),
                sp.GetService<ProcioneMGR.Services.Notifications.INotifier>()));

            // [B3 core caldo] Forward test del carry delta-neutro: vive nell'host del MOTORE, come
            // il feed R1 e il watchdog — è operatività continua che deve sopravvivere ai riavvii
            // del guscio (PRD §3). Default OFF nelle sue opzioni, mai Live per costruzione
            // (CarryWorker rifiuta tutto tranne Paper/Testnet, e Testnet degrada a Paper). Il
            // funding che legge (SentimentMetricPoints) lo scrive il SentimentSyncWorker del
            // guscio: a guscio giù lo stato resta, le decisioni riprendono col funding fresco.
            // Il binding di CarryOptions è più su, incondizionato (serve anche al solo pannello).
            services.AddSingleton<Carry.CarryWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<Carry.CarryWorker>());
        }

        // [AF5.1] Heartbeat incrociato fra i due host. Ognuno scrive SOLO la propria riga (la
        // regola "un scrittore, un host" vale a grana di riga) e sorveglia quella altrui: il
        // motore sorveglia sempre il guscio; il guscio sorveglia il motore solo quando il motore
        // È un altro processo. Default OFF nelle opzioni: a config vuota nessuno scrive né grida.
        services.Configure<Health.HeartbeatOptions>(configuration.GetSection("Heartbeat"));
        var ownRole = isTradingServiceHost ? Data.HostHeartbeat.EngineRole : Data.HostHeartbeat.ShellRole;
        services.AddSingleton<IHostedService>(sp => new Health.HostHeartbeatWorker(
            ownRole,
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            sp.GetRequiredService<IOptionsMonitor<Health.HeartbeatOptions>>(),
            sp.GetRequiredService<ILogger<Health.HostHeartbeatWorker>>()));
        var monitoredRole = isTradingServiceHost
            ? Data.HostHeartbeat.ShellRole
            : (useRemote ? Data.HostHeartbeat.EngineRole : null);
        if (monitoredRole is not null)
        {
            services.AddSingleton<IHostedService>(sp => new Health.HeartbeatMonitorWorker(
                monitoredRole,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<IOptionsMonitor<Health.HeartbeatOptions>>(),
                sp.GetRequiredService<ILogger<Health.HeartbeatMonitorWorker>>(),
                sp.GetService<Notifications.INotifier>()));
        }

        // Fallback non-keyed: risolve sempre la corsia 0. Serve ai consumer non ancora aggiornati con
        // un selettore di corsia esplicito (dashboard, retraining regime, applicazione raccomandazioni
        // pipeline) - comportamento identico a prima dell'introduzione delle corsie multiple.
        services.AddSingleton<IEnsembleManager>(sp => sp.GetRequiredKeyedService<IEnsembleManager>(0));
        services.AddSingleton<ITradingEngine>(sp => sp.GetRequiredKeyedService<ITradingEngine>(0));

        return services;
    }
}
