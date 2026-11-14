# Pizza Circuit Lab

A lab for observing and controlling the lifecycle of Blazor Server circuits.
.NET 11 · Blazor Interactive Server · Aspire 13.6.5.

- **Aspire Dashboard is the control plane.** Listing, pausing and resuming circuits, and starting or stopping the app, are all done from the `pizza-web` resource in the dashboard.
- **The browser has no lifecycle controls.** The only thing it starts on its own is the hidden-tab auto-pause.
- **Nothing is simulated.** A pause is the framework's real pause (state persisted, circuit disposed), and the UI and logs say so when a resume produced a *new* circuit.

## Run

Requirements: .NET SDK 11.0.100 and a trusted ASP.NET Core development certificate (`dotnet dev-certs https --trust`). No separate Aspire CLI install is needed; the AppHost restores it as a NuGet package.

```powershell
dotnet run --project PizzaCircuitLab.AppHost
```

or open `PizzaCircuitLab.slnx` in Visual Studio and start **PizzaCircuitLab.AppHost**.

The console prints the dashboard login URL (`https://localhost:17240/login?t=…`). In the dashboard, open the `pizza-web` URL (`https://localhost:9875`) to create a circuit.

It behaves the same from Visual Studio, `dotnet run` and the Aspire CLI.

> **Use the pizza-web link in the dashboard (`https://localhost:9875`).** The AppHost runs the Web project with its `aspire` launch profile (ports 9875/9876). If the Web project is also started on its own, for example by a Visual Studio multi-startup configuration, that copy uses its `standalone` profile on ports 7275/5275 and does not serve the app: it forwards every browser request to the Aspire-managed instance (`CircuitLab:ManagedUrl`, default `https://localhost:9875`). So whichever tab Visual Studio opens, you end up on the instance the dashboard controls. Originally both copies shared ports 9875/9876 and each served its own app, which caused the `401 Unauthorized` and then "circuit not found" for IDs copied from the wrong copy. To run the Web project by itself on purpose, set `CircuitLab:AllowStandalone` to `true`; it then shows a red "Standalone instance" banner and its admin API stays disabled.

## Dashboard commands

**Resources → pizza-web**. *List circuits* is pinned on the row; the others are under **⋯ → Commands**.

| Command | What it does |
|---|---|
| **List circuits** | Every session this instance tracks: state, pause reason, admin hold, session ID, circuit ID, circuit generation, tab visibility, last activity. |
| **Circuit diagnostics** | All tracked fields and timestamps for one session. |
| **Pause circuit** | Asks that circuit's browser to persist its state and disconnect, then holds the session paused. Reports `paused` only once the circuit has really been disposed. |
| **Resume circuit** | Releases one administratively paused session. |
| **Pause all circuits** | Pauses every live circuit and places a hold on sessions already paused by a hidden tab. Asks for confirmation. |
| **Resume all circuits** | Releases every administratively paused or held session. |
| **Start / Stop / Restart** | Aspire's built-in commands. They act on the *process*, not on circuits. |

The per-circuit commands open a dialog with a **Circuit** selector listing the live sessions. A session ID, a circuit ID, or a unique prefix of at least 6 characters can also be typed. Each result includes a command ID that appears in the `pizza-web` structured logs.

The same commands work from the Aspire CLI:

```powershell
aspire resource pizza-web list-circuits
aspire resource pizza-web pause-circuit --circuit <session-or-circuit-id>
aspire resource pizza-web resume-all-circuits
```

Circuit commands are disabled while `pizza-web` is not running.

## What the operations really are

These are different things and the lab keeps them apart.

| Event | Circuit | State | How it comes back |
|---|---|---|---|
| **Connection lost** (network drop) | Kept in server memory (3 minutes by default) | Untouched | The browser reconnects to the **same** circuit. |
| **Pause** (tab hidden, or operator) | Persisted, then **disposed** | `[PersistentState]` values are saved; everything else is lost | The browser resumes into a **new** circuit with a new ID. |
| **Tab closed** | Disposed | Discarded | Does not come back. |
| **Resource stopped or restarted** | Gone with the process | Gone (persisted state is in memory) | The page reloads as a **new session**. |

