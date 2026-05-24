var builder = DistributedApplication.CreateBuilder(args);

// Coolify deploy wiring — see Aspire.Hosting.Coolify.
// ADR-004: bearer token via Aspire secret parameters, per-instance.
var coolifyUrl = builder.AddParameter("coolify-url");
var coolifyToken = builder.AddParameter("coolify-token", secret: true);

// One containerisable workload — the minimal API.
var api = builder.AddProject<Projects.HelloWorldApp_Api>("api");

// Hook in the Coolify deploying publisher + the developer-chosen registry.
// ADR-005: developer-chosen, publisher-push.
var registryPrefix = builder.AddParameter("registry-prefix");
builder
    .WithCoolifyDeploy(coolifyUrl, coolifyToken)
    .WithImageRegistry(registryPrefix)
    .WithCoolifyDestination("homelab");  // FT-005 §0 — string overload, no parameter ceremony

builder.Build().Run();
