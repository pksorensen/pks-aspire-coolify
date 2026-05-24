---
id: FT-008
title: Service-to-service network wiring — Aspire WithReference() edges into Coolify intra-destination env-vars
phase: 1
status: complete
depends-on:
- FT-005
- FT-007
adrs:
- ADR-001
- ADR-002
- ADR-003
- ADR-004
- ADR-006
tests:
- TC-012
domains:
- aspire-publisher
- coolify-api
- resource-mapping
- secrets-env
domains-acknowledged: {}
---

## Description

FT-008 fills in the **service-to-service wiring hook point** that FT-005's
per-resource service-upsert loop calls between FT-007's env-var sync and the
per-service deploy-action trigger (FT-005 I-12). It is the feature that turns
Aspire's `WithReference(otherResource)` edges into Coolify-side env-vars at
**service scope** that point the consuming container at the target service's
Coolify-internal hostname — the hostname by which Coolify's destination
private network resolves the target service.

By the time FT-008 is invoked for a given service, FT-002 has resolved every
Aspire parameter, FT-005 has upserted every Coolify application / service /
database in the targeted environment and holds each one's
`(projectId, environmentId, serviceId)` in the in-phase scope, and FT-007
has just finished writing the **parameter** env-vars for the current service
(including emitting `envvar-skipped: …reason=awaiting-FT-008` for every
connection-string-named key that FT-008 is now responsible for filling).
FT-008 is the **value-side** of those reserved connection-string keys plus
the producer of the `services__<name>__<scheme>__<i>` endpoint-reference
keys Aspire's reference semantics expose to consumers.

The shape is dictated by ADR-001 (idempotency-by-name, lazy upsert of only
the targeted environment), ADR-002 (the typed `ICoolifyClient` exposes the
same env-var endpoint group FT-007 uses; FT-008 calls it through the same
client surface with the same GET-by-name / POST / PATCH discipline), and
ADR-003 (imperative per-step upsert; warn-and-overwrite on managed fields;
no plan/apply; no persistent state). Sentinel redaction is reused verbatim
from FT-007 / ADR-004 — connection-string values are the most likely
secret-carrying values in the whole publisher.

The exit criteria for this feature are: (a) for every successfully-upserted
Coolify service, FT-008 enumerates the resource's `WithReference()` edges,
classifies each as endpoint-reference or connection-string-reference, and
upserts the matching env-vars at **service scope** in Coolify using the same
naming convention Aspire's reference semantics expose to consumers (so a
container migrated from any other Aspire deployment sees the same env-var
names it would there); (b) every value resolves the target service by its
**Coolify-internal hostname** inside the destination's private network
(exact slug/service-name format is implementation-defined against the live
Coolify v4 API and is **not** locked by this feature's spec — the contract
is the resolution property, not the format); (c) FT-008 honours the
single-writer-per-key invariant with FT-007 — it writes connection-string
keys FT-007 reserved and the `services__…` keys, nothing else; (d) every
value derived from a secret parameter, or whose template carries an Aspire
redaction sentinel, lands as a Coolify env-var with the secret flag set;
plain endpoint-URL references with no secret contribution land non-secret;
(e) the sentinel-leak detector scans FT-008's own emitted output and any
Coolify response excerpt FT-008 would print, failing fast with
`E_REFERENCE_SECRET_LEAKED` if a sentinel slips through; (f) the three
`E_…` symbols are stable observable contract; (g) zero persistent on-disk
state.

## Functional Specification

### Inputs

- **Phase-scope handles from FT-005's per-resource loop, post-FT-007.** The
  Aspire `IResource` currently being processed (the **consumer**), the
  resolved Coolify-side `(projectId, environmentId, serviceId)` of its
  just-upserted service, the `ICoolifyClient` instance, and the in-phase
  map FT-005 maintains of `aspire-resource-name → (serviceId, kind,
  coolify-internal-hostname)` for every resource the deploy phase has
  upserted so far in the current walk. FT-008 reads target identity and
  Coolify-internal hostname from this map by Aspire resource name — it
  does **not** GET Coolify endpoints to resolve target identity.