Because a pause destroys the circuit, the lab tracks **sessions**: a random ID stored with the component state that survives pause and resume and links one page's successive circuits. *List circuits* shows the generation number, and the page shows which circuit its state was restored from.

### Limits set by Blazor

- **The server can request a pause but cannot perform a resume.** `Circuit.RequestCircuitPauseAsync` asks the connected browser to pause. A paused browser has no connection, so there is no server API to resume it. *Resume* therefore records a directive; the paused tab asks for it every 2 seconds (`GET /_lab/sessions/{id}/directive`) and calls `Blazor.resumeCircuit()` when told to. The tab must be open and visible. A closed tab cannot be resumed.
- **A resumed circuit is a new circuit.** Only `[PersistentState]` data crosses over. On the page, the draft and action counter survive; the component instance ID does not.
- **Pause needs a cooperating browser.** If the browser does not complete the pause within 5 seconds the command reports `pause-pending`, keeps the hold, and retries.
- **Auto-pause waits for unsaved typing.** The framework postpones the hidden-tab pause while a focused text field holds an uncommitted edit.
- **A client defect is worked around.** In the .NET 11.0.0 Blazor client, after a connection is lost and restored to the same circuit, every later pause request (from the tab or from the server) is silently ignored. `lab.js` detects that and carries out the same pause on the circuit's SignalR connection itself; the browser monitor logs when it does. The connection is obtained through the JS initializer `PizzaCircuitLab.Web.lib.module.js`. This relies on the hub method name `PauseCircuit` and on `Blazor.defaultReconnectionHandler`, so re-check it when updating the framework.
- **One instance, in memory.** The registry and the persisted circuit state live in the `pizza-web` process. A restart loses both, and each replica would list only its own circuits. Multiple replicas would need a shared `HybridCache` for circuit persistence and a shared store for the registry and directives; neither is included.

## Tab visibility

Handled by the framework's `Microsoft.AspNetCore.Components.Server.AutoPause` package:

1. The tab becomes hidden and a timer starts (default **12 seconds**).
2. Returning before it expires cancels it; the circuit is untouched.
3. Otherwise the browser pauses the circuit.
4. When the tab is visible again the browser resumes into a new circuit.

### Precedence

An operator's pause always wins over the browser.

- An administratively paused tab stays paused when it is hidden and shown again.
- If a tab was already paused by auto-pause when the operator paused it, a hold is placed. When the tab returns and resumes, the server pauses it again immediately.
- *Resume* resumes sessions the operator paused. For a session that was only held, it lifts the hold and the tab resumes when next visible.
- A manual page reload starts a new session, which is not covered by a hold on the old one.
- Holds are in memory and do not survive a restart of `pizza-web`.

## What the page shows

- **Circuit diagnostics** (reported by the server): session ID, current circuit ID, whether state was restored and from which circuit, counts for this instance.
- **This session's circuits**: one row per circuit the page has run in, newest first, with its ID, start time and how it ended (tab was hidden, paused by operator, evicted, closed) and how often its connection was lost and restored. This is the before-and-after view for a demonstration: hide the window, bring it back, and the old and new circuit are on screen together.
- **Browser monitor** (observed locally by the tab; keeps working while paused): tab visibility, circuit link state, what the server last said about an admin hold, and a timestamped event log.
- **Overlay** while disconnected or paused. It has no buttons.

## Security

