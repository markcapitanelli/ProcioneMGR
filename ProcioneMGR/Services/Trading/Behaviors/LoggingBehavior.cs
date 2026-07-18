using Mediator;

namespace ProcioneMGR.Services.Trading.Behaviors;

/// <summary>
/// Punto unico di logging per ogni comando/query di trading (Fase 1, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §4.5): sostituisce, mano a mano che i verbi migrano a Mediator, le chiamate
/// <c>logger.LogInformation</c>/<c>LogWarning</c> oggi sparse nei singoli metodi di
/// <see cref="TradingEngine"/>.
/// </summary>
public sealed class LoggingBehavior<TMessage, TResponse>(ILogger<LoggingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trading {RequestType} avviato: {@Request}", typeof(TMessage).Name, message);
        try
        {
            var response = await next(message, cancellationToken);
            logger.LogInformation("Trading {RequestType} completato", typeof(TMessage).Name);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trading {RequestType} fallito", typeof(TMessage).Name);
            throw;
        }
    }
}