- **The current Aspire resource's reference edges,** as exposed by the
  standard Aspire resource graph. Two kinds in scope:
  - **(a) Endpoint references** — `WithReference(otherResource)` where
    `otherResource` exposes one or more endpoints. Each endpoint carries
    a name, a scheme (e.g. `http`, `https`, `tcp`), and an index when
    multiple endpoints share a scheme. Aspire's consumer-side convention
    surfaces these as `services__<resource-name>__<scheme>__<i>` env-vars
    whose value is the endpoint URL.
  - **(b) Connection-string references** — `WithReference(otherResource)`
    where `otherResource` exposes a connection string (Postgres,
    Redis, etc.). Aspire's consumer-side convention surfaces these as
    `ConnectionStrings__<name>` env-vars whose value is the resolved
    connection-string template.
  - **(c) `ParameterResource` references — explicitly out of scope.**
    FT-007 owns the parameter projection. FT-008 skips parameter
    references with no log line (FT-007 already handled them).
- **The set of connection-string-named keys FT-007 reserved for this
  service.** FT-007 emits `envvar-skipped: resource=<n>
  key=<connstring-name> reason=awaiting-FT-008` for every
  connection-string reference whose value FT-007 did not have. FT-008 is
  the writer for exactly those keys plus the endpoint `services__…`
  keys. (Single-writer invariant — A2.)
- **The Aspire-side sentinel set** for redaction detection. Same sentinel
  set FT-007 consumes (ADR-004 §6). FT-008 uses it both for (i)
  classifying a value as secret-bearing and (ii) scanning its own
  outgoing output for leak detection.
- **The current `aspire deploy` `CancellationToken`,** propagated from
  FT-005's phase context.

### Outputs

- **On success (per service):** Coolify holds, at service scope for the
  just-upserted consumer service, exactly one env-var per
  endpoint-reference endpoint (one `services__<target>__<scheme>__<i>`
  per `(target, scheme, index)` tuple) plus one env-var per
  connection-string reference (one `ConnectionStrings__<name>` per
  reserved key). Each env-var:
    - is keyed by the **name the consuming container would see in any
      other Aspire deployment** — verbatim Aspire reference-semantics
      naming, no prefix, no transform;
    - carries a value that resolves the target service by its
      **Coolify-internal hostname** inside the destination's private
      network (the exact hostname format — Coolify slug, service-name,
      or other — is the implementation's concern against the live
      Coolify v4 API, and is not pinned by this spec; the contract is
      the resolution property);
    - is marked **secret on the Coolify side** when (i) any input secret
      Aspire parameter contributed to the resolved value OR (ii) the
      template carries an Aspire redaction sentinel; otherwise marked
      non-secret (plain endpoint URLs default to non-secret);
    - is upserted by name (GET-by-name → PATCH if present, POST if
      absent — ADR-001 / ADR-003 §D3) through the **same `ICoolifyClient`
      env-var endpoint group FT-007 uses**, so the FT-007 upsert
      mechanism is reused verbatim.
  FT-008 emits one Aspire-structured-log entry per env-var attributed to
  the `deploy` phase, naming the consumer resource, the target resource,
  the env-var key, and the outcome (`created` / `updated` /
  `unchanged`). For secret-flagged env-vars the log line records the key
  only — the value is **never** included.

