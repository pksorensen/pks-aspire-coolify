---
id: FT-003
title: Build phase — reuse Aspire container-image pipeline with deterministic per-resource tagging
phase: 1
status: complete
depends-on:
- FT-001
adrs:
- ADR-003
- ADR-005
- ADR-001
- ADR-004
- ADR-006
tests:
- TC-007
domains:
- aspire-publisher
- image-flow
domains-acknowledged: {}
---

## Description

FT-003 fills in the **build phase** body that FT-001's skeleton left as
a no-op. It is the first feature that actually walks the Aspire
resource graph, drives Aspire's existing container-image build
infrastructure once per containerisable resource, and tags each
emitted image with the deterministic, immutable tag mandated by
ADR-005 D4: `<registry-prefix>/<resource-name>:<apphost-version>`.

Per ADR-003, `build` runs after `configure` (FT-002 has already
established that the Coolify token is good and the version probe
passed) and before `push` (FT-004 owns push). The build phase is
the second of the five phases, and is the *first* phase in which
the publisher does work that is observable outside the AppHost
process — local image cache entries appear on the developer's host,
tagged in the registry-prefix namespace. **Nothing is pushed in
FT-003** — that is FT-004's job — and Coolify is not contacted at
all during the build phase.

This feature is also the first feature to introduce the public
`WithImageRegistry(prefix, [username, password])` extension required
by ADR-005 §1, because FT-003 is the first phase that needs the
prefix to tag with. Credential handles (`username`, `password`) are
captured on the publisher instance at registration time but are
**not read or dereferenced** in FT-003: they are FT-004's input.
This mirrors how FT-001 captures `(url, token)` but does not read
the token — FT-002 reads it later.

The exit criteria are: (a) `WithImageRegistry(...)` registers
correctly with the publisher and captures all three handles, (b)
the build phase walks every resource passed to it and drives
Aspire's own container-image pipeline once per resource, (c) every
emitted image carries exactly the deterministic tag from ADR-005
D4 and **no** `latest` tag appears anywhere in v1, (d) the AppHost
version is read once from `AssemblyInformationalVersionAttribute`
on the AppHost assembly and a missing/empty value fails fast with
`E_APPHOST_VERSION_MISSING`, and (e) on any build failure for any
resource, the publisher exits non-zero before entering `push`.

Filter-down-to-containerisable (skip-with-warning for resources
that cannot be expressed as containers) is **not** in scope for
FT-003. FT-003 assumes every resource the host hands it is
containerisable. FT-009 (planned) owns the containerisable-or-skip
classifier; until that feature lands, an AppHost that mixes
container and non-container resources will surface its own build
failure from Aspire's pipeline, which FT-003 will report verbatim
in its diagnostic.

## Functional Specification

### Inputs

- The `IResourceBuilder<ParameterResource>` triple `(prefix, username,
  password)` captured on the `CoolifyDeployingPublisher` instance by a
  prior call to `WithImageRegistry(prefix, username, password)` (this
  feature introduces that extension method — see Behaviour §0 below).
  `prefix` is required; `username` and `password` are optional and
  travel as a pair per ADR-005 §1. FT-003 reads only `prefix`;
  `username` and `password` are captured but never dereferenced by
  this feature.
- The Aspire resource graph reachable from the running
  `DistributedApplication` instance. FT-003 reads the graph through
  whatever enumeration API Aspire's `IDeployingPublisher` context
  exposes at build time; it does not snapshot or persist the graph.
- The AppHost assembly itself, read once for its
  `AssemblyInformationalVersionAttribute`. The publisher locates the
  AppHost assembly via the entry-assembly mechanism Aspire's
  publisher context already exposes (i.e. the same assembly that
  hosts the `Program.cs` calling `WithCoolifyDeploy(...)`).
- Aspire's existing container-image build infrastructure — the same
  surface the Docker Compose publisher and `aspire-ssh-deploy` use
  to turn a project resource into a tagged local image. FT-003
  consumes this as a black box: it supplies (resource, image-name,
  image-tag) and delegates execution. The exact API entry point is
  named at implementation time against the Aspire SDK version
  pinned by FT-001's source files.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context.

### Outputs

- **On success:** the build phase exits normally and yields control
  to the `push` phase. After exit, the developer's local image
  store contains exactly one image per containerisable resource,
  each tagged with `<registry-prefix>/<resource-name>:<apphost-version>`
  and **no other tag** authored by FT-003. The publisher has
  emitted, per resource, an Aspire-structured-log entry recording
  the resource name and the full image tag (no credentials, no
  secret content). No `latest` tag is emitted under any code path.
