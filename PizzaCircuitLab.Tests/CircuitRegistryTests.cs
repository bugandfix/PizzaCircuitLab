using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PizzaCircuitLab.Web.Services;

namespace PizzaCircuitLab.Tests;

/// <summary>Stand-in for a framework circuit. <see cref="OnPause"/> plays the browser's part.</summary>
internal sealed class FakeCircuit(string id) : ICircuitHandle
{
    private int _pauseRequests;

    public string Id { get; } = id;
    public int PauseRequests => Volatile.Read(ref _pauseRequests);
    public bool Accept { get; set; } = true;
    public Action<FakeCircuit>? OnPause { get; set; }

    public Task<bool> RequestPauseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pauseRequests);
        if (Accept) OnPause?.Invoke(this);
        return Task.FromResult(Accept);
    }
}

internal sealed class AdjustableTimeProvider : TimeProvider
{
    private TimeSpan _offset;
    public void Advance(TimeSpan by) => _offset += by;
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;
}

public sealed class CircuitRegistryTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().AddSingleton<LabTelemetry>().BuildServiceProvider();
    private readonly AdjustableTimeProvider _time = new();

    private CircuitRegistry Create(Action<CircuitLabOptions>? configure = null)
    {
        var options = new CircuitLabOptions { PauseConfirmationTimeout = TimeSpan.FromMilliseconds(300) };
        configure?.Invoke(options);
        return new CircuitRegistry(NullLogger<CircuitRegistry>.Instance, _services.GetRequiredService<LabTelemetry>(), _time, Options.Create(options));
    }

    private static string NewSession() => Guid.NewGuid().ToString("N");

    /// <summary>Opens a circuit and attaches a session, as a first interactive render does.</summary>
    private static (string Session, FakeCircuit Circuit) Connect(CircuitRegistry registry, string circuitId, string? session = null)
    {
        var circuit = new FakeCircuit(circuitId);
        registry.CircuitOpened(circuit);
        registry.ConnectionUp(circuitId);
        session ??= NewSession();
        registry.Attach(session, circuit);
        return (session, circuit);
    }

    /// <summary>What the framework does when the browser completes a pause.</summary>
    private static void CompletePause(CircuitRegistry registry, FakeCircuit circuit)
    {
        registry.StatePersisted(circuit.Id);
        registry.ConnectionDown(circuit.Id);
        registry.CircuitClosed(circuit.Id);
    }

    private static CircuitInfo Single(CircuitRegistry registry, string session) =>
        Assert.Single(registry.Snapshot().Circuits, c => c.SessionId == session);

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Condition was not met in time.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public void New_circuit_is_tracked_as_connected_with_its_session()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");

        var info = Single(registry, session);
        Assert.Equal(SessionState.Connected, info.State);
        Assert.Equal(circuit.Id, info.CircuitId);
        Assert.Equal(1, info.Generation);
        Assert.Null(info.RestoredFromCircuitId);
        Assert.Equal(1, registry.Snapshot().Connected);
    }

    [Fact]
    public void Connection_loss_keeps_the_same_circuit_as_disconnected_and_reconnect_restores_it()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");

        registry.ConnectionDown(circuit.Id);
        Assert.Equal(SessionState.Disconnected, Single(registry, session).State);
        Assert.NotNull(Single(registry, session).LastDisconnectedAt);

        registry.ConnectionUp(circuit.Id);
        var info = Single(registry, session);
        Assert.Equal(SessionState.Connected, info.State);
        Assert.Equal(1, info.Generation); // same circuit: nothing was restored
    }

    [Fact]
    public void Browser_initiated_pause_is_recorded_as_tab_auto_pause_and_resume_is_a_new_circuit()
    {
        var registry = Create();
        var (session, first) = Connect(registry, "c1");

        CompletePause(registry, first);
        var paused = Single(registry, session);
        Assert.Equal(SessionState.Paused, paused.State);
        Assert.Equal(PauseReason.TabAutoPause, paused.PauseReason);
        Assert.False(paused.AdminHold);
        Assert.True(registry.GetDirective(session).Known);

        var second = new FakeCircuit("c2");
        registry.CircuitOpened(second);
        var attach = registry.Attach(session, second);

        Assert.Equal(2, attach.Generation);
        Assert.Equal("c1", attach.RestoredFromCircuitId);
        var resumed = Single(registry, session);
        Assert.Equal(SessionState.Connected, resumed.State);
        Assert.Equal("c2", resumed.CircuitId);
        Assert.Equal(PauseReason.None, resumed.PauseReason);
        Assert.NotNull(resumed.LastRestoredAt);
    }

    [Fact]
    public async Task Session_history_records_each_circuit_and_how_it_ended()
    {
        var registry = Create();
        var (session, first) = Connect(registry, "c1");

        // Lost connection and reconnect: same circuit, no new entry.
        registry.ConnectionDown(first.Id);
        registry.ConnectionUp(first.Id);
        var entry = Assert.Single(registry.GetHistory(session));
        Assert.Equal((1, "c1", 1), (entry.Generation, entry.CircuitId, entry.Reconnects));
        Assert.Null(entry.EndedAt);

        // Hidden-tab pause, then resume into a second circuit.
        CompletePause(registry, first);
        var second = new FakeCircuit("c2") { OnPause = c => CompletePause(registry, c) };
        registry.CircuitOpened(second);
        registry.Attach(session, second);

        // Operator pause, then resume into a third circuit.
        await registry.PauseAsync(session, CancellationToken.None);
        registry.Resume(session);
        var third = new FakeCircuit("c3");
        registry.CircuitOpened(third);
        registry.Attach(session, third);

        var history = registry.GetHistory(session);
        Assert.Equal(["c1", "c2", "c3"], history.Select(h => h.CircuitId));
        Assert.Equal([1, 2, 3], history.Select(h => h.Generation));
        Assert.Equal([nameof(PauseReason.TabAutoPause), nameof(PauseReason.Administrative), null], history.Select(h => h.Ending));
        Assert.NotNull(history[0].EndedAt);
        Assert.NotNull(history[1].EndedAt);
        Assert.Null(history[2].EndedAt); // the live circuit
        Assert.Empty(registry.GetHistory(NewSession()));
    }

    [Fact]
    public void Session_history_is_bounded()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "h0");
        for (var i = 1; i <= 30; i++)
        {
            CompletePause(registry, circuit);
            circuit = new FakeCircuit($"h{i}");
            registry.CircuitOpened(circuit);
            registry.Attach(session, circuit);
        }

        var history = registry.GetHistory(session);
        Assert.Equal(12, history.Count);
        Assert.Equal("h30", history[^1].CircuitId);
        Assert.Equal(31, history[^1].Generation);
    }

    [Fact]
    public void Circuit_closed_without_persisted_state_is_closed_not_paused()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");

        registry.ConnectionDown(circuit.Id);
        registry.CircuitClosed(circuit.Id);

        Assert.Equal(SessionState.Closed, Single(registry, session).State);
        Assert.False(registry.GetDirective(session).Known); // the browser is told there is nothing to resume
    }

    [Fact]
    public void State_persisted_after_the_connection_dropped_is_an_eviction_not_a_tab_pause()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");

        registry.ConnectionDown(circuit.Id);
        registry.StatePersisted(circuit.Id);
        registry.CircuitClosed(circuit.Id);

        Assert.Equal(PauseReason.DisconnectedEviction, Single(registry, session).PauseReason);
    }

    [Fact]
    public async Task Administrative_pause_is_confirmed_only_when_the_circuit_really_paused()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");
        circuit.OnPause = c => CompletePause(registry, c);

        var (status, outcome) = await registry.PauseAsync(session, CancellationToken.None);

        Assert.Equal(LookupStatus.Found, status);
        Assert.Equal("paused", outcome!.Outcome);
        var info = Single(registry, session);
        Assert.Equal(SessionState.Paused, info.State);
        Assert.Equal(PauseReason.Administrative, info.PauseReason);
        Assert.True(info.AdminHold);
        Assert.Equal(new SessionDirective(true, true, false), registry.GetDirective(session));
    }

    [Fact]
    public async Task Administrative_pause_reports_pending_when_the_browser_does_not_complete_it()
    {
        var registry = Create();
        var (session, _) = Connect(registry, "c1"); // accepts the request but never pauses

        var (_, outcome) = await registry.PauseAsync(session, CancellationToken.None);

        Assert.Equal("pause-pending", outcome!.Outcome);
        var info = Single(registry, session);
        Assert.Equal(SessionState.Connected, info.State); // not reported as paused
        Assert.True(info.AdminHold);
    }

    [Fact]
    public async Task Pending_pause_is_retried_until_the_browser_complies()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");
        circuit.Accept = false; // e.g. the browser is mid-resume and ignores the first request

        var (_, outcome) = await registry.PauseAsync(session, CancellationToken.None);
        Assert.Equal("pause-pending", outcome!.Outcome);

        circuit.OnPause = c => CompletePause(registry, c);
        circuit.Accept = true;
        await Eventually(() => Single(registry, session).State == SessionState.Paused);
        Assert.Equal(PauseReason.Administrative, Single(registry, session).PauseReason);
    }

    [Fact]
    public async Task Administrative_resume_issues_a_directive_and_the_browser_resume_clears_it()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");
        circuit.OnPause = c => CompletePause(registry, c);
        await registry.PauseAsync(session, CancellationToken.None);

        var (_, outcome) = registry.Resume(session);

        Assert.Equal("resume-requested", outcome!.Outcome);
        Assert.Equal(new SessionDirective(true, false, true), registry.GetDirective(session));
        Assert.Equal(SessionState.Paused, Single(registry, session).State); // still paused until the browser acts

        var next = new FakeCircuit("c2");
        registry.CircuitOpened(next);
        registry.Attach(session, next);

        var info = Single(registry, session);
        Assert.Equal(SessionState.Connected, info.State);
        Assert.False(info.ResumeRequested);
        Assert.False(info.AdminHold);
        Assert.Equal(0, next.PauseRequests);
    }

    [Fact]
    public async Task Hold_on_a_tab_paused_session_re_pauses_it_when_the_tab_resumes()
    {
        var registry = Create();
        var (session, first) = Connect(registry, "c1");
        CompletePause(registry, first); // hidden-tab auto-pause

        var (_, outcome) = await registry.PauseAsync(session, CancellationToken.None);
        Assert.Equal("hold-placed", outcome!.Outcome);
        Assert.True(registry.GetDirective(session).AdminHold);

        // The tab becomes visible and the browser resumes on its own.
        var second = new FakeCircuit("c2");
        second.OnPause = c => CompletePause(registry, c);
        registry.CircuitOpened(second);
        registry.ConnectionUp(second.Id);
        registry.Attach(session, second);

        await Eventually(() => Single(registry, session).State == SessionState.Paused);
        var info = Single(registry, session);
        Assert.Equal(PauseReason.Administrative, info.PauseReason);
        Assert.True(info.AdminHold);
        Assert.True(second.PauseRequests >= 1);
    }

    [Fact]
    public async Task Lifting_a_hold_from_a_tab_paused_session_lets_the_tab_resume_normally()
    {
        var registry = Create();
        var (session, first) = Connect(registry, "c1");
        CompletePause(registry, first);
        await registry.PauseAsync(session, CancellationToken.None);

        var (_, outcome) = registry.Resume(session);
        Assert.Equal("hold-lifted", outcome!.Outcome);
        Assert.False(registry.GetDirective(session).ResumeRequested); // the tab resumes itself when visible

        var second = new FakeCircuit("c2");
        registry.CircuitOpened(second);
        registry.Attach(session, second);
        await Task.Delay(500);
        Assert.Equal(0, second.PauseRequests);
        Assert.Equal(SessionState.Connected, Single(registry, session).State);
    }

    [Fact]
    public async Task Pausing_a_disconnected_circuit_places_a_hold_that_is_enforced_on_reconnect()
    {
        var registry = Create();
        var (session, circuit) = Connect(registry, "c1");
        registry.ConnectionDown(circuit.Id);

        var (_, outcome) = await registry.PauseAsync(session, CancellationToken.None);
        Assert.Equal("hold-placed", outcome!.Outcome);
        Assert.Equal(0, circuit.PauseRequests); // nothing to ask while there is no connection

        circuit.OnPause = c => CompletePause(registry, c);
        registry.ConnectionUp(circuit.Id);

        await Eventually(() => Single(registry, session).State == SessionState.Paused);
        Assert.Equal(PauseReason.Administrative, Single(registry, session).PauseReason);
    }

    [Fact]
    public async Task Resume_refuses_to_claim_anything_for_a_live_circuit()
    {
        var registry = Create();
        var (session, _) = Connect(registry, "c1");

        var (status, outcome) = registry.Resume(session);

        Assert.Equal(LookupStatus.Found, status);
        Assert.Equal("nothing-to-resume", outcome!.Outcome);
        Assert.Empty(registry.ResumeAll());
        Assert.Equal("not-pausable", (await PauseClosedAsync(registry)).Outcome);
    }

    private static async Task<TargetOutcome> PauseClosedAsync(CircuitRegistry registry)
    {
        var (session, circuit) = Connect(registry, "closed-one");
        registry.CircuitClosed(circuit.Id);
        return (await registry.PauseAsync(session, CancellationToken.None)).Outcome!;
    }

    [Fact]
    public async Task Pause_all_and_resume_all_cover_every_eligible_session()
    {
        var registry = Create();
        var (liveSession, live) = Connect(registry, "live");
        live.OnPause = c => CompletePause(registry, c);
        var (tabSession, tab) = Connect(registry, "tab");
        CompletePause(registry, tab);
        var (closedSession, closed) = Connect(registry, "gone");
        registry.CircuitClosed(closed.Id);

        var paused = await registry.PauseAllAsync(CancellationToken.None);

        Assert.Equal(2, paused.Count);
        Assert.Equal("paused", paused.Single(o => o.SessionId == liveSession).Outcome);
        Assert.Equal("hold-placed", paused.Single(o => o.SessionId == tabSession).Outcome);
        Assert.DoesNotContain(paused, o => o.SessionId == closedSession);

        var resumed = registry.ResumeAll();
        Assert.Equal("resume-requested", resumed.Single(o => o.SessionId == liveSession).Outcome);
        Assert.Equal("hold-lifted", resumed.Single(o => o.SessionId == tabSession).Outcome);
        Assert.All(registry.Snapshot().Circuits, c => Assert.False(c.AdminHold));
    }

    [Fact]
    public void Lookup_accepts_session_id_circuit_id_and_unique_prefixes_only()
    {
        var registry = Create();
        Connect(registry, "circuit-alpha-1", "aaaaaaaa11111111aaaaaaaa11111111");
        Connect(registry, "circuit-alpha-2", "aaaaaaaa22222222aaaaaaaa22222222");

        Assert.Equal(LookupStatus.Found, registry.TryFind("aaaaaaaa11111111aaaaaaaa11111111", out var bySession));
        Assert.Equal("circuit-alpha-1", bySession!.CircuitId);
        Assert.Equal(LookupStatus.Found, registry.TryFind("circuit-alpha-2", out var byCircuit));
        Assert.Equal("aaaaaaaa22222222aaaaaaaa22222222", byCircuit!.SessionId);
        Assert.Equal(LookupStatus.Found, registry.TryFind("aaaaaaaa1", out _));
        Assert.Equal(LookupStatus.Ambiguous, registry.TryFind("aaaaaaaa", out _));
        Assert.Equal(LookupStatus.Ambiguous, registry.TryFind("circuit-alpha", out _));
        Assert.Equal(LookupStatus.NotFound, registry.TryFind("aaaaa", out _));   // too short to be a prefix
        Assert.Equal(LookupStatus.NotFound, registry.TryFind("zzzzzzzz", out _));
        Assert.Equal(LookupStatus.NotFound, registry.TryFind("  ", out _));
    }

    [Fact]
    public void Closed_and_paused_records_are_pruned_after_their_retention()
    {
        var registry = Create(o => { o.ClosedRetention = TimeSpan.FromMinutes(10); o.PausedRetention = TimeSpan.FromHours(2); });
        var (closedSession, closed) = Connect(registry, "c-closed");
        registry.CircuitClosed(closed.Id);
        var (pausedSession, paused) = Connect(registry, "c-paused");
        CompletePause(registry, paused);
        var (liveSession, _) = Connect(registry, "c-live");

        _time.Advance(TimeSpan.FromMinutes(11));
        var afterClosedRetention = registry.Snapshot().Circuits.Select(c => c.SessionId).ToList();
        Assert.DoesNotContain(closedSession, afterClosedRetention);
        Assert.Contains(pausedSession, afterClosedRetention);

        _time.Advance(TimeSpan.FromHours(2));
        var afterPausedRetention = registry.Snapshot().Circuits.Select(c => c.SessionId).ToList();
        Assert.DoesNotContain(pausedSession, afterPausedRetention);
        Assert.False(registry.GetDirective(pausedSession).Known);
        Assert.Contains(liveSession, afterPausedRetention); // live circuits are never pruned
    }

    [Fact]
    public void Record_count_is_capped()
    {
        var registry = Create(o => o.MaxRecords = 5);
        for (var i = 0; i < 40; i++)
        {
            var (_, circuit) = Connect(registry, $"capped-{i}");
            registry.CircuitClosed(circuit.Id);
        }

        Assert.True(registry.Snapshot().Circuits.Count <= 6);
    }

    [Fact]
    public async Task Concurrent_lifecycle_events_and_commands_stay_consistent()
    {
        var registry = Create(o => o.PauseConfirmationTimeout = TimeSpan.FromMilliseconds(50));
        var changes = 0;
        registry.Changed += () => Interlocked.Increment(ref changes);

        var work = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var (session, circuit) = Connect(registry, $"parallel-{i}");
            circuit.OnPause = c => CompletePause(registry, c);
            registry.Touch(circuit.Id);
            registry.ReportVisibility(circuit.Id, hidden: i % 2 == 0);
            switch (i % 4)
            {
                case 0: await registry.PauseAsync(session, CancellationToken.None); break;
                case 1: CompletePause(registry, circuit); break;
                case 2: registry.ConnectionDown(circuit.Id); registry.CircuitClosed(circuit.Id); break;
            }
            registry.Snapshot();
        })).Append(Task.Run(() => registry.PauseAllAsync(CancellationToken.None))).ToArray();

        await Task.WhenAll(work);
        registry.ResumeAll();

        var snapshot = registry.Snapshot();
        Assert.Equal(200, snapshot.Circuits.Count);
        Assert.Equal(200, snapshot.Connected + snapshot.Disconnected + snapshot.Paused + snapshot.Closed);
        Assert.InRange(snapshot.Closed, 1, 50); // the concurrent pause-all may pause a circuit before it closes
        Assert.Equal(200, snapshot.Circuits.Select(c => c.SessionId).Distinct().Count());
        Assert.True(changes > 0);
    }

    public void Dispose() => _services.Dispose();
}