- **On any fail-fast path:** the publisher prints a single diagnostic to
  stderr whose first whitespace-delimited token is one of the three
  literal `E_…` symbols below, exits non-zero, does not invoke the
  per-service deploy-action trigger for the failing service, and
  surfaces FT-008's symbol upward to FT-005 (which exits the deploy
  phase with that symbol — not `E_DEPLOY_PHASE_UNEXPECTED`, per FT-005
  §"Error handling"). Successfully-upserted env-vars on the same
  service (from FT-007 or from earlier FT-008 keys on the same service)
  and successfully-completed prior services are **not** torn down
  (ADR-003 §D6, FT-005 I-9). The three symbols are part of the
  **observable contract**:

  | Symbol                             | Stderr-visible literal               | Trigger                                                                                                                                                  |
  |------------------------------------|--------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------|
  | `E_REFERENCE_TARGET_NOT_DEPLOYED`  | `E_REFERENCE_TARGET_NOT_DEPLOYED`    | A `WithReference()` edge names a target Aspire resource that is **not** present in FT-005's in-phase upserted-services map (defensive — should not happen if FT-005's walk is well-ordered) |
  | `E_REFERENCE_ENVVAR_UPSERT_FAILED` | `E_REFERENCE_ENVVAR_UPSERT_FAILED`   | One or more env-var GET / POST / PATCH calls returned non-2xx (aggregated per service across both endpoint-ref and connection-string-ref keys)            |
  | `E_REFERENCE_PHASE_UNEXPECTED`     | `E_REFERENCE_PHASE_UNEXPECTED`       | Catch-all for unclassifiable failures inside the reference-wiring hook body                                                                                |

  A sentinel detection escalates to FT-007's `E_ENVVAR_SECRET_LEAKED`
  (the leak detector is a single discipline reused verbatim across
  FT-007 and FT-008; the symbol does not bifurcate). See §Error
  handling §"Sentinel-leak precedence" below.

  All diagnostics carry the same structured field-block shape as
  FT-005 / FT-007:

  ```
  <E_SYMBOL>: <one-line human description>
    project:     <apphost-name>                       (always)
    environment: <aspire-env-name>                    (always)
    resource:    <aspire-resource-name>               (always — consumer)
    service:     <coolify-service-name>               (always)
    target(s):   <aspire-target-resource-name>[, …]   (TARGET_NOT_DEPLOYED)
    keys:        <env-var-key>[, …]                   (UPSERT_FAILED — failing keys only; never values)
    coolify:     <HTTP status and response excerpt>   (UPSERT_FAILED — already sentinel-scanned)
    see:         ADR-001 §D2, FT-005 walk-order       (TARGET_NOT_DEPLOYED)
                 ADR-002, FT-007 upsert mechanism     (UPSERT_FAILED)
    remediation:
      verify the target resource is reachable in the AppHost graph and
      that FT-005's walk has not been re-ordered                       (TARGET_NOT_DEPLOYED)
      inspect the Coolify-side service's environment-variables panel
      for the failing key(s)                                           (UPSERT_FAILED)
  ```

  The first whitespace-delimited token on the first line is the literal
  `E_…` symbol.

### State

- **No persistent state on disk authored by FT-008.** Inherits ADR-003
  §4, FT-005 I-7, FT-007 I-10. No reference-cache file, no per-service
  manifest, no diff document.
- **No in-memory cross-deploy state.** Every invocation re-resolves
  references and re-issues GET-by-name upserts.
- **In-memory state is bounded to one service's hook invocation.** The
  resolved value for any secret-bearing reference lives only in the
  local scope of the upsert call and inside the request body the
  `ICoolifyClient` sends. It is **not** stored on any publisher-level
  object, **not** captured in a closure that outlives the per-service
  hook, and **not** returned to FT-005.
- **Aggregation buckets are per-service, not per-deploy.** The
  `REFERENCE_ENVVAR_UPSERT_FAILED` bucket and the
  `REFERENCE_TARGET_NOT_DEPLOYED` bucket are reset at the start of
  each service's hook invocation.

### Behaviour

The reference-wiring hook body executes the following steps in this
exact order, **per service** invoked by FT-005's per-resource loop,
**after FT-007 has returned successfully for the same service** (A5).

