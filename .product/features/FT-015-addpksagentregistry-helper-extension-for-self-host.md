---
id: FT-015
title: AddPksAgentRegistry helper extension for self-hosted registry workloads
phase: 2
status: complete
depends-on:
- FT-014
adrs:
- ADR-007
- ADR-001
- ADR-006
- ADR-003
- ADR-004
tests:
- TC-030
- TC-031
domains:
- aspire-publisher
- image-flow
domains-acknowledged: {}
---

## Description

FT-015 adds a thin, in-package convenience extension —
`AddPksAgentRegistry(this IDistributedApplicationBuilder builder,
string name)` — to `Aspire.Hosting.Coolify` that wires a
self-hosted Docker registry into an Aspire 13 AppHost using
**only** Aspire's built-in `AddContainer` and `AddContainerRegistry`
primitives. The helper exists so that an AppHost can write **one
line** to obtain (a) a running registry container the local dev
loop can push to, and (b) a `ContainerRegistryResource` that the
Coolify publisher's FT-014 read path will consume as the push
target for sibling workloads — with no new abstractions, no new
NuGet package, and no surface beyond what Aspire 13 already
exposes.

The image baked into the helper is
`ghcr.io/pksorensen/pks-agent-registry` — a downstream of the
upstream `registry:2` image. The helper publishes an HTTP endpoint
on container port 5000, bind-mounts `./.data/<name>/` into the
container's storage root so the registry's contents survive an
AppHost restart, and calls `AddContainerRegistry(name,
"localhost:5000")` on the same builder so workloads attached via
`WithContainerRegistry(reg)` push to the running container.

The helper returns a tuple `(IResourceBuilder<ContainerResource>
container, IResourceBuilder<ContainerRegistryResource> registry)`
so the caller can keep both handles — typically to attach the
registry to project resources and to express a `WaitFor(container)`
ordering edge.

FT-015 deliberately defines no new error symbols, no new domain
vocabulary, and no new persistent state on the publisher. It
depends on FT-014: the returned `ContainerRegistryResource` is
only meaningfully consumed by the Coolify publisher once FT-014's
native read path is in place.

## Functional Specification

### Inputs

- `IDistributedApplicationBuilder builder` — the AppHost builder
  the helper attaches both resources to.
- `string name` — a logical name used as **both** the container
  resource name and the `ContainerRegistryResource` name. The
  bind-mount path is derived as `./.data/<name>/`. Two callers
  passing the same `name` against the same builder converge on
  the same pair of resources.

### Outputs

- A tuple `(IResourceBuilder<ContainerResource>,
  IResourceBuilder<ContainerRegistryResource>)`.
- The container resource is registered on the builder, runs
  `ghcr.io/pksorensen/pks-agent-registry`, exposes an HTTP
  endpoint on container port 5000, and bind-mounts
  `./.data/<name>/` to the container's storage root
  (`/var/lib/registry` for the `registry:2`-lineage image).
- The registry resource is registered on the builder, named
  `name`, and has `Address = "localhost:5000"` so the Coolify
  publisher (FT-014) reads it as the push target for any
  workload attached via `WithContainerRegistry(...)`.

### State

- **No publisher-instance state.** FT-015 does not add fields to
  `CoolifyDeployingPublisher`. Both returned handles live in the
  Aspire resource graph just like any other `AddContainer` /
  `AddContainerRegistry` call site.
- **On-disk state under `./.data/<name>/`.** The registry
  container persists its blobs and manifests there across
  AppHost restarts. The directory is created lazily by Aspire's
  bind-mount machinery; FT-015 does not pre-create or clean it
  up.

### Behaviour

#### 1. Container resource registration

The helper calls `builder.AddContainer(name,
"ghcr.io/pksorensen/pks-agent-registry")` and chains:

- `.WithHttpEndpoint(port: 5000, targetPort: 5000, name:
  "registry")` so host port `5000` maps to container port
  `5000`. The endpoint is named `"registry"` so callers can
  `WaitFor(container.GetEndpoint("registry"))` if desired.
- `.WithBindMount("./.data/" + name, "/var/lib/registry")` so
  the registry's storage root persists across restarts.

