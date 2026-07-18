using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Queries;

public sealed record GetPerformanceQuery(int LaneId, DateTime? From = null) : IRequest<TradingPerformance>;

public sealed class GetPerformanceQueryHandler(IServiceProvider serviceProvider) : IRequestHandler<GetPerformanceQuery, TradingPerformance>
{
    public ValueTask<TradingPerformance> Handle(GetPerformanceQuery request, CancellationToken cancellationToken) =>
        new(serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).GetPerformanceAsync(request.From, cancellationToken));
}