1. **Enumerate the current Aspire resource's reference edges.** Walk
   the resource's `WithReference()`-derived references via the standard
   Aspire resource-graph API. Partition into three buckets:
    - **endpoint references** (target exposes endpoints),
    - **connection-string references** (target exposes a connection
      string),
    - **parameter references** — **skip silently** (FT-007 owned them;
      A1).

2. **Resolve each reference target against FT-005's upserted-services
   map.** For every endpoint and connection-string reference, look up
   the target Aspire resource name in the in-phase map FT-005 maintains
   of upserted services for the current deploy. Three outcomes:
    - **Target present:** capture the target's
      `(serviceId, coolify-internal-hostname)` for value composition.
    - **Target absent (defensive):** accumulate the target name into
      the per-service `REFERENCE_TARGET_NOT_DEPLOYED` bucket and
      continue. This should not happen if FT-005's walk order is
      well-formed — parents-before-children means any referenced
      resource has already been upserted by the time the consumer's
      hook runs. The bucket exists to surface a precise diagnostic if
      the walk order ever regresses (e.g. a future feature reorders
      the loop).
    - **Target is a non-containerisable Aspire resource that FT-005's
      input set never contained** (skip-with-warning resource per
      ADR-001 D5) — same `REFERENCE_TARGET_NOT_DEPLOYED` symbol with a
      remediation line pointing at the skip-with-warning class.

3. **Compose env-var (key, value, secret-flag) tuples.** For each
   resolvable reference:
    - **Endpoint reference** — for every `(scheme, index)` endpoint the
      target exposes, emit a tuple:
        - key = `services__<target-resource-name>__<scheme>__<index>`
          (verbatim Aspire consumer-side convention — no
          case-folding, no prefix, no transform);
        - value = `<scheme>://<coolify-internal-hostname>:<port>` (the
          hostname resolves inside the destination's private network;
          exact format of the hostname token is the implementation's
          concern — A4);
        - secret-flag = **false** (plain endpoint URL with no secret
          contribution; A3 default).
    - **Connection-string reference** — emit one tuple:
        - key = `ConnectionStrings__<connection-string-name>` (verbatim
          Aspire consumer-side convention; same key FT-007 reserved
          with `envvar-skipped: …reason=awaiting-FT-008`);
        - value = the connection-string template with the target host
          resolved to the Coolify-internal hostname and any parameter
          placeholders resolved through Aspire (same resolution path
          FT-007 uses; same sentinel discipline);
        - secret-flag = **true** if (i) any secret-marked Aspire
          parameter contributed to the resolved value OR (ii) the
          template (post-resolution) contains an Aspire redaction
          sentinel (A3); **false** otherwise.

4. **Pre-flight sentinel scan on resolved values.** For every tuple
   whose secret-flag is **false** but whose resolved value happens to
   contain an Aspire redaction sentinel (indicating a redaction-pipeline
   bug — a non-secret value should not carry a sentinel), escalate to
   step 7 (`E_ENVVAR_SECRET_LEAKED` via the FT-007 leak-detector
   discipline reused verbatim) before issuing any Coolify call.

5. **Per env-var upsert loop.** For every `(key, value, secret-flag)`
   tuple gathered in step 3 (after step-4 sentinel pre-flight),
   sequentially:
    1. **GET-by-name** through the same `ICoolifyClient` env-var
       endpoint group FT-007 uses, scoped to the current
       `serviceId`. Three outcomes:
        - **Found:** if the currently-deployed `(value, secret-flag)`
          disagrees with the AppHost's intent, PATCH with the AppHost's
          values for the v1 managed set (`value`, `secret-flag`). On
          2xx, emit one `drift-overwritten: resource=<consumer>
          field=envvar:<key> previous=<old-or-REDACTED>
          new=<REDACTED-if-secret-else-new>` warning per drifted
          attribute, and log `updated` or `unchanged` accordingly.
        - **Not found:** POST to create with `(key, value,
          secret-flag)`. On 2xx, log `created`.
        - **Non-2xx / transport failure for this key:** accumulate
          `(key, response-excerpt)` into the per-service
          `REFERENCE_ENVVAR_UPSERT_FAILED` bucket and continue. The
          loop does **not** short-circuit on first failure — attempt
          every key so the diagnostic can name the full failing set
          (matches FT-005 I-10, FT-007 I-8).
    2. **Sentinel-scan the response excerpt** before it would be logged
       or attached to a diagnostic. If a sentinel is detected,
       escalate via the FT-007 leak-detector discipline
       (`E_ENVVAR_SECRET_LEAKED`).

