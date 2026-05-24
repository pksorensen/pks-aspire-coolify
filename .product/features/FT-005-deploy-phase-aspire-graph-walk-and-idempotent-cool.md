---
id: FT-005
title: Deploy phase — Aspire-graph walk and idempotent Coolify upserts with deploy trigger
phase: 1
status: complete
depends-on:
- FT-001
- FT-002
- FT-003
- FT-004
adrs:
- ADR-001
- ADR-002
- ADR-003
- ADR-004
tests:
- TC-001
- TC-009
domains:
- aspire-publisher
- coolify-api
- resource-mapping
domains-acknowledged: {}
---

## Description

FT-005 fills in the **deploy phase** body that FT-001's skeleton left as a
no-op. It is the feature that actually walks the Aspire resource graph in
the fixed order ADR-003 D2 prescribes and turns each Aspire-side intent
into an idempotent name-keyed upsert against Coolify's REST API, ending
with a per-service deploy-action trigger. By the time `deploy` enters,
FT-002 has confirmed the Coolify token is good and the version probe
passed, FT-003 has produced one locally-tagged image per containerisable
resource, and FT-004 has pushed those images to a registry Coolify can
pull from (and, when credentialled, upserted the Coolify-side Private
Registry record). FT-005 is the first phase that mutates Coolify in any
way beyond ADR-005's Private Registry record.

This feature also introduces the public
`WithCoolifyDestination(name)` extension required to satisfy ADR-001 D4:
the Coolify destination is **chosen by config, not derived**, and FT-005
is the first feature that needs the destination identity at deploy time.
The destination-name parameter handle is captured on the publisher
instance at registration time, mirroring FT-001's `(url, token)` and
FT-003's `(prefix, username, password)` capture discipline, and is
resolved exactly once at the top of the deploy phase.

The shape is dictated by ADR-003: imperative orchestration, idempotent
per-resource upserts, name-keyed identity (no Coolify-assigned UUIDs
stored anywhere), no persistent on-disk publisher state, no plan/apply
separation, drift handled as warn-and-overwrite on managed fields. The
walk order is part of the **observable contract**, not an implementation
detail:

```
destination (lookup-or-upsert)
  → project (upsert by AppHost name)
    → environment (upsert by Aspire env name — ONLY the targeted environment, per ADR-001)
      → for each containerisable Aspire resource:
          → upsert app/service in environment, binding to destination, with image tag from FT-003/FT-004
      → for each service: env-var sync   ← OWNED BY FT-007/FT-008, NOT FT-005
      → for each service: reference wiring ← OWNED BY FT-007/FT-008, NOT FT-005
      → for each service: trigger Coolify deploy action (POST), confirm 2xx accepted
```

FT-005 owns the destination/project/environment/service upsert chain and
the per-service deploy-action trigger. It explicitly does **not** own
env-var sync or `WithReference()`-derived wiring — those are FT-007 /
FT-008 — but it provides the named hook points inside the per-service
upsert loop where those features attach their work between the
service-upsert call and the deploy-action trigger. FT-005 also does
**not** own the containerisable-vs-not filter: per the user instruction
and per FT-009's deferred status, FT-005 assumes the resource set it
receives from the publisher driver is already filtered to
containerisable resources (or that all resources in the AppHost are
containerisable). A non-containerisable resource reaching FT-005's
per-resource loop surfaces as `E_COOLIFY_SERVICE_UPSERT_FAILED` from the
Coolify-side rejection, not as a separate FT-005 symbol.

The exit criteria are: (a) `WithCoolifyDestination(name)` captures
correctly with FT-001/FT-003-equivalent discipline; (b) the deploy phase
walks the four-level hierarchy in the fixed ADR-003 D2 order; (c) every
write is a name-keyed upsert (GET-by-name → POST-if-absent /
PATCH-if-present with conservative serialisation of managed fields
only); (d) only the targeted Aspire environment is materialised on the
Coolify side, leaving sibling environments untouched (ADR-001 D2 lazy
materialisation, re-asserted at the deploy layer); (e) on any upsert or
trigger failure, the publisher surfaces the matching `E_…` symbol,
emits the deploy-phase boundary, and does not advance to `verify`;
(f) drift on managed fields is overwritten with the AppHost's value
and emits a single `drift-overwritten` warning line per affected field
(ADR-003 D5); (g) zero persistent on-disk publisher state is written
under any code path.

## Functional Specification

### Inputs

- The `IResourceBuilder<ParameterResource>` destination-name handle
  captured on the `CoolifyDeployingPublisher` instance by a prior call
  to `WithCoolifyDestination(name)` (this feature introduces that
  extension — see Behaviour §0). The handle is required; calling
  `WithCoolifyDestination(...)` with a `null` parameter handle throws
  `ArgumentNullException` at AppHost build time. The deploy phase
  resolves the handle to its string value exactly once at the top of
  the phase.
