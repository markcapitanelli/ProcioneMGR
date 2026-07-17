using Microsoft.EntityFrameworkCore;
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
                .AddInterceptor(() => new ProcioneMGR.Contracts.Grpc.SharedSecretClientInterceptor(sharedSecret));
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
                sp.GetRequiredService<ILogger<EnsembleManager>>()));

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
                services.AddKeyedSingleton<ITradingEngine>(laneId, (sp, _) => new TradingEngine(
                    laneId,
                    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                    sp.GetRequiredService<IStrategyFactory>(),
                    sp.GetRequiredService<ITechnicalIndicatorsService>(),
                    sp.GetRequiredService<IExchangeClientFactory>(),
                    sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
                    sp.GetRequiredService<IOptionsMonitor<SafetyConfiguration>>(),
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
                    sp.GetService<IOptionsMonitor<ML.MlComparisonOptions>>()));

                services.AddSingleton<IHostedService>(sp => new TradingWorker(
                    sp.GetRequiredKeyedService<ITradingEngine>(laneId),
                    sp.GetRequiredKeyedService<IEnsembleManager>(laneId),
                    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                    sp.GetRequiredService<ILogger<TradingWorker>>()));

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

        // Fallback non-keyed: risolve sempre la corsia 0. Serve ai consumer non ancora aggiornati con
        // un selettore di corsia esplicito (dashboard, retraining regime, applicazione raccomandazioni
        // pipeline) - comportamento identico a prima dell'introduzione delle corsie multiple.
        services.AddSingleton<IEnsembleManager>(sp => sp.GetRequiredKeyedService<IEnsembleManager>(0));
        services.AddSingleton<ITradingEngine>(sp => sp.GetRequiredKeyedService<ITradingEngine>(0));

        return services;
    }
}