6. **Aggregate per-service failures.** After the per-env-var loop:
    - If the `REFERENCE_TARGET_NOT_DEPLOYED` bucket is non-empty,
      fail-fast `E_REFERENCE_TARGET_NOT_DEPLOYED` with the full list of
      missing target names. FT-005's per-service deploy-action trigger
      is not invoked for this service.
    - Else if the `REFERENCE_ENVVAR_UPSERT_FAILED` bucket is non-empty,
      fail-fast `E_REFERENCE_ENVVAR_UPSERT_FAILED` with all failing
      `(key, response-excerpt)` tuples. Values of failing keys are
      **not** included; only the keys. FT-005's per-service
      deploy-action trigger is not invoked for this service.
    - Successfully-upserted env-vars on the same service remain
      (no rollback).

7. **Sentinel-scan FT-008's own outgoing diagnostic line, if any.**
   Reusing the FT-007 leak-detector discipline verbatim: immediately
   before any FT-008-authored log line, deploy-log entry, or stderr
   diagnostic is written, scan the assembled string for any Aspire
   sentinel. If found, replace the planned output with a fixed
   `E_ENVVAR_SECRET_LEAKED` diagnostic (FT-007's symbol — the leak
   detector is a single shared discipline) and exit non-zero. The
   scan covers (a) FT-008's own diagnostic / log content and (b) any
   Coolify response excerpt FT-008 would inline. It does **not** cover
   request bodies (sent encrypted-or-not by the client; never logged)
   and does **not** cover output authored by other features.

8. **Hand control back to FT-005.** On clean exit from steps 1–7, the
   hook returns; FT-005 proceeds to its per-service deploy-action
   trigger (FT-005 Behaviour §7) for this service. On any fail-fast,
   FT-005 short-circuits its own deploy-trigger step for this service
   and surfaces FT-008's symbol verbatim per FT-005 I-12 / §"Error
   handling".

**Managed-field set (v1, ADR-003 §D5) on each FT-008-written Coolify
env-var.** Same as FT-007:

- `value` — the resolved reference value (endpoint URL or
  connection-string template),
- `secret-flag` — Coolify's per-env-var "is this a secret" boolean,
  set per the A3 rule.

`scope` is fixed at service-scope by construction. All other Coolify
env-var fields (`is_build_time`, `is_preview`, `description`, etc.)
are **unmanaged** in v1: no GET-comparison, no PATCH, no drift
warning.

**Concurrency.** Sequential per-env-var iteration in v1, matching
FT-005 / FT-007. Per-key upserts are independent and idempotent;
concurrency permitted by the model. Across services, FT-008 inherits
FT-005's outer-loop ordering — never invoked in parallel for two
services.

**Cancellation.** If the `CancellationToken` is cancelled between
steps 1–7 or between two key upserts in step 5, the hook exits with
FT-001's cancellation diagnostic (not an `E_…` symbol) and FT-005
honours the cancellation by not advancing to the deploy-trigger step.

