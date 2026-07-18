using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record StartLaneCommand(int LaneId, TradingMode Mode) : IRequest;

public sealed class StartLaneCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<StartLaneCommand>
{
    public async ValueTask<Unit> Handle(StartLaneCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).StartAsync(request.Mode, cancellationToken);
        return Unit.Value;
    }
}
