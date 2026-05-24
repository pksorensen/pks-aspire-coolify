---
id: FT-006
title: Verify phase — poll Coolify deploy-action handles until each per-service deploy completes
phase: 1
status: planned
depends-on:
- FT-001
- FT-005
adrs:
- ADR-002
- ADR-003
tests:
- TC-010
domains:
- aspire-publisher
- coolify-api
domains-acknowledged:
  ADR-004: FT-006 does not resolve, read, log, or transmit the Coolify bearer token. Token resolution and the auth probe are owned by FT-002 in the configure phase per ADR-004 §5; verify consumes the already-constructed ICoolifyClient instance whose HttpClient carries the resolved bearer header. FT-006 has no token-handling code path and therefore inherits ADR-004's discipline transitively without needing to link it.
  ADR-001: FT-006 does not walk the Aspire resource graph and does not interact with Coolify's destination/project/environment/service hierarchy. It consumes the deploy-action handle list FT-005 produced after the graph walk completed and polls Coolify's deploy-job status endpoint only. The Aspire-graph-to-Coolify-hierarchy mapping concern is fully discharged by FT-005 upstream; verify operates on opaque per-service handles and has no opinion on how those services map onto the hierarchy.
---

## Description

FT-006 fills in the **verify phase** body that FT-001's skeleton left as a
no-op. By the time `verify` enters, FT-005's deploy phase has walked the
Aspire graph, upserted destination / project / environment / services
under the targeted Aspire environment, and POSTed a per-service
deploy-action trigger that Coolify accepted (HTTP 2xx) for each
successfully-upserted service. FT-005 collected the per-service
deploy-action handles into an in-phase list and handed that list to the
publisher's phase-progress object on its way out. FT-006 picks that list
up and **polls each handle through Coolify's deploy-job endpoint until
the deploy-action reports a terminal outcome** (succeeded / failed), or
until a configurable overall timeout elapses.

This feature is the **gate** that ADR-003 D6 names: verify is the only
phase that converts "Coolify accepted the trigger" into "the deploy
actually completed." Per ADR-003 D6 the rollback contract is
**verify-gated and non-transactional**: when one service's deploy-action
ends in `failed`, the publisher does **not** tear down or revert
siblings that already succeeded in this deploy or in prior deploys. The
diagnostic instead names the failing service(s) and the human-followup
URL of the Coolify deploy-job log so a developer can inspect what went
wrong on the Coolify side. Recovery is the next `aspire deploy`
invocation after fixing the root cause.

The shape is dictated by ADR-003: imperative, sequential-but-
concurrency-permitted, no persistent on-disk state, fail-fast with a
precise stable `E_…` symbol on any unsuccessful exit. The Coolify
endpoint surface is dictated by ADR-002: FT-006 names the
deploy-job-status group by intent — `client.DeployJobs.GetStatusAsync`
— and leaves the actual `/api/v1/...` path, request shape, response
DTO, and terminal-state enumeration to ADR-002's hand-written typed
client and the `coolify-api` domain.

This feature is intentionally **scoped to the verify phase only**: it
does not re-upsert anything, does not retrigger any deploy-action, does
not contact any Coolify endpoint outside the deploy-job-status group,
does not write any file, does not read or compose any env-var, does not
walk the Aspire resource graph (it only walks the handle list FT-005
handed it), and does not perform any health-check against the deployed
service's own HTTP / TCP surface. Per-service health is whatever
Coolify's deploy-action reports — that is the gate, and that is enough
for v1.

## Functional Specification

### Inputs

- **Per-service deploy-action handle list** from FT-005's in-phase
  scope, handed to FT-006 via the publisher's phase-progress object.
  Each entry pairs an Aspire resource name (the service name FT-005
  upserted) with the Coolify-assigned deploy-job identifier the
  trigger POST returned. FT-006 walks this list verbatim and does
  **not** re-derive the set of services to verify by walking the
  Aspire graph again (FT-005 already filtered to
  successfully-triggered services; verify must agree with that set
  exactly).
- The `ICoolifyClient` instance constructed by FT-002 during the
  configure phase. The verify phase issues calls through
  `client.DeployJobs.GetStatusAsync(handle, cancellationToken)`. The
  property-only surface name (`DeployJobs`) is fixed by this feature
  as part of the observable hook contract; the endpoint path, request
  shape, and the exact terminal-state enumeration (`succeeded`,
  `failed`, plus any in-progress states) are ADR-002 / `coolify-api`
  concerns and consumed here as a black box.
