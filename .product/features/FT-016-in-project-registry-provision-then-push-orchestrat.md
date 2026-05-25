---
id: FT-016
title: In-project registry provision-then-push orchestration
phase: 2
status: complete
depends-on:
- FT-014
- FT-015
adrs:
- ADR-001
- ADR-003
- ADR-004
- ADR-005
- ADR-007
- ADR-006
- ADR-002
tests:
- TC-032
- TC-033
- TC-034
- TC-035
- TC-036
- TC-037
- TC-038
domains:
- aspire-publisher
- coolify-api
- image-flow
domains-acknowledged: {}
---

## Description

FT-016 closes the loop opened by FT-015: when a `pks-agent-registry`
resource is declared inside the same AppHost as the workloads that
push to it, the Coolify publisher must (a) deploy the registry
application to Coolify **before** any sibling workload's image is
built or pushed, (b) discover the registry's runtime FQDN once
Coolify has accepted the application, and (c) thread that FQDN
through to the workload→registry edge the FT-014 read path consumes,
so the per-resource tags produced in `coolify-build` and pushed in
`coolify-push` target the just-created registry rather than a
stale, hard-coded address.

The mechanism mirrors the Azure exemplar documented in
`docs/azure-acr-cae-flow-recon.md` §1–§6: Azure splits "provision
the registry" and "log in to the registry" into two ordering
annotations (`provision-infrastructure-{acr}` and
`login-to-acr-{name}`), wired into the canonical step graph via
`requiredBy: push-prereq`. The Coolify analogue is a new
`coolify-prereq` pipeline phase inserted between `coolify-configure`
and `coolify-build`. The phase enumerates every
`PksAgentRegistryResource` (FT-015) reachable from the resource
graph and, for each one, upserts a Coolify Application via the
existing FT-013 client surface, triggers its deploy, polls the
registry's `/v2/` endpoint until it returns a non-5xx response, and
then writes the resolved FQDN onto the registry resource so that
every subsequent phase reading
`ContainerRegistryResource.Address` (FT-014 I-1) observes the
real, reachable host.

FT-016 introduces two new `E_…` symbols and one `W_…` symbol —
`E_PREREQ_REGISTRY_DEPLOY_FAILED`,
`E_PREREQ_REGISTRY_UNREACHABLE`, and
`W_REGISTRY_FQDN_FALLBACK`. It does **not** add new endpoints to
the Coolify client: the prereq phase composes
`IApplicationsApi` (create + deploy trigger), the existing
deploy-action polling used by `coolify-verify` (FT-006), and the
Coolify Private-Registry-attachment API surface already exposed
by FT-013 / FT-014 for the per-application attachment step
(property-only on the exact field name — the publisher attaches
the workload Application to the in-project registry's Private
Registry record by whatever field the Coolify v4 API of record
uses; the spec asserts the attachment, not the JSON spelling).

**v1 limitation (explicit).** The FQDN is resolved exactly once,
at `coolify-prereq` time, and stored on the registry resource for
the remainder of the deploy. A pre-set escape hatch is honoured:
if the registry resource has been configured with an explicit
domain via `WithDomain(...)` before the prereq phase runs, the
phase uses that domain verbatim, emits `W_REGISTRY_FQDN_FALLBACK`
so the user knows the auto-discovery branch was skipped, and
proceeds to the reachability probe against that explicit domain.
Dynamic re-resolution (e.g. if Coolify rotates the auto-FQDN
between deploys) is deferred.

FT-016 depends on FT-014 (the publisher's read path must already
follow the workload→registry edge) and FT-015 (the
`PksAgentRegistryResource` type and the `AddPksAgentRegistry`
helper must already exist). It composes with ADR-003's imperative
orchestration (the new phase is one more imperative, idempotent
step in the fixed phase graph) and ADR-005 / ADR-007 (the
address the prereq writes becomes the `<address>` substring of
the FT-003 tag formula).

## Functional Specification

### Inputs

- The Aspire resource graph reachable from the running
  `DistributedApplication` at deploy time, specifically the set
  of `PksAgentRegistryResource` instances declared via
  `AddPksAgentRegistry(...)` (FT-015) and the workload→registry
  edges established by `WithContainerRegistry(...)` (FT-014).
