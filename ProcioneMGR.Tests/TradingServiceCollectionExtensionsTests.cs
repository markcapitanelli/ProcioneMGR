using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA GARANZIA DI SICUREZZA CENTRALE DELLA FASE 2b. Il vincolo "mai due esecuzioni simultanee sulla
/// stessa corsia" non Ã¨ retto da un lock distribuito, ma dal fatto che monolite e servizio remoto
/// non registrano MAI entrambi un motore attivo per la stessa lane. Qui lo si verifica per
/// COSTRUZIONE (composizione DI, deterministica e istantanea) invece che a runtime con due processi
/// vivi â€” un test del genere sarebbe lento, e soprattutto fallirebbe a intermittenza proprio nello
/// scenario che deve escludere con certezza.
///
/// Si RISOLVONO davvero le istanze invece di ispezionare i ServiceDescriptor: le registrazioni sono
/// factory lambda, quindi il descriptor non espone il tipo concreto e un test su di essi passerebbe
/// anche se la factory costruisse la classe sbagliata.
/// </summary>
public class TradingServiceCollectionExtensionsTests
{
    private sealed class FakeMasterKeyStatus : IMasterKeyStatus
    {
        public bool IsDefaultDevKey => false;
    }

    /// <summary>
    /// Il cono di dipendenze del TradingEngine, registrato come nell'host reale. Connection string
    /// fittizia: Npgsql non si connette a startup e nessun test qui tocca il DB.
    /// </summary>
    private static ServiceProvider BuildProvider(bool useRemoteTrading, bool isTradingServiceHost = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:UseRemoteTrading"] = useRemoteTrading ? "true" : "false",
            ["Trading:RemoteUrl"] = "http://trading.local",
            ["Trading:GrpcSharedSecret"] = "test-only-shared-secret",
            ["ConnectionStrings:PostgresConnection"] = "Host=localhost;Database=unused;Username=x;Password=x",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<SafetyConfiguration>(config.GetSection("Trading:Safety"));
        services.Configure<LiveExecutionOptions>(config.GetSection("Trading:LiveExecution"));

        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddSingleton<IMasterKeyStatus, FakeMasterKeyStatus>();
        services.AddProcioneDatabase(config);
        services.AddExchangeClients();

        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IMarketFeatureExtractor, MarketFeatureExtractor>();
        services.AddSingleton<IMarketBreadthCalculator, MarketBreadthCalculator>();
        services.AddSingleton<IRegimeDetector, RegimeDetector>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddSingleton<IStrategyDecayMonitor, StrategyDecayMonitor>();
        services.AddSingleton<IExecutionAlgorithmFactory, ExecutionAlgorithmFactory>();
        services.AddSingleton<IFactorCache>(_ => new FactorCache(new FactorCacheOptions()));
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        services.AddSingleton(new ModelRegistryOptions());
        services.AddSingleton<IModelRegistry, ModelRegistry>();
        services.AddSingleton<ProcioneMetrics>();

        services.AddTradingLanes(config, isTradingServiceHost);

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void ToggleOff_RegistersLocalEngineAndWorkers_ForEveryLane()
    {
        using var sp = BuildProvider(useRemoteTrading: false);

        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var engine = sp.GetRequiredKeyedService<ITradingEngine>(lane);
            Assert.IsType<TradingEngine>(engine);
            Assert.Equal(lane, engine.LaneId);
        }

        // Il default (toggle assente/false) deve restare bit-identico al comportamento storico:
        // per ogni corsia un TradingWorker, un ExecutionWorker e un EnsembleRebalanceWorker.
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Equal(TradingLanes.Count, hosted.OfType<TradingWorker>().Count());
        Assert.Equal(TradingLanes.Count, hosted.OfType<ExecutionWorker>().Count());
        Assert.Equal(TradingLanes.Count, hosted.OfType<EnsembleRebalanceWorker>().Count());
    }

