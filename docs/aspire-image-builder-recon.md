# Aspire image-builder recon

Research target: how the dotnet/aspire repo's own publishers build and push container images, so pks-aspire-coolify can stop stubbing `IImageBuildPipeline` / `IImagePushPipeline` and use the canonical path.

Aspire version of interest: **13.x** (matches our `Aspire.Hosting` ref). Note that the type was renamed between v9 and v13:

| v9 (older snippets on the web) | v13 (current) |
| --- | --- |
| `IResourceContainerImageBuilder` | `IResourceContainerImageManager` (interface) — alias kept in some docs |
| `ContainerBuildOptions` | `ContainerImageBuildOptions` |
| File: `ResourceContainerImageBuilder.cs` | File: `src/Aspire.Hosting/Publishing/ResourceContainerImageManager.cs` |

The custom-deployment docs (Microsoft Learn / aspire.dev) still talk about `IResourceContainerImageBuilder` — in v13 that name is mapped to `IResourceContainerImageManager`. Both names show up in different doc pages. Functionally identical.

## 1. The canonical service

**Interface** (from `Aspire.Hosting.Publishing`, file `ResourceContainerImageManager.cs`, v13.1):

```csharp
public interface IResourceContainerImageManager // aka IResourceContainerImageBuilder
{
    Task BuildImageAsync(IResource resource, CancellationToken ct = default);
    Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken ct = default);
    Task PushImageAsync(IResource resource, CancellationToken ct);
}

public class ContainerImageBuildOptions
{
    public string? ImageName { get; init; }
    public string? Tag { get; init; }
    public ContainerImageDestination? Destination { get; init; }   // Local / Registry
    public string? OutputPath { get; init; }
    public ContainerImageFormat? ImageFormat { get; init; }        // Docker / OCI
    public ContainerTargetPlatform? TargetPlatform { get; init; }  // linux/amd64, linux/arm64, ...
}
```

- Registered in DI by `Aspire.Hosting` itself — any `IDeployingPublisher` / pipeline step can `ServiceProvider.GetRequiredService<IResourceContainerImageManager>()` (the older `IResourceContainerImageBuilder` symbol still resolves in v13).
- For `ProjectResource` it shells out to `dotnet publish /t:PublishContainer` with the right `PublishProfile`, `ContainerRepository`, `ContainerImageTag`, `ContainerImageFormat`, `ContainerRuntimeIdentifier`, env-var-as-build-arg plumbing. So yes: **internally it _is_ the same `dotnet publish /t:PublishContainer` we tested**, just configured by Aspire from the resource graph.
- For `ContainerResource` with a Dockerfile it calls `IContainerRuntime.BuildImageAsync(contextPath, dockerfilePath, options, buildArgs, buildSecrets, stage, ct)` — i.e. `docker build` or `podman build` via `DockerContainerRuntime` / `PodmanContainerRuntime`.
- For `ContainerResource` with a pre-pulled image it short-circuits (reuses the image reference).
- `PushImageAsync` resolves the final remote name via `ContainerImagePushOptions.GetFullRemoteImageNameAsync(registry, ct)` and then `docker push` / `podman push`.

## 2. How the in-tree publishers use it

From the **custom-deployments doc page** (aspire.dev/deployment/custom-deployments, also Microsoft Learn `dotnet/aspire/fundamentals/custom-deployments`), the canonical publishing-callback pattern is:

```csharp
var imageBuilder = context.Services.GetRequiredService<IResourceContainerImageBuilder>();
var buildStep    = await reporter.CreateStepAsync("Build container images", ct);
var buildTask    = await buildStep.CreateTaskAsync($"Building {projectResources.Count} container image(s)", ct);

var buildOptions = new ContainerImageBuildOptions
{
    ImageFormat    = ContainerImageFormat.Docker,
    TargetPlatform = ContainerTargetPlatform.LinuxAmd64,
};
await imageBuilder.BuildImagesAsync(projectResources, buildOptions, ct);
```