- The Coolify URL + token already resolved by the configure phase
  (FT-002), reachable through whatever surface
  `CoolifyDeployingPublisher` exposes to its phase bodies today.
- An optional pre-set domain on each `PksAgentRegistryResource`,
  established by a `WithDomain(domain)` extension call at AppHost
  wire-time. When present, this is the escape hatch the v1
  limitation documents.
- The Coolify v4 API surface already wrapped by
  `IApplicationsApi`, the deploy-action polling helper reused
  from FT-006's `coolify-verify`, and the Private-Registry
  endpoints reused from FT-004 / FT-014. **No new endpoints are
  added.**

### Outputs

- **On success:** every `PksAgentRegistryResource` in the graph
  has (a) a corresponding Coolify Application created or
  idempotently upserted under the AppHost's Coolify project,
  (b) a triggered Coolify deploy that completed without error,
  (c) a resolved FQDN string written onto the resource as its
  observable `Address` for the remainder of the deploy, and
  (d) a reachability probe to `https://<fqdn>/v2/` (or
  `http://<fqdn>/v2/` if the FQDN's resolved scheme is plain
  HTTP — see §Behaviour §3) that returned a non-5xx, non-network-
  error response within the configured timeout. The subsequent
  `coolify-build`, `coolify-push`, and `coolify-deploy` phases
  then run unchanged against the freshly resolved address.
- **On a deploy failure of the registry Application itself:**
  `E_PREREQ_REGISTRY_DEPLOY_FAILED` is emitted as the first
  whitespace-delimited token on stderr, with a structured field
  block naming the registry resource name, the Coolify
  Application UUID returned by the create call (if the create
  succeeded), the Coolify deploy-action handle being polled (if
  the trigger succeeded), and the underlying Coolify-side error
  message. The pipeline does not advance to `coolify-build`; no
  workload image is built; no Coolify side-effects beyond what
  the prereq phase itself produced are introduced.
- **On a reachability-probe timeout:**
  `E_PREREQ_REGISTRY_UNREACHABLE` is emitted with a structured
  field block naming the registry resource name, the FQDN that
  was probed, the probe URL (`/v2/`), the elapsed time, and the
  last network-level error observed. The pipeline does not
  advance to `coolify-build`.
- **On the escape-hatch path (pre-set `WithDomain(...)`):**
  `W_REGISTRY_FQDN_FALLBACK` is emitted to stderr as a warning
  token (not an error — pipeline continues), naming the registry
  resource name and the user-supplied domain, so users can tell
  from logs that auto-discovery was bypassed.

### State

- **No new persistent state on disk.** FT-016 inherits ADR-003 §4
  / FT-001 I-3 / FT-004 §State unchanged.
- **One in-memory address per registry, valid for one deploy.**
  The resolved FQDN is written onto the
  `PksAgentRegistryResource` instance through whatever mutable
  backing field the resource exposes (FT-015's design choice —
  the field exists as a single-shot completion sink so
  `ContainerRegistryResource.Address` materialises to the
  resolved value when subsequent phases read it). The lifetime
  of that value is bounded by the AppHost process; no on-disk
  cache, no Coolify-side cache, no cross-deploy memo.
