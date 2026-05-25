#pragma warning disable ASPIRECOMPUTE003 // WithContainerRegistry is [Experimental] in Aspire 13.3.5 — see ADR-007 for the v1 adoption decision.

var builder = DistributedApplication.CreateBuilder(args);

// Coolify deploy wiring — see Aspire.Hosting.Coolify.
// ADR-004: bearer token via Aspire secret parameters, per-instance.
var coolifyUrl = builder.AddParameter("coolify-url");
var coolifyToken = builder.AddParameter("coolify-token", secret: true);

// In-project registry — pks-agent-registry deployed as a Coolify service
// (FT-015). v1 helper just creates the container resource + a
// ContainerRegistryResource target; FT-016 will wire the workload's push to
// resolve to this registry's Coolify-assigned FQDN after deploy.
var (registry, registryTarget) = builder.AddPksAgentRegistry("pks-registry");

// The minimal API workload. WithContainerRegistry(registryTarget) is the
// canonical Aspire pattern (ADR-007) for declaring "this workload pushes
// here." FT-016 will make our publisher honour that against the in-project
// registry; today it's documented intent.
var api = builder.AddProject<Projects.HelloWorldApp_Api>("api")
    .WithContainerRegistry(registryTarget)
    .WaitFor(registry);

// Coolify publisher hook.
builder
    .WithCoolifyDeploy(coolifyUrl, coolifyToken)
    .WithCoolifyDestination("homelab-v6");

builder.Build().Run();
