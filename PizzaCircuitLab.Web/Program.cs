using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PizzaCircuitLab.Web.Admin;
using PizzaCircuitLab.Web.Components;
using PizzaCircuitLab.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var labSection = builder.Configuration.GetSection(CircuitLabOptions.SectionName);
var lab = labSection.Get<CircuitLabOptions>() ?? new CircuitLabOptions();
if (lab.AutoPauseHiddenDelay <= TimeSpan.Zero)
    throw new InvalidOperationException($"{CircuitLabOptions.SectionName}:{nameof(CircuitLabOptions.AutoPauseHiddenDelay)} must be greater than zero.");

builder.Services.Configure<CircuitLabOptions>(labSection);
builder.Services.Configure<CircuitControlOptions>(builder.Configuration.GetSection(CircuitControlOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(circuits =>
        // Keep the framework's persisted (paused) state exactly as long as the registry lists the session as resumable.
        circuits.PersistedCircuitInMemoryRetentionPeriod = lab.PausedRetention);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LabTelemetry>();
builder.Services.AddSingleton<CircuitRegistry>();
builder.Services.AddScoped<CircuitSession>();
builder.Services.AddScoped<CircuitHandler, PizzaCircuitHandler>();
builder.Services.AddHealthChecks();

// Logs, metrics and traces flow to the Aspire Dashboard when the AppHost supplies an OTLP endpoint.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});
var telemetry = builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(LabTelemetry.Name,
            "Microsoft.AspNetCore.Components",
            "Microsoft.AspNetCore.Components.Lifecycle",
            "Microsoft.AspNetCore.Components.Server.Circuits"))
    .WithTracing(tracing => tracing
        .AddSource(LabTelemetry.Name,
            "Microsoft.AspNetCore.Components",
            "Microsoft.AspNetCore.Components.Server.Circuits")
        .AddAspNetCoreInstrumentation(http => http.Filter = context =>
            !context.Request.Path.StartsWithSegments("/health") &&
            !context.Request.Path.StartsWithSegments("/_lab")));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    telemetry.UseOtlpExporter();

builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// A copy started on its own (for example by a Visual Studio multi-startup configuration) has no
// control key and is invisible to the Aspire Dashboard. Rather than serve a second lab whose
// circuits nobody can manage, it sends every browser to the instance the AppHost runs.
var startedByAspire = !string.IsNullOrWhiteSpace(
    app.Configuration[$"{CircuitControlOptions.SectionName}:{nameof(CircuitControlOptions.Key)}"]);
if (!startedByAspire && !lab.AllowStandalone)
{
    var managedUrl = lab.ManagedUrl.TrimEnd('/');
    app.Logger.LogWarning(
        "This pizza-web copy was not started by the Aspire AppHost. It will not serve the app; browsers are forwarded to {ManagedUrl}. " +
        "Set CircuitLab:AllowStandalone=true to run it on its own",
        managedUrl);
    app.Use((context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health")) return next(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(managedUrl + context.Request.Path + context.Request.QueryString, permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapCircuitAdminApi();
app.MapBrowserDirectiveApi();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // The framework's own hidden-tab auto-pause: the only lifecycle action the browser starts by itself.
    .WithBrowserOptions(options => options.AddAutoPause(pause => pause.HiddenDelay = lab.AutoPauseHiddenDelay));

if (!startedByAspire && lab.AllowStandalone)
    app.Logger.LogWarning(
        "No circuit control key is configured: the administrative API ({AdminPath}) is disabled. Start the app through the Aspire AppHost to enable it",
        CircuitAdminApi.BasePath);

app.Run();

public partial class Program;