**Catch-all (`E_REFERENCE_PHASE_UNEXPECTED`).** Any exception escaping
the hook body that is not classifiable as one of the two preceding
symbols (and is not a cancellation, and is not a sentinel leak) is
wrapped and surfaced as `E_REFERENCE_PHASE_UNEXPECTED` with the inner
exception's `Message` appended, **after** that `Message` has been
sentinel-scanned (a sentinel in an inner exception message escalates
to `E_ENVVAR_SECRET_LEAKED`, not to `E_REFERENCE_PHASE_UNEXPECTED`).
No stack trace, no value content. Mirrors FT-005 / FT-007 catch-all
discipline.

### Invariants

- **I-1: hook fires exactly once per successfully-upserted service,
  AFTER FT-007 and BEFORE the per-service deploy trigger.** Inherits
  FT-005 I-12 and refines it: within the hook block, the order is
  FT-007 then FT-008 (A5). FT-008 is never invoked on a failed
  service-upsert, never invoked after the deploy-action trigger,
  never invoked twice for the same service in one deploy, and never
  invoked before FT-007 has returned for the same service.
- **I-2: every env-var write is a name-keyed upsert at service scope,
  through the same `ICoolifyClient` env-var endpoint group FT-007
  uses.** GET-by-name precedes POST. PATCH targets a known key. No
  project-scope or environment-scope env-var endpoint is called by
  FT-008 in v1. Asserted by request-trace inspection.
- **I-3: single-writer-per-key with FT-007.** FT-008 writes exactly
  the keys it owns (`services__<target>__<scheme>__<i>` for endpoint
  refs; `ConnectionStrings__<name>` for connection-string refs that
  FT-007 reserved with `envvar-skipped: …reason=awaiting-FT-008`).
  FT-008 does not write any parameter env-var key, and FT-007 does
  not write any reference env-var key. Asserted by union/intersection
  inspection of the two features' emitted key sets across a
  representative AppHost. (A2)
- **I-4: env-var key follows Aspire's consumer-side naming verbatim.**
  `services__<target>__<scheme>__<i>` for endpoint refs (double
  underscores, lower-case, no transform), `ConnectionStrings__<name>`
  for connection-string refs (PascalCase prefix per Aspire
  convention, no transform). A consuming container migrated from any
  other Aspire deployment sees the same key names it would there.
  (A1, user instruction.)
- **I-5: every value resolves inside the destination's private
  network.** The host token in each FT-008-written value is the
  target service's Coolify-internal hostname — the name Coolify's
  destination private network resolves to the target container. The
  exact format of that hostname is implementation-defined against
  the live Coolify v4 API (Coolify slug, service-name, or other);
  the spec contract is the **resolution property**, not the format
  string. (A4)
- **I-6: secret-flag policy is rule-based, not opt-in.** An
  FT-008-written env-var has its Coolify secret-flag set when (i)
  any input secret Aspire parameter contributed to the resolved
  value OR (ii) the resolved template contains an Aspire redaction
  sentinel. Plain endpoint URL references with no secret
  contribution land non-secret. (A3) Asserted by Coolify-side
  inspection across a scenario AppHost with one Postgres
  (secret-flag set on `ConnectionStrings__db`) and one plain HTTP
  service (secret-flag clear on `services__api__http__0`).
- **I-7: sentinel-leak detection is shared with FT-007.** A
  sentinel detected in FT-008's own output or in a Coolify response
  excerpt FT-008 would inline escalates to
  `E_ENVVAR_SECRET_LEAKED` — the same symbol FT-007 uses. The leak
  detector is a single discipline, not bifurcated by feature.
- **I-8: aggregation discipline on per-key failures.** The
  per-env-var loop attempts every key before surfacing failure; the
  diagnostic carries every failing key. Matches FT-005 I-10,
  FT-007 I-8.
- **I-9: a failing service's reference aggregation prevents that
  service's deploy-trigger, but does not roll back its
  successfully-upserted env-vars (FT-007's or FT-008's earlier
  keys) or any prior service's state.** Matches FT-005 I-9,
  FT-007 I-9, ADR-003 §D6.
