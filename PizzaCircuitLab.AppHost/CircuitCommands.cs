#pragma warning disable ASPIREINTERACTION001 // Command arguments use the interaction input types, which are still marked experimental.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace PizzaCircuitLab.AppHost;

/// <summary>
/// The circuit control plane: Aspire Dashboard commands on the pizza-web resource. Each command is
/// an authenticated HTTP call to pizza-web's administrative API.
/// </summary>
internal static partial class CircuitCommands
{
    public const string HttpClientName = "circuit-admin";

    private const string BasePath = "/_admin/circuits";
    private const string KeyHeader = "X-Circuit-Control-Key";
    private const string CommandIdHeader = "X-Circuit-Command-Id";
    private const string CircuitArgument = "circuit";

    [GeneratedRegex("^[A-Za-z0-9_-]{6,128}$")]
    private static partial Regex TargetPattern();

    public static IResourceBuilder<ProjectResource> WithCircuitCommands(
        this IResourceBuilder<ProjectResource> web, IResourceBuilder<ParameterResource> controlKey)
    {
        HttpCommandOptions Command(HttpMethod method, string description, string icon,
            Func<JsonElement, (string Message, string? Markdown)> format,
            string? targetSuffix = null, string? confirmation = null, bool highlighted = false) => new()
        {
            Method = method,
            Description = description,
            IconName = icon,
            IsHighlighted = highlighted,
            ConfirmationMessage = confirmation,
            HttpClientName = HttpClientName,
            // Circuits only exist while the process runs; a stopped resource has nothing to command.
            UpdateState = context => context.ResourceSnapshot.State?.Text == KnownResourceStates.Running
                ? ResourceCommandState.Enabled
                : ResourceCommandState.Disabled,
            Arguments = targetSuffix is null ? [] : [CreateCircuitArgument(web, controlKey)],
            ValidateArguments = targetSuffix is null ? null : context =>
            {
                var value = context.Inputs.GetString(CircuitArgument);
                if (string.IsNullOrWhiteSpace(value))
                    context.AddValidationError(CircuitArgument, "Choose a circuit, or enter a session or circuit ID.");
                else if (!TargetPattern().IsMatch(value.Trim()))
                    context.AddValidationError(CircuitArgument, "Enter a full session/circuit ID or a prefix of at least 6 characters (letters, digits, '-' and '_').");
                return Task.CompletedTask;
            },
            PrepareRequest = async context =>
            {
                var key = await controlKey.Resource.GetValueAsync(context.CancellationToken);
                context.Request.Headers.Add(KeyHeader, key);
                context.Request.Headers.Add(CommandIdHeader, Guid.NewGuid().ToString("N"));
                if (targetSuffix is not null)
                {
                    var target = context.Arguments.GetString(CircuitArgument)!.Trim();
                    context.Request.RequestUri = new Uri(context.Request.RequestUri!,
                        $"{BasePath}/{Uri.EscapeDataString(target)}{targetSuffix}");
                }
            },
            GetCommandResult = context => ReadResultAsync(context.Response, format, context.CancellationToken)
        };

        return web
            .WithHttpCommand(BasePath, "List circuits", commandName: "list-circuits",
                commandOptions: Command(HttpMethod.Get,
                    "List every session and circuit this pizza-web instance tracks, with connection and pause state.",
                    "List", FormatList, highlighted: true))
            .WithHttpCommand(BasePath, "Circuit diagnostics", commandName: "circuit-diagnostics",
                commandOptions: Command(HttpMethod.Get,
                    "Show every tracked detail of one session: IDs, state, pause reason, hold and timestamps.",
                    "Stethoscope", FormatDiagnostics, targetSuffix: ""))
            .WithHttpCommand(BasePath, "Pause circuit", commandName: "pause-circuit",
                commandOptions: Command(HttpMethod.Post,
                    "Ask one circuit's browser to persist its state and disconnect, and hold it paused until an administrative resume.",
                    "Pause", FormatCommand, targetSuffix: "/pause"))
            .WithHttpCommand(BasePath, "Resume circuit", commandName: "resume-circuit",
                commandOptions: Command(HttpMethod.Post,
                    "Release one administratively paused session. Its browser tab resumes into a new circuit once it is open and visible.",
                    "Play", FormatCommand, targetSuffix: "/resume"))
            .WithHttpCommand($"{BasePath}/pause-all", "Pause all circuits", commandName: "pause-all-circuits",
                commandOptions: Command(HttpMethod.Post,
                    "Pause every live circuit and hold sessions already paused by a hidden tab. This does not stop the resource.",
                    "PauseCircle", FormatCommand,
                    confirmation: "Pause every circuit on pizza-web? Users see a paused overlay until you run 'Resume all circuits'."))
            .WithHttpCommand($"{BasePath}/resume-all", "Resume all circuits", commandName: "resume-all-circuits",
                commandOptions: Command(HttpMethod.Post,
                    "Release every administratively paused or held session. Each resumes when its browser tab is open and visible.",
                    "PlayCircle", FormatCommand));
    }