- The **configured polling interval and overall timeout** for the
  verify phase, captured via a new `WithVerifyPolling(interval,
  timeout)` extension introduced by this feature (Behaviour §0). Both
  arguments are `TimeSpan`-typed `IResourceBuilder<ParameterResource>`
  handles or direct `TimeSpan` values — final binding shape is
  determined at implementation time, but the contract is: both are
  optional, each falls back to the v1 defaults below if not set, and
  neither value is read by the skeleton (FT-001) at any point. The v1
  defaults are:
  - `interval` = **5 seconds** initial, with exponential backoff
    capped at **60 seconds** between polls per handle.
  - `timeout` = **10 minutes** total wall-clock for the entire verify
    phase, measured from `verify: enter` to `verify: exit`.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context. Cancellation pre-empts the
  configured timeout — a user-cancelled deploy exits with FT-001's
  cancellation diagnostic, not `E_VERIFY_TIMEOUT`.
- The resolved Coolify base URL (from FT-001's `(url, token)` capture,
  resolved by FT-002 in configure). FT-006 reads the base URL **only**
  to compose the human-followup deploy-job URL it includes in
  diagnostics; it does not issue calls against arbitrary paths.

### Outputs

- **On success:** every deploy-action handle in the input list has
  been polled to a `succeeded` terminal outcome before the configured
  overall timeout elapsed. The verify phase exits normally, emitting
  the `verify: enter … verify: exit (ok)` boundary, and the publisher
  process exits zero. For every handle the publisher has emitted one
  Aspire-structured-log entry attributed to the `verify` phase per
  state change observed (e.g. `queued → in_progress → succeeded`),
  with the Aspire resource name and the Coolify deploy-job handle
  attached. No credentials are emitted.
- **On any fail-fast path:** the publisher prints a single diagnostic
  to stderr whose first whitespace-delimited token is one of the
  literal symbols below, exits non-zero, and emits the
  `verify: enter … verify: exit (failed)` boundary. Successfully-
  deployed siblings — both those whose deploy-action `succeeded`
  during this verify phase and those that succeeded in prior deploys
  — are **not** torn down (ADR-003 D6). The three symbols are part of
  the **observable contract** and are matched as literal strings by
  exit-criteria tests:

  | Symbol                        | Stderr-visible literal        | Trigger                                                                                              |
  |-------------------------------|-------------------------------|------------------------------------------------------------------------------------------------------|
  | `E_VERIFY_FAILED`             | `E_VERIFY_FAILED`             | One or more deploy-action handles reached a terminal `failed` (or equivalent non-success) state.     |
  | `E_VERIFY_TIMEOUT`            | `E_VERIFY_TIMEOUT`            | The configured overall timeout elapsed and one or more handles were still in a non-terminal state.   |
  | `E_VERIFY_PHASE_UNEXPECTED`   | `E_VERIFY_PHASE_UNEXPECTED`   | Catch-all for unclassifiable failures inside the verify phase body (transport bug, JSON parse, etc.) |

  All diagnostics carry the same structured field-block shape as
  FT-002 / FT-003 / FT-004 / FT-005:

  ```
  <E_SYMBOL>: <one-line human description>
    project:       <apphost-name>                              (always present)
    environment:   <aspire-env-name>                           (always present)
    resource(s):   <aspire-resource-name>[, …]                 (FAILED / TIMEOUT — the offending services only)
    deploy-job:    <coolify-deploy-job-url>[, …]               (FAILED / TIMEOUT — one URL per offending service)
    elapsed:       <wall-clock since verify: enter>            (TIMEOUT)
    coolify:       <last observed state per handle>            (FAILED — terminal state; TIMEOUT — last non-terminal)
    see:           ADR-003 §D6                                 (all)
    remediation:
      inspect the Coolify deploy-job log at the URL above       (FAILED)
      check Coolify-side resource limits or pull failures       (FAILED)
      raise WithVerifyPolling(timeout) or inspect the job log   (TIMEOUT)
  ```

  The first whitespace-delimited token on the first line is the
  literal `E_…` symbol; this is what exit-criteria tests grep for.
  Successfully-completed siblings are **not** named in the diagnostic
  field-block (the diagnostic is scoped to the offending services
  only — naming successes alongside failures would dilute the signal
  a human responder needs).

