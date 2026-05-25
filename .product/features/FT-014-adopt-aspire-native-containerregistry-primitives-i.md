---
id: FT-014
title: Adopt Aspire native ContainerRegistry primitives in the publisher's push-target read path
phase: 1
status: complete
depends-on:
- FT-003
- FT-004
adrs:
- ADR-007
- ADR-001
- ADR-003
- ADR-004
- ADR-006
tests:
- TC-027
- TC-028
- TC-029
domains:
- aspire-publisher
- image-flow
domains-acknowledged: {}
---

## Description

FT-014 rewires the Coolify publisher's image-push target read path from
the publisher-instance triple `(prefix, username, password)` that FT-003
captured via `WithImageRegistry(...)` to the Aspire 13 native channel:
`ContainerRegistryResource` declared with `AddContainerRegistry(...)`
and attached to containerisable workloads via the
`WithContainerRegistry(...)` edge in the resource graph. The channel
change is the entirety of ADR-007's amendment to FT-003 (build) and
FT-004 (push + configure-phase Coolify Private Registry upsert).

After FT-014 lands the publisher has **one** code path for "which
registry does this workload push to": it follows the workload→registry
edge in the Aspire resource graph and reads
`ContainerRegistryResource.Address` for the tag prefix and whatever
credential surface the resource exposes for the push and the
configure-phase upsert. The pre-existing `WithImageRegistry(prefix,
[username, password])` extension introduced by FT-003 is preserved as
a **deprecated shim**: its public signature is unchanged, but its
implementation now synthesises an `AddContainerRegistry(syntheticName,
prefix)` on the builder and attaches *every* containerisable workload
that has no explicit `WithContainerRegistry(...)` edge to that
synthetic registry. The shim emits a one-time `[CS0618]`-style
obsolete warning at the call site; AppHosts that compiled against the
v1.x surface continue to deploy with byte-identical observable
behaviour.

This feature does **not** introduce new error symbols. The four `E_…`
symbols owned by FT-003 (`E_REGISTRY_NOT_CONFIGURED`,
`E_APPHOST_VERSION_MISSING`, `E_IMAGE_BUILD_FAILED`,
`E_BUILD_PHASE_UNEXPECTED`) and the four owned by FT-004
(`E_COOLIFY_REGISTRY_UPSERT_FAILED`, `E_REGISTRY_AUTH_FAILED`,
`E_IMAGE_PUSH_FAILED`, `E_PUSH_PHASE_UNEXPECTED`) are preserved
verbatim as observable contract. Their *triggers* are restated against
the new channel (specifically: `E_REGISTRY_NOT_CONFIGURED` now fires
when a containerisable workload has no `WithContainerRegistry(...)`
edge in the resource graph *and* the shim did not synthesise one).

The exit criteria are: (a) the publisher reads the push target by
enumerating the resource graph for `ContainerRegistryResource`s
reachable from each containerisable workload through the
`WithContainerRegistry(...)` edge, and never by reading a `(prefix,
username, password)` field on the publisher instance; (b) tags
emitted in build and reused in push are
`<ContainerRegistryResource.Address>/<resource.Name>:<apphost-version>`
exactly, with no `latest` under any code path; (c) the configure-phase
Coolify Private Registry upsert iterates the *distinct* `(host,
username)` set derived from the resource-graph edges, producing one
upsert per pair; (d) `WithImageRegistry(...)` still compiles, still
deploys with identical observable behaviour, and emits exactly one
obsolete warning attributable to its call site; (e) a containerisable
workload that has neither an explicit `WithContainerRegistry(...)`
edge nor an edge synthesised by the shim fails fast at build with
`E_REGISTRY_NOT_CONFIGURED`, citing the workload name and pointing
the user at both the native shape and the deprecated shim shape in
the remediation block.

## Functional Specification

### Inputs

