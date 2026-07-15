using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// Smoke test del wiring DI di <see cref="IngestionServiceCollectionExtensions.AddOhlcvIngestion"/>
/// (Fase 1 microservizi), stesso stile di ObservabilityWiringTests: verifica che l'infrastruttura
/// condivisa di ingestione si registri e si risolva senza dipendere da un DB reale.
/// </summary>
public class AddOhlcvIngestionTests
{
    [Fact]
    public void AddOhlcvIngestion_RegistersExchangeClientsAndFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOhlcvIngestion();

        using var provider = services.BuildServiceProvider();

        // La factory e i client exchange (sorgente OHLCV) si risolvono senza DB.
        var factory = provider.GetRequiredService<IExchangeClientFactory>();
        Assert.NotNull(factory.Create(ExchangeName.Bitget));
        Assert.NotNull(factory.Create(ExchangeName.Binance));
    }

    [Fact]
    public void AddOhlcvIngestion_RegistersIngestionService_WithoutSyncServiceOrWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOhlcvIngestion();

        // IOhlcvIngestionService è registrato (dipende da IDbContextFactory, quindi non lo
        // risolviamo qui: basta il descriptor). La sync service e il worker restano fuori:
        // sono la parte commutata dal toggle, responsabilità dell'host.
        Assert.Contains(services, d => d.ServiceType == typeof(IOhlcvIngestionService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMarketDataSyncService));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(MarketDataSyncWorker));
    }
}
