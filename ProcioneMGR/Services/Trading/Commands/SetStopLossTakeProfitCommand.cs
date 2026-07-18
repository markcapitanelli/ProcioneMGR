using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record SetStopLossTakeProfitCommand(
    int LaneId,
    string PositionId,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? TrailingStopPercent = null) : IRequest;

public sealed class SetStopLossTakeProfitCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<SetStopLossTakeProfitCommand>
{
    public async ValueTask<Unit> Handle(SetStopLossTakeProfitCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId)
            .SetStopLossTakeProfitAsync(request.PositionId, request.StopLoss, request.TakeProfit, request.TrailingStopPercent, cancellationToken);
        return Unit.Value;
    }
}
