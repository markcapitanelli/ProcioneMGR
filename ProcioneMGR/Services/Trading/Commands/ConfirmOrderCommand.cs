using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record ConfirmOrderCommand(int LaneId, string OrderId, string? UserId) : IRequest;

public sealed class ConfirmOrderCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<ConfirmOrderCommand>
{
    public async ValueTask<Unit> Handle(ConfirmOrderCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).ConfirmOrderAsync(request.OrderId, request.UserId, cancellationToken);
        return Unit.Value;
    }
}
