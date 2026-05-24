---
id: FT-001
title: WithCoolifyDeploy extension and 5-phase publisher skeleton
phase: 1
status: complete
depends-on: []
adrs:
- ADR-003
- ADR-001
- ADR-002
- ADR-004
- ADR-005
tests:
- TC-003
- TC-017
domains:
- aspire-publisher
domains-acknowledged: {}
---

## Description

FT-001 is the **spine** of `pks-aspire-coolify`: the one-line developer
entry point (`WithCoolifyDeploy(url, token)`) and the `aspire deploy`
publisher that hosts the five-phase pipeline every other feature plugs
into. v1 of this feature delivers the **skeleton only** — wiring,
registration, phase-boundary observability, the public signature fixed
by ADR-004, and a clean integration with the Aspire deploy CLI. The
phases themselves are no-op-but-wired callbacks; subsequent features
(FT-002+) fill them in (configure-phase token-resolution and
version/auth probe, graph-walk, image build/push, env-var sync, verify
polling, managed dashboard, etc.).

The shape is dictated by ADR-003: the publisher implements Aspire's
imperative phase decomposition explicitly, in the fixed order
`configure → build → push → deploy → verify`, with each phase
observable as a boundary in `aspire deploy` output. The public surface
is dictated by ADR-004: `WithCoolifyDeploy` takes the Coolify base URL
and the bearer-token parameter as typed `IResourceBuilder<ParameterResource>`
handles, both required, travelling together. Without this skeleton in
place there is nothing for downstream features to hang off; with it in
place, every later feature is a localised change inside one of the five
phase shells (and FT-002 picks up the stored `(url, token)` to do the
configure-phase probe).

This feature is intentionally **scoped to plumbing**: it does not talk
to Coolify, does not resolve the token value, does not build or push
images, does not read or write any Aspire resources beyond storing the
two parameter handles on the registered publisher, and does not require
a Coolify instance to exercise. Its exit criteria are entirely about
wiring being correct, the signature matching ADR-004, and phase
boundaries being honoured.

## Functional Specification

### Inputs

- An Aspire AppHost author's call site, matching ADR-004's fixed
  signature:
  ```csharp
  var builder = DistributedApplication.CreateBuilder(args);

  var coolifyUrl   = builder.AddParameter("coolify-homelab-url");
  var coolifyToken = builder.AddParameter("coolify-homelab-token", secret: true);

  builder.WithCoolifyDeploy(coolifyUrl, coolifyToken);
  // ... AddProject / AddContainer / AddParameter calls ...
  builder.Build().Run();
  ```
  `WithCoolifyDeploy` is an extension method on
  `IDistributedApplicationBuilder` taking two
  `IResourceBuilder<ParameterResource>` arguments — `url` and `token`
  — both required, in that order, no other overloads in v1. The signature
  is load-bearing for ADR-004 (per-instance scoping, no implicit
  cross-wiring, typed at the call site) and for the eventual TypeScript
  AppHost-parity feature, so it is fixed by this feature and inherited
  unchanged by FT-002+.

  **§B — Extension namespace convention.** Every public `With*` extension
  method this package exposes on `IDistributedApplicationBuilder`
  (`WithCoolifyDeploy`, `WithImageRegistry`, `WithCoolifyDestination`,
  `WithVerifyPolling`, `WithManagedDashboard`) is declared in the
  **`Aspire.Hosting` namespace**, not `Aspire.Hosting.Coolify`. AppHost
  projects already pull in `Aspire.Hosting` via implicit usings, so the
  one-liner above resolves with no extra `using` directive — matching
  `Aspire.Hosting.Redis` → `WithRedis()`,
  `Aspire.Hosting.Postgres` → `WithPostgres()`, and the rest of the
  Aspire ecosystem. Internals (`CoolifyDeployingPublisher`, `CoolifyPhase`,
  diagnostic types, etc.) stay in `Aspire.Hosting.Coolify` — only the
  public extension surface is hoisted. Enforced by TC-017.
- The `aspire deploy [--environment <env>]` CLI invocation, run by
  the developer (or CI) against an AppHost that called
  `WithCoolifyDeploy(...)`. The CLI's `--environment` flag, working
  directory, and exit-code expectations come from the Aspire deploy
  pipeline itself; this feature consumes them, it does not redefine
  them.

### Outputs

- A registered Aspire deploying-publisher (working name
  `CoolifyDeployingPublisher`, implementing `IDeployingPublisher`
  or the current Aspire 10 equivalent — final type name chosen at
  implementation time against the Aspire version pinned by FT-001's
  source files). Registration happens inside `WithCoolifyDeploy(...)`
  via Aspire's publisher registration extension point.