- **`verify-progress` log lines** — for every observed state
  transition on every handle, the publisher emits exactly one
  Aspire-structured-log entry attributed to the `verify` phase, on
  any path (success, failed, timeout), of the form:

  ```
  verify-progress: resource=<name> state=<coolify-state>
      handle=<deploy-job-id> elapsed=<wall-clock since verify: enter>
  ```

  A handle that is already `succeeded` on its first poll still
  produces exactly one such line (one transition from "unobserved"
  to `succeeded`).

### State

- **No persistent state on disk authored by FT-006** (inherits ADR-003
  §4 and FT-001 invariant I-3). No `verify.lock`, no
  `last-verify-status.json`, no per-handle history file. The verify
  phase's view of each handle is reconstructed from Coolify GETs on
  every invocation.
- **No in-memory cross-deploy state.** Each `aspire deploy` invocation
  starts the verify phase with the handle list FT-005 just produced;
  it does not remember prior deploys.
- **In-memory state is bounded to the verify phase.** A per-handle
  poll-state struct (current backoff interval, last observed state,
  elapsed wall-clock) lives only in the local scope of the verify
  phase body and is discarded when the phase exits.

### Behaviour

The verify phase body executes the following in this exact order. Any
fail-fast short-circuits with the matching `E_…` symbol and refuses to
re-enter the loop.

0. **(Registration-time, not verify-time.) `WithVerifyPolling(interval,
   timeout)` extension.** FT-006 introduces a new extension method on
   the same publisher-builder surface that `WithCoolifyDeploy(...)`,
   `WithImageRegistry(...)`, and `WithCoolifyDestination(...)` chain
   on:

   ```csharp
   builder.WithCoolifyDeploy(coolifyUrl, coolifyToken)
          .WithImageRegistry(prefix, user, pass)
          .WithCoolifyDestination(coolifyDest)
          .WithVerifyPolling(
              interval: TimeSpan.FromSeconds(10),
              timeout:  TimeSpan.FromMinutes(15));
   ```

   Both arguments are **optional** and the method has an overload that
   accepts zero, one, or both. Calling the method zero times leaves
   the v1 defaults in place (5s initial / 60s cap interval, 10min
   total timeout). Calling it with `null` for either argument throws
   `ArgumentNullException` at AppHost build time naming the offending
   argument (matching FT-001 / FT-003 / FT-005 null-handle
   discipline). The method is idempotent with **last-call-wins**
   semantics (matching FT-003 §I-8 and FT-005 §0): calling it twice
   replaces the captured pair with the second call's values. Negative
   or zero `TimeSpan` values for either argument throw
   `ArgumentOutOfRangeException` at AppHost build time.

1. **Resolve and snapshot the polling configuration.** Read the
   captured `(interval, timeout)` pair (or the v1 defaults if
   unset). The initial per-handle interval starts at the configured
   `interval` and grows under exponential backoff up to a hard cap of
   **60 seconds** between polls per handle, regardless of how high
   the caller set the configured interval. (Rationale: bounding the
   cap protects Coolify from pathological misconfiguration where a
   developer sets `interval: TimeSpan.FromHours(1)` and then waits
   an hour for the first poll. The configured `interval` controls
   the *first* poll only; the cap controls the worst case.) Start
   the wall-clock for the overall `timeout`.

2. **If the handle list is empty, exit immediately.** An empty list
   means FT-005 successfully triggered zero services (an AppHost
   with no containerisable resources). The verify phase emits no
   `verify-progress` lines and exits normally with the standard
   boundary.

3. **Per-handle polling loop.** For every handle in the input list,
   poll `client.DeployJobs.GetStatusAsync(handle, cancellationToken)`
   until it reaches a terminal state (`succeeded` / `failed` or
   equivalent — ADR-002 owns the enumeration). The model permits
   per-handle concurrency (each handle is independent and each poll
   is read-only and idempotent); v1 ships **sequential** iteration in
   the same order FT-005 collected the handles, matching the
   sequential-but-concurrency-permitted discipline of FT-003 /
   FT-004 / FT-005. Within the loop:

   1. Issue one `GetStatusAsync` call for the current handle.
   2. Compare the returned state to the per-handle last-observed
      state. On any change, emit one `verify-progress` log line
      (§Outputs).
   3. If the state is `succeeded`, mark the handle as terminal-ok
      and continue to the next handle (sequential) / mark and let
      the concurrent worker exit (concurrent).
   4. If the state is `failed` (or any non-success terminal state
      ADR-002 enumerates), accumulate `(resource, handle,
      deploy-job-url, terminal-state)` into the `VERIFY_FAILED`
      bucket and continue with the remaining handles. The loop does
      **not** short-circuit on the first failure — it attempts to
      drive every remaining non-terminal handle to a terminal state
      (or to timeout) so the diagnostic can name the full set of
      failures (matching FT-004 §I-9 / FT-005 §I-10 aggregation
      discipline).
   5. If the state is non-terminal, sleep for the current
      per-handle interval (subject to cancellation pre-emption),
      double the per-handle interval for the next iteration, clamp
      to the 60-second cap, and re-poll.
   6. If the overall wall-clock `timeout` elapses before a handle
      reaches a terminal state, mark that handle as `TIMEOUT` and
      stop polling it. (Other handles, in concurrent mode, are
      also stopped — the timeout is **phase-level**, not
      per-handle.)