- The Aspire 13 resource graph reachable from the running
  `DistributedApplication` at deploy time. FT-014 reads the graph
  through whatever enumeration API Aspire 13's `IDeployingPublisher`
  context exposes for `ContainerRegistryResource`s and for the
  workload→registry edge established by `WithContainerRegistry(...)`.
  **The precise enumeration property / accessor on Aspire 13's
  publisher context is named at implementation time** against the SDK
  version pinned by FT-001's source files. FT-014's contract is
  property-only on the channel ("workloads carry a registry edge, the
  publisher follows it") and not on the exact call shape.
- The set of containerisable workloads passed to the build and push
  phases by the publisher driver (the same set FT-003 / FT-004 iterate
  today; FT-009 owns containerisability filtering).
- Whatever credential surface `ContainerRegistryResource` exposes in
  Aspire 13 (username/password parameter handles, a credential
  sub-resource, or whatever shape Aspire's SDK ships). FT-014 reads
  this surface in the same two phases FT-004 read the
  `WithImageRegistry(...)` triple in: configure (for the Coolify-side
  Private Registry upsert) and push (for the push pipeline).
- The pre-existing `WithImageRegistry(prefix, [username, password])`
  extension introduced by FT-003. Its call sites in pre-existing
  AppHosts are FT-014's source-compat target.

### Outputs

- **On success:** the publisher deploys end-to-end with image tags
  shaped `<ContainerRegistryResource.Address>/<resource.Name>:<apphost-
  version>`, identical Coolify-side Private Registry record presence
  (one per distinct `(host, username)` pair across the graph), and no
  observable behavioural change for AppHosts compiled against the
  v1.x `WithImageRegistry(...)` surface — apart from the obsolete
  warning at the shim's call site.
- **On the shim's call site:** exactly one `[CS0618]`-style obsolete
  warning per call site, with text pointing at
  `AddContainerRegistry` + `WithContainerRegistry` and citing ADR-007.
- **On a workload with no registry edge:** `E_REGISTRY_NOT_CONFIGURED`
  fired at the start of the build phase, citing the workload name in
  the structured field block and listing both remediation shapes in
  the remediation block:

  ```
  E_REGISTRY_NOT_CONFIGURED: workload <name> has no container registry
    resource: <aspire-resource-name>
    see:      ADR-007
    remediation:
      var reg = builder.AddContainerRegistry("name", "host/prefix");
      builder.AddProject<...>("<name>")
             .WithContainerRegistry(reg);
      // or (deprecated, v1.x source-compat only):
      builder.AddDockerComposeEnvironment("env")
             .WithCoolifyDeploy(url, token)
             .WithImageRegistry("host/prefix", [user, pass]);
  ```

### State

- **No new persistent state on disk.** Inherits ADR-003 §4 / FT-001
  I-3 / FT-003 §State / FT-004 §State unchanged.
- **No new long-lived in-memory state on the publisher instance.**
  The publisher no longer captures `(prefix, username, password)` as
  its own fields (that capture happened in `WithImageRegistry(...)`
  per FT-003 §Behaviour §0); after FT-014, the publisher reads the
  resource graph fresh at the start of each phase that needs the
  push target, and the resolved address / credential strings are
  bounded to the local scope of that phase (matches FT-004 §State /
  §I-11).
- **Synthetic registry resources produced by the shim are part of the
  resource graph itself**, not a side store. They are created by
  the shim's call to `AddContainerRegistry(syntheticName, prefix)`
  and are addressable by the same enumeration API every other
  registry resource is addressable by. The shim's deterministic
  `syntheticName` (e.g. `coolify-legacy-<stableHash(prefix)>`)
  guarantees that two `WithImageRegistry(...)` calls with the same
  prefix converge on the same synthetic resource (matches ADR-007
  §Decision §4.1's "repeated calls converge").

### Behaviour

The phases that change are (a) the deprecated `WithImageRegistry(...)`
extension's implementation at registration time, (b) the build phase
body's registry-prefix lookup at the start of FT-003 §Behaviour §1
and §4.1, and (c) the configure-phase upsert step and the push-phase
body of FT-004 §Behaviour. The five-phase skeleton, the four-bucket
error model in build, the four-bucket error model in push, the
anonymous-push path, the catch-all `_UNEXPECTED` symbols, the
cancellation discipline, and the structured-log shapes are all
unchanged.

#### 0. (Registration-time) `WithImageRegistry(...)` becomes a shim

The extension's signature is unchanged: `WithImageRegistry(prefix,
[username, password])` on the same builder surface FT-003 placed it.
Its body is rewritten:

1. Compute `syntheticName = "coolify-legacy-" + stableHash(prefix)`
   so repeated calls with the same prefix converge on the same
   synthetic resource (deterministic across builds and across
   distinct `WithImageRegistry(...)` call sites).
2. Call `builder.AddContainerRegistry(syntheticName, prefix)` on the
   builder, capturing the returned `IResourceBuilder<
   ContainerRegistryResource>` handle. If a registry resource with
   that exact name already exists in the graph (a prior
   `WithImageRegistry(...)` with the same prefix, or an explicit
   user `AddContainerRegistry(...)` happening to use the same name),
   the shim reuses the existing resource and does not create a
   duplicate.
3. If `username` and `password` are both supplied, attach them to
   the synthetic registry resource through whatever credential
   surface `ContainerRegistryResource` exposes in Aspire 13. If
   exactly one is supplied, raise `ArgumentException` at AppHost
   build time (matches FT-003 §Error handling — credentials travel
   as a pair).
4. Attach the synthetic registry to **every containerisable
   workload that has no explicit `WithContainerRegistry(...)` edge
   at the point the publisher's deploy hook fires**. Workloads that
   the user has already attached to a different
   `ContainerRegistryResource` are left untouched — the explicit
   edge wins.
5. Mark the extension `[Obsolete("Use AddContainerRegistry +
   WithContainerRegistry — see ADR-007.", error: false)]` so call
   sites emit a `[CS0618]`-style compiler warning. The warning is
   non-fatal — AppHosts continue to compile and deploy.

The shim's implementation contains **no** branch on "was-it-called-
via-shim-or-natively" inside the publisher's read paths. Its only
job is to produce a workload→registry edge that the publisher's
graph-based read path then follows uniformly.

#### A. (Build phase, FT-003 §Behaviour §1) Registry-prefix lookup

The pre-walk gate "verify a registry prefix is configured" is
re-expressed against the resource graph:

1. For each containerisable workload in the resource set the build
   phase receives, locate the `ContainerRegistryResource` attached
   to it via the `WithContainerRegistry(...)` edge. The shim, if it
   ran at registration time, has already populated this edge for
   workloads the user did not attach explicitly.
2. If **any** workload has no registry edge, fail-fast with
   `E_REGISTRY_NOT_CONFIGURED`, naming the offending workload(s) in
   the structured field block. The remediation block lists both
   the native shape and the deprecated shim shape (see §Outputs
   above).
3. If every workload has a registry edge, proceed to the AppHost-
   version read (FT-003 §Behaviour §2) unchanged.

#### B. (Build phase, FT-003 §Behaviour §4.1) Per-resource tag composition

The tag composition is re-expressed:

- Old (FT-003): `<prefix>/<resource.Name>:<apphost-version>` where
  `<prefix>` came from the publisher-instance triple.
- New (FT-014): `<address>/<resource.Name>:<apphost-version>` where
  `<address>` is the `Address` property of the
  `ContainerRegistryResource` attached to *this* workload via the
  edge located in §A. Different workloads attached to different
  registries produce tags against different addresses; workloads
  attached to the same registry produce tags against the same
  address (the ADR-005 §D4 shape is preserved verbatim — only the
  source of `<address>` changes).

Everything else in FT-003 §Behaviour §4 — single Aspire image
pipeline invocation per resource, `E_IMAGE_BUILD_FAILED` on failure,
no `latest`, no Coolify contact — is unchanged.

#### C. (Configure phase, FT-004 §Behaviour) Coolify Private Registry upsert

The upsert step is re-expressed:

1. Enumerate the distinct set of `(host, username)` pairs across
   the containerisable workloads' registry edges. Workloads sharing
   a `ContainerRegistryResource` contribute one entry; workloads
   attached to distinct registries contribute one entry each;
   anonymous registries (no credentials attached to the resource)
   contribute zero entries.
2. For each distinct pair, derive `host` from the registry
   resource's `Address` (leading segment before the first `/`,
   matching FT-004 §Behaviour §3) and resolve `(username,
   password)` from whatever credential surface the registry
   resource exposes.
3. Issue one `client.PrivateRegistries.UpsertAsync(host, username,
   password, ct)` call per pair, with the same idempotency
   contract FT-004 §I-2 guarantees today (GET-then-POST-if-absent
   / PATCH-if-password-changed, marker-suffixed `managed-by:
   pks-aspire-coolify`).
4. Any non-2xx response → `E_COOLIFY_REGISTRY_UPSERT_FAILED` with
   the failing `host` / `username` in the structured field block.
   Configure does not advance to `build`.

If the distinct set is empty (every registry is anonymous), no
upsert calls are issued and the configure phase proceeds to build
exactly as FT-004's anonymous-push path does today.

#### D. (Push phase, FT-004 §Behaviour) Per-resource push

The push loop is re-expressed:

1. For each containerisable workload, read the tag attached to its
   local image-cache entry (the tag build emitted in §B above).
2. Resolve the workload's push credentials by following the
   `WithContainerRegistry(...)` edge to its
   `ContainerRegistryResource` and reading whatever credential
   surface the resource exposes. Anonymous registries pass no
   credentials.
3. Invoke Aspire's push pipeline for the (tag, credentials) pair.
4. Aggregate failures into the `REGISTRY_AUTH_FAILED` /
   `IMAGE_PUSH_FAILED` buckets exactly as FT-004 §Behaviour §3.iii
   / §3.iv specify today.

The "attempt every resource before reporting failure" discipline
(FT-004 §I-9) is preserved. The "successfully-pushed siblings are
not unpushed" discipline (FT-004 §Behaviour §5) is preserved.

### Invariants

- **I-1: the publisher reads the push target from the resource
  graph, not from a publisher-instance field.** No code path in
  build, push, or configure reads a `(prefix, username, password)`
  triple from a field on `CoolifyDeployingPublisher`. Asserted by
  source inspection (the triple-field is removed) and by the
  native-path TC (TC-027): an AppHost that uses
  `AddContainerRegistry` + `WithContainerRegistry` and **never**
  calls `WithImageRegistry(...)` deploys successfully with tags
  shaped against the registry resource's `Address`.
- **I-2: the shim produces exactly the same workload→registry edge
  the native call would.** An AppHost compiled against
  `WithImageRegistry("ghcr.io/acme", user, pass)` produces a
  resource graph indistinguishable, at the publisher's read sites,
  from one written as `var r = builder.AddContainerRegistry(
  "coolify-legacy-<hash>", "ghcr.io/acme"); /* attach credentials
  */; workload.WithContainerRegistry(r);` for every otherwise-
  unattached workload. The publisher cannot tell, by inspecting
  the graph, which path was used.
- **I-3: the shim emits exactly one obsolete warning per call
  site.** Compiling an AppHost that calls `WithImageRegistry(...)`
  once produces one `[CS0618]`-style warning; compiling one that
  calls it twice produces two. No warning is emitted for AppHosts
  that use only the native shape.
- **I-4: tag shape is preserved.** Every tag emitted by the build
  phase still matches `<address>/<resource.Name>:<apphost-version>`
  exactly. The `<address>` source changes (from publisher field
  to registry-resource property); the shape, the lack of `latest`,
  and the one-tag-per-resource discipline are unchanged. (Composes
  with FT-003 §I-1 and §I-2.)
- **I-5: per-workload distinct registries are first-class.** Two
  workloads attached to two different `ContainerRegistryResource`s
  with different `Address`es emit tags against their respective
  addresses, push to their respective registries, and produce one
  Coolify Private Registry upsert per distinct `(host, username)`
  pair across the graph. This is a new capability the channel
  change unlocks; FT-014 asserts it as a behavioural invariant
  rather than a feature-flag.
- **I-6: the four FT-003 `E_…` symbols and the four FT-004 `E_…`
  symbols are preserved verbatim.** Their spellings, their
  ordering as the first whitespace-delimited token on stderr, and
  their structured field blocks are unchanged. Only the *trigger*
  of `E_REGISTRY_NOT_CONFIGURED` is restated against the new
  channel (no workload→registry edge instead of no triple on the
  publisher).
- **I-7: workloads with explicit `WithContainerRegistry(...)`
  attachment are not overridden by the shim.** A user who calls
  both `WithImageRegistry("legacy-prefix", ...)` and
  `WithContainerRegistry(explicitResource)` on the same workload
  ends up with the explicit resource (the shim's "attach to every
  workload without an edge" only fires when no edge exists). The
  shim's synthetic registry still exists in the graph (because
  other workloads in the same AppHost may attach to it), but the
  explicitly-attached workload pushes to the explicit resource's
  address.
- **I-8: the shim's synthetic registry resource is deterministic
  across builds.** Two builds of the same AppHost source produce
  the same `syntheticName` for the same prefix (because
  `stableHash` is deterministic), so the graph shape — and hence
  the Coolify-side `(host, username)` upsert key — is stable
  across deploys for source-compat AppHosts.
- **I-9: no observable behaviour changes for AppHosts that already
  used `WithImageRegistry(...)`.** The configure-phase upsert
  produces the same `(host, username)` pair, the push phase pushes
  to the same registry host, the tag shape is the same, the
  deploy log lines are the same (modulo the obsolete warning at
  compile time, which is not a deploy-log artefact). Asserted by
  the shim source-compat TC (TC-028).

### Error handling

- **No new `E_…` symbols.** The eight existing symbols (four from
  FT-003, four from FT-004) are preserved verbatim. FT-014 changes
  only the trigger restatement of `E_REGISTRY_NOT_CONFIGURED`:
  fires when a containerisable workload has no
  `WithContainerRegistry(...)` edge **and** the shim did not
  synthesise one for it (i.e. neither native nor shim shape was
  used to point this workload at a registry).
- **`ArgumentException` on `(username XOR password)`** at the shim's
  call site is preserved verbatim from FT-003 §Error handling.
- **The shim attempting to attach to a workload that is already
  attached** is a no-op, not an error. The explicit edge wins
  (I-7); the shim does not warn or fail.
- **`AddContainerRegistry(syntheticName, prefix)` failing inside
  the shim** (for example because a same-named resource of a
  different type already exists in the graph) is surfaced as the
  Aspire SDK's own exception, at AppHost build time. FT-014 does
  not invent a new symbol for this; the underlying SDK error is
  more diagnostic than anything we would synthesise.

### Boundaries

- **In scope for FT-014:**
  - rewriting `WithImageRegistry(...)`'s body to synthesise an
    `AddContainerRegistry` + per-workload `WithContainerRegistry`
    edge; marking the extension `[Obsolete]`
  - changing the publisher's build-phase registry-prefix lookup to
    follow the workload→registry edge in the resource graph
  - changing the push-phase credential read to follow the same
    edge and read whatever credential surface
    `ContainerRegistryResource` exposes in Aspire 13
  - changing the configure-phase Coolify Private Registry upsert
    to iterate the distinct `(host, username)` set across the
    graph rather than the publisher-instance triple
  - restating `E_REGISTRY_NOT_CONFIGURED`'s trigger against the
    new channel; preserving every other symbol verbatim
  - the three exit-criteria TCs (TC-027, TC-028, TC-029)
  - bookkeeping amendments to FT-003 and FT-004 bodies to reflect
    the new channel (FT-014 is the implementation; the amendments
    are tracked alongside this feature)
- **Out of scope for FT-014:**
  - changing the tag *shape* (still
    `<address>/<resource.Name>:<apphost-version>`)
  - changing any of the eight `E_…` symbols' spellings
  - removing the `WithImageRegistry(...)` extension entirely
    (deferred to a future v2.0 ADR; v1.x preserves source compat)
  - pinning the exact Aspire 13 enumeration API name for the
    workload→registry edge (property-only contract; the precise
    accessor is chosen at implementation time)
  - GC of stale `coolify-legacy-<hash>` synthetic registries
    (none are stale within one deploy — the synthetic resource
    lives only inside the in-memory resource graph; no Coolify-
    side or registry-side artefact is named after the synthetic
    resource)
  - drift detection on whether a workload's registry edge changed
    between deploys (deferred)
  - the "deploy a `registry:2` as a workload of the same AppHost"
    case (unblocked by FT-014's channel change but tested
    end-to-end by a separate feature — FT-014 only asserts the
    publisher follows the edge, not that the in-graph registry
    case deploys end-to-end)
  - TypeScript AppHost parity for `AddContainerRegistry` /
    `WithContainerRegistry` (owned by the `apphost-ts` domain's
    feature, not FT-014)

## Out of scope

- **Removing the shim.** `WithImageRegistry(...)` is marked
  `[Obsolete(..., error: false)]` and continues to compile and
  deploy in every v1.x release. A future v2.0 may remove it; that
  removal requires its own ADR.
- **Hard-pinning the Aspire 13 enumeration API.** FT-014's
  contract is property-only on the mechanism ("the publisher
  follows the workload→registry edge in the resource graph"). The
  precise accessor on `IDeployingPublisher`'s context is named at
  implementation time.
- **Migrating documentation and examples.** ADR-007 §Consequences
  notes that the user-facing examples should migrate to the
  native shape with the shim shown once as the "migration from
  v1.x" example; the migration itself is a documentation task,
  not part of FT-014.
- **Coolify-side cleanup of stale Private Registry records.**
  ADR-007 leaves ADR-003's "no tear-down in v1" discipline
  intact; FT-014 inherits it.
- **Multi-architecture / multi-platform manifest lists.** Still
  one tag per resource for the local build host's architecture
  (FT-003 §Out of scope).
- **Image signing / attestation / SBOM upload.** Inherited from
  the push pipeline if Aspire 13 ships it; FT-014 does not
  configure it.
