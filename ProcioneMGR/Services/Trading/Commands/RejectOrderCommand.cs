using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record RejectOrderCommand(int LaneId, string OrderId, string? UserId) : IRequest;

public sealed class RejectOrderCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<RejectOrderCommand>
{
    public async ValueTask<Unit> Handle(RejectOrderCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).RejectOrderAsync(request.OrderId, request.UserId, cancellationToken);
        return Unit.Value;
    }
}