Push is delegated to `imageBuilder.PushImageAsync(resource, ct)` — and the remote ref it pushes to is whatever the user configured via `WithImagePushOptions` / `WithRemoteImageName` / `WithRemoteImageTag` / `WithContainerRegistry`. The publisher does **not** hand it a tag; it reads it from the resource's annotations.

`Aspire.Hosting.Azure.AppContainers` does exactly this in `AzureContainerAppEnvironmentContext` — for each `IResource` mapped to ACA it calls `BuildImageAsync` and then `PushImageAsync`, using the ACR registry resource attached via `WithContainerRegistry(acr)`.

## 3. `dotnet publish /t:PublishContainer` reality

Confirmed: for project resources, the manager runs `dotnet publish -p:PublishProfile=DefaultContainer -p:ContainerRepository=… -p:ContainerImageTag=…` in-process via `Process.Start`. It is **not** the in-process SDK container library; it is a real `dotnet publish` invocation, just with all parameters derived from the Aspire model. The image lands in the local container runtime (docker / podman). For us this means: zero functional difference from "shell out to `dotnet publish`", but we get the tag, platform, runtime-identifier, build-args, env-var-as-secret all wired correctly from the resource graph.

## 4. The right hook point for pks-aspire-coolify

Three pipeline steps already exist in `Aspire.Hosting` 13:

- `DeployPrereq` — auth / target ready
- `ProcessParameters`
- `Deploy`

For a publisher like ours, the recommended layout (mirroring `Azure.AppContainers` and the docs) is:

1. **A "Build images" step** that we register, depending on `DeployPrereq`. Calls `BuildImagesAsync` over all containerizable workloads.
2. **A "Push images" step** that depends on "Build images". Calls `PushImageAsync` per workload after we've ensured `WithImagePushOptions` is populated with Coolify-bound coordinates.
3. **Our existing "Coolify deploy" step** depends on "Push images".

The built-in `Deploy` step does NOT automatically call `BuildImagesAsync` for arbitrary publishers — that's something each publisher kicks off. Hence option (a) from the question: invoke from our own step.

## 5. `AddContainerRegistry` + `WithContainerRegistry` semantics

These do **not** by themselves trigger a push. They register an `IContainerRegistry` model resource and link it to workloads. The push happens when **a publisher** calls `imageBuilder.PushImageAsync(resource, ct)`. At that point, `ContainerImagePushOptions.GetFullRemoteImageNameAsync(registry, ct)` reads the registry endpoint from the linked `IContainerRegistry` and combines it with `RemoteImageName` / `RemoteImageTag` to compute the final `host/repo/name:tag`.

So our FT-014 wiring of `AddContainerRegistry("coolify-registry", "<host>")` + `WithContainerRegistry` is *necessary plumbing* but currently inert — nothing in our codepath ends up calling `PushImageAsync`. The UnconfiguredImagePushPipeline returns success without doing anything.

## 6. Minimal diff to switch to the canonical path

Replace the stubs inside `CoolifyDeployingPublisher.RunBuildAsync` / `RunPushAsync` (or, better, the pipeline-step registrations in `CoolifyBuilderExtensions`) with direct calls to the manager. Snippet for `RunBuildAsync`:

```csharp
var imageMgr = _services.GetRequiredService<IResourceContainerImageManager>();
// (or IResourceContainerImageBuilder — same DI registration in 13)

var buildOptions = new ContainerImageBuildOptions
{
    ImageFormat    = ContainerImageFormat.Docker,
    TargetPlatform = ContainerTargetPlatform.LinuxAmd64,
};

await imageMgr.BuildImagesAsync(resources, buildOptions, ct);
```

…and `RunPushAsync`:

```csharp
var imageMgr = _services.GetRequiredService<IResourceContainerImageManager>();
foreach (var r in pushResources)
{
    // ContainerImagePushOptions on the resource (set via WithImagePushOptions /
    // WithRemoteImageName/Tag / WithContainerRegistry from FT-014) is honored automatically.
    await imageMgr.PushImageAsync(r, ct);
}
```