- The `ICoolifyClient` instance constructed by FT-002 during the
  configure phase. The deploy phase issues calls through
  `client.Destinations`, `client.Projects`, `client.Environments`,
  `client.Applications` / `client.Services` / `client.Databases` (final
  endpoint-group naming is ADR-002's concern — FT-005 names the groups
  by intent, not by path).
- The Aspire resource graph reachable from the running
  `DistributedApplication` instance, as enumerated by the publisher
  driver in the same order FT-003's build phase iterated. FT-005 does
  **not** re-walk the graph from scratch and does **not** re-classify
  containerisable-vs-not — it consumes the same enumeration the build
  and push phases consumed, so build / push / deploy operate on the
  same resource set by construction.
- The image tags that FT-003 attached to local images and FT-004
  pushed to the registry — one per resource, in the deterministic
  shape `<registry-prefix>/<resource-name>:<apphost-version>`. FT-005
  reads these tags from whatever channel the publisher driver
  surfaces (the same channel FT-004's push phase read them from) and
  uses them verbatim as the `image` field on each Coolify
  application/service upsert. FT-005 does **not** recompute the tag
  from the prefix and version.
- The active Aspire environment name (e.g. `Production`), as resolved
  by `aspire deploy [--environment <name>]` and propagated through
  FT-001's phase context. This is the **only** environment that
  FT-005 materialises on the Coolify side per ADR-001 D2.
- The AppHost identity (its `DistributedApplication` builder name /
  AppHost project name) as exposed by the publisher context. This
  becomes the Coolify project name per ADR-001 D1.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context.
- The Coolify-side Private Registry record key `(host, username)`
  upserted by FT-004's configure-phase contribution, when credentials
  are present. FT-005 reads this key from the publisher state established
  by FT-004 and attaches the registry reference to each service upsert.
  When FT-004 ran in anonymous-push mode, FT-005 emits no registry
  reference field on the service upsert and Coolify pulls anonymously
  (assumed to be a public registry or a Coolify-side
  `insecure-registries`-allowed host).

### Outputs

- **On success:** the deploy phase exits normally and yields control
  to the `verify` phase (FT-006). After exit, Coolify holds exactly:
  - one destination matching the resolved destination-name handle
    (pre-existing or lazily upserted),
  - one project matching the AppHost name, with no field changes
    on re-runs against an unchanged AppHost,
  - exactly one environment under that project — the targeted one —
    with no sibling Aspire environments pre-created,
  - one application/service/database per containerisable Aspire
    resource in the targeted environment, each bound to the
    resolved destination, each carrying the deterministic image tag
    from FT-003/FT-004, each with the FT-004 Private Registry record
    referenced when credentials are present (or no registry
    reference for anonymous push),
  - a Coolify-side deploy-action accepted (HTTP 2xx) for each
    service the publisher just upserted. The action itself is still
    running; `verify` (FT-006) polls for completion.
  The publisher has emitted, for every upsert, a single
  Aspire-structured-log entry attributed to the `deploy` phase
  recording the resource kind (`destination` / `project` /
  `environment` / `service`), the resource name, and the Coolify-side
  outcome (`created` / `updated` / `unchanged`). No credentials are
  emitted.
- **On any fail-fast path:** the publisher prints a single diagnostic
  to stderr whose first whitespace-delimited token is one of the
  literal symbols below, exits non-zero, does not enter the `verify`
  phase, and emits the `deploy: enter … deploy: exit (failed)`
  boundary. Successfully-upserted siblings earlier in the walk are
  **not** torn down or reverted (ADR-003 D6 — verify-gated,
  non-transactional rollback). The symbols are part of the
  **observable contract** and are matched as literal strings by
  exit-criteria tests:

  | Symbol                              | Stderr-visible literal              | Trigger                                                                              |
  |-------------------------------------|-------------------------------------|--------------------------------------------------------------------------------------|
  | `E_COOLIFY_DESTINATION_UPSERT_FAILED` | `E_COOLIFY_DESTINATION_UPSERT_FAILED` | GET / POST / PATCH on the destination endpoint returned non-2xx, threw, or timed out |
  | `E_COOLIFY_PROJECT_UPSERT_FAILED`   | `E_COOLIFY_PROJECT_UPSERT_FAILED`   | GET / POST / PATCH on the project endpoint returned non-2xx, threw, or timed out     |
  | `E_COOLIFY_ENVIRONMENT_UPSERT_FAILED` | `E_COOLIFY_ENVIRONMENT_UPSERT_FAILED` | GET / POST / PATCH on the environment endpoint returned non-2xx, threw, or timed out |
  | `E_COOLIFY_SERVICE_UPSERT_FAILED`   | `E_COOLIFY_SERVICE_UPSERT_FAILED`   | GET / POST / PATCH on the application/service/database endpoint returned non-2xx for any resource |
  | `E_COOLIFY_DEPLOY_TRIGGER_FAILED`   | `E_COOLIFY_DEPLOY_TRIGGER_FAILED`   | POST to the per-service deploy-action endpoint returned non-2xx for any service      |
  | `E_DEPLOY_PHASE_UNEXPECTED`         | `E_DEPLOY_PHASE_UNEXPECTED`         | Catch-all for unclassifiable failures inside the deploy phase body                   |

  All diagnostics carry the same structured field-block shape as
  FT-002 / FT-003 / FT-004:

  ```
  <E_SYMBOL>: <one-line human description>
    destination: <resolved-destination-name>        (always present)
    project:     <apphost-name>                     (PROJECT / ENV / SERVICE / TRIGGER)
    environment: <aspire-env-name>                  (ENV / SERVICE / TRIGGER)
    resource(s): <aspire-resource-name>[, …]        (SERVICE / TRIGGER)
    tag(s):      <prefix>/<resource>:<version>[, …] (SERVICE / TRIGGER)
    coolify:     <HTTP status and response excerpt> (all upsert / trigger failures)
    see:         ADR-003 §D2                        (all)
    remediation:
      verify the destination "<name>" exists in Coolify or can be auto-upserted   (DESTINATION)
      check Coolify project list for a stale or name-colliding project            (PROJECT)
      check Coolify project's environment list                                    (ENVIRONMENT)
      inspect the Coolify-side service for a previously-failed deploy / drift     (SERVICE)
      inspect the Coolify-side deploy-job log for the failing service             (TRIGGER)
  ```

  The first whitespace-delimited token on the first line is the
  literal `E_…` symbol; this is what exit-criteria tests grep for.

- **`drift-overwritten` warning lines** — for every field in the
  managed set whose GET-observed value disagrees with the AppHost's
  intended value, the publisher emits exactly one Aspire-
  structured-log warning attributed to the `deploy` phase, on a
  successful path, of the form:

  ```
  drift-overwritten: resource=<name> field=<managed-field-name>
      previous=<old-value-or-REDACTED> new=<new-value>
  ```

  The deploy continues (exit zero on otherwise-successful runs).
  Unmanaged fields produce **no** warning under any condition.

### State

- **No persistent state on disk authored by FT-005** (inherits ADR-003
  §4 and FT-001 invariant I-3). No `coolify.lock`, no
  `coolify-deploy-state.json`, no UUID cache, no
  `.coolify/last-deploy/`. Identity is re-derived by name on every
  invocation (ADR-001).
- **No in-memory cross-deploy state.** Each `aspire deploy` invocation
  reconstructs the entire walk from scratch.
- **In-memory state is bounded to the deploy phase.** The resolved
  destination-name string lives only in the local scope of the deploy
  phase body and in the `ICoolifyClient` calls it issues. Coolify-
  assigned IDs returned by GET/POST responses (e.g. project UUID,
  environment UUID) are used **only within the current deploy** to
  scope subsequent calls (parent-child path resolution) and are
  discarded when the phase exits. They are **never** persisted, never
  written to log, and never propagated to `verify` other than via the
  deploy-action handles the trigger step yields.
- **Per-service deploy-action handles** — the response from each
  per-service trigger POST may carry a Coolify-assigned deploy-job
  identifier. The deploy phase collects these into an in-memory list
  scoped to the current invocation and hands the list to `verify`
  (FT-006) via the publisher's phase-progress object. The list is
  not persisted.

### Behaviour

The deploy phase body executes the following steps in this exact
order. Steps 1–4 are the pre-walk pipeline; steps 5+ are the
per-resource loop. Any failure at any step short-circuits with the
matching `E_…` symbol and refuses to advance to the next phase.

0. **(Registration-time, not deploy-time.) `WithCoolifyDestination(name)`
   extension.** FT-005 introduces a new extension method on
   `IDistributedApplicationBuilder` (or on the same environment-builder
   surface FT-001/FT-003 chain on, matching the call site shape):

   ```csharp
   var coolifyUrl   = builder.AddParameter("coolify-homelab-url");
   var coolifyToken = builder.AddParameter("coolify-homelab-token", secret: true);
   var coolifyDest  = builder.AddParameter("coolify-homelab-destination");

   builder.WithCoolifyDeploy(coolifyUrl, coolifyToken)
          .WithImageRegistry(prefix, user, pass)
          .WithCoolifyDestination(coolifyDest);
   ```

   The method captures the `IResourceBuilder<ParameterResource>`
   handle for the destination name on the `CoolifyDeployingPublisher`
   instance that `WithCoolifyDeploy(...)` registered. The handle is
   required; passing `null` throws `ArgumentNullException` at AppHost
   build time, naming the offending argument (matching FT-001 /
   FT-003 null-handle discipline). The destination-name parameter is
   **not** marked `secret: true` — destination names are not secrets,
   and they appear verbatim in deploy diagnostics. The method is
   idempotent with **last-call-wins** semantics (matching FT-003 §I-8
   for `WithImageRegistry(...)`, differing from FT-001's first-call-
   wins for `WithCoolifyDeploy(...)`): calling it twice replaces the
   captured handle with the second call's value. Calling it zero
   times leaves the publisher with no captured destination handle,
   and the deploy phase fails fast in step 1.

1. **Verify a destination is configured and resolve its name.** Look
   at the publisher's captured destination handle. If
   `WithCoolifyDestination(...)` was never called, or the captured
   parameter resolves to null / empty (after trimming), fail-fast
   `E_COOLIFY_DESTINATION_UPSERT_FAILED` with a remediation line
   pointing at ADR-001 D4. No further Coolify call is attempted.

2. **Destination lookup-or-upsert.** Call
   `client.Destinations.GetByNameAsync(name, cancellationToken)`.
   Three outcomes:
   - **Found:** capture the Coolify-assigned destination ID into the
     in-phase scope and continue to step 3.
   - **Not found (404 / explicit absent):** call
     `client.Destinations.CreateAsync(name, cancellationToken)`. On
     2xx, capture the new destination ID and continue. On any other
     response, fail-fast `E_COOLIFY_DESTINATION_UPSERT_FAILED`. (ADR-001
     D4 permits lazy upsert "where Coolify's API allows"; if the
     Coolify v4 API does not permit programmatic destination creation
     for the requested destination kind, the client's `CreateAsync`
     surfaces that constraint as a precise error which the deploy
     phase wraps verbatim into the diagnostic.)
   - **Transport failure / non-classifiable response:** fail-fast
     `E_COOLIFY_DESTINATION_UPSERT_FAILED`.

3. **Project upsert (name-keyed by AppHost identity).** Compose the
   project name from the AppHost identity (ADR-001 D1) and call
   `client.Projects.GetByNameAsync(<apphost-name>, cancellationToken)`.
   On found → if any managed project-level field has drifted, PATCH
   with the AppHost's value and emit a `drift-overwritten` warning
   line; otherwise no-op. On not-found → POST to create with the
   managed fields. On failure → `E_COOLIFY_PROJECT_UPSERT_FAILED`.
   **Project-level managed fields in v1: project name only.** All
   other project-level settings (description, team membership,
   webhooks, default-environment) are unmanaged and left to
   Coolify's UI.

4. **Environment upsert — targeted environment only.** Inside the
   project resolved in step 3, call
   `client.Environments.GetByNameAsync(projectId, <aspire-env-name>,
   cancellationToken)`. On found → if any managed environment-level
   field has drifted, PATCH and warn; otherwise no-op. On not-found
   → POST to create. On failure →
   `E_COOLIFY_ENVIRONMENT_UPSERT_FAILED`. **Crucially, only the
   environment matching the active `aspire deploy --environment` is
   touched.** No sibling environments declared by the AppHost but
   not targeted by the current deploy are GETted, POSTted, or
   PATCHed. (Re-asserts ADR-001 D2 lazy materialisation at the
   deploy layer; asserted by the `aspire deploy --environment
   Production` exit-criteria test that the Coolify project's
   environment list contains exactly `Production`.) **Environment-
   level managed fields in v1: environment name only.**

5. **Per-resource service upsert loop.** For every resource in the
   enumeration the publisher driver hands to the deploy phase, in
   graph order, sequentially:

   1. **Compose the service name.** Use the Aspire resource's
      `Name` property verbatim (matching FT-003 §Behaviour §4.i —
      no case-folding, no slugification). The service name is the
      identity key for GET-by-name within the targeted environment.

   2. **GET-by-name.** Call
      `client.<group>.GetByNameAsync(projectId, environmentId,
      <resource-name>, cancellationToken)` where `<group>` is
      `Applications`, `Services`, or `Databases` depending on the
      Aspire resource kind (the kind-to-group mapping belongs to
      the `resource-mapping` domain and is consumed by FT-005 as a
      black box). Three outcomes:
      - **Found:** for every field in the **managed set** below
        whose currently-deployed value disagrees with the AppHost's
        intended value, accumulate a drift entry. Then PATCH with
        the AppHost's intended values for the managed set only
        (ADR-002 conservative serialisation: unmanaged fields are
        not sent and are therefore not changed). On 2xx, emit one
        `drift-overwritten` warning per drift entry, log the
        outcome as `updated` (with-drift) or `unchanged` (no
        drift), and continue to step 5.3.
      - **Not found:** POST to create with the managed set fields
        populated and unmanaged fields omitted (Coolify defaults
        apply). On 2xx, log the outcome as `created` and continue
        to step 5.3.
      - **Non-2xx / transport failure for this resource:**
        accumulate `(resource, tag, response-excerpt)` into the
        `SERVICE_UPSERT_FAILED` bucket and continue to the next
        resource. The loop does **not** short-circuit on the first
        failure — it attempts all N so the diagnostic can name the
        full set (matching FT-004 §I-9 push-aggregation
        discipline).

   3. **Hand off to FT-007 / FT-008 hook points.** After the
      service upsert succeeds, the deploy phase invokes the named
      hook points owned by FT-007 (env-var sync) and FT-008
      (`WithReference()`-derived wiring). Those features write env
      vars and reference fields into the Coolify service through
      their own client endpoints. FT-005 does **not** dereference,
      compose, or set any env-var on the service. If either hook
      surfaces a fail-fast diagnostic, the deploy phase exits
      non-zero with **that feature's `E_…` symbol** (not FT-005's).
      Hooks observe the current Coolify-side `projectId`,
      `environmentId`, `serviceId` from the in-phase scope.