4. **Aggregate.** After every handle has reached `succeeded`,
   `failed`, or `timeout`:
   - If the `VERIFY_FAILED` bucket is non-empty **and** no handles
     hit `timeout`, fail-fast `E_VERIFY_FAILED` with every
     `(resource, deploy-job-url, terminal-state)` tuple in the
     field-block.
   - If any handle hit `timeout`, fail-fast `E_VERIFY_TIMEOUT` with
     every still-pending `(resource, deploy-job-url,
     last-observed-state)` tuple in the field-block. **Precedence:**
     when both failures and timeouts occur in the same verify phase,
     surface as `E_VERIFY_TIMEOUT` because the elapsed-clock symptom
     is the actionable one for a human responder (raise the timeout
     or inspect the still-running jobs); the failed-handle data is
     still emitted in the per-handle `verify-progress` log lines.
   - Otherwise (all handles `succeeded`), exit the verify phase
     normally.

5. **Exit the verify phase.** Emit the boundary and yield control
   back to the publisher driver.

**Concurrency.** Per the same pattern as FT-003 / FT-004 / FT-005,
FT-006 ships **sequential** per-handle polling in the order FT-005
collected the handles. The model permits per-handle concurrency
(each poll is read-only against an independent deploy-job
identifier), and a future feature may parallelise without
re-deciding. The overall phase-level timeout is invariant under that
future change.

**Cancellation.** If the deploy `CancellationToken` is cancelled at
any point during the polling loop or during an in-flight sleep, the
verify phase exits with FT-001's cancellation diagnostic (not an
`E_…` symbol). Cancellation pre-empts both the configured
`interval` sleep and the configured `timeout` wall-clock.

**Catch-all (`E_VERIFY_PHASE_UNEXPECTED`).** Any exception escaping
the verify phase that is not classifiable as one of the two
preceding symbols (and is not a cancellation) is wrapped and
surfaced as `E_VERIFY_PHASE_UNEXPECTED` with the inner exception's
`Message` appended (no stack trace, no secret content). Mirrors
FT-002 / FT-003 / FT-004 / FT-005 catch-all discipline: better a
precise fail-fast than exiting in an unknown state.

### Invariants

- **I-1: verify is the gate.** No code path in FT-006 may exit zero
  unless every handle in the input list reached a `succeeded`
  terminal state inside the configured timeout. (Re-asserts ADR-003
  D6 at the verify layer.)
- **I-2: non-rolling-back.** No code path in FT-006 may issue a
  DELETE, a PATCH, a POST, or any state-mutating call against any
  Coolify endpoint, including against the services whose
  deploy-action `failed`. Verify is read-only. Asserted by
  request-trace inspection: every request issued by FT-006-attributed
  code paths targets the deploy-job-status endpoint group, and every
  such request is a GET.
- **I-3: per-handle polling honours the overall timeout.** The verify
  phase exits with `E_VERIFY_TIMEOUT` no later than `timeout +
  (one in-flight GET round-trip)` after `verify: enter`. The 60s
  per-poll cap exists exactly so a long sleep cannot push the
  effective exit time beyond this bound by more than one round-trip.
- **I-4: aggregation discipline on per-handle failures.** The
  per-handle loop attempts every non-terminal handle to either
  `succeeded`, `failed`, or `timeout` before surfacing failure, so
  the diagnostic carries all offending tuples. Matches FT-004 §I-9 /
  FT-005 §I-10. Asserted by forcing two of three handles to fail
  and verifying both appear in stderr.
- **I-5: the three `E_…` symbols are stable observable contract.**
  Their spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any symbol is a
  breaking change to the publisher's CLI contract and requires a new
  ADR.
- **I-6: no persistent on-disk publisher state.** No file under the
  AppHost directory is written by FT-006 code under any path.
  Asserted by filesystem-diff before and after a verify-phase exit.