The exact image tag (`:latest` vs version-pinned) is
**property-only at this layer** — see §Out of scope.

#### 2. ContainerRegistry resource registration

The helper calls `builder.AddContainerRegistry(name,
"localhost:5000")`. The `Address` string is the bare authority
`localhost:5000` (no scheme, no trailing slash) so the FT-014
tag composer emits
`localhost:5000/<workload.Name>:<apphost-version>` and the push
pipeline targets the running container on the host loopback.

The two resources are deliberately **not** linked by a
`WaitFor` edge inside the helper — that ordering is the
caller's choice.

#### 3. Idempotency on repeat calls

If `AddPksAgentRegistry(builder, name)` is called more than
once with the same `(builder, name)` pair, the helper does
**not** register duplicate resources. Implementation chooses
one of:

- **Last-call-wins**: the helper looks up the existing pair on
  the builder and returns the existing handles (preferred —
  cheapest and matches FT-014's shim convergence discipline).
- **No-op-after-first**: the second call returns the same
  handles the first call returned without mutating the graph.

Either way the observable invariant is the same: at most one
container named `name` and at most one
`ContainerRegistryResource` named `name` exist in the graph
regardless of how many times the helper was called.

Two calls with **different** `name` values produce two
independent pairs in the graph.

### Invariants

- **I-1: helper returns both handles.** Every successful
  invocation returns a non-null tuple where both elements are
  live `IResourceBuilder<…>` handles registered on the
  argument builder.
- **I-2: registry resource address is `localhost:5000`.** The
  returned `ContainerRegistryResource`'s `Address` property is
  exactly the string `"localhost:5000"`.
- **I-3: bind-mount is scoped to `name`.** Distinct `name`
  values produce distinct bind-mount paths under `./.data/`.
- **I-4: at-most-one resource per name.** Calling the helper
  `N` times for the same `(builder, name)` produces exactly
  one container resource named `name` and exactly one registry
  resource named `name` on the builder.
- **I-5: no new error symbols.** FT-015 introduces no new
  `E_…` symbols. Failures surface as the Aspire SDK's own
  diagnostics.
- **I-6: composes with FT-014.** The returned registry handle,
  when attached to a workload via `WithContainerRegistry(...)`,
  is consumed verbatim by the FT-014 publisher read path.

### Error handling

- **`ArgumentNullException`** on null `builder` or `name`.
- **`ArgumentException`** on an empty or whitespace-only
  `name`.
- **Port collisions / image-pull failures** surface as the
  Aspire SDK's own resource-lifecycle exceptions. FT-015 does
  not wrap them.

### Boundaries

- **In scope for FT-015:**
  - the `AddPksAgentRegistry(builder, name)` extension method
    in `Aspire.Hosting.Coolify`
  - calling `AddContainer` with the `pks-agent-registry`
    image, the `:5000` HTTP endpoint, and the `./.data/<name>/`
    bind-mount
  - calling `AddContainerRegistry(name, "localhost:5000")` on
    the same builder
  - returning the `(container, registry)` handle pair
  - idempotency on repeat `(builder, name)` calls
  - the two TCs scaffolded alongside this feature
- **Out of scope for FT-015:**
  - the exact image tag (`:latest` vs version-pinned)
  - auth / TLS configuration on the registry container
  - a typed `Aspire.Hosting.PksAgentRegistry` NuGet package
  - GC / pruning of `./.data/<name>/`
  - the FT-014 native read path itself
  - TypeScript AppHost parity for `AddPksAgentRegistry`

## Out of scope

- **Exact Docker image tag.** FT-015 is property-only on the
  tag — the implementation picks `:latest` or a version-pinned
  tag per its own judgement at implementation time. A TC does
  **not** assert which tag was used.
- **Auth configuration on the registry.** Anonymous-push by
  default; configurable auth is a future feature.
- **TLS termination.** Plain HTTP on `localhost:5000` only.
- **Coolify-side consumption.** FT-014 owns "the publisher
  reads the workload→registry edge"; FT-015 owns only the
  registration of the registry resource.
- **Container teardown.** Aspire's normal container-resource
  lifecycle handles it.