6. **Aggregate service-upsert failures.** After the per-resource
   loop has attempted every resource, if the
   `SERVICE_UPSERT_FAILED` bucket is non-empty, fail-fast
   `E_COOLIFY_SERVICE_UPSERT_FAILED` with all failing (resource,
   tag, response-excerpt) tuples in the diagnostic. The
   deploy-action trigger step (7) is **not** executed.
   Successfully-upserted siblings remain in Coolify and are not
   torn down.

7. **Per-service deploy-action trigger.** For every service the
   loop upserted successfully, in the same graph order, call
   `client.<group>.TriggerDeployAsync(serviceId, cancellationToken)`.
   The trigger is **fire-and-confirm-accepted**: a 2xx response
   (typically 202 Accepted with a deploy-job handle) is sufficient
   to record the service as "triggered"; the actual Coolify-side
   deploy progress is `verify` (FT-006)'s concern. Triggers attempt
   every service before reporting failure (same aggregation
   discipline as step 6): a non-2xx response for any service
   accumulates into the `DEPLOY_TRIGGER_FAILED` bucket; after the
   loop, if the bucket is non-empty, fail-fast
   `E_COOLIFY_DEPLOY_TRIGGER_FAILED` with all failing
   (resource, response-excerpt) tuples. Deploy-job handles from
   successful triggers are collected into the in-phase deploy-action
   list described in §State for handoff to `verify`.

