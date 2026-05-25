---
id: ADR-007
title: Adopt Aspire native container-registry primitives as single source of truth for push target (v1)
status: accepted
features:
- FT-003
- FT-004
- FT-014
- FT-015
- FT-016
supersedes: []
superseded-by: []
domains:
- aspire-publisher
- image-flow
scope: cross-cutting
content-hash: sha256:f27c66ed00e44642d8ea5a9a5421635b004d2736f23a8441c7c9c5e6d2c01513
---

## Context

ADR-005 (image registry strategy, accepted) introduced a Coolify-
specific extension `WithImageRegistry(prefix, [username, password])`
that captures three Aspire parameter handles on the publisher
instance. FT-003 (build) and FT-004 (push + configure-phase Coolify
Private Registry upsert) are both implemented against that surface
and against the assumption that the publisher carries one
`(prefix, username, password)` triple as a private property of the
Coolify deployer.

Two pieces of evidence collected after FT-013 (the concrete
`ICoolifyClient` HTTP implementation) landed force this decision
back open:

1. **End-to-end smoke testing** against a live Coolify instance
   produced the expected per-deploy artefacts (tagged image at the
   developer's registry, Coolify-side Private Registry record,
   service upsert, deploy-action). The mechanism works. What it
   does *not* compose with is the very next case on the brief's
   roadmap: "the developer wants to deploy a self-hosted
   `registry:2` *as a workload of this same AppHost*." Because
   `WithImageRegistry(...)` takes a `string` parameter prefix and
   not a `ContainerRegistryResource` reference, the registry the
   publisher pushes to cannot itself be an Aspire resource in the
   same graph. There is no way to express "push the rest of the
   workloads *to this registry I just declared two lines above.*"

2. **Research into the Aspire 13 surface** (captured in
   `docs/aspire-registry-package-research.md`) shows that Aspire
   ships native primitives for precisely this concern:

   - `AddContainerRegistry(name, address)` — declares a
     `ContainerRegistryResource` in the resource graph, addressable
     by other resources and other publishers.
   - `WithContainerRegistry(resource, registry)` — attaches a
     workload resource to a registry resource via a typed edge in
     the graph, expressing "this workload is pushed to that
     registry."

   These primitives are how every other first-party Aspire
   publisher reads its push target: Azure Container Apps reads the
   destination ACR from the `ContainerRegistryResource` attached
   to the workload, the AWS publisher does the same against ECR,
   and the Docker Compose publisher honours the same edge when
   present. The Aspire 13 ecosystem has converged on this as the
   canonical channel for "where does this image go."

The Coolify publisher's `WithImageRegistry(...)` is therefore not
just a custom shape — it is a *duplicate* of an existing,
canonical Aspire mechanism. The duplication has three concrete
costs:

- **It forecloses on the "registry as a workload" case** (point 1
  above). A string prefix cannot reference a resource.
- **It splits the mental model.** A developer who already knows
  Aspire's container-registry pattern from Azure Container Apps
  has to learn a parallel pattern for Coolify, and an AppHost that
  targets *both* (multi-publisher) has to declare its registry
  identity twice in two different shapes.
- **It is a maintenance burden.** Every future cross-cutting
  Aspire concern around registries (credential rotation surface,
  pull-secret synthesis, registry-resource health checks, registry
  drift detection) will appear on the native primitives first. The
  Coolify publisher would have to re-port each of them onto its
  parallel surface, indefinitely.

ADR-005's accepted decision-text is otherwise unchanged: registry-
agnostic, developer-chosen, publisher-push; deterministic tag scheme
(`<host>/<resource>:<apphost-version>`); no `latest`; Coolify-side
Private Registry record upsert when credentials are present; no
auto-spun in-Coolify registry; no SSH-coupled bypass. What changes
is the *channel* the publisher reads the push target from, and the
*public surface* the AppHost author calls.

This ADR amends FT-003 and FT-004 — both of which are tagged
`complete` against the v1 surface — and is therefore cross-cutting.
A bookkeeping pass to update the feature bodies is required as part
of the implementation work that follows this ADR; the test
criteria (TC-005, TC-007, TC-008) need adjustment to assert the
new edge-based reading and the deprecation-shim behaviour rather
than the old triple-on-publisher reading.

## Decision

**The Coolify publisher reads its image-push target from the Aspire
resource graph via `ContainerRegistryResource` and the workload→
registry edge established by `WithContainerRegistry(...)`. This is
the same mechanism the Azure Container Apps and AWS Container
publishers use, and it becomes the single source of truth for v1.**

Concretely:

