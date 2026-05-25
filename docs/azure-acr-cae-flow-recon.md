# Azure ACR + CAE deploy-flow recon (for FT-016)

Goal: understand how the dotnet/aspire repo's Azure publisher orchestrates **registry provisioning → image build → image push → workload deploy**, so pks-aspire-coolify can mirror it for the case where the registry is itself an in-AppHost resource (a pks-agent-registry deployed to Coolify).

Aspire version examined: **main (13.x)**. All paths below are `src/...` inside `github.com/dotnet/aspire`.

## 1. Where the orchestration lives

There is **no monolithic "Azure deploying publisher" class** in Aspire 13. The pipeline is composed from independent step-registrations contributed by three packages, then run by the generic `aspire deploy` engine. The relevant contributors:

- `src/Aspire.Hosting.Azure.AppContainers/AzureContainerAppExtensions.cs`
  - `AddAzureContainerAppsInfrastructureCore` (lines ~38-66) registers a single global step `validate-azure-container-apps` with `requiredBy: WellKnownPipelineSteps.BeforeStart`. Its body validates that no `PublishAs*` annotation exists without a CAE.
  - `AddAzureContainerAppEnvironment` (lines ~106-206) creates an `AzureContainerAppEnvironmentResource`, and at lines ~198-201 calls `CreateDefaultAzureContainerRegistry(...)` which adds an `AzureContainerRegistryResource` and assigns it as `containerAppEnvironment.DefaultContainerRegistry`. This is the "every CAE gets an ACR for free" hook.
- `src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryResource.cs`
  - Constructor registers two pipeline annotations on the registry resource itself:
    - A **`PipelineStepAnnotation`** named `login-to-acr-{name}`, tagged `acr-login`, with `RequiredBySteps = [WellKnownPipelineSteps.PushPrereq]`, action calls `AzureContainerRegistryHelpers.LoginToRegistryAsync(this, context)`.
    - A **`PipelineConfigurationAnnotation`** that walks all steps tagged `acr-login` and adds `DependsOn(provisionSteps)` (where `provisionSteps` are the steps tagged `WellKnownPipelineTags.ProvisionInfrastructure`). So login runs **after** Bicep provisioning, and **before** push.
  - `IContainerRegistry` impl: `Name = NameOutputReference` ("name" bicep output), `Endpoint = RegistryEndpoint` ("loginServer" bicep output).
- `src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryExtensions.cs`
  - `AddAzureContainerRegistry` constructs the resource, then calls `SubscribeToAddRegistryTargetAnnotations(builder, resource)` which on `OnBeforeStart` walks every resource in the model and stamps a `RegistryTargetAnnotation(registry)` — i.e. "this registry is available as a default push target".
  - `WithAzureContainerRegistry<T>(...)` simply adds a `ContainerRegistryReferenceAnnotation(registryBuilder.Resource)` to the compute environment resource (`T : IComputeEnvironmentResource`). It does **not** push, build, or set image options directly.

### The well-known step graph that ends up running
From `src/Aspire.Hosting/Publishing/WellKnownPipelineSteps.cs` (values verified):

| Constant | Value |
|---|---|
| Publish / PublishPrereq | `publish` / `publish-prereq` |
| Deploy / DeployPrereq | `deploy` / `deploy-prereq` |
| Build / BuildPrereq | `build` / `build-prereq` |
| Push / PushPrereq | `push` / `push-prereq` |
| ProcessParameters | `process-parameters` |
| ValidateComputeEnvironments | `validate-compute-environments` |
| BeforeStart | `before-start` |
| CheckContainerRuntime | `check-container-runtime` |

The generic Aspire deploy engine wires `build → push → deploy` as the canonical chain (each prereq slot existing for plug-ins to hook). Azure does **not** override `build` or `push`; it relies on the default registration in `Aspire.Hosting` which iterates compute-environment resources and calls `IResourceContainerImageManager.BuildImagesAsync` / `PushImageAsync`. What Azure adds:

