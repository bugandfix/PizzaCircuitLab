using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using PizzaCircuitLab.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Shared secret between the AppHost's resource commands and pizza-web's administrative API.
// A fresh random value is generated on every AppHost run and is never written to disk, so there is
// nothing to configure and nothing to leak. To pin a value (for example to call the API by hand),
// set Parameters:circuit-control-key through user secrets or the Parameters__circuit-control-key
// environment variable.
var controlKey = builder.AddParameter(
    "circuit-control-key",
    new GenerateParameterDefault { MinLength = 48, Special = false },
    secret: true);

builder.Services.AddHttpClient(CircuitCommands.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// The "aspire" launch profile owns ports 9875/9876. The Web project's default ("standalone") profile
// uses different ports, so a copy of the Web project started on its own (for example by a Visual
// Studio multi-startup configuration) can never answer the dashboard's commands in this one's place.
builder.AddProject<Projects.PizzaCircuitLab_Web>("pizza-web", launchProfileName: "aspire")
    .WithEnvironment("CircuitControl__Key", controlKey)
    .WithHttpHealthCheck("/health")
    .WithCircuitCommands(controlKey);

// Start / Stop / Restart of pizza-web are Aspire's built-in resource commands. They act on the
// process, which is a different operation from pausing or resuming a circuit inside it.
builder.Build().Run();