- **I-7: no Coolify endpoint outside the deploy-job-status group is
  contacted.** Verify does not GET the project, environment, service,
  destination, or registry endpoints; it does not call any health
  endpoint on the deployed service's own surface. Asserted by
  request-trace inspection.
- **I-8: empty handle list exits zero immediately.** An AppHost that
  reached verify with zero containerisable resources triggered (FT-005
  collected an empty list) does not contact Coolify at all and exits
  the phase with `verify: enter … verify: exit (ok)` and zero
  `verify-progress` log lines.
- **I-9: 60-second per-poll cap is invariant under
  `WithVerifyPolling(interval, ...)`.** A caller cannot push the
  per-handle interval above 60s no matter what value they pass for
  `interval`; the configured value controls the **initial** interval
  only. Asserted by injecting `interval: TimeSpan.FromMinutes(5)`
  and verifying observable polls occur at most 60s apart after the
  first.
- **I-10: phase boundary is honoured.** Every fail-fast exit emits
  `verify: enter … verify: exit (failed)`. Successful exit emits
  `verify: enter … verify: exit (ok)`. No other boundary lines fire.
- **I-11: precedence on mixed failure + timeout** —
  `E_VERIFY_TIMEOUT` wins. Asserted by a scenario where one handle
  fails and another times out: the surfaced symbol is
  `E_VERIFY_TIMEOUT`, and the per-handle `verify-progress` lines
  still record the failed handle's terminal state.
- **I-12: deploy-job URL composition is path-free.** FT-006 composes
  the human-followup deploy-job URL using the resolved Coolify base
  URL and an opaque path or query the `coolify-api` domain exposes
  (e.g. `client.DeployJobs.GetHumanUrl(handle)`); FT-006 does not
  hard-code any `/projects/.../deployments/...` shape. Hard-coded
  paths would couple FT-006 to a Coolify URL convention that ADR-002
  owns.

### Error handling

The three `E_…` diagnostics enumerated above are the only error paths
this feature introduces. Beyond them:

- **Cancellation during sleep or during an in-flight poll** → FT-001
  cancellation diagnostic (not an `E_…` symbol).
- **Null `interval` or `timeout` at the call site of
  `WithVerifyPolling(...)`** → `ArgumentNullException` thrown at
  AppHost build time, naming the offending argument. Same discipline
  as FT-001 / FT-003 / FT-005.
- **Zero or negative `TimeSpan` for either argument** →
  `ArgumentOutOfRangeException` thrown at AppHost build time, naming
  the offending argument.
- **Coolify returns a transient transport failure on a single poll**
  → the failure does not immediately surface as
  `E_VERIFY_PHASE_UNEXPECTED`; the per-handle loop treats a single
  failed GET as "state unchanged, sleep and retry," because verify is
  inherently a long-running poll and a transient 502 in the middle
  of a 10-minute window is expected. The loop only surfaces
  `E_VERIFY_PHASE_UNEXPECTED` when the underlying transport throws an
  exception the client cannot classify (e.g. a TLS handshake
  failure, a DNS resolution error) — a state the client itself
  distinguishes from a 4xx/5xx response. ADR-002's typed client owns
  this classification.
- **Coolify returns a 404 on a deploy-job handle** → treat as
  `failed` for that handle (the trigger said the job was accepted,
  but the job has vanished — that is a Coolify-side bug or admin
  action, not an unexpected publisher exception). Accumulated into
  the `VERIFY_FAILED` bucket with the 404 status in the
  `last-observed` field.
- **Bug or unclassifiable exception inside the verify phase body** →
  `E_VERIFY_PHASE_UNEXPECTED` with the inner `Message` appended (no
  stack trace, no secret content).

### Boundaries

- **In scope for FT-006:**
  - the `WithVerifyPolling(interval, timeout)` extension (introduced
    here, fixed signature, both arguments optional, last-call-wins
    idempotency, `ArgumentNullException` on null, `ArgumentOutOfRangeException`
    on zero/negative)
  - capturing the polling configuration on the publisher instance
  - the verify-phase body: per-handle polling against
    `client.DeployJobs.GetStatusAsync(...)` until each handle
    reaches a terminal state or the configured overall timeout
    elapses
  - exponential backoff with a hard 60-second per-poll cap
  - the three `E_…` diagnostics with literal symbols, structured
    field blocks, and zero secret content
  - sequential per-handle iteration in the order FT-005 collected
    handles (concurrency permitted by the model, not implemented in
    v1)
  - aggregation of per-handle failures (attempt all handles to
    terminal, then surface the full set)
  - `E_VERIFY_TIMEOUT` precedence over `E_VERIFY_FAILED` when both
    occur in the same phase
  - cancellation honouring at every poll and every sleep
  - empty-handle-list short-circuit (zero Coolify calls)
  - composing the human-followup deploy-job URL via the
    `coolify-api` client (no hard-coded paths)
  - structured `verify-progress` log emission attributed to the
    `verify` phase