- **On any fail-fast path:** the publisher prints a single
  diagnostic to stderr whose first whitespace-delimited token is
  one of the literal symbols below, exits non-zero, does not enter
  the `push` phase, and does not contact Coolify. The four
  symbols are part of the **observable contract** and are matched
  as literal strings by FT-003's exit-criteria test — they are
  not internal exception type names. They are:

  | Symbol                       | Stderr-visible literal        | Trigger                                                                              |
  |------------------------------|-------------------------------|--------------------------------------------------------------------------------------|
  | `E_REGISTRY_NOT_CONFIGURED`  | `E_REGISTRY_NOT_CONFIGURED`   | `WithImageRegistry(...)` was not called, or `prefix` resolved to null/empty          |
  | `E_APPHOST_VERSION_MISSING`  | `E_APPHOST_VERSION_MISSING`   | `AssemblyInformationalVersionAttribute` is absent or empty on the AppHost assembly   |
  | `E_IMAGE_BUILD_FAILED`       | `E_IMAGE_BUILD_FAILED`        | Aspire's image pipeline reported a non-zero exit / threw for any resource            |
  | `E_BUILD_PHASE_UNEXPECTED`   | `E_BUILD_PHASE_UNEXPECTED`    | Catch-all for unclassifiable failures inside the build phase body                    |

  All four diagnostics carry the same structured field block as
  FT-002's diagnostics:

  ```
  <E_SYMBOL>: <one-line human description>
    resource: <aspire-resource-name>          (for IMAGE_BUILD_FAILED / BUILD_PHASE_UNEXPECTED, when known)
    tag:      <prefix>/<resource>:<version>   (for IMAGE_BUILD_FAILED, when computed)
    apphost:  <AppHost assembly simple name>  (for APPHOST_VERSION_MISSING)
    see:      ADR-005 §1                      (for REGISTRY_NOT_CONFIGURED)
    remediation:
      builder.WithCoolifyDeploy(url, token)
             .WithImageRegistry(prefix, [user, pass]);    (for REGISTRY_NOT_CONFIGURED)
      [<Assembly: AssemblyInformationalVersion("1.0.0")>] (for APPHOST_VERSION_MISSING)
  ```

  The first whitespace-delimited token on the first line is the
  literal `E_…` symbol; this is what TC-005 (and any future build-
  phase tests) grep for.

### State