- **I-10: managed-set discipline is honoured on PATCH.** Every
  PATCH body sent by FT-008 contains only the v1 managed set
  (`value`, `secret-flag`). Unmanaged fields are absent from the
  payload entirely (ADR-002 conservative serialisation).
- **I-11: no persistent on-disk publisher state.** Asserted by
  filesystem-diff before/after a successful deploy that exercises
  FT-008.
- **I-12: idempotency on unchanged AppHost.** Running the same
  `aspire deploy --environment <env>` twice in a row produces zero
  net change in FT-008-written env-vars on the second run: every
  key takes the `unchanged` branch. Composes with FT-005 I-6 and
  FT-007 I-11.
- **I-13: orphan reference env-vars are left in place.** A
  `services__…` or `ConnectionStrings__…` key that Coolify holds at
  service scope but that no current AppHost reference produces is
  **not** deleted, not PATCHed, not GETted by FT-008. Matches
  FT-007 I-6 and ADR-003 §D6.
- **I-14: the three `E_…` symbols are stable observable contract.**
  Their spellings appear verbatim as the first whitespace-delimited
  token on stderr. Changing any symbol is a breaking change.
- **I-15: target identity is resolved against FT-005's in-phase map,
  not against Coolify.** FT-008 issues zero GET calls to Coolify
  for the purpose of discovering target `serviceId` or hostname; it
  consumes both from the in-phase map. (Reduces traffic and avoids
  a race between two upsert loops.) Asserted by request-trace
  inspection.

### Error handling

The three `E_…` diagnostics enumerated above are the only error paths
this feature introduces (plus the shared `E_ENVVAR_SECRET_LEAKED`
escalation via the FT-007 leak-detector discipline). Beyond them:

- **Cancellation** → FT-001 cancellation diagnostic.
- **Aspire reference-resolution failure** (e.g. an endpoint metadata
  read throws) → `E_REFERENCE_PHASE_UNEXPECTED` with the
  sentinel-scanned `Message`.
- **Coolify response carries a sentinel substring** → escalate to
  `E_ENVVAR_SECRET_LEAKED` (FT-007's symbol; shared discipline).
  Excerpt is dropped from the diagnostic.
- **Failing key's response excerpt contains a sentinel** → excerpt is
  dropped; key is still named; diagnostic is
  `E_ENVVAR_SECRET_LEAKED`, not `E_REFERENCE_ENVVAR_UPSERT_FAILED`.
- **Bug or unclassifiable exception** → `E_REFERENCE_PHASE_UNEXPECTED`
  with sentinel-scanned inner `Message`.

**Sentinel-leak precedence (load-bearing):**

```
E_ENVVAR_SECRET_LEAKED                      (shared with FT-007)
  > E_REFERENCE_TARGET_NOT_DEPLOYED
  > E_REFERENCE_ENVVAR_UPSERT_FAILED
  > E_REFERENCE_PHASE_UNEXPECTED
```

A sentinel detection always wins. Within the non-leak symbols,
target-not-deployed dominates upsert-failed (if the target doesn't
exist we should not be POSTing reference env-vars at all), which
dominates the catch-all.

### Boundaries

- **In scope for FT-008:**
  - hooking into FT-005's per-service post-upsert / pre-trigger
    point, **after** FT-007's return (A5)
  - enumerating endpoint references and connection-string references
    on the current Aspire resource
  - resolving target `(serviceId, Coolify-internal-hostname)` against
    FT-005's in-phase upserted-services map
  - composing `services__<target>__<scheme>__<i>` env-vars for
    endpoint references and `ConnectionStrings__<name>` env-vars for
    connection-string references, using Aspire's verbatim
    consumer-side naming
  - upserting those env-vars at Coolify service-scope via the same
    `ICoolifyClient` env-var endpoint group FT-007 uses (same upsert
    mechanism reused verbatim — user instruction)
  - applying the A3 secret-flag rule (secret if any secret parameter
    contributed OR template carries a sentinel; non-secret
    otherwise)
  - the three `E_…` diagnostics with literal symbols and
    sentinel-scanned content
  - aggregation of per-key failures into one
    `E_REFERENCE_ENVVAR_UPSERT_FAILED` per service, and aggregation
    of missing targets into one `E_REFERENCE_TARGET_NOT_DEPLOYED`
    per service
  - reuse of FT-007's leak-detector discipline verbatim (same
    `E_ENVVAR_SECRET_LEAKED` symbol on detection)
  - structured deploy-log emission attributed to the `deploy` phase
