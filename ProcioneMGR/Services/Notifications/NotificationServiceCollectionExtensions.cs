using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// Composizione DI del canale di notifica (Fase 4, PRD Autonomia): condivisa dagli host che
/// hanno producer (monolite; ProcioneMGR.Trading per il watchdog in modalità remota).
/// TryAdd ovunque: i test possono sostituire il notifier registrando prima il proprio fake.
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddProcioneNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection("Notifications"));
        // L'API Telegram vuole il token del bot NEL PATH dell'URL (/bot<token>/sendMessage) e il
        // logging di default di HttpClientFactory scrive l'URI completo a Information: senza
        // RemoveAllLoggers il token finirebbe nei log di sviluppo. Il filtro in Logging:LogLevel
        // ("System.Net.Http.HttpClient.telegram-notifier") resta solo come difesa in profondità.
        services.AddHttpClient(TelegramNotifier.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, LoggingNotifier>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, TelegramNotifier>());
        services.TryAddSingleton<INotifier, NotificationDispatcher>();
        return services;
    }
}