1. **Canonical AppHost shape.** The supported v1 call site becomes:

   ```csharp
   var registry = builder.AddContainerRegistry("ghcr", "ghcr.io/pksorensen");
   // (credentials supplied through whatever surface
   //  ContainerRegistryResource exposes — username/password
   //  parameters, or a credential resource — per Aspire 13's API)

   var api = builder.AddProject<Projects.Api>("api")
                    .WithContainerRegistry(registry);

   builder.AddDockerComposeEnvironment("env")
          .WithCoolifyDeploy(coolifyUrl, coolifyToken);
   ```

   The publisher discovers each workload's registry by following
   the workload→registry edge in the resource graph at the start
   of the configure phase. Workloads that lack an edge fail fast
   per the existing `E_REGISTRY_NOT_CONFIGURED` symbol (no global
   default — ADR-005's "explicit or fail" floor is preserved,
   only the channel changes).

2. **Per-workload registry targets are first-class.** Different
   workloads in the same AppHost may push to different registries
   simply by attaching different `ContainerRegistryResource`s.
   ADR-005's example call site (one registry for all workloads)
   remains expressible — attach the same registry resource to
   every workload — but is no longer the *only* expressible shape.

3. **In-graph registry workloads are unblocked.** Because the
   registry is a resource, an AppHost may declare a `registry:2`
   container resource and reference it as the push target for
   sibling workloads. The publisher orders the deploy graph so
   the registry resource is created and reachable before the
   first `push` is attempted (concrete ordering is FT-005's
   concern, which already sequences resources by dependency
   edges).

4. **`WithImageRegistry(prefix, [username, password])` becomes a
   deprecated shim.** Its public signature is unchanged. Its
   implementation is rewritten to:

   1. Call `AddContainerRegistry(syntheticName, prefix-as-address)`
      on the builder, where `syntheticName` is a deterministic
      string derived from the prefix (e.g. `"coolify-legacy-" +
      stableHash(prefix)`), so repeated calls converge on the
      same synthetic resource.
   2. If `username` / `password` are supplied, attach them to the
      synthetic registry resource through whatever credential
      surface `ContainerRegistryResource` exposes in Aspire 13.
   3. Call `WithContainerRegistry(workload, syntheticRegistry)`
      on *every* workload that would otherwise have had no
      explicit registry edge, so that pre-existing AppHosts
      compiled against the v1.x surface continue to deploy with
      identical behaviour. (The shim emits a one-time build-time
      `[CS0618]`-style obsolete warning on the extension call;
      AppHosts that opt to keep using it continue to work.)
   4. The publisher itself contains **no** branch on
      "was-it-called-via-shim-or-natively" — it reads the
      workload→registry edge in both cases. The shim's only job
      is to produce that edge.

5. **Deprecation policy.** `WithImageRegistry(...)` is marked
   `[Obsolete("Use AddContainerRegistry + WithContainerRegistry —
   see ADR-007.", error: false)]` in the next published version of
   `pks-aspire-coolify`. It is **not** removed in any v1.x
   release. A future v2.0 may remove it, gated by a separate ADR;
   v1.x preserves source compatibility with every AppHost that
   compiled against ADR-005's surface.

6. **Configure-phase Coolify Private Registry upsert is unchanged
   in intent.** The upsert keyed by `(host, username)` and tagged
   `managed-by: pks-aspire-coolify` (ADR-005 §D5, FT-004) still
   runs once per *distinct* `(host, username)` pair discovered
   across all workloads' registry edges. Workloads attached to
   the same registry resource produce one upsert; workloads
   attached to different registries produce one upsert each.

7. **The four `E_…` symbols introduced by FT-003 / FT-004 are
   preserved verbatim** as observable contract. Their *triggers*
   are restated against the new channel:
   - `E_REGISTRY_NOT_CONFIGURED` — a workload has no registry
     edge (and the shim did not synthesise one for it).
   - `E_APPHOST_VERSION_MISSING`, `E_IMAGE_BUILD_FAILED`,
     `E_BUILD_PHASE_UNEXPECTED`, `E_REGISTRY_AUTH_FAILED`,
     `E_IMAGE_PUSH_FAILED`, `E_PUSH_PHASE_UNEXPECTED`,
     `E_COOLIFY_REGISTRY_UPSERT_FAILED` — meaning unchanged;
     only the source of the `host` / credentials in the
     structured field block changes (now read from the
     `ContainerRegistryResource`).

8. **Tag scheme (ADR-005 §D4) is unchanged.** Each containerisable
   resource emits one tag of shape `<registry-address>/<resource-
   name>:<apphost-version>`, where `<registry-address>` is the
   `ContainerRegistryResource.Address` of the registry attached
   to that workload. No `latest` is pushed (ADR-005 §6, FT-003
   I-2 — re-asserted). The address replaces "prefix" in the tag
   formula; the formula is otherwise identical.

## Rationale