1. **Provisioning steps** tagged `provision-infrastructure` (one per Azure resource — CAE, ACR, identities, log analytics, …), gated by `deploy-prereq`. These are emitted by `AzureProvisioning` (added in `AddAzureContainerAppsInfrastructureCore` via `builder.AddAzureProvisioning()`).
2. **`login-to-acr-{name}`** steps — one per ACR resource — that `dependsOn` the provision steps and are `requiredBy: push-prereq`. This is the bridge that guarantees: ACR exists → docker has creds → push runs.
3. The workload deploy bodies (Bicep emit) that reference `containerImageParam` resolved at deploy-time from `ContainerRegistryReferenceAnnotation` → `RegistryEndpoint`.

> **Net effect**: provision-ACR → login-to-ACR → (default) push → (default) deploy. No custom "AzureContainerAppsDeployer" class is needed because the work is split across resource-level annotations.

## 2. How `WithAzureContainerRegistry` rewires workload push targets

The full source of the extension:

```csharp
public static IResourceBuilder<T> WithAzureContainerRegistry<T>(
    this IResourceBuilder<T> builder,
    IResourceBuilder<AzureContainerRegistryResource> registryBuilder)
    where T : IResource, IComputeEnvironmentResource
{
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(registryBuilder);

    builder.WithAnnotation(
        new ContainerRegistryReferenceAnnotation(registryBuilder.Resource));

    return builder;
}
```

It does **not** call `WithContainerRegistry`, **not** mutate `WithImagePushOptions`, **not** touch workloads at all. It puts a single annotation on the compute environment (CAE). The downstream wiring is:

- The default push step (in `Aspire.Hosting`) iterates compute workloads, finds the workload's owning environment, reads the env's `ContainerRegistryReferenceAnnotation` (or, if absent, falls back to `RegistryTargetAnnotation` stamped by `SubscribeToAddRegistryTargetAnnotations`), and uses that `IContainerRegistry` as the push target via `ContainerImagePushOptions.GetFullRemoteImageNameAsync(registry, ct)`.
- The login step's effect persists at the container-runtime level (docker/podman config) for the lifetime of the publish process, so `imageManager.PushImageAsync` just works.

Implication for us: the **registry is owned by the environment, not by each workload**. Coolify's analogous shape is "registry is owned by the project (or the AppHost-as-CAE-equivalent)".

## 3. ACR-side provisioning + URL discovery

The ACR loginServer comes back from Bicep. Inside `AzureContainerRegistryResource`:

- `RegistryEndpoint` is a `BicepOutputReference("loginServer", this)`. It is unresolved at model-build time and resolved at run-time after the ACR's deployment completes.
- `IContainerRegistry.Endpoint => ReferenceExpression.Create($"{RegistryEndpoint}")` — so consumers (the default push step, the Bicep emitter for the CAE) get a `ReferenceExpression` that the materialiser fills in from the Bicep output.

The image manager learns the URL via `ContainerImagePushOptions.GetFullRemoteImageNameAsync(registry, ct)` — that helper awaits `registry.Endpoint.GetValueAsync(ct)`, which awaits the bicep output, which is set only after provisioning completes. Because the `login-to-acr-{name}` step `dependsOn(provisionSteps)`, by the time anything pushes, the endpoint is materialised.

## 4. ACR auth

`AzureContainerRegistryHelpers.LoginToRegistryAsync` (called by the `acr-login` step):

```csharp
var acrLoginService = context.Services.GetRequiredService<IAcrLoginService>();
var tokenCredential = context.Services.GetRequiredService<ITokenCredentialProvider>()
                              .TokenCredential;
var registryEndpoint = await registry.RegistryEndpoint.GetValueAsync(ct);
var tenantId         = provisioningContext.Tenant.TenantId?.ToString();
await acrLoginService.LoginAsync(registryEndpoint, tenantId, tokenCredential, ct);
```