    [Fact]
    public void ToggleOn_ReplacesEveryLaneEngineWithRemoteClient()
    {
        using var sp = BuildProvider(useRemoteTrading: true);

        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            var engine = sp.GetRequiredKeyedService<ITradingEngine>(lane);
            Assert.IsType<RemoteTradingEngineClient>(engine);
            Assert.Equal(lane, engine.LaneId);
        }
    }

    [Fact]
    public void ToggleOn_RegistersNoLocalTradingWorkers()
    {
        // IL CUORE DEL TEST: se un TradingWorker o un ExecutionWorker sopravvivesse qui, il monolite
        // elaborerebbe candele e fette di esecuzione in parallelo al servizio remoto â€” due processi
        // che aprono ordini sulla stessa corsia, con soldi veri in Live. Deve essere l'insieme vuoto.
        using var sp = BuildProvider(useRemoteTrading: true);

        var hosted = sp.GetServices<IHostedService>().ToList();

        Assert.Empty(hosted.OfType<TradingWorker>());
        Assert.Empty(hosted.OfType<ExecutionWorker>());
    }

    [Fact]
    public void EnsembleRebalanceStaysInTheMonolith_RegardlessOfToggle()
    {
        // L'ensemble non segue mai il trading nel servizio remoto: il motore lo legge una tantum a
        // StartAsync, quindi due istanze non possono produrre doppie scritture, e tenerlo qui lascia
        // Ensemble.razor e il ribilanciamento funzionanti anche in modalitÃ  remota.
        foreach (var useRemote in new[] { false, true })
        {
            using var sp = BuildProvider(useRemote);

            for (var lane = 0; lane < TradingLanes.Count; lane++)
            {
                Assert.IsType<EnsembleManager>(sp.GetRequiredKeyedService<IEnsembleManager>(lane));
            }
            Assert.Equal(TradingLanes.Count, sp.GetServices<IHostedService>().OfType<EnsembleRebalanceWorker>().Count());
        }
    }

    [Fact]
    public void TradingServiceHost_RegistersNoEnsembleRebalanceWorker()
    {
        // L'ALTRA METÃ€ DELLA GARANZIA ANTI-DOPPIO-SCRITTORE, accanto ai TradingWorker. A differenza
        // dell'IEnsembleManager (che il motore usa in SOLA LETTURA, e che quindi puÃ² esistere in
        // entrambi gli host), EnsembleRebalanceWorker SCRIVE: RebalanceAsync ricalcola e salva i pesi
        // delle strategie. Se fosse registrato anche qui, in modalitÃ  remota monolite e servizio
        // ribilancerebbero la stessa corsia sullo stesso Postgres, in race fra loro.
        using var sp = BuildProvider(useRemoteTrading: false, isTradingServiceHost: true);

        Assert.Empty(sp.GetServices<IHostedService>().OfType<EnsembleRebalanceWorker>());

        // Ma il manager (sola lettura) deve esserci: il TradingEngine lo richiede nel costruttore.
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            Assert.IsType<EnsembleManager>(sp.GetRequiredKeyedService<IEnsembleManager>(lane));
            Assert.IsType<TradingEngine>(sp.GetRequiredKeyedService<ITradingEngine>(lane));
        }
    }

    [Fact]
    public void TradingServiceHost_IgnoresTheToggle_AndRunsTheEngineItself()
    {
        // Monolite e servizio condividono il file di configurazione (PVC), dove UseRemoteTrading=true:
        // senza questo vincolo il servizio di trading punterebbe a se stesso e nessuno eseguirebbe.
        using var sp = BuildProvider(useRemoteTrading: true, isTradingServiceHost: true);

        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            Assert.IsType<TradingEngine>(sp.GetRequiredKeyedService<ITradingEngine>(lane));
        }
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Equal(TradingLanes.Count, hosted.OfType<TradingWorker>().Count());
        Assert.Equal(TradingLanes.Count, hosted.OfType<ExecutionWorker>().Count());
    }

    [Fact]
    public void RealtimeFeed_LivesOnlyWhereTheEngineIsLocal()
    {
        // [R1] Stessa regola "un scrittore, un host" degli altri componenti attivi. Il feed non puÃ²
        // stare nel monolite quando il trading Ã¨ remoto per DUE motivi, entrambi sufficienti:
        //  - i tick dovrebbero attraversare gRPC, reintroducendo dal lato sbagliato proprio la
        //    latenza che il feed serve a togliere;
        //  - RemoteTradingEngineClient.ProcessPriceTickAsync LANCIA di proposito, quindi un feed
        //    registrato lÃ¬ produrrebbe un fiume di eccezioni invece di chiudere posizioni.
        using (var remote = BuildProvider(useRemoteTrading: true))
        {
            Assert.Empty(remote.GetServices<IHostedService>().OfType<ProcioneMGR.Services.MarketData.RealtimePriceWorker>());
        }

        using (var local = BuildProvider(useRemoteTrading: false))
        {
            Assert.Single(local.GetServices<IHostedService>().OfType<ProcioneMGR.Services.MarketData.RealtimePriceWorker>());
        }

        // Il servizio di trading standalone Ãˆ l'host locale: lÃ¬ il feed deve esserci.
        using (var tradingHost = BuildProvider(useRemoteTrading: true, isTradingServiceHost: true))
        {
            Assert.Single(tradingHost.GetServices<IHostedService>().OfType<ProcioneMGR.Services.MarketData.RealtimePriceWorker>());
        }
    }

    [Fact]
    public void RealtimeFeed_IsOneForTheFleet_NotOnePerLane()
    {
        // Una connessione WebSocket per exchange, condivisa da tutte le corsie. Registrarne una per
        // corsia aprirebbe TradingLanes.Count connessioni identiche allo stesso stream: spreco e
        // rumore verso l'exchange, senza alcun beneficio.
        using var sp = BuildProvider(useRemoteTrading: false);

        Assert.Single(sp.GetServices<IHostedService>().OfType<ProcioneMGR.Services.MarketData.RealtimePriceWorker>());
        Assert.Equal(2, sp.GetServices<ProcioneMGR.Services.MarketData.IExchangeStreamMapper>().Count()); // Binance + Bitget
    }

    [Fact]
    public void ToggleOff_StartsWorkersInTheHistoricalOrder()
    {
        // Gli IHostedService partono in ordine di registrazione. Il ramo locale Ã¨ un'estrazione a
        // comportamento invariato del vecchio loop di Program.cs: l'ordine per corsia era
        // TradingWorker â†’ ExecutionWorker â†’ EnsembleRebalanceWorker, e deve restare tale.
        using var sp = BuildProvider(useRemoteTrading: false);

        var order = sp.GetServices<IHostedService>()
            .Where(h => h is TradingWorker or ExecutionWorker or EnsembleRebalanceWorker)
            .Select(h => h.GetType().Name)
            .ToList();

        Assert.Equal(
            Enumerable.Range(0, TradingLanes.Count)
                .SelectMany(_ => new[] { nameof(TradingWorker), nameof(ExecutionWorker), nameof(EnsembleRebalanceWorker) })
                .ToList(),
            order);
    }

    [Fact]
    public void NonKeyedFallback_ResolvesLaneZero_InBothModes()
    {
        // Consumer storici senza selettore di corsia (dashboard, pipeline) risolvono ITradingEngine
        // non-keyed: deve restare la corsia 0 in entrambe le modalitÃ .
        using var local = BuildProvider(useRemoteTrading: false);
        Assert.Equal(0, local.GetRequiredService<ITradingEngine>().LaneId);
        Assert.Same(local.GetRequiredKeyedService<ITradingEngine>(0), local.GetRequiredService<ITradingEngine>());

        using var remote = BuildProvider(useRemoteTrading: true);
        Assert.Equal(0, remote.GetRequiredService<ITradingEngine>().LaneId);
        Assert.Same(remote.GetRequiredKeyedService<ITradingEngine>(0), remote.GetRequiredService<ITradingEngine>());
    }

    [Fact]
    public void ToggleOn_WithoutRemoteUrl_FailsFast()
    {
        // Meglio non partire che partire con un trading muto: senza URL il client remoto non saprebbe
        // dove chiamare, e ogni comando fallirebbe solo al primo uso (magari un emergency stop).
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:UseRemoteTrading"] = "true",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTradingLanes(config));
        Assert.Contains("Trading:RemoteUrl", ex.Message);
    }

    [Fact]
    public void ToggleOn_WithoutGrpcSharedSecret_FailsFast()
    {
        // P1-6: stesso principio fail-fast di RemoteUrl. Senza il segreto il client remoto
        // partirebbe e ogni chiamata fallirebbe solo al primo uso, rifiutata dal
        // SharedSecretAuthInterceptor lato servizio â€” meglio non partire.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:UseRemoteTrading"] = "true",
            ["Trading:RemoteUrl"] = "http://trading.local",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTradingLanes(config));
        Assert.Contains("Trading:GrpcSharedSecret", ex.Message);
    }

    [Fact]
    public void TradingServiceHost_RegistersNoGrpcClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:UseRemoteTrading"] = "true",
            ["Trading:RemoteUrl"] = "http://trading.local",
        }).Build();

        var services = new ServiceCollection();
        services.AddTradingLanes(config, isTradingServiceHost: true);

        // Nessun client gRPC registrato = nessuna delega remota: questo host esegue in proprio.
        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(ProcioneMGR.Contracts.Trading.V1.TradingCommandService.TradingCommandServiceClient));
    }

    [Fact]
    public void TradingServiceHost_WithoutRemoteUrl_DoesNotFailFast()
    {
        // Il fail-fast su Trading:RemoteUrl riguarda solo chi delega. Il servizio di trading non
        // delega a nessuno: se pretendesse un RemoteUrl, non partirebbe per la mancanza di un
        // indirizzo che non userebbe mai.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:UseRemoteTrading"] = "true",
        }).Build();

        var ex = Record.Exception(() => new ServiceCollection().AddTradingLanes(config, isTradingServiceHost: true));

        Assert.Null(ex);
    }
}