- **Out of scope for FT-006** (handled elsewhere or deferred):
  - the `WithCoolifyDeploy(...)` extension and 5-phase skeleton →
    **FT-001**
  - configure-phase token resolution and version+auth probe →
    **FT-002**
  - build-phase image production → **FT-003**
  - push-phase image push and Coolify Private Registry upsert →
    **FT-004**
  - deploy-phase Aspire-graph walk and per-service upserts /
    trigger → **FT-005**
  - the actual `/api/v1/...` path, request / response DTOs,
    terminal-state enumeration, and human-followup URL composition
    helper for the deploy-job-status endpoint group →
    **ADR-002 + the `coolify-api` domain's client**
  - per-service health checks against the deployed service's own
    HTTP / TCP surface (verify gates on Coolify's deploy-action
    outcome only — Coolify itself decides "healthy" per its own
    healthcheck configuration on the service)
  - rolling back successfully-deployed siblings on partial failure →
    forbidden by ADR-003 D6 (re-asserted by I-2)
  - retrying failed deploy-actions automatically → out of v1; the
    next `aspire deploy` invocation retries after the developer
    fixes the underlying cause
  - surfacing Coolify deploy-job log contents inline in the
    diagnostic → out of v1; the diagnostic includes the URL for
    human follow-up, not the log body (which can be large, noisy,
    and contain secrets)
  - per-handle (not phase-level) timeouts → v1 has one phase-level
    timeout; per-handle timeouts are a future affordance
  - per-handle concurrency in v1 → sequential by implementation;
    concurrency-permitted by the model
  - TypeScript AppHost parity for `WithVerifyPolling(...)` →
    **`apphost-ts` domain's feature**

## Out of scope

- **Per-service health probing on the deployed surface.** v1 trusts
  Coolify's own deploy-action outcome as the gate. If Coolify says
  `succeeded`, the service is considered verified. Probing the
  deployed service's HTTP / TCP surface for "is it actually serving"
  is a future affordance — likely a separate `WithVerifyHealthCheck(...)`
  extension — and is deliberately not bundled into the verify phase's
  v1 contract.
- **Rolling back successful siblings when one service fails.** ADR-003
  D6 forbids this; I-2 re-asserts it in the verify layer. Coolify
  retains the previous good version of each service through its own
  deploy history; recovery is a fresh `aspire deploy` after fixing
  the root cause, or a manual Coolify-side rollback.
- **Automatic retry of failed deploy-actions.** A failed deploy-action
  exits the publisher non-zero with `E_VERIFY_FAILED`. There is no
  in-band retry; the developer reads the deploy-job log at the URL
  the diagnostic provides, fixes the underlying cause (broken image,
  bad env-var, missing volume), and re-runs `aspire deploy`.
- **Inlining Coolify deploy-job log content in the diagnostic.** The
  diagnostic emits the human-followup URL only. Log content can be
  arbitrarily large, can contain secret values that Coolify itself
  did not mask, and is unstable in shape across Coolify versions —
  the URL is the safe handoff.
- **Per-handle timeouts.** v1 has exactly one phase-level wall-clock
  timeout. A future feature may add per-handle timeouts for
  workloads where some services are known to take much longer than
  others (database migrations vs. stateless HTTP services); v1
  does not.
- **Persistent on-disk state of any kind.** Forbidden by ADR-003 §4
  and I-6.
- **Concurrent per-handle polling in v1.** Sequential by
  implementation; concurrency-permitted by the model. A later
  feature may parallelise without re-deciding the model.
- **TypeScript AppHost parity** for `WithVerifyPolling(...)` →
  `apphost-ts` feature.
- **Multi-target verify.** Verify operates on exactly the handle list
  FT-005 produced in this deploy invocation. Cross-deploy or
  multi-target verify is not a v1 concept.
- **Aspire dashboard / progress UI integration.** `verify-progress`
  log lines are emitted as Aspire-structured-logs attributed to the
  `verify` phase; how `aspire deploy`'s progress UI consumes those
  is not FT-006's concern.
