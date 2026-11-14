using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PizzaCircuitLab.Web.Services;

namespace PizzaCircuitLab.Web.Admin;

/// <summary>Body returned by every command endpoint.</summary>
public sealed record CommandResponse(string Command, string CommandId, bool Success, string Message, IReadOnlyList<TargetOutcome> Outcomes);

/// <summary>Body returned when a request is refused or cannot be applied.</summary>
public sealed record AdminError(string Error, string Message, string? CommandId = null);

/// <summary>
/// Administrative HTTP API used by the Aspire AppHost resource commands. Every route requires the
/// shared control key; the browser never receives that key and never calls these routes.
/// </summary>
public static partial class CircuitAdminApi
{
    public const string BasePath = "/_admin/circuits";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex TargetPattern();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SessionIdPattern();

    public static IEndpointRouteBuilder MapCircuitAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(BasePath).AddEndpointFilter<ControlKeyFilter>();

        admin.MapGet("", (CircuitRegistry registry) => Results.Json(registry.Snapshot()));

        admin.MapGet("/{target}", (string target, HttpContext http, CircuitRegistry registry) =>
            Execute(http, "diagnostics", target, _ =>
            {
                var status = registry.TryFind(target, out var info);
                return Task.FromResult(status == LookupStatus.Found ? Results.Json(info) : LookupFailure(http, status, target));
            }));

        admin.MapPost("/pause-all", (HttpContext http, CircuitRegistry registry, CancellationToken ct) =>
            Execute(http, "pause-all", null, async id =>
            {
                var outcomes = await registry.PauseAllAsync(ct);
                var paused = outcomes.Count(o => o.Outcome == "paused");
                return Results.Json(new CommandResponse("pause-all", id, true,
                    outcomes.Count == 0
                        ? "No live or paused circuits to pause."
                        : $"{paused} of {outcomes.Count} session(s) confirmed paused; see outcomes for the rest.",
                    outcomes));
            }));

        admin.MapPost("/resume-all", (HttpContext http, CircuitRegistry registry) =>
            Execute(http, "resume-all", null, id =>
            {
                var outcomes = registry.ResumeAll();
                return Task.FromResult(Results.Json(new CommandResponse("resume-all", id, true,
                    outcomes.Count == 0
                        ? "No administratively paused or held sessions."
                        : $"Resume issued for {outcomes.Count} session(s). Each resumes when its browser tab is open and visible.",
                    outcomes)));
            }));

        admin.MapPost("/{target}/pause", (string target, HttpContext http, CircuitRegistry registry, CancellationToken ct) =>
            Execute(http, "pause", target, async id =>
            {
                var (status, outcome) = await registry.PauseAsync(target, ct);
                if (status != LookupStatus.Found) return LookupFailure(http, status, target);
                var success = outcome!.Outcome is not "not-pausable";
                return Results.Json(new CommandResponse("pause", id, success, outcome.Detail, [outcome]),
                    statusCode: success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
            }));

        admin.MapPost("/{target}/resume", (string target, HttpContext http, CircuitRegistry registry) =>
            Execute(http, "resume", target, id =>
            {
                var (status, outcome) = registry.Resume(target);
                if (status != LookupStatus.Found) return Task.FromResult(LookupFailure(http, status, target));
                var success = outcome!.Outcome is not "nothing-to-resume";
                return Task.FromResult(Results.Json(new CommandResponse("resume", id, success, outcome.Detail, [outcome]),
                    statusCode: success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict));
            }));

        return app;
    }

