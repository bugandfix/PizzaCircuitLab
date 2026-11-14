using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace PizzaCircuitLab.Web.Services;

public enum SessionState
{
    /// <summary>A circuit exists in server memory and its SignalR connection is up.</summary>
    Connected,
    /// <summary>The circuit is retained in server memory but its connection is down; the same circuit can still reconnect.</summary>
    Disconnected,
    /// <summary>State was persisted and the circuit disposed. Only the browser can resume, and doing so creates a NEW circuit.</summary>
    Paused,
    /// <summary>The circuit was disposed without persisting state. Nothing to resume.</summary>
    Closed
}

public enum PauseReason
{
    None,
    /// <summary>Requested from the Aspire Dashboard (server-initiated).</summary>
    Administrative,
    /// <summary>Initiated by the browser while connected; in this app only the hidden-tab auto-pause does that.</summary>
    TabAutoPause,
    /// <summary>The framework persisted the circuit when evicting it after a lost connection.</summary>
    DisconnectedEviction
}

/// <summary>Point-in-time, serialisable view of one tracked session/circuit.</summary>
public sealed record CircuitInfo(
    string? SessionId,
    string CircuitId,
    int Generation,
    SessionState State,
    PauseReason PauseReason,
    bool AdminHold,
    bool ResumeRequested,
    string? RestoredFromCircuitId,
    string TabVisibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? StatePersistedAt,
    DateTimeOffset? PausedAt,
    DateTimeOffset? LastRestoredAt,
    DateTimeOffset? ClosedAt);

public sealed record RegistrySnapshot(
    DateTimeOffset GeneratedAt,
    string Instance,
    int Connected,
    int Disconnected,
    int Paused,
    int Closed,
    IReadOnlyList<CircuitInfo> Circuits);

/// <summary>What a paused browser tab is told when it asks whether it may resume.</summary>
public sealed record SessionDirective(bool Known, bool AdminHold, bool ResumeRequested);

public sealed record AttachResult(int Generation, string? RestoredFromCircuitId, DateTimeOffset? RestoredAt);

/// <summary>
/// One circuit in the life of a session. <see cref="Ending"/> is null while the circuit is live,
/// otherwise a <see cref="PauseReason"/> name or "Closed".
/// </summary>
public sealed record CircuitHistoryEntry(int Generation, string CircuitId, DateTimeOffset OpenedAt, DateTimeOffset? EndedAt, string? Ending, int Reconnects);

/// <summary>Result of applying an administrative command to one session.</summary>
public sealed record TargetOutcome(string? SessionId, string CircuitId, string Outcome, string Detail);

public enum LookupStatus { Found, NotFound, Ambiguous }