- The AppHost generates a random 48-character control key on every run, passes it to `pizza-web` as `CircuitControl__Key`, and sends it in the `X-Circuit-Control-Key` header. It is not stored and never reaches the browser. To pin a value, set the `circuit-control-key` parameter (`Parameters:circuit-control-key`) in AppHost user secrets.
- `/_admin/circuits/*` rejects a missing or wrong key with `401`, compared in constant time.
- With no key configured (app started outside Aspire) the admin API is disabled and answers `503` with an explanation.
- Admin requests from non-loopback addresses get `403` unless `CircuitControl:AllowRemote` is `true`. Behind a reverse proxy on the same machine every caller looks local, so do not expose this API through one.
- The browser's directive endpoint is read-only and keyed by the unguessable session ID.
- Rejected requests are logged with their reason and counted. Keys are never logged.

This is a development lab. Keep the Aspire Dashboard private: anyone who can use it can pause circuits.

## Observability

In the Aspire Dashboard for `pizza-web`:

- **Structured logs**: circuit opened / connection up / connection down / `PAUSED (reason)` / `RESUMED … into NEW circuit` / closed, and `ADMIN command … [commandId]` start and finish, plus rejected admin requests.
- **Traces**: an `admin <command>` span under each command's HTTP request, alongside the framework's circuit and SignalR hub spans.
- **Metrics**: meter `PizzaCircuitLab` (`pizzalab.sessions` by state, `pizzalab.circuits.opened`, `pizzalab.circuits.closed`, `pizzalab.circuit.pauses` by reason, `pizzalab.circuit.resumes`, `pizzalab.admin.commands`, `pizzalab.admin.rejected`) and the built-in Blazor circuit meters.

## Configuration

`PizzaCircuitLab.Web/appsettings.json`:

| Setting | Default | Meaning |
|---|---|---|
| `CircuitLab:AutoPauseHiddenDelay` | `00:00:12` | Hidden time before the browser pauses. |
| `CircuitLab:PauseConfirmationTimeout` | `00:00:05` | How long a pause command waits for the browser. |
| `CircuitLab:PausedRetention` | `02:00:00` | How long a paused session stays resumable (also applied to the framework's persisted state). |
| `CircuitLab:ClosedRetention` | `00:10:00` | How long closed sessions stay listed. |
| `CircuitControl:AllowRemote` | `false` | Accept admin requests from non-loopback addresses. |

## Tests

```powershell
dotnet test
```

52 tests: admin API authorization (missing, wrong and near-miss keys on every route, disabled API, non-loopback callers), command behaviour, the browser directive endpoint, the prerendered page (no lifecycle buttons, no key), forwarding by a copy not started by Aspire, and the registry (pause reasons, resume as a new circuit, hold enforcement, pruning, concurrency).

## Layout

| Path | Purpose |
|---|---|
| `PizzaCircuitLab.AppHost/AppHost.cs` | Resources, control key, health check. |
| `PizzaCircuitLab.AppHost/CircuitCommands.cs` | The dashboard commands. |
| `PizzaCircuitLab.Web/Services/CircuitRegistry.cs` | Session and circuit tracking, pause/resume/hold logic. |
| `PizzaCircuitLab.Web/Services/PizzaCircuitHandler.cs` | Feeds framework circuit events into the registry. |
| `PizzaCircuitLab.Web/Admin/CircuitAdminApi.cs` | Authenticated admin API and the browser directive endpoint. |
| `PizzaCircuitLab.Web/Components/Pages/Home.razor` | The pizza form and server-reported diagnostics. |
| `PizzaCircuitLab.Web/wwwroot/lab.js` | Browser monitor, directive polling, pause-after-reconnect workaround. |
| `PizzaCircuitLab.Web/wwwroot/PizzaCircuitLab.Web.lib.module.js` | Blazor JS initializer that gives `lab.js` the circuit's connection. |
| `PizzaCircuitLab.Tests/` | Automated tests. |

## References

- https://learn.microsoft.com/aspnet/core/blazor/state-management/server?view=aspnetcore-11.0
- https://aspire.dev/fundamentals/http-commands/


