using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PizzaCircuitLab.Web.Services;

namespace PizzaCircuitLab.Tests;

/// <summary>Authorization and behaviour of the administrative HTTP API, hosted in-process.</summary>
public sealed class AdminApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Key = "integration-test-control-key";
    private const string AdminPath = "/_admin/circuits";

    private readonly WebApplicationFactory<Program> _factory;

    public AdminApiTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("CircuitControl:Key", Key));

    private static HttpRequestMessage Request(HttpMethod method, string path, string? key)
    {
        var request = new HttpRequestMessage(method, path);
        if (key is not null) request.Headers.Add(CircuitControlOptions.HeaderName, key);
        return request;
    }

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "GET", AdminPath },
        { "GET", $"{AdminPath}/abcdef123456" },
        { "POST", $"{AdminPath}/pause-all" },
        { "POST", $"{AdminPath}/resume-all" },
        { "POST", $"{AdminPath}/abcdef123456/pause" },
        { "POST", $"{AdminPath}/abcdef123456/resume" }
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Every_admin_route_rejects_a_missing_key(string method, string path)
    {
        var response = await _factory.CreateClient().SendAsync(Request(new HttpMethod(method), path, key: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", error.GetProperty("error").GetString());
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Every_admin_route_rejects_a_wrong_key(string method, string path)
    {
        var response = await _factory.CreateClient().SendAsync(Request(new HttpMethod(method), path, "not-the-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(Key, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("integration-test-control-ke")]    // prefix of the key
    [InlineData("integration-test-control-key ")]  // key plus trailing whitespace is trimmed by HTTP, so vary the content
    [InlineData("INTEGRATION-TEST-CONTROL-KEY")]
    public async Task Near_miss_keys_are_rejected(string candidate)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AdminPath);
        request.Headers.TryAddWithoutValidation(CircuitControlOptions.HeaderName, candidate + (candidate.EndsWith(' ') ? "x" : ""));

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_succeeds_with_the_correct_key()
    {
        var response = await _factory.CreateClient().SendAsync(Request(HttpMethod.Get, AdminPath, Key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("circuits").ValueKind);
        Assert.Equal(0, snapshot.GetProperty("connected").GetInt32());
    }

    [Theory]
    [InlineData("pause-all")]
    [InlineData("resume-all")]
    public async Task Bulk_commands_succeed_with_the_correct_key_and_echo_the_command_id(string command)
    {
        var request = Request(HttpMethod.Post, $"{AdminPath}/{command}", Key);
        request.Headers.Add(CircuitControlOptions.CommandIdHeaderName, "cmd-12345");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("cmd-12345", body.GetProperty("commandId").GetString());
        Assert.Equal("cmd-12345", response.Headers.GetValues(CircuitControlOptions.CommandIdHeaderName).Single());
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/pause")]
    [InlineData("POST", "/resume")]
    public async Task Unknown_circuit_returns_not_found(string method, string suffix)
    {
        var response = await _factory.CreateClient().SendAsync(Request(new HttpMethod(method), $"{AdminPath}/doesnotexist{suffix}", Key));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not-found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Malformed_target_is_rejected_as_bad_request()
    {
        var response = await _factory.CreateClient().SendAsync(Request(HttpMethod.Post, $"{AdminPath}/bad%24id%3B/pause", Key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pause_and_resume_act_on_a_tracked_circuit()
    {
        var registry = _factory.Services.GetRequiredService<CircuitRegistry>();
        var session = Guid.NewGuid().ToString("N");
        var circuit = new FakeCircuit("api-circuit-1") { OnPause = c => { registry.StatePersisted(c.Id); registry.CircuitClosed(c.Id); } };
        registry.CircuitOpened(circuit);
        registry.Attach(session, circuit);
        var client = _factory.CreateClient();

        var pause = await client.SendAsync(Request(HttpMethod.Post, $"{AdminPath}/{session}/pause", Key));
        var pauseBody = await pause.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        Assert.Equal("paused", pauseBody.GetProperty("outcomes")[0].GetProperty("outcome").GetString());

        var details = await (await client.SendAsync(Request(HttpMethod.Get, $"{AdminPath}/{session}", Key))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paused", details.GetProperty("state").GetString());
        Assert.Equal("Administrative", details.GetProperty("pauseReason").GetString());

        // The browser's view: held until the operator resumes.
        var directive = await client.GetFromJsonAsync<JsonElement>($"/_lab/sessions/{session}/directive");
        Assert.True(directive.GetProperty("adminHold").GetBoolean());
        Assert.False(directive.GetProperty("resumeRequested").GetBoolean());

        var resume = await client.SendAsync(Request(HttpMethod.Post, $"{AdminPath}/{session}/resume", Key));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        directive = await client.GetFromJsonAsync<JsonElement>($"/_lab/sessions/{session}/directive");
        Assert.False(directive.GetProperty("adminHold").GetBoolean());
        Assert.True(directive.GetProperty("resumeRequested").GetBoolean());

        // Resuming something that is not administratively paused is refused, not faked.
        registry.CircuitOpened(new FakeCircuit("api-circuit-2"));
        var refused = await client.SendAsync(Request(HttpMethod.Post, $"{AdminPath}/api-circuit-2/resume", Key));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_key_the_admin_api_is_disabled_even_for_callers_that_send_one()
    {
        using var unconfigured = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("CircuitControl:Key", "")
            .UseSetting("CircuitLab:AllowStandalone", "true"));
        var client = unconfigured.CreateClient();

        foreach (var key in new[] { null, "", "anything" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, AdminPath);
            if (key is not null) request.Headers.TryAddWithoutValidation(CircuitControlOptions.HeaderName, key);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("control-plane-disabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
    }

    [Theory]
    [InlineData("/", "https://localhost:9875/")]
    [InlineData("/?x=1", "https://localhost:9875/?x=1")]
    [InlineData("/_admin/circuits", "https://localhost:9875/_admin/circuits")]
    [InlineData("/_blazor/negotiate", "https://localhost:9875/_blazor/negotiate")]
    public async Task A_copy_not_started_by_aspire_serves_nothing_and_forwards_to_the_managed_instance(string path, string expected)
    {
        using var stray = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("CircuitControl:Key", ""));
        var client = stray.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Non_loopback_callers_are_refused_even_with_the_correct_key()
    {
        using var remote = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse("203.0.113.7")))));

        var response = await remote.CreateClient().SendAsync(Request(HttpMethod.Get, AdminPath, Key));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Browser_directive_endpoint_needs_no_key_validates_ids_and_reveals_nothing_for_unknown_sessions()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/_lab/sessions/not-a-session-id/directive")).StatusCode);

        var response = await client.GetAsync($"/_lab/sessions/{Guid.NewGuid():N}/directive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var directive = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(directive.GetProperty("known").GetBoolean());
        Assert.False(directive.GetProperty("resumeRequested").GetBoolean());
    }

    [Fact]
    public async Task Home_page_prerenders_without_manual_lifecycle_controls_or_the_control_key()
    {
        var response = await _factory.CreateClient().GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("components-reconnect-modal", html);
        Assert.Contains("data-session-id=\"", html);
        Assert.DoesNotContain(Key, html);

        var buttons = System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*>(.*?)</button>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Reset draft", "Place DEMO order"], buttons);
    }

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health")).StatusCode);
    }

    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