- **No persistent state on disk authored by FT-003** (inherits ADR-003
  §4 and FT-001 invariant I-3). The only artefacts that appear on
  the developer's host as a side effect of the build phase are
  entries in the **local container-image store** (e.g. the local
  Docker daemon's image cache). Those are not files FT-003 wrote —
  they are managed by Aspire's container-image pipeline, which is
  the same surface every other publisher uses, and they live in
  the user's existing local image cache, not in the AppHost
  directory.
- **In-memory state is bounded to the build phase.** The resolved
  `prefix` string and the resolved `<apphost-version>` string live
  in the local scope of the build-phase body and in the structured
  image-name argument passed to the Aspire image pipeline once per
  resource. They are not stored as fields on the publisher and not
  captured in any closure that outlives the build phase. The
  captured `username` / `password` parameter handles **on the
  publisher instance** survive the build phase because they belong
  to FT-004's input contract; FT-003 does not read them and does
  not clear them.
- **Per-resource build progress is reported through Aspire's
  pipeline.** FT-003 does not maintain a parallel progress object;
  it observes the Aspire pipeline's progress events and re-emits
  the relevant lines into the publisher's deploy log, attributed
  to the build phase.

### Behaviour

The build phase body executes the following steps in this exact
order. Steps 1–3 are pre-walk gates: if any of them fails, the
publisher exits non-zero with the matching diagnostic and does
not iterate the resource graph.

0. **(Registration-time, not build-time.) `WithImageRegistry(prefix,
   username, password)` extension.** FT-003 introduces a new
   extension method on `IDistributedApplicationBuilder` (or on
   whatever environment-builder surface the eventual implementation
   chooses, matching ADR-005's example call site):

   ```csharp
   builder.AddDockerComposeEnvironment("env")
          .WithCoolifyDeploy(coolifyUrl, coolifyToken)
          .WithImageRegistry(registryPrefix, registryUser, registryPass);
   ```

   The method captures the three `IResourceBuilder<ParameterResource>`
   handles on the `CoolifyDeployingPublisher` instance that
   `WithCoolifyDeploy(...)` registered. `prefix` is required;
   `username` and `password` are jointly optional (either both are
   set, or both are null/omitted — they travel as a pair per
   ADR-005 §1). If `prefix` is `null` at the call site, the method
   throws `ArgumentNullException` at AppHost build time (matching
   FT-001's null-handle discipline). If exactly one of `username`
   / `password` is non-null and the other is null, the method
   throws `ArgumentException` at AppHost build time. The method
   is idempotent: calling it twice on the same builder replaces
   the previously-captured handles with the new ones (last call
   wins). Calling it zero times leaves the publisher with no
   captured registry handles, and the build phase fails fast in
   step 1.

1. **Verify a registry prefix is configured.** Look at the
   publisher's captured `prefix` handle. If `WithImageRegistry(...)`
   was never called, or the captured `prefix` parameter resolves
   to null / empty (after trimming surrounding whitespace), fail-
   fast with `E_REGISTRY_NOT_CONFIGURED`. No image work is
   attempted, no graph walk is performed.

2. **Resolve the AppHost version.** Read
   `AssemblyInformationalVersionAttribute` from the AppHost
   assembly exactly once. If the attribute is absent, or its
   `InformationalVersion` is null / empty / whitespace, fail-fast
   with `E_APPHOST_VERSION_MISSING`. **There is no fallback** —
   git-sha derivation, timestamp derivation, and assembly
   `Version` fallback are all explicitly excluded by Q3 (the
   value comes from `AssemblyInformationalVersionAttribute` or
   the deploy fails). Any trimming or normalisation (e.g.
   stripping a `+gitsha` suffix Aspire's SDK adds) is implementation-
   level and must be documented in the implementation PR; for FT-003
   the value used in the tag is exactly what
   `AssemblyInformationalVersionAttribute.InformationalVersion`
   reports after a single `.Trim()`.

3. **Enumerate the resource set to build.** The build phase
   receives a set of resources from its caller (the publisher
   driver). For FT-003, **the publisher assumes every resource
   in this set is containerisable** — no skip-with-warning logic,
   no classifier, no `if (resource is ProjectResource) …`. The
   set is consumed verbatim. FT-009 (planned) introduces the
   classifier and reduces the set before it reaches FT-003.

4. **For each resource in the enumeration, build and tag.** For
   every resource:
   1. Compose the deterministic image tag:
      `<prefix>/<resource.Name>:<apphost-version>`, where
      `<resource.Name>` is the Aspire resource's `Name` property
      verbatim (no case-folding, no slugification — Aspire's
      naming rules already produce registry-safe names, and any
      deviation is the caller's problem to surface). The tag
      string is computed once per resource and reused for every
      log line and for the Aspire pipeline call.
   2. Invoke Aspire's container-image build pipeline for this
      resource, passing the composed image tag as the
      authoritative `<image-name>:<tag>` for the build. The
      pipeline's existing semantics for base-image selection,
      build context, layer caching, and SDK version pinning are
      inherited unchanged.
   3. If the pipeline call returns a failure or throws, classify
      the failure as `E_IMAGE_BUILD_FAILED`, surface the
      resource name and computed tag in the diagnostic, and
      exit the build phase non-zero. **Sibling resources whose
      build had already succeeded earlier in the iteration are
      not "unbuilt"** — their image-cache entries remain on the
      local host. This matches ADR-003 §6's verify-gated, non-
      transactional rollback contract: a build failure refuses
      to advance to `push`, but does not pretend to undo
      already-completed work.
   4. On success, emit a single Aspire-structured-log entry
      attributed to the build phase recording the resource name
      and the full image tag. No credentials and no AppHost
      file paths beyond the resource name are emitted.

5. **Exit the build phase.** Once every resource in the
   enumeration has built successfully, the build phase exits
   normally. The push phase (FT-004) takes over.

**Concurrency.** Per Q2, FT-003 ships **sequential** but the
contract is "concurrency-permitted": no behavioural invariant
depends on serial ordering across resources, and a future feature
may parallelise the per-resource loop without re-deciding the
model. Within FT-003 the per-resource loop runs one resource at a
time, in graph enumeration order, and the deploy log reflects
that order.

**Cancellation.** If the deploy `CancellationToken` is cancelled
between any of steps 1–5 (including between two resource builds
in step 4), the build phase exits with FT-001's cancellation
diagnostic (not one of the four `E_…` symbols) and does not enter
`push`. A cancellation observed *inside* Aspire's image pipeline
mid-build is propagated by the pipeline; FT-003 re-emits it as a
cancellation exit, not as `E_IMAGE_BUILD_FAILED`.

**Catch-all (`E_BUILD_PHASE_UNEXPECTED`).** Any exception escaping
the build phase that is not classifiable as the three preceding
symbols (and is not a cancellation) is wrapped and surfaced as
`E_BUILD_PHASE_UNEXPECTED` with the inner exception's `Message`
appended (no stack trace, no secret content). This mirrors
FT-002's "catch-all on configure → `E_COOLIFY_UNREACHABLE`"
discipline: better a precise fail-fast than entering `push` in
an unknown state.

### Invariants

- **I-1: every emitted image tag matches
  `<prefix>/<resource.Name>:<apphost-version>` exactly.** No tag
  authored by FT-003 may deviate from this shape — no suffix, no
  digest, no environment tag, no per-deploy stamp. The shape is
  the observable contract that ADR-005 D4 fixes and that downstream
  features (FT-004 push, future Coolify deploy-phase) consume by
  reading from the local image store.
- **I-2: no `latest` tag is emitted under any code path.** This
  is the load-bearing prohibition from ADR-005 §4 / §6 ("a
  `latest` tag is **not** pushed in v1") restated at the build
  layer. Asserted by inspecting the local image store after a
  successful build and grepping the deploy log for `:latest`.
- **I-3: the AppHost version is read exactly once per deploy.**
  Re-reading the attribute between resources would (in principle)
  produce identical results, but the invariant is "the value is
  captured once at the top of the build phase and reused for
  every tag." This makes the build phase observably deterministic
  across resources within one deploy.
- **I-4: no Coolify call is issued during the build phase.** FT-003
  does not contact Coolify. The HTTP client wired by FT-002 is
  available on the publisher but FT-003 must not call it. The
  Coolify-side Private Registry record upsert mentioned in
  ADR-005 §5 is **not** FT-003's work — it is a configure-phase
  step owned by a separate feature (FT-004 or later) and happens
  before build runs.
- **I-5: no registry push is issued during the build phase.** FT-003
  produces local image-cache entries only. Asserted by intercepting
  outbound HTTP and asserting zero requests to the registry host
  during a happy-path build.
- **I-6: credential handles are captured but never dereferenced.**
  The `username` and `password` parameter handles attached to the
  publisher by `WithImageRegistry(...)` are visible to FT-003 but
  FT-003 must not call their value-resolution API. Asserted by
  injecting sentinel values into the `password` parameter and
  verifying the sentinel never reaches stdout, stderr, deploy logs,
  or any HttpClient during a successful build phase.
- **I-7: build failure refuses to advance to `push`.** Asserted by
  forcing one resource's image build to fail and verifying that
  no log line attributed to the `push` phase appears in the deploy
  output, and that no registry contact occurs.
- **I-8: `WithImageRegistry(...)` idempotency is last-call-wins.**
  Calling the method twice on the same builder replaces the
  captured handles; the publisher uses the most recent triple.
  This differs from FT-001's `WithCoolifyDeploy(...)` first-call-
  wins idempotency for url/token, and is the deliberate choice for
  registry config because (unlike the Coolify target) developers
  legitimately reconfigure their registry between local
  experiments. Asserted by calling the method twice with
  distinguishable prefixes and observing tags built against the
  second prefix.
- **I-9: every fail-fast exit emits the build-phase boundary.**
  FT-001's phase-boundary logging shows `build: enter … build:
  exit (failed)` with no `push: enter` line. Asserted by deploy-
  log scraping in TC-005.
- **I-10: the four `E_…` symbols are stable observable contract.**
  Their spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any symbol
  is a breaking change requiring a new ADR.

### Error handling

The four `E_…` diagnostics enumerated above are the only error
paths this feature introduces. Beyond them:

- **Cancellation between steps or during a resource build** →
  FT-001 cancellation diagnostic (not an `E_…` symbol).
- **Null `prefix` handle at the call site of
  `WithImageRegistry(...)`** → `ArgumentNullException` thrown at
  AppHost build time, naming the offending argument. This is a
  build-time error, not an `E_…` symbol — the program never
  reaches `aspire deploy`.
- **Exactly one of `(username, password)` non-null at the call
  site of `WithImageRegistry(...)`** → `ArgumentException` thrown
  at AppHost build time, naming the offending argument. Same
  rationale as above.
- **Aspire image pipeline failure for a single resource** →
  `E_IMAGE_BUILD_FAILED`, carrying resource name and computed
  image tag. Earlier-resource builds that already succeeded are
  not torn down.
- **Aspire image pipeline returns success but no image appears in
  the local cache for the computed tag** → treated as
  `E_IMAGE_BUILD_FAILED` (the pipeline lied about its result and
  the build phase refuses to advance to `push` in an unknown
  state). This is a defensive verification step, not a poll loop.
- **Bug or unclassifiable exception inside the build phase body** →
  `E_BUILD_PHASE_UNEXPECTED` with the inner `Message` appended (no
  stack trace, no secret content).

### Boundaries

- **In scope for FT-003:**
  - the `WithImageRegistry(prefix, username, password)` extension
    method (introduced here for the first time, fixed signature
    per ADR-005 §1)
  - capturing the `(prefix, username, password)` handles on the
    publisher instance at registration time
  - the build-phase body: registry-config check, AppHost-version
    read, per-resource loop driving Aspire's image pipeline
  - deterministic tag composition matching ADR-005 D4 exactly
  - the four `E_…` diagnostics with the literal symbols, the
    structured field blocks, and zero secret content
  - sequential iteration of the resource set passed to the build
    phase (concurrency permitted by the model, not implemented in
    v1)
  - cancellation honouring between resources and as propagated by
    the image pipeline
  - reporting per-resource build progress through the publisher's
    deploy log under the `build` phase attribution
- **Out of scope for FT-003** (handled elsewhere or deferred):
  - resolving the registry `username` / `password` parameters →
    **FT-004 (push phase)** consumes them via the same captured
    handles
  - the Coolify-side Private Registry record upsert from ADR-005
    §5 → owned by a separate configure-phase or push-phase feature
    (FT-004 or later); FT-003 does not touch Coolify
  - any actual registry push → **FT-004 (push phase)**
  - filtering the resource set to containerisable resources, with
    skip-with-warning semantics for non-container resources → **FT-009
    (planned)**; FT-003 assumes the caller has already filtered
  - the Aspire-graph walk that produces the resource set in the
    first place (which graph nodes are visited at all, in what
    order, with what parent-child edges) → owned by the deploy-
    phase walker, not the build-phase iterator; FT-003 simply
    iterates whatever set it receives
  - any persistent on-disk publisher state (forbidden by ADR-003
    §4 / FT-001 I-3 / this feature's I-1 implication)
  - `latest` tag emission of any kind (explicitly prohibited by I-2)
  - TypeScript AppHost parity for `WithImageRegistry(...)` → owned
    by the dedicated `apphost-ts` feature
  - drift detection on emitted image tags → not a concern for the
    build phase; tags are immutable per ADR-005 D4 and rebuilding
    the same `<version>` with different content is a developer
    error the publisher does not police in v1
  - retry / backoff on image-pipeline failures → single attempt;
    failure → fail-fast (matches FT-002's transport-failure
    discipline)

## Out of scope

- **Push.** FT-003 never contacts a registry. FT-004 owns push and
  consumes the same `(prefix, username, password)` handles captured
  here.
- **Coolify-side Private Registry upsert.** ADR-005 §5 places this
  in `configure` (after FT-002's auth probe, before any image
  work); FT-003 does not own that step. A separate feature
  (FT-004 or a configure-phase extension) will land it.
- **Skip-with-warning for non-containerisable resources.** FT-009
  owns the classifier. FT-003 assumes every input resource is
  containerisable.
- **Multi-architecture builds.** FT-003 emits one tag per resource,
  for the local build host's architecture. Cross-arch / multi-arch
  manifest lists are a later concern.
- **Image signing / attestations / SBOM emission.** Aspire's image
  pipeline may grow these affordances; if it does, they appear in
  FT-003's output by inheritance. FT-003 does not configure them.
- **Custom Dockerfile resolution policy.** Aspire's image pipeline
  already has its own resolution rules (project's `Dockerfile`,
  derived-from-SDK fallback, etc.); FT-003 inherits whatever the
  pipeline does. No FT-003-level override.
- **Tag mutation across deploys.** Tags are immutable per ADR-005
  D4. Building the same `<apphost-version>` twice against
  different source content produces two different local-cache
  entries with the same tag and is a developer error the
  publisher does not police; FT-004's push phase will simply push
  whatever the local cache holds, and ADR-005's "deterministic
  tag" contract relies on developer-side version discipline.
- **Reading any value from the registry credential parameters.**
  Captured, never dereferenced. FT-004 reads them.
- **Plan / dry-run mode for the build phase.** No `aspire deploy
  --plan` variant in v1; FT-003 always builds, or fails fast.
- **Hooks into intra-build progress for downstream features.** The
  build phase emits structured log entries per resource; that is
  the only progress channel FT-003 introduces. A future feature
  may layer richer reporting on top.
