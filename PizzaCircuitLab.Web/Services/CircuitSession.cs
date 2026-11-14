using Microsoft.AspNetCore.Components.Server.Circuits;

namespace PizzaCircuitLab.Web.Services;

/// <summary>
/// The registry's view of a live circuit. An abstraction over the sealed framework
/// <see cref="Circuit"/> type so the registry can be unit tested.
/// </summary>
public interface ICircuitHandle
{
    /// <summary>The public circuit identifier (<see cref="Circuit.Id"/>); not a SignalR connection ID.</summary>
    string Id { get; }

    /// <summary>Asks the connected browser to begin the framework's graceful pause flow.</summary>
    Task<bool> RequestPauseAsync(CancellationToken cancellationToken);
}

public sealed class FrameworkCircuitHandle(Circuit circuit) : ICircuitHandle
{
    public string Id => circuit.Id;

    public Task<bool> RequestPauseAsync(CancellationToken cancellationToken) =>
        circuit.RequestCircuitPauseAsync(cancellationToken);
}

/// <summary>
/// Scoped per circuit. <see cref="Circuit"/> is null while prerendering over plain HTTP
/// and set by <see cref="PizzaCircuitHandler"/> once an interactive circuit exists.
/// </summary>
public sealed class CircuitSession
{
    public ICircuitHandle? Circuit { get; set; }
}
