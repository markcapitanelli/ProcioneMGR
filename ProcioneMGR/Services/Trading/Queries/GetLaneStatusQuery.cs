using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Queries;

public sealed record GetLaneStatusQuery(int LaneId) : IRequest<TradingEngineStatus>;

public sealed class GetLaneStatusQueryHandler(IServiceProvider serviceProvider) : IRequestHandler<GetLaneStatusQuery, TradingEngineStatus>
{
    public ValueTask<TradingEngineStatus> Handle(GetLaneStatusQuery request, CancellationToken cancellationToken) =>
        new(serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).GetStatusAsync(cancellationToken));
}
