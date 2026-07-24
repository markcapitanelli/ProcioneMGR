using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Infrastruttura di ingestione OHLCV condivisa: client exchange (sorgente dei dati) +
/// <see cref="IOhlcvIngestionService"/>. Estratta da Program.cs per essere riusata verbatim
/// dal servizio standalone <c>ProcioneMGR.Ingestion</c> (Fase 1 microservizi).
///
/// NON registra <see cref="IMarketDataSyncService"/> nè il worker schedulato
/// (<see cref="MarketDataSyncWorker"/>): quella è la parte che il feature toggle
/// <c>MarketData:UseRemoteIngestion</c> commuta tra implementazione locale e remota, quindi
/// resta responsabilità dell'host. I client exchange e <see cref="IOhlcvIngestionService"/>
/// invece servono sempre (trading, pipeline, dashboard li usano a prescindere dal toggle).
/// </summary>
public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Solo i client exchange (Binance/Bitget + <see cref="IExchangeClientFactory"/>), senza
    /// <see cref="IOhlcvIngestionService"/>. Estratto da <see cref="AddOhlcvIngestion"/> per
    /// <c>ProcioneMGR.Trading</c> (Fase 2b), che deve firmare le chiamate Testnet/Live ma non
    /// ingerisce candele: trascinare l'ingestione nel suo host sarebbe una dipendenza inutile.
    /// </summary>
    public static IServiceCollection AddExchangeClients(this IServiceCollection services)
    {
        // [R1] Orologio allineato ai server degli exchange: evita che una deriva dell'orologio
        // locale faccia rifiutare richieste firmate valide (Binance -1021, "timestamp outside of
        // recvWindow"). Parte a offset zero, quindi finché il worker non ha misurato il
        // comportamento è quello storico.
        services.TryAddSingleton<IExchangeClock, ExchangeClock>();
        services.AddHttpClient();   // IHttpClientFactory per la sonda di ora del server
        services.AddHostedService<ExchangeClockSyncWorker>();

        // [R1] Disciplina di rate-limit su OGNI chiamata REST: token bucket proattivo + ritiro su
        // 429/418 rispettando Retry-After. Come DelegatingHandler vale per tutti i punti di
        // chiamata senza doverli toccare uno per uno.
        services.AddTransient<ExchangeRateLimitHandler>();

        // I client sono typed HttpClient: base address e User-Agent centralizzati qui.
        services.AddHttpClient<BinanceClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.binance.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
        }).AddHttpMessageHandler<ExchangeRateLimitHandler>();
        services.AddHttpClient<BitgetClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.bitget.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProcioneMGR/1.0");
        }).AddHttpMessageHandler<ExchangeRateLimitHandler>();
        services.AddSingleton<IExchangeClientFactory, ExchangeClientFactory>();

        return services;
    }

    public static IServiceCollection AddOhlcvIngestion(this IServiceCollection services)
    {
        services.AddExchangeClients();

        // Servizio di ingestione OHLCV (usato anche da pipeline e dashboard, non solo dalla sync).
        services.AddScoped<IOhlcvIngestionService, OhlcvIngestionService>();

        return services;
    }
}