- The publisher instance carries the two parameter handles
  (`url`, `token`) as fields/properties, accessible to phase bodies.
  FT-001 **stores** them; it does **not** resolve them, dereference
  them, or read their values. Resolution is FT-002's job inside the
  configure phase (ADR-004 §5).
- `aspire deploy` output that reports the five phases by name, in
  order, with one log line per phase entry and one per phase exit,
  even when every phase body is a no-op. Phase names are exactly
  `configure`, `build`, `push`, `deploy`, `verify` — they match
  ADR-003's vocabulary verbatim so hook authors and downstream
  features can refer to them by name.
- A zero exit code on the skeleton path: with all five phases as
  no-ops, `aspire deploy` against an AppHost that called
  `WithCoolifyDeploy(...)` runs to completion and exits zero —
  regardless of whether the `url` / `token` parameter values are
  actually set, because the skeleton never reads them. (FT-002
  introduces the `E_AUTH_TOKEN_MISSING` / `E_AUTH_TOKEN_INVALID`
  fail-fast paths inside `configure`.)

### State

- **No persistent state on disk.** ADR-003 §4 forbids the publisher
  from writing any `coolify.lock` / `desired.json` / `.coolify-state`
  file. The skeleton inherits this invariant: nothing in FT-001 may
  write files alongside the AppHost.
- **No in-memory cross-deploy state.** Each `aspire deploy`
  invocation reconstructs everything from the AppHost graph.
- The only state FT-001 introduces is (a) the two parameter handles
  (`url`, `token`) captured on the publisher instance at registration
  time, and (b) the publisher's own per-invocation phase-progress
  object, scoped to one deploy call, used for logging and exit-code
  aggregation. Neither outlives the process. The token *value* is
  never read or stored by FT-001 — only the typed parameter handle is.

### Behaviour

1. `WithCoolifyDeploy(url, token)` is invoked during AppHost
   construction. It captures the two parameter-resource handles and
   registers the Coolify deploying-publisher with Aspire's publisher
   registry, attaching the handles to the publisher instance. It is
   idempotent: calling it twice on the same builder is equivalent to
   calling it once (the second call is a no-op — it does **not**
   throw, and it does **not** register a second publisher). Calling
   it twice with *different* `(url, token)` pairs is out of scope for
   v1 and the second call is also treated as a no-op (the first pair
   wins). Multi-target support, if added, is a later feature behind a
   different surface.
2. When `aspire deploy` runs against the AppHost, Aspire's deploy
   pipeline discovers the registered publisher and invokes it.
3. The publisher runs the five phases sequentially:
   1. **configure** — enters, logs entry, exits (no-op body in v1
      skeleton; FT-002 fills in token resolution, version probe, and
      auth probe per ADR-002 / ADR-004).
   2. **build** — enters, logs entry, exits (no-op body in v1).
   3. **push** — enters, logs entry, exits (no-op body in v1).
   4. **deploy** — enters, logs entry, exits (no-op body in v1).
   5. **verify** — enters, logs entry, exits (no-op body in v1).
4. Phase order is fixed and not configurable (ADR-003 §1, §7). The
   skeleton must not permit reordering, skipping, or interleaving
   phases.
5. Phase shells are exposed as **named extension points** with a
   defined async-callback signature, so FT-002+ can attach their
   work inside the matching phase without touching the spine. The
   exact signature is determined at implementation time, but the
   contract is: each phase body is an async unit of work with
   access to the AppHost graph, the deploy context (environment
   name, cancellation token, logger), the stored `(url, token)`
   parameter handles, and the running phase-progress object.
6. The publisher honours `aspire deploy`'s cancellation token —
   if Aspire signals cancellation between phases, the publisher
   exits non-zero with a cancellation diagnostic and does not enter
   the next phase.
7. **The token value is never read by FT-001 code.** The skeleton
   holds the `IResourceBuilder<ParameterResource>` handle only.
   Resolving the parameter to its underlying string value is
   FT-002's responsibility inside the configure phase, where it is
   immediately consumed by the auth probe (ADR-004 §5) and not
   retained beyond the HttpClient that ADR-002 owns.

### Invariants

- **I-1: phases run in the fixed order** `configure → build →
  push → deploy → verify`. No code path in this feature may emit
  a different order.
- **I-2: each phase runs to completion (or fails) before the next
  phase enters.** No phase overlap, even across sibling sub-tasks
  within a phase. (ADR-003 §1; §8 permits intra-phase concurrency
  but not inter-phase.)
- **I-3: no persistent on-disk publisher state.** No file written
  by FT-001's code may survive the deploy process.
- **I-4: `WithCoolifyDeploy(url, token)` is idempotent at the
  builder level.** Calling it N times registers exactly one
  publisher.
- **I-5: skeleton-only behaviour is observable.** With no other
  features implemented, `aspire deploy` exits zero, prints the
  five phase boundaries, and does not contact any external system
  (no HTTP, no DNS lookup, no registry push, no file write, no
  parameter dereference).