- **Coolify-side state introduced by the phase** — the
  registry Application record itself, plus its Private Registry
  attachment used by sibling workloads — is name-keyed and
  idempotent per ADR-003 §4. Re-running the prereq phase against
  a Coolify instance that already holds the registry Application
  produces no duplicate records and either zero updates or one
  update per field whose source-of-truth changed (matches
  FT-005's deploy-phase upsert discipline).

### Behaviour

The pipeline phase order becomes:

```
coolify-configure → coolify-prereq → coolify-build → coolify-push → coolify-deploy → coolify-verify
```

`coolify-prereq` is a new phase inserted between configure and
build. It is a no-op when the resource graph contains zero
`PksAgentRegistryResource` instances (the deploy proceeds
exactly as it does today for AppHosts that use only external
registries). When one or more in-project registries are present,
the phase body runs the following steps **per registry**:

#### 1. Upsert the Coolify Application for the registry

The phase looks up the Coolify project the AppHost is bound to
(per FT-001 / FT-005) and either creates a new Coolify
Application for the registry resource or, if a same-named
application already exists under the project, reuses it
idempotently (matching FT-005's upsert discipline). The
application payload references the same container image, ports,
env-vars, and bind-mounts that FT-015 already records on the
`pks-agent-registry` container resource — `coolify-prereq`
is the same upsert FT-005 performs for any other workload,
run earlier in the pipeline because the registry must exist
before sibling workloads can push to it.

On a non-2xx response from the create / upsert call, the phase
emits `E_PREREQ_REGISTRY_DEPLOY_FAILED` and does not advance.

#### 2. Trigger the Coolify deploy for the registry Application

Using the same deploy-trigger endpoint FT-005 invokes in the
deploy phase, the prereq phase triggers a deploy for the
registry Application and captures the returned deploy-action
handle (UUID).

On a non-2xx response, `E_PREREQ_REGISTRY_DEPLOY_FAILED` fires
with the create-call's Application UUID in the structured field
block (the create succeeded; the trigger did not).

#### 3. Resolve the registry FQDN

The phase establishes the registry's runtime FQDN by one of two
branches:

- **Auto-discovery (default):** poll the Coolify Application's
  record (or whatever read endpoint exposes the assigned domain)
  until the canonical domain field — *property-only on the exact
  field name; the spec asserts the read-back, not the JSON
  spelling* — is populated. The polling cadence and total budget
  match FT-006's verify-phase polling (same backoff, same hard
  timeout). If the auto-discovery branch never produces a
  domain, the reachability probe in §4 surfaces the failure as
  `E_PREREQ_REGISTRY_UNREACHABLE`.
- **Pre-set escape hatch:** if
  `PksAgentRegistryResource.PreSetDomain` is non-null (set by a
  `WithDomain(...)` extension on the registry builder before the
  prereq phase runs), the phase uses that value verbatim and
  skips the auto-discovery poll entirely. The phase emits
  `W_REGISTRY_FQDN_FALLBACK` to stderr to make the bypass
  visible in deploy logs.

Either way, the resolved FQDN string is written to the
`PksAgentRegistryResource`'s observable address sink so that
`IContainerRegistry.Address` returns the resolved value on
subsequent reads.

#### 4. Probe the registry's `/v2/` endpoint until reachable

The phase issues `HEAD /v2/` (falling back to `GET /v2/` if
`HEAD` returns 405) against the resolved FQDN. The probe
succeeds on any non-5xx, non-network-error response (HTTP 200
and HTTP 401 both count as success — both mean the registry's
`/v2/` handler is live; the second case just means it requires
auth, which is exactly what sibling workloads' push step will
perform via the FT-014 credential surface). The probe retries
with the same backoff cadence used in step §3 up to a hard
timeout. On timeout, the phase emits
`E_PREREQ_REGISTRY_UNREACHABLE` and does not advance.

#### 5. Attach the in-project registry to its Coolify Private Registry record

For each sibling workload whose `WithContainerRegistry(...)`
edge points at *this* `PksAgentRegistryResource`, the prereq
phase ensures the workload's eventual Coolify Application (which
`coolify-deploy` will upsert later) will be wired to the
in-project registry's Coolify Private Registry record via the
exact attachment field the Coolify v4 API exposes for this
purpose — **property-only on the exact field name**; the spec
asserts the attachment exists and is keyed by the in-project
registry's Private-Registry UUID, not the JSON spelling. This
composes with FT-004 / FT-014's configure-phase upsert (which
created the Private Registry record itself) by adding the
per-application attachment edge.

The attachment is recorded in an in-memory map keyed by workload
resource name → in-project registry UUID. `coolify-deploy`
(FT-005) reads this map when emitting each workload's
Application payload and sets the attachment field accordingly.
The map is bounded to the deploy's lifetime; no on-disk
persistence.

#### 6. Phase advancement

Once every registry has been processed through steps §1–§5
without an `E_…` exit, the phase yields control to
`coolify-build`. `coolify-build` and `coolify-push` then run
exactly as FT-003 / FT-004 / FT-014 specify today — but the
`<address>` substring of the per-resource tag formula
(`<address>/<resource.Name>:<apphost-version>`) now reads the
freshly resolved FQDN for any workload whose registry edge
points at an in-project registry.

#### 7. Multiple in-project registries

If the resource graph contains N in-project registries, the
phase processes them in resource-graph order (the same iteration
order FT-005 uses) and applies steps §1–§5 to each. Failures
on any registry abort the phase before subsequent registries
are processed — the pipeline is fail-fast (matches FT-004 §I-9
with respect to push: the prereq phase does not partially
provision).

