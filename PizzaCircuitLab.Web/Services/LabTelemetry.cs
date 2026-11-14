using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PizzaCircuitLab.Web.Services;

/// <summary>Custom metrics and traces for the lab, exported to the Aspire Dashboard over OTLP.</summary>
public sealed class LabTelemetry : IDisposable
{
    public const string Name = "PizzaCircuitLab";

    public static readonly ActivitySource Activities = new(Name);

    private readonly Meter _meter;
    private readonly Counter<long> _circuitsOpened;
    private readonly Counter<long> _circuitsClosed;
    private readonly Counter<long> _pauses;
    private readonly Counter<long> _resumes;
    private readonly Counter<long> _adminCommands;
    private readonly Counter<long> _adminRejected;
    private Func<IEnumerable<Measurement<int>>>? _sessionCounts;

    public LabTelemetry(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(Name);
        _circuitsOpened = _meter.CreateCounter<long>("pizzalab.circuits.opened", description: "Circuits created (first load or resume).");
        _circuitsClosed = _meter.CreateCounter<long>("pizzalab.circuits.closed", description: "Circuits disposed, tagged by outcome (paused or closed).");
        _pauses = _meter.CreateCounter<long>("pizzalab.circuit.pauses", description: "Completed circuit pauses, tagged by reason.");
        _resumes = _meter.CreateCounter<long>("pizzalab.circuit.resumes", description: "Sessions restored into a new circuit.");
        _adminCommands = _meter.CreateCounter<long>("pizzalab.admin.commands", description: "Administrative commands executed, tagged by command and outcome.");
        _adminRejected = _meter.CreateCounter<long>("pizzalab.admin.rejected", description: "Administrative requests rejected before execution, tagged by reason.");
        _meter.CreateObservableGauge("pizzalab.sessions", () => _sessionCounts?.Invoke() ?? [],
            description: "Tracked sessions by state.");
    }

    public void SetSessionCountProvider(Func<IEnumerable<Measurement<int>>> provider) => _sessionCounts = provider;

    public void CircuitOpened() => _circuitsOpened.Add(1);
    public void CircuitClosed(string outcome) => _circuitsClosed.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public void Paused(string reason) => _pauses.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void Resumed() => _resumes.Add(1);

    public void AdminCommand(string command, string outcome) => _adminCommands.Add(1,
        new KeyValuePair<string, object?>("command", command),
        new KeyValuePair<string, object?>("outcome", outcome));

    public void AdminRejected(string reason) => _adminRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose() => _meter.Dispose();
}