- **I-6: signature matches ADR-004.** The public surface in v1
  is exactly `WithCoolifyDeploy(IResourceBuilder<ParameterResource> url,
  IResourceBuilder<ParameterResource> token)` — no parameterless
  overload, no string-named overload, no implicit defaults. Both
  arguments are required.
- **I-7: token value is never read by FT-001.** No code path in
  this feature dereferences the token parameter to its underlying
  string. (Asserted in the skeleton-path test by injecting a
  sentinel value and verifying it never reaches logs, stdout, or
  any HttpClient.)

### Error handling

- **Unsupported Aspire version.** If `WithCoolifyDeploy(...)` is
  called against an Aspire builder whose deploying-publisher
  registration surface is not available (older Aspire, or a future
  version that has moved the API), the extension throws a
  diagnostic exception at AppHost build time naming the minimum
  supported Aspire version. This is preferred over silently
  registering nothing and discovering at `aspire deploy` time that
  no publisher fires.
- **Null parameter handle.** If either `url` or `token` is `null`
  at the call site, `WithCoolifyDeploy(...)` throws
  `ArgumentNullException` at AppHost build time naming the offending
  argument. The skeleton does **not** check whether the parameter
  value is set — that is a configure-phase concern (FT-002,
  ADR-004 §5, `E_AUTH_TOKEN_MISSING`).
- **Cancellation between phases.** As above (Behaviour §6): exit
  non-zero with a precise diagnostic; do not enter the next phase.
- **Phase body throws.** If a (future, non-skeleton) phase body
  throws an unhandled exception, the publisher aborts the deploy,
  emits the exception with the phase name attached, and exits
  non-zero. Sibling phases that already ran are not "rolled back"
  — this matches ADR-003 §6's verify-gated-non-transactional
  rollback contract and is part of the spine, not phase-body work.
- **Aspire deploy invoked without `WithCoolifyDeploy(...)`.** Not an
  error condition for this feature — the publisher is simply not
  registered and Aspire's normal pipeline runs.

### Boundaries

- **In scope for FT-001:**
  - the `WithCoolifyDeploy(url, token)` extension method (fixed v1
    signature per ADR-004)
  - capturing the `(url, token)` parameter handles on the publisher
    instance
  - the deploying-publisher class registration and discovery
  - the five named, no-op-but-wired phase shells
  - phase-boundary logging and ordering invariants
  - cancellation honouring at phase boundaries
  - the Aspire-version compatibility check
  - `ArgumentNullException` on null `url` / `token` at build time
- **Out of scope for FT-001** (deferred to later features):
  - resolving the token parameter to its string value
    (FT-002, ADR-004 §5)
  - the configure-phase version probe and auth probe
    (FT-002, ADR-002 / ADR-004)
  - `E_AUTH_TOKEN_MISSING` / `E_AUTH_TOKEN_INVALID` diagnostics
    (FT-002, ADR-004)
  - any other Coolify API call whatsoever (FT for ADR-002's typed
    client)
  - Aspire-graph walking and resource-to-Coolify-object mapping
    (FT-mapping, ADR-001)
  - image build and registry push (FT-image-flow, ADR-005)
  - env-var/secret sync into Coolify
  - the managed Aspire dashboard
  - TypeScript AppHost parity (the generated module mirroring
    `WithCoolifyDeploy(url, token)`)
  - drift detection, plan/apply, rollback machinery beyond the
    cancellation/throw semantics above

## Out of scope

- **No Coolify integration in v1 skeleton.** The deploy is
  observably a no-op against an actual Coolify instance — exiting
  zero without touching the server is the correct skeleton
  behaviour. Real upserts arrive with the FT that implements
  ADR-003's deploy-phase walk.
- **No token resolution in v1 skeleton.** The handle is captured;
  the value is never read. FT-002 owns the configure-phase
  resolution and the fail-fast paths defined by ADR-004 §5.
- **No additional configuration surface in v1 skeleton.**
  Destination, registry, dashboard opt-in are later features and
  arrive as new extension methods or new overloads of
  `WithCoolifyDeploy` — not by mutating this one's signature.
- **No TypeScript AppHost work.** `aspire restore` generated-module
  parity for `WithCoolifyDeploy(url, token)` is a dedicated later
  feature (domain `apphost-ts`).
- **No drift detection, plan/apply, or multi-AppHost composition.**
  Already deferred by ADR-001 / ADR-003 and the brief's non-goals.
- **No managed dashboard plumbing.** Domain `managed-dashboard` is
  a dedicated later feature.
- **No multi-target `WithCoolifyDeploy` on a single builder.**
  Calling it twice with different `(url, token)` pairs is a no-op
  on the second call in v1; multi-target deploys are a later
  feature behind a separate surface.