- **Out of scope for FT-008** (handled elsewhere or deferred):
  - `WithReference(parameter)` for parameter resources →
    **FT-007** (A1)
  - parameter-derived env-vars of any kind → **FT-007** (A2,
    single-writer)
  - the Coolify env-var endpoint path, request/response DTOs, the
    secret-flag field name, and the GET-by-name helper →
    **`coolify-api` domain, ADR-002**
  - the exact format of the Coolify-internal hostname token
    (slug-vs-service-name) → **implementation against live Coolify
    v4 API** (A4); spec pins the resolution property only
  - the Aspire-side sentinel format and the redaction pipeline that
    places it on resolved secret values → **Aspire / ADR-004**
  - cross-destination references — a reference to a service in a
    different Coolify destination is post-v1; v1 assumes all
    services in the targeted environment share one destination
    (ADR-001 D4)
  - project-scope or environment-scope env-vars → **post-v1**
  - orphan reference env-var tear-down / GC → **post-v1**
    (`aspire coolify gc`); FT-008 leaves orphans (I-13)
  - retry / backoff on env-var upsert failures → single attempt per
    call (matches FT-002 / FT-005 / FT-007)
  - per-env-var concurrency → sequential v1; permitted by the model
  - TypeScript AppHost parity → FT-008 introduces no public-API
    extension method; no parity work required
  - drift on env-var keys FT-008 holds but the AppHost no longer
    references → I-13 (no GET, no PATCH, no warning on orphans)
  - polling Coolify deploy-action completion → **FT-006**

## Out of scope

- **`WithReference(parameter)` projection.** FT-007 owns parameter
  env-vars; FT-008 covers only endpoint refs and connection-string
  refs (A1).
- **Project-scope and environment-scope env-vars.** v1 keeps
  everything at service scope, matching FT-007.
- **Orphan deletion.** No `aspire coolify gc` in v1; orphan
  `services__…` and `ConnectionStrings__…` keys remain in Coolify
  until removed in the UI.
- **Cross-destination references.** v1 assumes the targeted
  environment lives in a single Coolify destination (ADR-001 D4);
  a future ADR will revisit cross-destination wiring if needed.
- **The exact slug/service-name format for the Coolify-internal
  hostname.** Implementation-defined against the live Coolify v4
  API (A4). The spec pins resolution behaviour, not the literal
  token.
- **Sentinel scan of request bodies, FT-005-authored deploy-log
  lines, or FT-007-authored output.** Each feature owns its own
  redaction surface; FT-008 only scans what FT-008 emits.
- **Retry / backoff on env-var upsert failures.** Single attempt per
  call; failure → aggregated fail-fast per service.
- **Per-env-var concurrency.** Sequential v1.
- **Plan / `--dry-run` for reference env-vars.** ADR-003 already
  defers.
- **Strict-mode refuse-to-deploy on reference drift.** v1
  warn-and-overwrite via PATCH + `drift-overwritten` log line,
  matching ADR-003 §D5 and FT-007.
- **Persistent on-disk state.** Forbidden by ADR-003 §4 and I-11.
- **A standalone `aspire coolify references` / `aspire coolify
  whoami-points-where` command.** FT-008 is a hook inside `aspire
  deploy`'s deploy phase only; v1 exposes no standalone CLI verb.
