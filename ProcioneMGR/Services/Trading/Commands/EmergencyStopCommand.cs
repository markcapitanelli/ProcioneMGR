using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading.Commands;

public sealed record EmergencyStopCommand(int LaneId, string Reason) : IRequest;

public sealed class EmergencyStopCommandHandler(IServiceProvider serviceProvider) : IRequestHandler<EmergencyStopCommand>
{
    public async ValueTask<Unit> Handle(EmergencyStopCommand request, CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId).EmergencyStopAsync(request.Reason, cancellationToken);
        return Unit.Value;
    }
}