- **Alignment with the Aspire ecosystem.** Aspire 13 has converged
  on `ContainerRegistryResource` + `WithContainerRegistry(...)` as
  the canonical channel for push targets, and every other
  first-party publisher reads from it. By adopting the same
  channel, the Coolify publisher inherits every future Aspire-side
  improvement to registry handling (credential surfaces, drift
  signals, multi-arch helpers) without re-porting. The brief's
  posture is "use Aspire's primitives where they exist"; ADR-005
  predated this primitive's clarity, and now that the primitive
  is visible the right move is to adopt it.
- **Unblocks the "deploy a registry as the first service" case.**
  This is the next concrete user story on the roadmap and the
  most-asked homelab pattern (one AppHost that brings up a
  `registry:2`, then deploys sibling workloads through it). A
  string-prefix surface cannot express it; a resource-reference
  surface does so trivially.
- **Single channel eliminates duplicate state.** With the shim
  routed through the native primitives, the publisher has one
  code path for "which registry does this workload push to." No
  dual reads, no shim-vs-native branching inside the configure /
  build / push phases. The duplication ADR-005's surface
  introduced is paid down completely.
- **Source-compatibility for v1.x AppHosts is preserved.** Every
  AppHost that compiled against ADR-005's `WithImageRegistry(...)`
  continues to compile and deploy with identical observable
  behaviour. The shim produces exactly the same end-state — one
  synthetic registry resource with the prefix as address, the same
  Coolify-side Private Registry record, the same per-resource
  tags — so users who do not migrate experience nothing but a
  one-line obsolete warning at build time.
- **Per-workload registry targets are a free bonus.** Once the
  publisher reads from workload→registry edges, the long-standing
  "what if my dashboard and my app belong to different registries"
  request becomes naturally expressible. We did not seek this
  feature; the native primitive gives it to us.
- **Cross-cutting amendment is the honest framing.** FT-003 and
  FT-004 are both tagged `complete` against the old surface. The
  channel change is a behavioural amendment to two completed
  features; ADR-007 marks itself `cross-cutting` so the spec
  graph reflects that two feature bodies and three test criteria
  need bookkeeping follow-up.

## Rejected alternatives

### (i) Keep `WithImageRegistry(...)` as the only surface (status quo)

Leave ADR-005's surface as-is, accept that "deploy a registry as
a workload" is not expressible, and document the limitation.

**Rejected because:** the limitation is exactly the next user
story on the roadmap. Deferring it means either (a) writing a
future ADR that *does* adopt the native primitives but on top of
a now-larger deprecated surface, or (b) writing a *second*
custom extension (`WithImageRegistryResource(...)` or similar)
that duplicates the native primitive in a third shape. Both
outcomes are worse than amending now. The status quo also leaves
every cross-publisher Aspire improvement in the registry area
behind a porting tax indefinitely.

### (ii) Dual-channel forever — accept both `WithImageRegistry(...)` and `WithContainerRegistry(...)` as first-class

Keep both surfaces on equal footing. The publisher reads from
either: if the workload has a `WithContainerRegistry(...)` edge,
use it; otherwise fall back to the publisher's
`WithImageRegistry(...)` triple. No deprecation, no shim — both
are supported permanently.

**Rejected because:** dual-channel reads guarantee permanent
duplication in the publisher's read path, in the documentation
("two ways to declare a registry — here is when to use each"), in
the diagnostic shape (the `host` field can come from two
different sources, complicating "which line of the AppHost is
wrong"), and in the credential-resolution code (two sets of
parameter handles, two redaction surfaces, two places to forget a
test). The cost of "remember to update both" compounds with every
future registry concern. A shim that produces the native edge has
the same source-compat properties without the dual-read tax.

### (iii) Hard-break — remove `WithImageRegistry(...)` immediately

Delete `WithImageRegistry(...)` in the next release. Every
existing AppHost must migrate to the native primitives or fail
to compile.

**Rejected because:** v1 is published. Source-breaking the public
surface of a published v1 package without a deprecation cycle
violates the implicit contract Aspire-ecosystem packages keep
with their consumers, and produces no benefit over the shim path
beyond saving a small amount of shim code. The shim is ~20 lines;
the goodwill it preserves is unbounded. A future v2.0 may remove
the shim; v1.x does not.

### (iv) Introduce a Coolify-specific `WithCoolifyImageRegistry(ContainerRegistryResource)` overload alongside the string overload

Add a new overload of `WithImageRegistry(...)` (or a sibling
method) that takes a `ContainerRegistryResource` directly,
without adopting the native edge in the publisher's read path.

