using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Queries;

public sealed record GetOrderHistoryQuery(int LaneId, DateTime? From = null) : IRequest<List<Order>>;

public sealed class GetOrderHistoryQueryHandler(IServiceProvider serviceProvider) : IRequestHandler<GetOrderHistoryQuery, List<Order>>
{
    public ValueTask<List<Order>> Handle(GetOrderHistoryQuery request, CancellationToken cancellationToken) =>
        new(serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).GetOrderHistoryAsync(request.From, cancellationToken));
}