    /// <summary>
    /// The circuit selector: a list loaded from the running app when the dashboard opens the dialog,
    /// while still accepting a typed ID (which is also how the CLI supplies it).
    /// </summary>
    private static InteractionInput CreateCircuitArgument(
        IResourceBuilder<ProjectResource> web, IResourceBuilder<ParameterResource> controlKey) => new()
    {
        Name = CircuitArgument,
        Label = "Circuit",
        Description = "Session ID, circuit ID, or a unique prefix (6+ characters) of either.",
        InputType = InputType.Choice,
        Required = true,
        AllowCustomChoice = true,
        Placeholder = "Select a circuit or paste an ID",
        DynamicLoading = new InputLoadOptions
        {
            AlwaysLoadOnStart = true,
            LoadCallback = async context =>
            {
                try
                {
                    var endpoint = web.GetEndpoint("https");
                    var client = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(endpoint.Url), BasePath));
                    request.Headers.Add(KeyHeader, await controlKey.Resource.GetValueAsync(context.CancellationToken));
                    using var response = await client.SendAsync(request, context.CancellationToken);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.CancellationToken));
                    context.Input.Options = document.RootElement.GetProperty("circuits").EnumerateArray()
                        .Select(c =>
                        {
                            var session = Text(c, "sessionId");
                            var circuit = Text(c, "circuitId");
                            var label = $"{Text(c, "state")}{(Flag(c, "adminHold") ? " (admin hold)" : "")} · session {Shorten(session)} · circuit {Shorten(circuit)}";
                            return KeyValuePair.Create(session ?? circuit ?? "", label);
                        })
                        .Where(option => option.Key.Length > 0)
                        .ToList();
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    // The list is a convenience; an ID can always be typed.
                    context.Input.Options = [];
                }
            }
        }
    };

    private static async Task<ExecuteCommandResult> ReadResultAsync(HttpResponseMessage response,
        Func<JsonElement, (string Message, string? Markdown)> format, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument? document = null;
        try
        {
            if (body.Length > 0) document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not our API's JSON; handled below.
        }

        using (document)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = document is not null && document.RootElement.ValueKind == JsonValueKind.Object
                    ? Text(document.RootElement, "message")
                    : null;
                message ??= response.StatusCode == HttpStatusCode.Unauthorized
                    ? "The responder did not accept the control key. It is probably not the pizza-web instance this AppHost started: another process may be listening on the same port."
                    : "The response was not produced by pizza-web's administrative API.";
                return CommandResults.Failure($"HTTP {(int)response.StatusCode} {response.StatusCode}: {message}");
            }

            if (document is null)
                return CommandResults.Failure("pizza-web returned an empty or non-JSON response.");

            var (text, markdown) = format(document.RootElement);
            return markdown is null
                ? CommandResults.Success(text)
                : CommandResults.Success(text, markdown, CommandResultFormat.Markdown, true);
        }
    }

    private static (string, string?) FormatList(JsonElement root)
    {
        var circuits = root.GetProperty("circuits");
        var summary = $"{Number(root, "connected")} connected · {Number(root, "disconnected")} disconnected · {Number(root, "paused")} paused · {Number(root, "closed")} closed";
        var markdown = new StringBuilder()
            .AppendLine($"**pizza-web instance `{Text(root, "instance")}`** — {summary}")
            .AppendLine();

        if (circuits.GetArrayLength() == 0)
        {
            markdown.AppendLine("No circuits tracked. Open the pizza-web URL in a browser to create one.");
        }
        else
        {
            // One entry per session rather than a table: eight columns do not fit the dashboard dialog.
            foreach (var c in circuits.EnumerateArray())
            {
                var state = Text(c, "state");
                var reason = Text(c, "pauseReason");
                var hold = Flag(c, "resumeRequested") ? "resume requested, waiting for the browser tab"
                    : Flag(c, "adminHold") ? "admin hold: yes" : "admin hold: no";
                markdown.AppendLine($"- **{state}{(reason is null or "None" ? "" : $" ({reason})")}** · {hold} · tab {Text(c, "tabVisibility")} · last activity {Time(c, "lastActivityAt")} UTC  ")
                    .AppendLine($"  session `{Text(c, "sessionId") ?? "(none)"}`  ")
                    .AppendLine($"  circuit `{Text(c, "circuitId")}` (circuit #{Number(c, "generation")} of this session)");
            }
            markdown.AppendLine()
                .AppendLine("The circuit number increases each time a session is resumed into a new circuit. A paused entry shows the ID of the circuit that was disposed. The list covers this instance only.");
        }

        return ($"{circuits.GetArrayLength()} tracked: {summary}", markdown.ToString());
    }

    private static (string, string?) FormatDiagnostics(JsonElement root)
    {
        var markdown = new StringBuilder().AppendLine("| Field | Value |").AppendLine("|---|---|");
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Null => "–",
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText()
            };
            markdown.AppendLine($"| {property.Name} | `{value}` |");
        }
        return ($"Session {Shorten(Text(root, "sessionId"))} is {Text(root, "state")}", markdown.ToString());
    }

    private static (string, string?) FormatCommand(JsonElement root)
    {
        var message = Text(root, "message") ?? "Done.";
        var outcomes = root.GetProperty("outcomes");
        if (outcomes.GetArrayLength() == 0) return (message, message);

        // A list rather than a table: the dashboard dialog is narrow and the detail text is long.
        var markdown = new StringBuilder();
        if (outcomes.GetArrayLength() > 1) markdown.AppendLine(message).AppendLine();
        foreach (var o in outcomes.EnumerateArray())
        {
            markdown.AppendLine($"- **{Text(o, "outcome")}** · session `{Text(o, "sessionId") ?? "(none)"}` · circuit `{Text(o, "circuitId")}`  ")
                .AppendLine($"  {Text(o, "detail")}");
        }
        markdown.AppendLine().AppendLine($"Command ID `{Text(root, "commandId")}` — search for it in the pizza-web structured logs.");
        return (message, markdown.ToString());
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "?";

    private static string Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time)
            ? time.UtcDateTime.ToString("HH:mm:ss")
            : "–";

    private static string Shorten(string? id) => id is null ? "(none)" : id.Length <= 10 ? id : id[..10] + "…";
}

