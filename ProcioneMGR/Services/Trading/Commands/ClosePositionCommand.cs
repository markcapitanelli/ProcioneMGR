using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record ClosePositionCommand(int LaneId, string PositionId) : IRequest;

public sealed class ClosePositionCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<ClosePositionCommand>
{
    public async ValueTask<Unit> Handle(ClosePositionCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).ClosePositionAsync(request.PositionId, cancellationToken);
        return Unit.Value;
    }
}