/// <summary>
/// In-memory, per-process registry of the circuits this instance has seen.
/// <para>
/// A paused circuit is disposed by the framework and resuming creates a new circuit with a new ID,
/// so the registry tracks <em>sessions</em>: a stable ID persisted with the component state that links
/// the successive circuits of one browser page.
/// </para>
/// </summary>
public sealed class CircuitRegistry
{
    private static readonly TimeSpan[] s_holdEnforcementDelays =
    [
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)
    ];

    private const int MaxHistory = 12;

    private sealed class HistoryItem
    {
        public int Generation;
        public required string CircuitId;
        public DateTimeOffset OpenedAt;
        public DateTimeOffset? EndedAt;
        public string? Ending;
        public int Reconnects;
    }

    private sealed class Record
    {
        public readonly List<HistoryItem> History = [];

        public void BeginCircuit(DateTimeOffset openedAt)
        {
            // A resume can be observed before the old circuit's close event; never leave two entries open.
            if (History.Count > 0 && History[^1].EndedAt is null)
            {
                History[^1].EndedAt = openedAt;
                History[^1].Ending ??= "Replaced";
            }
            History.Add(new HistoryItem { Generation = Generation, CircuitId = CircuitId, OpenedAt = openedAt });
            if (History.Count > MaxHistory) History.RemoveAt(0);
        }

        public HistoryItem? CurrentHistory(string circuitId) =>
            History.Count > 0 && History[^1].CircuitId == circuitId ? History[^1] : null;

        public string? SessionId;
        public required string CircuitId;
        public ICircuitHandle? Circuit;
        public int Generation = 1;
        public SessionState State = SessionState.Connected;
        public PauseReason PauseReason;
        public bool AdminHold;
        public bool ResumeRequested;
        public bool EnforcingHold;
        public bool PersistedThisCircuit;
        public bool PersistedWhileConnected;
        public string? RestoredFromCircuitId;
        public string TabVisibility = "unknown";
        public DateTimeOffset CreatedAt;
        public DateTimeOffset LastActivityAt;
        public DateTimeOffset? LastConnectedAt;
        public DateTimeOffset? LastDisconnectedAt;
        public DateTimeOffset? StatePersistedAt;
        public DateTimeOffset? PausedAt;
        public DateTimeOffset? LastRestoredAt;
        public DateTimeOffset? ClosedAt;

        public CircuitInfo ToInfo() => new(SessionId, CircuitId, Generation, State, PauseReason, AdminHold,
            ResumeRequested, RestoredFromCircuitId, TabVisibility, CreatedAt, LastActivityAt, LastConnectedAt,
            LastDisconnectedAt, StatePersistedAt, PausedAt, LastRestoredAt, ClosedAt);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Record> _live = new(StringComparer.Ordinal);     // live circuit ID -> record
    private readonly Dictionary<string, Record> _sessions = new(StringComparer.Ordinal); // session ID -> record
    private readonly ILogger<CircuitRegistry> _logger;
    private readonly LabTelemetry _telemetry;
    private readonly TimeProvider _time;
    private readonly CircuitLabOptions _options;
    private readonly string _instance = $"{Environment.MachineName}/{Environment.ProcessId}";

    public CircuitRegistry(ILogger<CircuitRegistry> logger, LabTelemetry telemetry, TimeProvider time,
        IOptions<CircuitLabOptions> options)
    {
        _logger = logger;
        _telemetry = telemetry;
        _time = time;
        _options = options.Value;
        _telemetry.SetSessionCountProvider(MeasureSessions);
    }

    /// <summary>Raised (outside the lock) whenever tracked state changes, so open pages can refresh.</summary>
    public event Action? Changed;

    // ---------------------------------------------------------------- lifecycle (from CircuitHandler)

    public void CircuitOpened(ICircuitHandle circuit)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            _live[circuit.Id] = new Record
            {
                CircuitId = circuit.Id, Circuit = circuit, CreatedAt = now, LastActivityAt = now, LastConnectedAt = now
            };
        }
        _telemetry.CircuitOpened();
        _logger.LogInformation("Circuit {CircuitId} opened", circuit.Id);
        RaiseChanged();
    }

    public void ConnectionUp(string circuitId)
    {
        Record? enforce = null;
        lock (_gate)
        {
            if (!_live.TryGetValue(circuitId, out var record)) return;
            if (record.State == SessionState.Disconnected && record.CurrentHistory(circuitId) is { } reconnected) reconnected.Reconnects++;
            record.State = SessionState.Connected;
            record.LastConnectedAt = record.LastActivityAt = _time.GetUtcNow();
            if (record.AdminHold) enforce = record;
        }
        _logger.LogInformation("Circuit {CircuitId} connection up", circuitId);
        if (enforce is not null) StartHoldEnforcement(enforce);
        RaiseChanged();
    }

    public void ConnectionDown(string circuitId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(circuitId, out var record) || record.State != SessionState.Connected) return;
            record.State = SessionState.Disconnected;
            record.LastDisconnectedAt = _time.GetUtcNow();
        }
        _logger.LogInformation("Circuit {CircuitId} connection down (circuit retained in memory)", circuitId);
        RaiseChanged();
    }

    /// <summary>
    /// Called from the component's persistence callback: the framework is persisting this circuit's
    /// state, which only happens when the circuit is being paused or evicted.
    /// </summary>
    public void StatePersisted(string circuitId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(circuitId, out var record)) return;
            record.StatePersistedAt = _time.GetUtcNow();
            record.PersistedThisCircuit = true;
            record.PersistedWhileConnected = record.State == SessionState.Connected;
        }
        _logger.LogInformation("Circuit {CircuitId} state persisted by the framework", circuitId);
    }

    public void CircuitClosed(string circuitId)
    {
        Record? record;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // Not present when the session has already moved on to a newer circuit.
            if (!_live.Remove(circuitId, out record)) return;
            record.Circuit = null;
            if (record.State == SessionState.Connected) record.LastDisconnectedAt = now;

            if (record.SessionId is not null && record.PersistedThisCircuit)
            {
                record.State = SessionState.Paused;
                record.PausedAt = now;
                record.PauseReason = record.AdminHold ? PauseReason.Administrative
                    : record.PersistedWhileConnected ? PauseReason.TabAutoPause
                    : PauseReason.DisconnectedEviction;
            }
            else
            {
                record.State = SessionState.Closed;
                record.ClosedAt = now;
                record.AdminHold = false;
                record.ResumeRequested = false;
            }

            if (record.CurrentHistory(circuitId) is { } ended)
            {
                ended.EndedAt = now;
                ended.Ending = record.State == SessionState.Paused ? record.PauseReason.ToString() : "Closed";
            }
        }

        if (record.State == SessionState.Paused)
        {
            _telemetry.CircuitClosed("paused");
            _telemetry.Paused(record.PauseReason.ToString());
            _logger.LogInformation(
                "Circuit {CircuitId} PAUSED ({PauseReason}): state persisted and circuit disposed; session {SessionId} awaits a browser resume",
                circuitId, record.PauseReason, record.SessionId);
        }
        else
        {
            _telemetry.CircuitClosed("closed");
            _logger.LogInformation("Circuit {CircuitId} closed without persisted state (session {SessionId})",
                circuitId, record.SessionId ?? "none");
        }
        RaiseChanged();
    }

    public void Touch(string circuitId)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(circuitId, out var record)) record.LastActivityAt = _time.GetUtcNow();
        }
    }

    // ---------------------------------------------------------------- from the interactive component

    /// <summary>
    /// Binds a session ID to the circuit it is now running in. When the session was previously tracked
    /// under another circuit, this is a resume: state was restored into a new circuit.
    /// </summary>
    public AttachResult Attach(string sessionId, ICircuitHandle circuit)
    {
        Record record;
        Record? enforce = null;
        string? resumedFrom = null;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _live.TryGetValue(circuit.Id, out var current);
            if (_sessions.TryGetValue(sessionId, out var existing) && !ReferenceEquals(existing, current))
            {
                if (existing.Circuit is { } old) _live.Remove(old.Id);
                resumedFrom = existing.CircuitId;
                existing.RestoredFromCircuitId = resumedFrom;
                existing.CircuitId = circuit.Id;
                existing.Circuit = circuit;
                existing.Generation++;
                existing.State = current?.State ?? SessionState.Connected;
                existing.LastActivityAt = now;
                existing.LastConnectedAt = existing.LastRestoredAt = now;
                existing.PauseReason = PauseReason.None;
                existing.ResumeRequested = false;
                existing.PersistedThisCircuit = false;
                existing.ClosedAt = null;
                existing.BeginCircuit(now);
                _live[circuit.Id] = record = existing;
            }
            else
            {
                record = current ?? new Record
                {
                    CircuitId = circuit.Id, Circuit = circuit, CreatedAt = now, LastActivityAt = now, LastConnectedAt = now
                };
                _live[circuit.Id] = record;
                if (record.SessionId is null)
                {
                    record.SessionId = sessionId;
                    _sessions[sessionId] = record;
                    record.BeginCircuit(record.CreatedAt);
                }
            }
            if (record.AdminHold && record.State == SessionState.Connected) enforce = record;
        }

        if (resumedFrom is not null)
        {
            _telemetry.Resumed();
            _logger.LogInformation(
                "Session {SessionId} RESUMED: state restored into NEW circuit {CircuitId} (generation {Generation}); previous circuit {PreviousCircuitId} no longer exists",
                sessionId, circuit.Id, record.Generation, resumedFrom);
            if (enforce is not null)
                _logger.LogWarning(
                    "Session {SessionId} resumed while under an administrative hold; circuit {CircuitId} will be paused again",
                    sessionId, circuit.Id);
        }
        else
        {
            _logger.LogInformation("Session {SessionId} attached to circuit {CircuitId}", sessionId, circuit.Id);
        }

        if (enforce is not null) StartHoldEnforcement(enforce);
        RaiseChanged();
        return new AttachResult(record.Generation, record.RestoredFromCircuitId, record.LastRestoredAt);
    }

    /// <summary>Visibility as reported by the browser over the circuit; "unknown" until it reports.</summary>
    public void ReportVisibility(string circuitId, bool hidden)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(circuitId, out var record)) return;
            record.TabVisibility = hidden ? "hidden" : "visible";
        }
        _logger.LogDebug("Circuit {CircuitId} tab reported {Visibility}", circuitId, hidden ? "hidden" : "visible");
        RaiseChanged();
    }

    // ---------------------------------------------------------------- queries

    public SessionDirective GetDirective(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var record) && record.State != SessionState.Closed
                ? new SessionDirective(true, record.AdminHold, record.ResumeRequested)
                : new SessionDirective(false, false, false);
        }
    }

    /// <summary>The circuits a session has run in, oldest first (the most recent 12).</summary>
    public IReadOnlyList<CircuitHistoryEntry> GetHistory(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var record)
                ? record.History.Select(h => new CircuitHistoryEntry(h.Generation, h.CircuitId, h.OpenedAt, h.EndedAt, h.Ending, h.Reconnects)).ToList()
                : [];
        }
    }

    public RegistrySnapshot Snapshot()
    {
        var now = _time.GetUtcNow();
        List<CircuitInfo> circuits;
        lock (_gate)
        {
            Prune(now);
            circuits = AllRecords().Select(r => r.ToInfo()).ToList();
        }
        circuits.Sort((a, b) => a.State != b.State ? a.State.CompareTo(b.State) : a.CreatedAt.CompareTo(b.CreatedAt));
        return new RegistrySnapshot(now, _instance,
            circuits.Count(c => c.State == SessionState.Connected),
            circuits.Count(c => c.State == SessionState.Disconnected),
            circuits.Count(c => c.State == SessionState.Paused),
            circuits.Count(c => c.State == SessionState.Closed),
            circuits);
    }

    /// <summary>Finds a session by full session ID, full circuit ID, or a unique prefix (6+ characters) of either.</summary>
    public LookupStatus TryFind(string target, out CircuitInfo? info)
    {
        lock (_gate)
        {
            var status = Resolve(target, out var record);
            info = record?.ToInfo();
            return status;
        }
    }

    // ---------------------------------------------------------------- administrative commands

    public async Task<(LookupStatus Status, TargetOutcome? Outcome)> PauseAsync(string target, CancellationToken cancellationToken)
    {
        Record? record;
        LookupStatus status;
        lock (_gate) status = Resolve(target, out record);
        return status == LookupStatus.Found ? (status, await PauseCoreAsync(record!, cancellationToken)) : (status, null);
    }

    public async Task<IReadOnlyList<TargetOutcome>> PauseAllAsync(CancellationToken cancellationToken)
    {
        Record[] targets;
        lock (_gate) targets = AllRecords().Where(r => r.State != SessionState.Closed).ToArray();
        return await Task.WhenAll(targets.Select(r => PauseCoreAsync(r, cancellationToken)));
    }

    public (LookupStatus Status, TargetOutcome? Outcome) Resume(string target)
    {
        TargetOutcome? outcome = null;
        LookupStatus status;
        lock (_gate)
        {
            status = Resolve(target, out var record);
            if (status == LookupStatus.Found) outcome = ResumeCore(record!);
        }
        if (outcome is not null) RaiseChanged();
        return (status, outcome);
    }

    public IReadOnlyList<TargetOutcome> ResumeAll()
    {
        List<TargetOutcome> outcomes;
        lock (_gate)
        {
            outcomes = AllRecords()
                .Where(r => r.AdminHold || r is { State: SessionState.Paused, PauseReason: PauseReason.Administrative })
                .Select(ResumeCore).ToList();
        }
        RaiseChanged();
        return outcomes;
    }

    private async Task<TargetOutcome> PauseCoreAsync(Record record, CancellationToken cancellationToken)
    {
        ICircuitHandle? circuit;
        SessionState state;
        lock (_gate)
        {
            switch (record.State)
            {
                case SessionState.Closed:
                    return Outcome(record, "not-pausable", "The circuit is closed; there is nothing to pause.");
                case SessionState.Paused when record.AdminHold:
                    return Outcome(record, "already-paused", "Already paused and under an administrative hold.");
                case SessionState.Paused:
                    record.AdminHold = true;
                    record.ResumeRequested = false;
                    _logger.LogInformation("ADMIN hold placed on session {SessionId}, already paused by {PauseReason}",
                        record.SessionId, record.PauseReason);
                    return Outcome(record, "hold-placed",
                        $"Already paused ({record.PauseReason}). Administrative hold placed: if the browser resumes it (tab visible again) it is paused again until an administrative resume.");
            }
            record.AdminHold = true;
            record.ResumeRequested = false;
            circuit = record.Circuit;
            state = record.State;
        }
        RaiseChanged();

        if (state == SessionState.Disconnected || circuit is null)
        {
            _logger.LogInformation("ADMIN hold placed on circuit {CircuitId}: connection is down, pause deferred until it reconnects",
                record.CircuitId);
            return Outcome(record, "hold-placed",
                "The connection is currently down, so the browser cannot be asked to pause. Hold placed: the circuit is paused as soon as it reconnects.");
        }

        bool accepted;
        try
        {
            accepted = await circuit.RequestPauseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "ADMIN pause request failed for circuit {CircuitId}; hold remains in place", circuit.Id);
            StartHoldEnforcement(record);
            return Outcome(record, "pause-pending", $"The framework pause request failed ({ex.GetType().Name}). The hold remains and the request is retried.");
        }

        if (!accepted)
        {
            _logger.LogWarning("ADMIN pause request rejected by the framework for circuit {CircuitId}; hold remains in place", circuit.Id);
            StartHoldEnforcement(record);
            return Outcome(record, "pause-pending",
                "The framework rejected the pause request (circuit not in a connected state). The hold remains and the request is retried.");
        }

        _logger.LogInformation("ADMIN pause request accepted for circuit {CircuitId}; waiting for the browser to persist and disconnect", circuit.Id);
        if (await WaitForPauseAsync(record, circuit, cancellationToken))
            return Outcome(record, "paused", "State persisted and circuit disposed. The browser tab shows the paused overlay until an administrative resume.");

        StartHoldEnforcement(record);
        return Outcome(record, "pause-pending",
            $"Accepted by the framework, but the browser did not finish pausing within {_options.PauseConfirmationTimeout.TotalSeconds:0.#}s. The hold remains in place.");
    }

    private TargetOutcome ResumeCore(Record record)
    {
        // Caller holds the lock.
        var hadHold = record.AdminHold;
        record.AdminHold = false;
        switch (record.State)
        {
            case SessionState.Paused when record.PauseReason == PauseReason.Administrative:
                var repeat = record.ResumeRequested;
                record.ResumeRequested = true;
                _logger.LogInformation("ADMIN resume directive issued for session {SessionId} (paused circuit {CircuitId})",
                    record.SessionId, record.CircuitId);
                return Outcome(record, "resume-requested", repeat
                    ? "Resume was already requested; still waiting for the browser tab (it must be open and visible)."
                    : "Resume directive issued. The server cannot reconnect a paused browser itself: the tab picks the directive up while open and visible, and state is restored into a NEW circuit.");
            case SessionState.Paused when hadHold:
                _logger.LogInformation("ADMIN hold lifted from session {SessionId} (paused by {PauseReason})", record.SessionId, record.PauseReason);
                return Outcome(record, "hold-lifted", $"Hold lifted. The session was paused by {record.PauseReason} and resumes when its tab becomes visible.");
            case SessionState.Connected or SessionState.Disconnected when hadHold:
                _logger.LogInformation("ADMIN hold lifted from circuit {CircuitId} before its pause completed", record.CircuitId);
                return Outcome(record, "hold-lifted", "Hold lifted before the pause completed; the circuit stays live.");
            case SessionState.Paused:
                return Outcome(record, "nothing-to-resume", $"Paused by {record.PauseReason}, not by an administrator; it resumes when its tab becomes visible.");
            case SessionState.Closed:
                return Outcome(record, "nothing-to-resume", "The circuit is closed and has no persisted state to resume.");
            default:
                return Outcome(record, "nothing-to-resume", "The circuit is live and not administratively paused.");
        }
    }

    private async Task<bool> WaitForPauseAsync(Record record, ICircuitHandle circuit, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _options.PauseConfirmationTimeout;
        while (true)
        {
            lock (_gate)
            {
                if (record.State == SessionState.Paused) return true;
                if (!ReferenceEquals(record.Circuit, circuit)) return false;
            }
            if (_time.GetUtcNow() >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _time, cancellationToken);
        }
    }

    /// <summary>
    /// Re-requests the pause for a held session whose circuit is live. Needed when a held session is
    /// resumed by its browser, or when the browser was not able to act on the first request.
    /// </summary>
    private void StartHoldEnforcement(Record record)
    {
        ICircuitHandle? circuit;
        lock (_gate)
        {
            if (record.EnforcingHold || record.Circuit is null) return;
            record.EnforcingHold = true;
            circuit = record.Circuit;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in s_holdEnforcementDelays)
                {
                    await Task.Delay(delay, _time);
                    lock (_gate)
                    {
                        if (!record.AdminHold || record.State != SessionState.Connected || !ReferenceEquals(record.Circuit, circuit))
                            return;
                    }
                    var accepted = await circuit.RequestPauseAsync(CancellationToken.None);
                    _logger.LogInformation("ADMIN hold enforcement: pause re-requested for circuit {CircuitId} (accepted: {Accepted})",
                        circuit.Id, accepted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ADMIN hold enforcement failed for circuit {CircuitId}", circuit.Id);
            }
            finally
            {
                lock (_gate) record.EnforcingHold = false;
            }
        });
    }

    // ---------------------------------------------------------------- helpers (caller holds the lock)

    private IEnumerable<Record> AllRecords() => _live.Values.Union(_sessions.Values);

    private LookupStatus Resolve(string target, out Record? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(target)) return LookupStatus.NotFound;
        target = target.Trim();

        if (_sessions.TryGetValue(target, out record)) return LookupStatus.Found;
        var all = AllRecords().ToList();
        record = all.FirstOrDefault(r => string.Equals(r.CircuitId, target, StringComparison.Ordinal));
        if (record is not null) return LookupStatus.Found;
        if (target.Length < 6) return LookupStatus.NotFound;

        var matches = all.Where(r =>
            r.CircuitId.StartsWith(target, StringComparison.Ordinal) ||
            (r.SessionId?.StartsWith(target, StringComparison.OrdinalIgnoreCase) ?? false)).Take(2).ToList();
        if (matches.Count == 1) record = matches[0];
        return matches.Count switch { 0 => LookupStatus.NotFound, 1 => LookupStatus.Found, _ => LookupStatus.Ambiguous };
    }

    private void Prune(DateTimeOffset now)
    {
        List<Record>? expired = null;
        foreach (var record in _sessions.Values)
        {
            var gone = record.State switch
            {
                SessionState.Closed => now - (record.ClosedAt ?? now) > _options.ClosedRetention,
                SessionState.Paused => now - (record.PausedAt ?? now) > _options.PausedRetention,
                _ => false
            };
            if (gone) (expired ??= []).Add(record);
        }

        var overflow = _sessions.Count - (expired?.Count ?? 0) - _options.MaxRecords;
        if (overflow > 0)
        {
            (expired ??= []).AddRange(_sessions.Values
                .Where(r => r.Circuit is null && !expired.Contains(r))
                .OrderBy(r => r.ClosedAt ?? r.PausedAt ?? r.CreatedAt)
                .Take(overflow));
        }

        if (expired is null) return;
        foreach (var record in expired) _sessions.Remove(record.SessionId!);
        _logger.LogInformation("Pruned {Count} expired session record(s)", expired.Count);
    }

    private IEnumerable<Measurement<int>> MeasureSessions()
    {
        int connected = 0, disconnected = 0, paused = 0, closed = 0;
        lock (_gate)
        {
            foreach (var record in AllRecords())
            {
                switch (record.State)
                {
                    case SessionState.Connected: connected++; break;
                    case SessionState.Disconnected: disconnected++; break;
                    case SessionState.Paused: paused++; break;
                    default: closed++; break;
                }
            }
        }
        return
        [
            new(connected, new KeyValuePair<string, object?>("state", "connected")),
            new(disconnected, new KeyValuePair<string, object?>("state", "disconnected")),
            new(paused, new KeyValuePair<string, object?>("state", "paused")),
            new(closed, new KeyValuePair<string, object?>("state", "closed"))
        ];
    }

    private static TargetOutcome Outcome(Record record, string outcome, string detail) =>
        new(record.SessionId, record.CircuitId, outcome, detail);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A registry change subscriber failed");
        }
    }
}