`IAcrLoginService` shells out to `az acr login --name {endpoint}` (or the equivalent OAuth dance), which writes a docker `config.json` cred-helper entry for `*.azurecr.io`. After that, `docker push` / the image-manager push call authenticates implicitly.

Important: **the manager itself never sees a credential argument**. The credential is installed into the host docker daemon's config before push runs. This is by design — it sidesteps Aspire having to know about every registry's auth scheme.

For pks-agent-registry: same trick applies. We need an analogous step `coolify-registry-login-{name}` that runs after the registry is reachable, executes `docker login {registry-fqdn} -u {admin-user} -p {admin-token}` (or writes config.json directly), and is `requiredBy: push-prereq` (or our equivalent `coolify-push`).

## 5. CAE-side: how is the workload application created with the right image?

From `ContainerAppContext.cs` / `BaseContainerAppContext.cs`:

```csharp
if (!TryGetContainerImageName(Resource, out var containerImageName))
{
    AllocateContainerRegistryParameters();
    containerImageParam = AllocateContainerImageParameter();
}
containerAppContainer.Image = containerImageParam is null
    ? containerImageName!
    : containerImageParam;
```

`AllocateContainerRegistryParameters` plumbs `_containerAppEnvironmentContext.Environment.ContainerRegistryUrl` and `ContainerRegistryManagedIdentityId` into the Bicep parameter list, and the resulting `containerImageParam` is a Bicep `param` whose value is `{loginServer}/{repository}:{tag}` — assembled at materialisation time from the registry's `RegistryEndpoint` + the workload's `WithRemoteImageName` / `WithRemoteImageTag`.

The CAE then sets `configuration.Registries` to `[{ Server = registryUrlParam, Identity = mgmtIdentityParam }]`, so the Bicep-generated Container App knows how to *pull* from ACR (managed-identity pull, not docker login).

So the deploy path uses **two** auth flows: push uses `az acr login` shimmed into docker; pull uses managed identity attached to the Container App.

For Coolify with pks-agent-registry: pull-side auth is configured via Coolify's "private registry" API (we already have `IPrivateRegistriesApi.cs`). The Coolify application body needs `registry_id` (or equivalent) pointing at the private-registry record that holds the admin token for pks-agent-registry.

## 6. Five-phase analogy for Coolify

| Azure step (effective order) | Today's Coolify step | Change needed for in-project registry |
|---|---|---|
| `validate-azure-container-apps` (req-by `before-start`) | `coolify-configure` (incl. containerisability filter) | Add a guard: if any `pks-agent-registry` resource is in the graph and any workload `WithContainerRegistry(...)` references it, mark the graph as "in-project-registry mode". |
| `provision-infrastructure-{cae}` + `provision-infrastructure-{acr}` (tagged `provision-infrastructure`, req-by `deploy-prereq`) | **MISSING** — no equivalent today | **NEW** step `coolify-prereq` (or split: `coolify-deploy-registry`) that (a) creates the Coolify project + envs, (b) upserts the `pks-agent-registry` Coolify application, (c) triggers its deploy, (d) polls until reachable. `requiredBy: coolify-build`. Lives in `CoolifyDeployingPublisher.RunPhaseAsync(Configure)` extension or a new phase enum value. |
| `login-to-acr-{name}` (tagged `acr-login`, deps-on provision, req-by `push-prereq`) | **MISSING** | **NEW** step `coolify-registry-login-{name}` (one per in-project registry) that runs `docker login` against the registry's resolved Coolify FQDN with the admin token from `pks-agent-registry`'s settings. File: new `CoolifyRegistryLoginHook.cs` analogous to `ReferenceWiringHook.cs`; wired in `CoolifyBuilderExtensions.AddPksAgentRegistry`. |
| default `build` step (calls `IResourceContainerImageManager.BuildImagesAsync`) | `coolify-build` (FT-014, manager-driven) | Same — but `WithRemoteImageName` must be re-tagged so the registry-prefix is the in-project registry's FQDN. We already prepend `IContainerRegistry.Endpoint`; the only change is that `Endpoint` must now be a `ReferenceExpression` whose materialisation awaits the registry-deploy step. Implement `PksAgentRegistryResource.Endpoint` as a deferred reference (mirror `BicepOutputReference`) backed by the Coolify-assigned FQDN read in `coolify-prereq`. File: new `PksAgentRegistryResource.cs`. |
| default `push` step (calls `PushImageAsync`) | `coolify-push` | Already manager-driven (FT-014). No code change beyond the dependsOn edge: `coolify-push` must `dependsOn` the new `coolify-registry-login-*` steps. Add that wiring in `CoolifyBuilderExtensions.WithCoolifyDeploy` (lines 83-109). |
| workload deploy (Bicep param `containerImageParam` = `{loginServer}/{name}:{tag}`) | `coolify-deploy` (FT-013) | When creating each Coolify Application, fill `docker_registry_image_name = "{registryFqdn}/{repo}:{tag}"` and `private_registry_uuid = {in-project registry's coolify private-registry uuid}`. File: `CoolifyDeployingPublisher.cs` deploy branch + `ICoolifyDeployApi.cs` payload shape. |
| `coolify-verify` | unchanged | unchanged |

