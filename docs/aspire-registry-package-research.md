# Aspire Registry Hosting Package — Research

**Question:** Does a generic Aspire hosting package for "any OCI/Docker registry" already exist, so we can avoid writing a bespoke `Aspire.Hosting.PksAgentRegistry`?

**Short answer:** No package *runs a registry as a resource*. But Aspire 13's built-in `AddContainer` + `AddContainerRegistry` primitives already cover the two halves of what we need, so a bespoke hosting package is unnecessary.

## 1. Aspire-official packages

| Package | What it actually does | Fits our need? |
|---|---|---|
| `Aspire.Hosting.Docker` (13.0.0-preview) | Docker Compose **publisher** — emits a `compose.yaml` for the AppHost graph. | No — publishing target, not a registry resource. |
| `Aspire.Hosting.Azure.ContainerRegistry` | Provisions an **Azure** ACR via Bicep. | No — Azure-specific, not generic OCI. |
| `AddContainerRegistry(name, url)` (built-in) | Configures the **push destination** for `aspire do push`. Pure config — does not provision or run anything. | Partial — we'd still use this to point Aspire at our registry. |

`IDistributedApplicationBuilder.AddContainerRegistry` is documented as the way to declare "DockerHub, GHCR, Harbor, or any Docker-compatible registry" as a push target. It is a string URL, not a runnable resource.

Source: [aspire.dev/app-host/container-registry](https://aspire.dev/app-host/container-registry/), [aspire.dev/integrations/compute/docker](https://aspire.dev/integrations/compute/docker/).

## 2. CommunityToolkit.Aspire

Surveyed `github.com/CommunityToolkit/Aspire/tree/main/src` — 60+ integrations covering databases, brokers, search engines, language runtimes (Go, Rust, Bun, Deno…), Flyway, DbGate, etc. **No registry / OCI / distribution integration exists.**

## 3. Other community NuGet packages

NuGet.org searches for `Aspire registry`, `Aspire OCI`, `Aspire docker registry` surface only:

- `Aspire.Hosting.Docker` (Microsoft, Compose publisher)
- `Aspire.Hosting.Azure.ContainerRegistry` (Microsoft, ACR)
- Various consumers of `AddContainerRegistry` (auth, GHCR helpers — none run a registry)

No third-party generic "self-hosted registry" hosting package was found.

## 4. DIY with `AddContainer` — is it good enough?

Yes. Aspire's built-in container resource is sufficient. Sketch using the upstream `registry:2` (or our pks-agent-registry image once published):

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var registry = builder.AddContainer("pks-registry", "ghcr.io/pks/pks-agent-registry", "latest")
    .WithHttpEndpoint(port: 5000, targetPort: 5000, name: "registry")
    .WithEnvironment("REGISTRY_STORAGE_FILESYSTEM_ROOTDIRECTORY", "/var/lib/registry")
    .WithBindMount("./.data/registry", "/var/lib/registry");

// Tell Aspire's publisher to push workload images here
var pushTarget = builder.AddContainerRegistry("local", "localhost:5000");

builder.AddProject<Projects.Api>("api")
    .WithContainerRegistry(pushTarget)
    .WaitFor(registry);

builder.Build().Run();
```

That's the whole integration. ~10 lines. No new NuGet package, no new abstractions.

Edge cases to confirm during FT implementation:
- **Push URL**: `AddContainerRegistry` takes a string; passing the resource's endpoint expression (so port allocation works in dynamic mode) may require a small helper. This is the one place a thin extension method would earn its keep.
- **TLS / auth**: pks-agent-registry's auth model needs `WithEnvironment(...)` lines; trivial.
- **Coolify publisher**: our own publisher needs to honor the configured `ContainerRegistry` when emitting deploy manifests — that's a feature of the Coolify publisher, not of any registry package.

## 5. Recommendation (ranked)

1. **(b) Use `AddContainer` + `AddContainerRegistry` directly.** Optionally wrap in a 1-file extension `AddPksAgentRegistry(this IDistributedApplicationBuilder b, string name)` inside the existing `Aspire.Hosting.Coolify` package — no separate NuGet, no new package boundary.
2. **(c) Bespoke `Aspire.Hosting.PksAgentRegistry`** — only justified if pks-agent-registry grows distinctive config surface (custom auth modes, GC tuning, replication) that warrants a typed resource. Not the case today.
3. **(a) Reuse existing generic package** — not available; nothing on NuGet runs a registry as a resource.

**Recommendation:** Skip the bespoke package; add a thin `AddPksAgentRegistry` extension inside `Aspire.Hosting.Coolify` that wraps `AddContainer` with the right image, port, bind-mount, and env defaults, and wire its endpoint into `AddContainerRegistry`.