We can keep `IImageBuildPipeline` / `IImagePushPipeline` as thin façades around the manager for tests, but the `Unconfigured*` defaults should become `AspireImage*Pipeline` that delegates to the manager.

## 7. What we'd lose by NOT using Aspire's native builder

- Multi-arch + platform-aware builds (`ContainerTargetPlatform`).
- BuildKit layer caching, build args, build secrets — the manager reads `WithBuildArg`, `WithBuildSecret` annotations.
- Automatic `PublishProfile=DefaultContainer` selection per TFM, RID handling.
- Env-var → build-arg propagation for project resources.
- Correct `ContainerRepository` / `ContainerImageTag` derivation from `WithRemoteImageName` / `WithRemoteImageTag` / `WithContainerRegistry`.
- OCI vs Docker image format toggle, sidecar/data-volume mounts at build time.
- Progress reporting in the Aspire publish UI (via `PipelineActivityReporter`).
- Future fixes/features (e.g. dotnet/aspire#8770 build args/secrets support) come for free.

## Recommendation

Adopt the canonical path. Concretely: in `src/Aspire.Hosting.Coolify/` change `UnconfiguredImageBuildPipeline` → a new `AspireImageBuildPipeline` that resolves `IResourceContainerImageManager` from DI and calls `BuildImagesAsync(resources, new ContainerImageBuildOptions { TargetPlatform = LinuxAmd64 }, ct)`; change `UnconfiguredImagePushPipeline` → an `AspireImagePushPipeline` that calls `PushImageAsync(resource, ct)` per workload after we've ensured `ContainerImagePushOptions` is set (we already do this in FT-014 via `WithContainerRegistry`). Wire those as the defaults in `CoolifyDeployingPublisher.ImagePipeline` / `ImagePushPipeline` instead of the unconfigured stubs (files: `IImageBuildPipeline.cs`, `IImagePushPipeline.cs`, `CoolifyDeployingPublisher.cs` lines 212 + 274). This drops our `dotnet publish` shell-out plan, gets us multi-arch + secrets + cache for free, and leaves Coolify-specific logic (project/env upsert, deploy trigger) untouched.

## Sources

- [Custom deployment pipelines — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/custom-deployments)
- [Custom deployment pipelines — aspire.dev](https://aspire.dev/deployment/custom-deployments/)
- [IResourceContainerImageBuilder.BuildImageAsync (v9 docs)](https://learn.microsoft.com/dotnet/api/aspire.hosting.publishing.iresourcecontainerimagebuilder.buildimageasync?view=dotnet-aspire-9.0)
- [IResourceContainerImageManager.BuildImageAsync (v13 docs)](https://learn.microsoft.com/dotnet/api/aspire.hosting.publishing.iresourcecontainerimagemanager.buildimageasync?view=dotnet-aspire-13.0)
- [ContainerImageBuildOptions (v13)](https://learn.microsoft.com/dotnet/api/aspire.hosting.publishing.containerimagebuildoptions?view=dotnet-aspire-13.0)
- [IContainerRuntime.BuildImageAsync (v13)](https://learn.microsoft.com/dotnet/api/aspire.hosting.publishing.icontainerruntime.buildimageasync?view=dotnet-aspire-13.0)
- [WithImagePushOptions / RemoteImageName / RemoteImageTag (v13)](https://learn.microsoft.com/dotnet/api/aspire.hosting.resourcebuilderextensions.withimagepushoptions?view=dotnet-aspire-13.0)
- [ContainerImagePushOptions.GetFullRemoteImageNameAsync (v13)](https://learn.microsoft.com/dotnet/api/aspire.hosting.applicationmodel.containerimagepushoptions.getfullremoteimagenameasync?view=dotnet-aspire-13.0)
- [dotnet/aspire#8770 — build args/secrets gap](https://github.com/dotnet/aspire/issues/8770)
- [dotnet/aspire PR #8264 — bootstrapping container builds](https://github.com/dotnet/aspire/pull/8264)
- [davidfowl/aspire-ssh-deploy](https://github.com/davidfowl/aspire-ssh-deploy)
