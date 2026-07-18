using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Queries;

public sealed record GetPendingOrdersQuery(int LaneId) : IRequest<List<Order>>;

public sealed class GetPendingOrdersQueryHandler(IServiceProvider serviceProvider) : IRequestHandler<GetPendingOrdersQuery, List<Order>>
{
    public ValueTask<List<Order>> Handle(GetPendingOrdersQuery request, CancellationToken cancellationToken) =>
        new(serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).GetPendingOrdersAsync(cancellationToken));
}