    /// <summary>
    /// The only circuit-related route the browser may call. Read-only: it reports whether an
    /// administrator holds or has released this session. The random session ID is the capability.
    /// </summary>
    public static IEndpointRouteBuilder MapBrowserDirectiveApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/_lab/sessions/{sessionId}/directive", (string sessionId, HttpContext http, CircuitRegistry registry) =>
        {
            if (!SessionIdPattern().IsMatch(sessionId)) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(registry.GetDirective(sessionId));
        });
        return app;
    }

    private static async Task<IResult> Execute(HttpContext http, string command, string? target, Func<string, Task<IResult>> action)
    {
        var services = http.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PizzaCircuitLab.Admin");
        var telemetry = services.GetRequiredService<LabTelemetry>();
        var commandId = CommandId(http);

        if (target is not null && !TargetPattern().IsMatch(target))
        {
            telemetry.AdminCommand(command, "invalid");
            logger.LogWarning("ADMIN command {Command} [{CommandId}] rejected: malformed target", command, commandId);
            return Results.Json(new AdminError("invalid-target",
                "The circuit or session ID may only contain letters, digits, '-' and '_' (max 128 characters).", commandId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var activity = LabTelemetry.Activities.StartActivity($"admin {command}");
        activity?.SetTag("pizzalab.command", command);
        activity?.SetTag("pizzalab.command_id", commandId);
        activity?.SetTag("pizzalab.target", target ?? "*");
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CommandId"] = commandId });

        logger.LogInformation("ADMIN command {Command} [{CommandId}] started for target {Target}", command, commandId, target ?? "*");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action(commandId);
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            var outcome = status < 400 ? "ok" : "refused";
            activity?.SetTag("pizzalab.outcome", outcome);
            telemetry.AdminCommand(command, outcome);
            logger.LogInformation("ADMIN command {Command} [{CommandId}] finished with HTTP {StatusCode} in {ElapsedMs:0}ms",
                command, commandId, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            telemetry.AdminCommand(command, "cancelled");
            logger.LogWarning("ADMIN command {Command} [{CommandId}] cancelled by the caller", command, commandId);
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            telemetry.AdminCommand(command, "error");
            logger.LogError(ex, "ADMIN command {Command} [{CommandId}] failed", command, commandId);
            return Results.Json(new AdminError("command-failed", $"The command failed on the server ({ex.GetType().Name}). See the pizza-web logs.", commandId),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult LookupFailure(HttpContext http, LookupStatus status, string target) => status switch
    {
        LookupStatus.Ambiguous => Results.Json(new AdminError("ambiguous-target",
            $"'{target}' matches more than one circuit or session. Use a longer prefix or the full ID.", CommandId(http)),
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new AdminError("not-found",
            $"No circuit or session matches '{target}' on this instance. Run 'List circuits' for current IDs.", CommandId(http)),
            statusCode: StatusCodes.Status404NotFound)
    };

    private static string CommandId(HttpContext http)
    {
        if (http.Items.TryGetValue(nameof(CommandId), out var existing) && existing is string id) return id;
        var supplied = http.Request.Headers[CircuitControlOptions.CommandIdHeaderName].ToString();
        id = TargetPattern().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
        http.Items[nameof(CommandId)] = id;
        http.Response.Headers[CircuitControlOptions.CommandIdHeaderName] = id;
        return id;
    }

    /// <summary>Rejects administrative requests that do not carry the configured control key.</summary>
    private sealed class ControlKeyFilter(IOptionsMonitor<CircuitControlOptions> options, LabTelemetry telemetry,
        ILogger<ControlKeyFilter> logger) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var settings = options.CurrentValue;
            var remote = http.Connection.RemoteIpAddress;
            var path = http.Request.Path.Value;

            if (string.IsNullOrWhiteSpace(settings.Key))
            {
                telemetry.AdminRejected("not-configured");
                logger.LogWarning(
                    "ADMIN request {Method} {Path} from {RemoteIp} refused: no control key is configured, so the administrative API is disabled. " +
                    "This instance was probably not started by the Aspire AppHost",
                    http.Request.Method, path, remote);
                return Results.Json(new AdminError("control-plane-disabled",
                    "This pizza-web instance has no control key configured, so its administrative API is disabled. " +
                    "It was probably started on its own instead of by the Aspire AppHost and is answering on the AppHost's ports. Stop it and run only the AppHost."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Null means an in-process (test server) connection.
            if (!settings.AllowRemote && remote is not null && !IPAddress.IsLoopback(remote))
            {
                telemetry.AdminRejected("remote-address");
                logger.LogWarning("ADMIN request {Method} {Path} from {RemoteIp} refused: non-loopback callers are not allowed",
                    http.Request.Method, path, remote);
                return Results.Json(new AdminError("forbidden", "Administrative requests are only accepted from the local machine."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var supplied = http.Request.Headers[CircuitControlOptions.HeaderName].ToString();
            if (supplied.Length == 0 || !KeysMatch(supplied, settings.Key))
            {
                var reason = supplied.Length == 0 ? "missing-key" : "wrong-key";
                telemetry.AdminRejected(reason);
                logger.LogWarning("ADMIN request {Method} {Path} from {RemoteIp} rejected as unauthorized ({Reason})",
                    http.Request.Method, path, remote, reason);
                return Results.Json(new AdminError("unauthorized", supplied.Length == 0
                        ? $"The {CircuitControlOptions.HeaderName} header is required."
                        : "The supplied control key does not match this instance's key."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        }

        // Hash first so the comparison is constant-time regardless of the supplied length.
        private static bool KeysMatch(string supplied, string expected) => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