### Concrete file-by-file edits

- `src/Aspire.Hosting.Coolify/CoolifyPhase.cs` — insert `Prereq` between `Configure` and `Build`. Add `RegistryLogin` between `Build` and `Push`, OR keep `RegistryLogin` as a per-registry resource-emitted step (preferred — matches Azure pattern).
- `src/Aspire.Hosting.Coolify/CoolifyBuilderExtensions.cs` — new `AddPksAgentRegistry(name)` extension returning `IResourceBuilder<PksAgentRegistryResource>`; new `WithContainerRegistry<T>(this builder, PksAgentRegistryResource)` (or rely on the existing `WithContainerRegistry` from `Aspire.Hosting` since `PksAgentRegistryResource` will implement `IContainerRegistry`).
- New `src/Aspire.Hosting.Coolify/PksAgentRegistryResource.cs` — implements `IContainerRegistry`. `Name` and `Endpoint` are `ReferenceExpression`s backed by a `TaskCompletionSource<string>` populated by the prereq step.
- New `src/Aspire.Hosting.Coolify/CoolifyRegistryDeploymentStep.cs` (or fold into `CoolifyDeployingPublisher.RunRegistryPrereqAsync`) — deploys the registry Coolify application first, polls, then completes the TCS.
- `src/Aspire.Hosting.Coolify/CoolifyDeployingPublisher.cs` — in deploy branch, when emitting a Coolify Application body for a workload that references a `PksAgentRegistryResource`, set `private_registry_uuid` from a lookup table populated during prereq.

## 7. Recommended FT-016 shape

