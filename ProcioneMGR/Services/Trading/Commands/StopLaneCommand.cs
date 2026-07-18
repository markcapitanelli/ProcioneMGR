using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record StopLaneCommand(int LaneId) : IRequest;

public sealed class StopLaneCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<StopLaneCommand>
{
    public async ValueTask<Unit> Handle(StopLaneCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).StopAsync(cancellationToken);
        return Unit.Value;
    }
}