8. **Exit the deploy phase.** Once every service has been upserted
   and triggered successfully, hand the deploy-action handle list
   to the publisher driver and exit the phase normally. The verify
   phase (FT-006) takes over.

**Managed-field set (v1, ADR-003 D5).** The deploy phase considers
exactly the following fields **managed** on each Coolify
application/service/database it upserts. PATCH writes only these
fields; drift on any of them produces a `drift-overwritten` warning;
all other fields are unmanaged and left untouched (no GET-comparison,
no warning, no PATCH):

- `image` — the deterministic image tag from FT-003/FT-004
- `registry-reference` — the Coolify Private Registry record handle
  from FT-004 (omitted in anonymous-push mode)
- `destination-binding` — the destination ID from step 2

Ports, domains, FQDNs, healthchecks, restart policy, build args,
volumes, env-vars (which are FT-007's concern even when present),
and every other field Coolify exposes are explicitly **unmanaged**
in v1. This matches the user-stated minimal managed-field set and
ADR-003 D5's "warn-and-overwrite on managed fields, leave unmanaged
fields untouched" contract.

**Concurrency.** Per the same pattern as FT-003 / FT-004, FT-005
ships **sequential** per-resource iteration in graph order. The
model permits per-resource concurrency at the service-upsert and
deploy-trigger steps (each upsert and each trigger is independent
and idempotent), and a future feature may parallelise without
re-deciding. The pre-resource pipeline (destination → project →
environment) is inherently sequential because each step depends on
its parent's ID.

**Cancellation.** If the deploy `CancellationToken` is cancelled
between any of steps 1–8 (including between two resource upserts
in step 5 or between two trigger calls in step 7), the deploy phase
exits with FT-001's cancellation diagnostic (not an `E_…` symbol)
and does not enter `verify`. A cancellation observed inside an
in-flight `ICoolifyClient` call is propagated by the client; FT-005
re-emits it as a cancellation exit, not as an upsert-failure
symbol.

**Catch-all (`E_DEPLOY_PHASE_UNEXPECTED`).** Any exception escaping
the deploy phase that is not classifiable as one of the five
preceding symbols (and is not a cancellation) is wrapped and
surfaced as `E_DEPLOY_PHASE_UNEXPECTED` with the inner exception's
`Message` appended (no stack trace, no secret content). Mirrors
FT-002 / FT-003 / FT-004 catch-all discipline: better a precise
fail-fast than entering `verify` in an unknown state.

### Invariants

- **I-1: walk order is fixed and observable.** Destination →
  project → environment → per-resource service upserts → (FT-007 /
  FT-008 hooks) → per-service deploy triggers, in that exact order.
  No code path may reorder, skip, or interleave the four hierarchy
  levels. The deploy log reflects this order.
- **I-2: every Coolify write is a name-keyed upsert.** The publisher
  never issues a blind POST: every create is preceded by a
  GET-by-name. This re-asserts ADR-001's idempotency-by-name and
  ADR-003 §D3 at the deploy layer. Asserted by request-trace
  inspection: every `POST` on a managed endpoint is preceded by a
  `GET` on the same name-key within the same deploy.
- **I-3: only the targeted environment is materialised.** No sibling
  Aspire environment declared by the AppHost but not targeted by
  the current deploy receives any GET, POST, or PATCH from FT-005.
  Asserted by deploy-log scraping and Coolify-side environment-list
  diff: after `aspire deploy --environment Production`, the Coolify
  project's environment list contains exactly `Production` (matches
  ADR-001 TC-001 invariant under this feature's walk).
- **I-4: managed-set discipline is honoured on PATCH.** Every PATCH
  body sent by FT-005 contains only fields from the v1 managed set
  (`image`, `registry-reference`, `destination-binding`). Unmanaged
  fields are absent from the PATCH payload entirely (ADR-002
  conservative serialisation). Asserted by request-body inspection.
- **I-5: drift warnings fire exactly when a managed field
  disagrees.** For every managed field whose GET-observed value
  differs from the AppHost's intended value, exactly one
  `drift-overwritten` warning is emitted; for unmanaged fields,
  zero warnings are emitted under any condition. Asserted by a
  scenario in which a managed field (`image`) and an unmanaged
  field (e.g. `restart-policy`) are both edited out-of-band before
  a re-deploy: the redeploy emits exactly one warning naming
  `image`, overwrites it, leaves `restart-policy` as the
  out-of-band value, and exits zero.
- **I-6: idempotency on unchanged AppHost.** Running the same
  `aspire deploy --environment <env>` twice in succession produces
  zero net change in Coolify on the second run: no new destinations,
  no new projects, no new environments, no new services, no new
  deploy-actions beyond the standard per-service triggers (which
  are themselves trivially idempotent on Coolify's side — they
  simply queue a fresh deploy job). The deploy log on the second
  run shows every upsert step taking the `unchanged` branch.
- **I-7: no persistent on-disk publisher state.** No file under the
  AppHost directory is written by FT-005 code under any code path.
  Asserted by filesystem-diff before and after a successful
  deploy.
- **I-8: no UUID is propagated across deploys.** Coolify-assigned
  IDs are scoped to the in-flight deploy invocation only. Asserted
  by inspecting in-memory state at phase exit and at process
  termination — no static field, no published manifest, no
  side-channel.
- **I-9: fail-fast preserves successfully-upserted siblings.** A
  service-upsert or trigger failure does not tear down or revert
  siblings that already succeeded earlier in the same loop. Matches
  ADR-003 §D6 verify-gated, non-transactional rollback. Asserted by
  forcing the second of three resources to fail upsert and
  verifying the first resource's Coolify-side state is present and
  unchanged after the deploy exits non-zero.
- **I-10: aggregation discipline on per-resource failures.** The
  service-upsert loop and the deploy-trigger loop both attempt
  every resource before surfacing failure, so the diagnostic carries
  all failing tuples. (Matches FT-004 §I-9.) Asserted by forcing two
  of three resources to fail and verifying both appear in stderr.
- **I-11: the six `E_…` symbols are stable observable contract.**
  Their spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any symbol is
  a breaking change to the publisher's CLI contract and requires a
  new ADR.
- **I-12: FT-007 / FT-008 hook points are invoked exactly once per
  successfully-upserted service, between upsert success and trigger
  POST.** FT-005 does not invoke them on failed upserts (no
  serviceId to pass) and does not invoke them after the trigger
  (env-vars must be in place before Coolify pulls and starts the
  container). Asserted by hook-invocation counting in a test that
  installs no-op hooks.
- **I-13: phase boundary is honoured.** Every fail-fast exit emits
  `deploy: enter … deploy: exit (failed)` with no `verify: enter`
  line. Successful exit emits `deploy: enter … deploy: exit (ok)`
  followed by `verify: enter`.
- **I-14: no env-var write is issued by FT-005.** This feature does
  not call any Coolify env-var endpoint, does not read or compose
  any Aspire parameter / connection-string value beyond the
  destination-name handle, and does not interpret
  `WithReference()` edges. Asserted by request-trace inspection:
  zero requests against the env-var endpoint group originate from
  FT-005-attributed code paths.
- **I-15: anonymous-push mode propagates correctly.** When FT-004
  ran in anonymous-push mode (no Private Registry record exists),
  the service-upsert payload omits the `registry-reference` field
  entirely (rather than sending null or empty). Coolify pulls
  anonymously. Asserted by request-body inspection.

### Error handling

The six `E_…` diagnostics enumerated above are the only error paths
this feature introduces. Beyond them:

- **Cancellation between or during steps** → FT-001 cancellation
  diagnostic (not an `E_…` symbol).
- **Null destination handle at the call site of
  `WithCoolifyDestination(...)`** → `ArgumentNullException` thrown
  at AppHost build time, naming the offending argument. Same
  discipline as FT-001 / FT-003.
- **Destination handle captured but parameter resolves to
  null/empty at deploy time** → `E_COOLIFY_DESTINATION_UPSERT_FAILED`
  with a remediation pointing at the `coolify-<name>-destination`
  Aspire parameter.
- **Coolify's API refuses destination creation for the requested
  destination kind** (e.g. localhost Docker destination can be
  created programmatically, but a remote-SSH destination requires
  UI-side server registration first) → the
  `client.Destinations.CreateAsync(...)` call surfaces a precise
  error which FT-005 wraps verbatim into
  `E_COOLIFY_DESTINATION_UPSERT_FAILED`. The remediation line
  instructs the developer to create the destination in the Coolify
  UI and re-run. (ADR-001 D4 anticipates this: "upserts it lazily
  where Coolify's API allows, or fails with an actionable error
  otherwise.")
- **Name collision with a pre-existing non-managed Coolify resource**
  (e.g. a project of the AppHost's name already exists and was
  created by hand in the Coolify UI) → ADR-001 §Consequences
  requires fail-with-actionable-error early. v1 treats a found
  project / environment / service as ours and proceeds with
  upsert-in-place per the idempotency-by-name contract. A future
  ADR may add a "claim marker" check to distinguish publisher-
  managed from hand-managed resources; v1 does not, and a manually-
  created project of the same name is silently adopted on first
  deploy. This is a known v1 limitation and is documented in §Out
  of scope.
- **FT-007 / FT-008 hook surfaces a fail-fast diagnostic** → the
  deploy phase exits non-zero with **that feature's** `E_…` symbol;
  no `E_DEPLOY_PHASE_UNEXPECTED` wrapping. The deploy-trigger step
  is not executed.
- **Bug or unclassifiable exception inside the deploy phase body** →
  `E_DEPLOY_PHASE_UNEXPECTED` with the inner `Message` appended (no
  stack trace, no secret content).

### Boundaries

- **In scope for FT-005:**
  - the `WithCoolifyDestination(name)` extension (introduced here,
    fixed signature, last-call-wins idempotency, `ArgumentNullException`
    on null handle)
  - capturing the destination-name handle on the publisher instance
  - the deploy-phase body: destination lookup-or-upsert → project
    upsert → environment upsert (targeted only) → per-resource
    service upsert loop → per-service deploy trigger
  - name-keyed GET-then-POST-or-PATCH discipline at every level
  - managed-field discipline (v1 set: `image`, `registry-reference`,
    `destination-binding`); unmanaged fields untouched
  - drift detection on managed fields: warn-and-overwrite per ADR-003
    §D5; one `drift-overwritten` warning per affected managed field
  - the six `E_…` diagnostics with literal symbols, structured field
    blocks, and zero secret content
  - sequential per-resource iteration in graph order (concurrency
    permitted by the model, not implemented in v1)
  - aggregation of per-resource failures (attempt all N, then
    surface the full set)
  - cancellation honouring at every step
  - fire-and-confirm-accepted semantics for the per-service deploy
    trigger (2xx ⇒ recorded; FT-006 polls)
  - invoking FT-007 / FT-008 hook points between service upsert and
    deploy trigger, exactly once per successfully-upserted service
  - collecting deploy-action handles into the in-phase list handed
    to `verify` (FT-006)
  - structured deploy-log emission attributed to the `deploy` phase
- **Out of scope for FT-005** (handled elsewhere or deferred):
  - env-var sync into Coolify environments / services → **FT-007**
  - `WithReference()`-derived service-to-service wiring → **FT-008**
  - filtering the resource set to containerisable resources →
    **FT-009 (deferred)**; FT-005 assumes the caller has already
    filtered
  - polling Coolify deploy-action completion / verification →
    **FT-006**
  - the `WithCoolifyDeploy(...)` extension and 5-phase skeleton →
    **FT-001**
  - configure-phase token resolution and version+auth probe →
    **FT-002**
  - build-phase image production → **FT-003**
  - push-phase image push and Coolify Private Registry upsert →
    **FT-004**
  - the actual `/api/v1/...` paths, request/response DTOs,
    name-keyed GET helpers, conservative serialisation, and
    kind-to-endpoint-group dispatch for destinations / projects /
    environments / applications / services / databases →
    **ADR-002 + the `coolify-api` domain's client**
  - the Aspire-resource-kind → Coolify-resource-kind classifier
    (Project → Application, Container → Service, Database →
    Database, etc.) → **`resource-mapping` domain**; FT-005
    consumes the classifier as a black box
  - claim-marker / managed-by tagging on Coolify-side resources to
    distinguish publisher-managed from hand-managed (a future ADR
    may add this; v1 silently adopts)
  - tear-down of resources removed from the AppHost between
    deploys (the publisher does not delete; orphans accumulate)
  - strict-mode / refuse-to-deploy on drift → ADR-003 already
    defers
  - plan / `--dry-run` mode → ADR-003 already defers
  - multi-destination deploys (the `WithCoolifyDestination(...)`
    extension captures one destination per builder; multi-destination
    is post-v1)
  - retry / backoff on upsert or trigger failures → single attempt
    per resource; failure → fail-fast aggregation (matches FT-002 /
    FT-003 / FT-004 transport-failure discipline)
  - TypeScript AppHost parity for `WithCoolifyDestination(...)` →
    **`apphost-ts` domain's feature**

## Out of scope

- **Env-var / secret sync.** FT-007 owns env-var sync into Coolify
  environment-variable scope. FT-005 calls the FT-007 hook between
  service upsert and trigger; it does not read, compose, or write any
  env-var itself. Asserted by I-14.
- **`WithReference()` wiring.** FT-008 owns translating
  `WithReference(otherService)` edges into Coolify intra-network DNS
  / env-var references. FT-005 provides the hook point and the
  current Coolify-side IDs the wiring needs, nothing more.
- **Containerisable-vs-not filtering.** FT-009 (deferred) owns the
  classifier. FT-005 assumes every input resource is containerisable
  and surfaces Coolify's own rejection of a non-containerisable
  resource as `E_COOLIFY_SERVICE_UPSERT_FAILED`.
- **Verification of deploy-action completion.** FT-006 polls the
  per-service deploy jobs whose handles FT-005 collected. FT-005's
  exit criterion is "Coolify accepted the trigger (2xx)," not
  "Coolify completed the deploy."
- **Tear-down / GC of removed resources.** Aspire resources deleted
  from `AppHost.cs` between deploys are **not** deleted from Coolify
  by FT-005. The orphaned Coolify-side service remains until removed
  in the Coolify UI. A future `aspire coolify gc` is out of v1.
- **Claim-marker / managed-by tagging.** v1 silently adopts an
  existing Coolify project / environment / service of the same name
  as if the publisher created it. A hand-created project with the
  AppHost's name will be PATCHed in place on first deploy. ADR-001
  §Consequences flags this; a future ADR may add markers.
- **Multi-destination.** `WithCoolifyDestination(...)` captures one
  destination per AppHost in v1; cross-destination service placement
  is post-v1.
- **Plan / `--dry-run` / strict-drift modes.** Already deferred by
  ADR-003.
- **Persistent on-disk state of any kind.** Forbidden by ADR-003 §4
  and I-7.
- **Retry / backoff on upsert or trigger failures.** Single attempt
  per call; failure → aggregated fail-fast at end of the loop.
- **Rolling-back successfully-upserted siblings on later failure.**
  ADR-003 §D6: verify-gated, non-transactional. Siblings stay
  upserted; recovery is the next deploy after fixing the underlying
  cause.
- **Reading env-var or `WithReference()`-related fields on the
  service GET for drift detection.** Those fields are FT-007 /
  FT-008 managed; FT-005's drift check looks only at its own
  managed set (`image`, `registry-reference`, `destination-binding`).
- **Per-resource concurrency in v1.** Sequential by implementation;
  concurrency-permitted by the model.
- **TypeScript AppHost parity** for `WithCoolifyDestination(...)` →
  `apphost-ts` feature.