**Rejected because:** this is alternative (ii) wearing a slightly
different costume — the publisher would still read from a
Coolify-specific channel (now of polymorphic type "either string
prefix or registry resource") instead of from the canonical
workload→registry edge. It preserves the duplication with the
Aspire ecosystem and adds a *new* shape to maintain. The whole
point of this decision is to align with the native channel; a
Coolify-specific overload that happens to *accept* the native
resource type still routes through a Coolify-specific read.

## Test coverage

Exit-criteria tests follow the FT-003 / FT-004 test shape and the
TC-005 / TC-007 / TC-008 grep-for-symbol discipline. New / amended
criteria:

- **Native primitive happy path.** Given an AppHost that declares
  `AddContainerRegistry("ghcr", "ghcr.io/acme")` and attaches it
  to two project workloads via `WithContainerRegistry(...)`, the
  publisher (a) discovers the registry from the resource graph,
  (b) emits exactly two tags of shape `ghcr.io/acme/<resource>:
  <apphost-version>`, (c) issues exactly one Coolify Private
  Registry upsert keyed by `("ghcr.io", <username>)` when
  credentials are present, and (d) pushes both images. No call
  to `WithImageRegistry(...)` is made; no synthetic registry
  resource appears.
- **Per-workload distinct registries.** Given two workloads
  attached to *different* `ContainerRegistryResource`s with
  different hosts, the publisher emits per-workload tags against
  each workload's registry address, and issues exactly one
  Coolify Private Registry upsert per distinct `(host, username)`
  pair (one or two upserts depending on whether the usernames
  match).
- **Shim source-compat.** Given an AppHost compiled against the
  v1.x `WithImageRegistry(prefix, user, pass)` surface and *no*
  other registry calls, observable behaviour matches a byte-for-
  byte snapshot of the v1.x reference deploy: same tag scheme
  (with `<prefix>` as the address), same Coolify Private
  Registry upsert, same image presence at the registry. The
  AppHost compiles with exactly one `[CS0618]`-style obsolete
  warning attributed to the `WithImageRegistry(...)` call site.
- **Shim + native composability.** An AppHost that calls
  `WithImageRegistry(prefix, ...)` *and* also explicitly attaches
  one workload via `WithContainerRegistry(...)` resolves to the
  explicit attachment for that workload and to the shim's
  synthetic registry for the others. No workload is double-
  attached; no diagnostic regression.
- **In-graph registry workload (the unblocked case).** An AppHost
  that declares a `registry:2` container resource, wraps it as a
  `ContainerRegistryResource` (or uses Aspire's helper for in-
  graph registries), and attaches sibling workloads to it
  deploys successfully end-to-end: the registry resource is
  created first, the sibling workloads' push targets resolve to
  the in-graph registry's address, and the Coolify-side state
  reflects exactly one `managed-by: pks-aspire-coolify` Private
  Registry record per distinct `(host, username)` pair.
- **`E_REGISTRY_NOT_CONFIGURED` triggered by missing edge.** A
  workload with neither `WithContainerRegistry(...)` nor a shim
  call covering it fails fast with `E_REGISTRY_NOT_CONFIGURED`
  in the build phase, citing the workload name and pointing at
  both the native and the (deprecated) shim shapes in the
  remediation block.
- **Redaction.** With a sentinel value injected into the registry
  password (whether supplied through the native credential
  surface or through the shim), no log line, dashboard parameter
  display, exception trace, or push-progress line contains the
  sentinel. (Re-asserts ADR-005's "Redaction" test and FT-004
  I-3 against the new channel.)
- **No `latest` ever.** Re-asserts FT-003 I-2 / FT-004 I-5 under
  the new read path: inspect the registry's tag list after push
  and grep push-phase logs for `:latest`; both must be empty.
- **TC-005 / TC-007 / TC-008 amended.** The three existing test
  criteria are updated (as bookkeeping follow-up to this ADR) to
  assert the new edge-based reading and the shim behaviour rather
  than the publisher's old `(prefix, username, password)` triple
  property.

## Consequences

- `WithImageRegistry(...)` is `[Obsolete]` in the next release;
  the shim implementation routes through `AddContainerRegistry`
  + `WithContainerRegistry` to preserve v1.x AppHost behaviour
  byte-for-byte.
- FT-003 and FT-004 bodies require bookkeeping amendments to
  reflect the new channel (read from workload→registry edge
  rather than from publisher-instance triple). The four `E_…`
  symbols and the tag scheme are preserved verbatim as
  observable contract.
- The `coolify-api` domain's `PrivateRegistries` endpoint group
  (ADR-002 / FT-013) is unchanged. The configure-phase upsert
  step's *input* changes (per-distinct-`(host, username)` set
  derived from the resource graph rather than the publisher's
  one triple), not its endpoint shape.
- The "deploy a registry as a workload of the same AppHost" case
  becomes expressible without further surface work.
- A future v2.0 may remove the shim; that removal requires its
  own ADR. v1.x preserves source compatibility.
- Documentation and the `aspire-publisher` domain's user-facing
  examples migrate to the native shape; the shim shape is shown
  once as a "migration path from v1.x" example, with the obsolete
  warning called out.
