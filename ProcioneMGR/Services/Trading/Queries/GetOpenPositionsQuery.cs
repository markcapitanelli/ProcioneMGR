using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Queries;

public sealed record GetOpenPositionsQuery(int LaneId) : IRequest<List<OpenPosition>>;

public sealed class GetOpenPositionsQueryHandler(IServiceProvider serviceProvider) : IRequestHandler<GetOpenPositionsQuery, List<OpenPosition>>
{
    public ValueTask<List<OpenPosition>> Handle(GetOpenPositionsQuery request, CancellationToken cancellationToken) =>
        new(serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).GetOpenPositionsAsync(cancellationToken));
}