- **Title**: "In-project image registry: deploy a `pks-agent-registry` resource ahead of workload build/push"
- **One-sentence description**: Add an `AddPksAgentRegistry` resource type implementing `IContainerRegistry`, plus a new pre-build pipeline phase that deploys the registry's Coolify application and resolves its runtime FQDN before `coolify-build` / `coolify-push` execute.
- **Depends on**: FT-014 (canonical `IResourceContainerImageManager` adoption — already merged), FT-015 (helper for `WithContainerRegistry` plumbing).
- **Open questions** (for the author session):
  1. **URL discovery**: three options — (a) trust Coolify's auto-FQDN (`{appUuid}.{coolify-host}`) reading it from the create-application response; (b) require the user to set the registry's domain explicitly via `WithDomain("registry.example.com")`; (c) expose it as an env-var the registry app emits and read via Coolify's `GET /applications/{uuid}/envs`. Recommend (a) for v1 with fallback to (b).
  2. **Auth shape**: pks-agent-registry exposes an admin token from its own settings (see `projects/pks-agent-registry/`). Should we surface it as an `IResourceBuilder<ParameterResource>` arg to `AddPksAgentRegistry`, or auto-generate and persist? Recommend arg-style (matches `WithCoolifyDeploy`'s `token` parameter).
  3. **Reachability check**: simple `HEAD {registryFqdn}/v2/` or wait on Coolify's deploy-finished webhook? Recommend the former — registry-API-spec-compliant probe is cheaper and self-contained.
  4. **Multiple registries per AppHost**: Azure permits zero or one default + N explicit. Should we cap at one in v1? Recommend: support N, but emit a `W_…` warning if a workload has no edge and >1 registry exists.
- **Test cases** (TC ideas):
  - TC-FT-016-01: `AddPksAgentRegistry("reg")` adds a `PksAgentRegistryResource` to the graph that implements `IContainerRegistry`.
  - TC-FT-016-02: The publisher orders steps `coolify-prereq → coolify-registry-login → coolify-build → coolify-push → coolify-deploy`.
  - TC-FT-016-03: `coolify-registry-login` is skipped (no-op) when no `PksAgentRegistryResource` is in the graph.
  - TC-FT-016-04: Workload `WithContainerRegistry(registry)` causes `coolify-push` to push to `{registryFqdn}/{name}:{tag}`.
  - TC-FT-016-05: Coolify application body for the workload carries `private_registry_uuid` pointing at the in-project registry's private-registry record (mock `IPrivateRegistriesApi`).
  - TC-FT-016-06: Registry-deploy failure surfaces as `E_PREREQ_REGISTRY_DEPLOY_FAILED`, halts the pipeline before build.
  - TC-FT-016-07: FQDN resolution timeout surfaces as `E_PREREQ_REGISTRY_UNREACHABLE`.

## Recommendation

**Do FT-016 as a methodology-pure full FT, but split the URL-discovery question into a v1 limitation explicitly.** The Azure exemplar is genuinely two concerns stitched by annotations: (a) order — the registry must provision before push, (b) credentials — push needs docker-login state in place before push runs. Both map cleanly onto Coolify primitives we already have (deploy-trigger + private-registries API). The only piece where the Coolify side is meaningfully harder than Azure is the URL: Azure has `BicepOutputReference("loginServer")` as a typed deferred reference, while Coolify's auto-FQDN is only knowable after `POST /applications` returns (or after the first deploy completes, depending on Coolify version). For v1, accept the limitation that the in-project registry resource resolves its FQDN at `coolify-prereq` time and stores it on the resource — and document that pre-configuring `WithDomain(...)` is the supported escape hatch if Coolify's auto-FQDN proves flaky. That keeps FT-016 a single, methodology-pure feature instead of fragmenting it into separate "registry resource", "URL resolver", "private-registry plumbing" sub-FTs.

## Sources

- [src/Aspire.Hosting.Azure.AppContainers/AzureContainerAppExtensions.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.AppContainers/AzureContainerAppExtensions.cs)
- [src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryResource.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryResource.cs)
- [src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryExtensions.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryExtensions.cs)
- [src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryHelpers.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.ContainerRegistry/AzureContainerRegistryHelpers.cs)
- [src/Aspire.Hosting.Azure.AppContainers/BaseContainerAppContext.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.AppContainers/BaseContainerAppContext.cs)
- [src/Aspire.Hosting.Azure.AppContainers/ContainerAppContext.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Azure.AppContainers/ContainerAppContext.cs)
- [src/Aspire.Hosting/Publishing/WellKnownPipelineSteps.cs](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting/Publishing/WellKnownPipelineSteps.cs)
- [PR #8718 — Capture container registry information in CAE](https://github.com/dotnet/aspire/pull/8718)
- [Custom deployment pipelines — aspire.dev](https://aspire.dev/deployment/custom-deployments/)
- [Pipe dreams to pipeline realities — Aspire blog](https://devblogs.microsoft.com/aspire/aspire-pipelines/)
- [captainsafia/aspire-image-push](https://github.com/captainsafia/aspire-image-push)