### Invariants

- **I-1: prereq runs before build, build before push, push before
  deploy.** The phase ordering
  `configure → prereq → build → push → deploy → verify` is a
  hard invariant. No code path enters `coolify-build` before
  every in-project registry has cleared steps §1–§5 of the
  prereq phase. Asserted by TC-FT-016-02.
- **I-2: prereq is a no-op when no in-project registries exist.**
  An AppHost that uses only external registries (e.g. `ghcr.io`)
  sees zero prereq-phase Coolify-side calls and zero observable
  log differences compared with an FT-014-only deploy. Asserted
  by TC-FT-016-03.
- **I-3: registry resource's resolved address is the FQDN
  produced by step §3.** After the prereq phase completes,
  `IContainerRegistry.Address` on the in-project registry
  resource returns the resolved FQDN string. Subsequent reads
  by `coolify-build` / `coolify-push` observe that value and
  not a stale or placeholder value. Asserted by TC-FT-016-04.
- **I-4: workload tags target the in-project registry's
  resolved address.** Every workload whose
  `WithContainerRegistry(...)` edge points at the in-project
  registry produces tags shaped `<fqdn>/<workload.Name>:
  <apphost-version>` exactly, where `<fqdn>` is the value
  resolved in step §3. Asserted by TC-FT-016-04.
- **I-5: per-application attachment to the Coolify Private
  Registry record is set.** Each sibling workload's eventual
  Coolify Application body carries the attachment field pointing
  at the in-project registry's Private-Registry UUID — exact
  field name property-only; presence + keying asserted by
  TC-FT-016-05.
- **I-6: deploy failure halts the pipeline.** A non-2xx response
  from the registry's create / upsert / deploy-trigger steps
  surfaces as `E_PREREQ_REGISTRY_DEPLOY_FAILED` and prevents
  `coolify-build` from running. Asserted by TC-FT-016-06.
- **I-7: unreachability halts the pipeline.** A reachability-
  probe timeout against the resolved FQDN surfaces as
  `E_PREREQ_REGISTRY_UNREACHABLE` and prevents `coolify-build`
  from running. Asserted by TC-FT-016-07.
- **I-8: escape-hatch warning is visible.** When
  `WithDomain(...)` was set at wire-time, the prereq phase
  emits `W_REGISTRY_FQDN_FALLBACK` to stderr exactly once per
  registry that used the escape hatch.
