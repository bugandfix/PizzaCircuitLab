using Microsoft.AspNetCore.Components.Server.Circuits;

namespace PizzaCircuitLab.Web.Services;

/// <summary>Feeds framework circuit lifecycle events into the <see cref="CircuitRegistry"/>.</summary>
public sealed class PizzaCircuitHandler(CircuitRegistry registry, CircuitSession session) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var handle = new FrameworkCircuitHandle(circuit);
        session.Circuit = handle;
        registry.CircuitOpened(handle);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        registry.ConnectionUp(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        registry.ConnectionDown(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        registry.CircuitClosed(circuit.Id);
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => context =>
    {
        registry.Touch(context.Circuit.Id);
        return next(context);
    };
}