- **I-9: idempotency on re-deploy.** Re-running the prereq
  phase against a Coolify instance that already holds the
  registry Application produces no duplicate Application
  records and either zero updates or only the minimal set of
  field updates required to converge (matches ADR-003 §4 and
  FT-005's upsert discipline).
- **I-10: bounded in-memory state.** The workload→
  in-project-registry-UUID attachment map and the resolved-FQDN
  sink on each registry resource exist only for the lifetime
  of the AppHost process. No on-disk cache; no Coolify-side
  cache beyond the records the upsert step itself creates.

### Error handling

Three new symbols are introduced and exposed through the
structured-log channel ADR-003 / FT-001 I-7 specifies:

- **`E_PREREQ_REGISTRY_DEPLOY_FAILED`** — registry-Application
  create, upsert, or deploy-trigger returned non-2xx, or the
  deploy-action polled to a `failed` terminal state. Structured
  field block names registry resource, project UUID, application
  UUID (when known), deploy-action UUID (when known), and the
  underlying Coolify-side error message. Phase exits before
  `coolify-build` runs.
- **`E_PREREQ_REGISTRY_UNREACHABLE`** — the reachability probe
  against the resolved FQDN exceeded the configured timeout
  with no non-5xx response. Structured field block names
  registry resource, resolved FQDN, probe URL, elapsed time,
  and the last network-level error. Phase exits before
  `coolify-build` runs.
- **`W_REGISTRY_FQDN_FALLBACK`** — the auto-discovery branch
  was bypassed because `WithDomain(...)` pre-set a domain.
  Warning only; phase continues. Field block names registry
  resource and the user-supplied domain.

The phase preserves ADR-003 §4's no-rollback discipline:
partially-provisioned Coolify-side state (e.g. an Application
created in step §1 but whose deploy-trigger failed in step §2)
is left intact, with `E_PREREQ_REGISTRY_DEPLOY_FAILED` naming
the residue so the user can decide whether to re-run or
manually clean up. This matches the rest of the publisher's
fail-fast posture and avoids inventing a tear-down code path
v1 does not have elsewhere.

Cancellation token propagation, redaction of secret parameters
(the Coolify token is the only secret the prereq phase
handles; registry admin credentials supplied by FT-015 are
read-only inputs already redaction-discipline-compliant),
and `_UNEXPECTED`-bucket catch-all behaviour follow the same
patterns FT-002 / FT-004 / FT-005 establish. No new
`_UNEXPECTED` symbol is introduced; uncaught prereq-phase
exceptions surface through whatever catch-all the existing
phase host provides.

### Boundaries

- **In scope for FT-016:**
  - the new `coolify-prereq` pipeline phase and its placement
    between `coolify-configure` and `coolify-build`
  - the per-registry orchestration steps §1–§5: Coolify
    Application upsert, deploy trigger, FQDN resolution (auto +
    escape-hatch branches), reachability probe, per-workload
    Private-Registry attachment recording
  - the three new symbols
    `E_PREREQ_REGISTRY_DEPLOY_FAILED`,
    `E_PREREQ_REGISTRY_UNREACHABLE`, and
    `W_REGISTRY_FQDN_FALLBACK`
  - writing the resolved FQDN onto the
    `PksAgentRegistryResource`'s observable address sink so
    subsequent phases read the resolved value through the
    FT-014 channel
  - the workload→in-project-registry-UUID attachment map and
    `coolify-deploy`'s consumption of it when emitting workload
    Application bodies
  - the seven exit-criteria TCs (TC-FT-016-01 through
    TC-FT-016-07) listed in the recon doc
- **Out of scope for FT-016:**
  - the exact JSON field name Coolify v4 uses for per-
    application Private-Registry attachment (property-only —
    the spec asserts the attachment, not the spelling)
  - the exact JSON field name Coolify v4 uses for the
    Application's resolved domain (property-only on read-back)
  - the exact polling cadence / backoff constants (inherited
    from FT-006 verify-phase polling; tuning is implementation
    choice)
  - re-resolving the FQDN within a single deploy if Coolify
    rotates it (v1 limitation — resolved once at prereq time)
  - cross-deploy FQDN persistence / caching
  - tear-down of partially-provisioned Coolify state on
    failure (inherits ADR-003 §4's no-rollback discipline)
  - configurable registry admin credentials surface
    (FT-015's concern; FT-016 only reads whatever the
    `PksAgentRegistryResource` exposes)
  - TypeScript AppHost parity for `coolify-prereq` /
    `WithDomain(...)` (owned by the `apphost-ts` domain's
    parity feature — domain acknowledged below)
  - registry health-check beyond the `/v2/` reachability
    probe (e.g. write-then-read smoke push)
  - multi-region / multi-Coolify-instance in-project registries

## Out of scope

- **Removing the v1 FQDN-resolved-once limitation.** Any
  future enhancement that re-resolves the FQDN mid-deploy, or
  that detects FQDN rotation between deploys and rewrites
  downstream tags accordingly, is deferred to a separate
  feature gated by its own ADR.
- **Coolify-side tear-down.** Partially-provisioned registry
  Applications left behind by a failed prereq phase are not
  cleaned up by the publisher — inherited from ADR-003 §4.
- **Dashboard or UI exposure of the resolved FQDN.** The
  FQDN appears in the deploy log (and in any subsequent
  per-workload tag log line) but is not surfaced to the
  Aspire dashboard as a first-class endpoint of the registry
  resource. A future feature may add a dashboard endpoint
  binding; FT-016 does not.
- **Registry GC / pruning.** Inherits FT-015's
  out-of-scope clause — registry contents survive across
  deploys via the bind-mount; reclamation is the user's
  concern.
- **Multiple registries with the same name across distinct
  Coolify projects.** The phase scopes its idempotency to
  the Coolify project the AppHost is bound to; cross-project
  name collisions are an AppHost-author error and not
  diagnosed by FT-016.
- **TypeScript AppHost parity.** The `apphost-ts` domain
  owns mirroring of any new wire-time surface (`WithDomain`
  in particular); FT-016 acknowledges that gap.
