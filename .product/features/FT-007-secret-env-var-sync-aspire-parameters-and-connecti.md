---
id: FT-007
title: Secret / env-var sync — Aspire parameters and connection strings into Coolify service-scope env vars
phase: 1
status: complete
depends-on:
- FT-002
- FT-005
adrs:
- ADR-001
- ADR-002
- ADR-003
- ADR-004
tests:
- TC-011
domains:
- aspire-publisher
- coolify-api
- secrets-env
domains-acknowledged: {}
---

## Description

FT-007 fills in the **env-var sync hook point** that FT-005's per-resource
service-upsert loop calls between a successful service upsert and the
per-service deploy-action trigger (FT-005 I-12). It is the feature that
turns the Aspire-side notion of "this resource references these
parameters and these connection strings" into Coolify-side environment
variables at the **service scope**.

By the time FT-007 is invoked for a given service, FT-002 has already
resolved every Aspire parameter the AppHost declares (including the
`secret: true` ones), FT-005 has upserted the Coolify application /
service / database for the current Aspire resource and holds the
Coolify-side `projectId`, `environmentId`, `serviceId` in the in-phase
scope, and FT-008 has **not yet** wired `WithReference()`-derived
service-to-service edges. FT-007 owns only the static-parameter
projection: every `AddParameter(...)` handle (secret or not) the resource
references, and the *names* of every connection string the resource
consumes. The actual reference values that connection-string names point
at are FT-008's concern — FT-007 records the names so FT-008 has a stable
key to write against, but emits no env-var for a connection-string name
whose value FT-008 has not yet supplied.

The shape is dictated by ADR-001 (idempotency-by-name), ADR-002 (the
typed `ICoolifyClient` exposes an env-var endpoint group with
GET-by-name / POST / PATCH the same way every other group does),
ADR-003 (imperative per-step upsert, no plan/apply, no persistent
state, drift on managed fields is warn-and-overwrite), and ADR-004
(the redaction discipline for secret-marked parameters is reused
verbatim — the sentinel pattern that proves the value never escapes is
exactly the one ADR-004 §"Test coverage" pins for the configure-phase
token).

The exit criteria for this feature are: (a) for every successfully-
upserted Coolify service, FT-007 enumerates the static env-vars
contributed by the Aspire resource's referenced parameters and
upserts them at **service scope** in Coolify (not project scope, not
environment scope — v1 keeps it tight); (b) connection-string-named
env-vars are skipped-with-log-line when no FT-008-supplied value
exists yet, and emitted when one does (forward-compatible with FT-008);
(c) every secret-parameter value goes onto the wire encrypted by
Coolify's own secret-field semantics and never appears in any FT-007-
emitted log, diagnostic, or deploy-log line; (d) the sentinel-leak
detector scans both FT-007's own emitted output and any Coolify
response excerpt FT-007 would print before that output reaches
stderr, failing fast with `E_ENVVAR_SECRET_LEAKED` if a sentinel
slips through; (e) the three `E_…` symbols are stable observable
contract; (f) zero persistent on-disk state.

## Functional Specification

### Inputs

- **Phase-scope handles from FT-005's per-resource loop:** the
  Aspire `IResource` currently being processed, the resolved
  Coolify-side `projectId`, `environmentId`, `serviceId` of the
  just-upserted service, and the `ICoolifyClient` instance whose
  bearer header was validated by FT-002. FT-007 is invoked exactly
  once per successfully-upserted service, between the service-upsert
  call and the per-service deploy-action trigger (FT-005 I-12).
- **The full Aspire parameter set declared by the AppHost,** reachable
  through the same standard parameter-resolution API FT-002 uses. For
  each parameter referenced by the current Aspire resource, FT-007
  consumes:
    - the parameter's `Name` (the Aspire-side identifier, e.g.
      `coolify-homelab-token`),
    - the parameter's `secret: true` / `secret: false` flag,
    - the parameter's resolved string value (resolved through Aspire,
      same path FT-002 took for the Coolify token).
  FT-007 does **not** re-read `Environment` directly, does **not**
  open `secrets.json`, does **not** invoke `dotnet user-secrets`
  itself. ADR-004 §1 discipline applies.
- **The set of connection-string names the current Aspire resource
  consumes,** as exposed by the Aspire resource graph. FT-007 only
  reads the *names* — the value side (which other resource the name
  points at, what `WithReference()` edge produces it) is FT-008's
  concern.
- **The current `aspire deploy` `CancellationToken`,** propagated
  from FT-005's phase context.
- **The set of env-var-name sentinels** Aspire's secret-parameter
  redaction pipeline (ADR-004 §6) places on every resolved
  `secret: true` value. FT-007 does not generate the sentinel; it
  consumes the same sentinel Aspire already plants in its logs so
  that a leaked secret value is detectable by literal substring
  match. (The sentinel format and placement are owned by Aspire's
  redaction layer and ADR-004's redaction test; FT-007 reuses them
  verbatim.)

### Outputs

- **On success (per service):** Coolify holds, at service scope for
  the just-upserted service, exactly one env-var per Aspire parameter
  the resource references whose resolved value is non-null and
  non-empty. Each env-var:
    - is keyed by the **name the consuming container would see** —
      i.e. the env-var name Aspire surfaces to a consumer of the
      parameter resource, used verbatim (no upper-snake conversion,
      no prefix, no transform);
    - carries the resolved value, marked as **secret on the Coolify
      side** for every Aspire parameter with `secret: true`, and as
      **non-secret** for the rest;
    - is upserted by name (GET-by-name → PATCH if present, POST if
      absent — ADR-001 / ADR-003 §D3).
  FT-007 emits one Aspire-structured-log entry per env-var attributed
  to the `deploy` phase, naming the resource, the env-var key, and
  the outcome (`created` / `updated` / `unchanged`). For
  `secret: true` parameters, the log line records the key only —
  the value is **never** included.
- **For connection-string names whose value FT-008 has not yet
  supplied:** FT-007 emits one Aspire-structured-log line per skipped
  name, attributed to the `deploy` phase, of the form
  `envvar-skipped: resource=<name> key=<connstring-name>
  reason=awaiting-FT-008`. No Coolify call is made for that key. The
  service-upsert continues normally; the deploy-trigger fires
  regardless. (When FT-008 lands, it will write those env-vars
  itself; FT-007 is forward-compatible by leaving the key untouched.)
- **On any fail-fast path:** the publisher prints a single diagnostic
  to stderr whose first whitespace-delimited token is one of the
  three literal `E_…` symbols below, exits non-zero, does not
  invoke the per-service deploy-action trigger for the failing
  service, and surfaces FT-007's symbol upward to FT-005 (which exits
  the deploy phase with that symbol — not `E_DEPLOY_PHASE_UNEXPECTED`,
  per FT-005 §"Error handling"). Successfully-upserted env-vars on
  the same service and successfully-completed prior services are
  **not** torn down (matches ADR-003 §D6 verify-gated, non-
  transactional rollback, and FT-005 I-9). The three symbols are
  part of the **observable contract**:

  | Symbol                          | Stderr-visible literal           | Trigger                                                                                  |
  |---------------------------------|----------------------------------|------------------------------------------------------------------------------------------|
  | `E_ENVVAR_UPSERT_FAILED`        | `E_ENVVAR_UPSERT_FAILED`         | One or more env-var GET / POST / PATCH calls returned non-2xx (aggregated per service)   |
  | `E_ENVVAR_SECRET_LEAKED`        | `E_ENVVAR_SECRET_LEAKED`         | Sentinel detected in FT-007's own diagnostic output or in a Coolify response excerpt FT-007 would print, before that output reaches stderr |
  | `E_ENVVAR_PHASE_UNEXPECTED`     | `E_ENVVAR_PHASE_UNEXPECTED`      | Catch-all for unclassifiable failures inside the env-var sync hook body                  |

  All diagnostics carry the same structured field-block shape as
  FT-002 / FT-005:

  ```
  <E_SYMBOL>: <one-line human description>
    project:     <apphost-name>                       (always)
    environment: <aspire-env-name>                    (always)
    resource:    <aspire-resource-name>               (always)
    service:     <coolify-service-name>               (always)
    keys:        <env-var-key>[, …]                   (UPSERT_FAILED — failing keys only; never values)
    coolify:     <HTTP status and response excerpt>   (UPSERT_FAILED — already sentinel-scanned)
    see:         ADR-004 §"Test coverage", FT-007     (SECRET_LEAKED)
    remediation:
      inspect the Coolify-side service's environment-variables panel for the
      failing key(s)                                                  (UPSERT_FAILED)
      file a bug — a redaction discipline regression was caught       (SECRET_LEAKED)
  ```

  The first whitespace-delimited token on the first line is the
  literal `E_…` symbol; exit-criteria tests grep for it.

### State

- **No persistent state on disk authored by FT-007.** No
  `coolify-envvars.cache`, no per-service manifest, no diff file.
  Inherits ADR-003 §4 and FT-005 I-7.
- **No in-memory cross-deploy state.** Every invocation of FT-007's
  hook re-resolves the parameter set and re-issues the GET-by-name
  upsert.
- **In-memory state is bounded to one service's hook invocation.**
  The resolved value for any `secret: true` parameter lives only in
  the local scope of the upsert call and inside the request body the
  `ICoolifyClient` sends. It is **not** stored as a field on any
  publisher-level object, **not** captured in a closure that outlives
  the per-service hook, and **not** returned to FT-005.
- **Aggregation buckets are per-service, not per-deploy.** The
  `ENVVAR_UPSERT_FAILED` bucket is reset at the start of each
  service's hook invocation. If service A's env-var sync fails and
  service B's succeeds, the deploy phase still surfaces the failure
  (FT-005 short-circuits before its deploy-trigger step) but service
  B's env-vars remain present in Coolify.

### Behaviour

The env-var sync hook body executes the following steps in this exact
order, **per service** invoked by FT-005's per-resource loop.

1. **Enumerate the parameter references of the current Aspire
   resource.** Walk the resource's parameter-references via the
   standard Aspire API (the same surface a consumer would use to
   discover \"what parameters does this resource want as env-vars\").
   For each referenced parameter, capture:
    - the env-var name the consuming container would see (Aspire-
      surfaced, verbatim);
    - the parameter's `secret: true` / `secret: false` flag;
    - the resolved string value.

2. **Enumerate the connection-string names the current Aspire
   resource consumes.** For each name, check whether a value is
   available in the current deploy invocation (FT-008 is the feature
   that would supply one; in FT-007's v1 world that lookup returns
   "no value" for every connection-string name). For each name with
   no value, **skip-with-log-line** as specified in §Outputs and
   continue. For each name with a value (forward-compatible with
   FT-008), treat it as an additional env-var with the
   connection-string name as the key, value as supplied, and
   secret-flag determined by whether the supplying resource is
   itself secret (FT-008's decision — FT-007 simply propagates the
   flag).

3. **Pre-flight sentinel scan on resolved values.** For every
   `secret: false` parameter whose resolved value happens to contain
   an Aspire redaction sentinel (this indicates a redaction-pipeline
   bug — non-secret values should never carry a sentinel), fail-fast
   `E_ENVVAR_SECRET_LEAKED` before issuing any Coolify call. (This
   guards against the upstream-bug case where Aspire mistakenly
   stamps a sentinel onto a non-secret value, which would otherwise
   reach the wire.)

4. **Per env-var upsert loop.** For every (key, value, secret-flag)
   tuple gathered in steps 1–2 whose value is non-null and non-empty
   after trimming, sequentially:
    1. **GET-by-name.** Call
       `client.ServiceEnvVars.GetByNameAsync(serviceId, key,
       cancellationToken)`. The `coolify-api` domain owns the actual
       endpoint group naming and path; FT-007 consumes it by intent.
       Three outcomes:
        - **Found:** if the currently-deployed value or secret-flag
          disagrees with the AppHost's intended value/flag, PATCH
          with the AppHost's value, value-encryption-flag, and a
          secret-flag matching the parameter's `secret: true/false`.
          On 2xx, emit `updated` (with one
          `drift-overwritten: resource=<n> field=envvar:<key>
          previous=<old-or-REDACTED> new=<REDACTED-if-secret-else-new>`
          warning per drifted attribute), or `unchanged` if neither
          differed. Continue.
        - **Not found:** POST to create with `(key, value,
          secret-flag)`. On 2xx, log `created` and continue.
        - **Non-2xx / transport failure for this key:** accumulate
          `(key, response-excerpt)` into the per-service
          `ENVVAR_UPSERT_FAILED` bucket and continue to the next
          key. The loop does **not** short-circuit on first failure
          — it attempts every key so the diagnostic can name the
          full failing set (matches FT-005 I-10 and confirmed
          A1).
    2. **Sentinel scan the response excerpt before it would be
       logged or attached to a diagnostic.** If a sentinel is
       detected, immediately escalate to step 6
       (`E_ENVVAR_SECRET_LEAKED`) — the leaked value must not be
       printed.

5. **Aggregate per-service upsert failures.** After the per-env-var
   loop has attempted every key for this service, if the
   `ENVVAR_UPSERT_FAILED` bucket is non-empty, fail-fast
   `E_ENVVAR_UPSERT_FAILED` with all failing `(key,
   response-excerpt)` tuples. The values of the failing keys are
   **not** included; only the keys. FT-005's per-service
   deploy-action trigger is not invoked for this service.
   Successfully-upserted env-vars on the same service remain.

6. **Sentinel scan FT-007's own outgoing diagnostic line, if any.**
   Immediately before any FT-007-authored log line, deploy-log
   entry, or stderr diagnostic is written, scan the assembled string
   for any Aspire sentinel. If found, replace the entire planned
   output with a fixed `E_ENVVAR_SECRET_LEAKED` diagnostic and
   exit non-zero — do **not** write the original line. The scan
   covers (a) FT-007's own diagnostic / log content and (b) any
   Coolify response excerpt FT-007 would inline. It does **not**
   cover request bodies (those are sent encrypted-or-not by the
   client and never logged by FT-007) and does **not** cover
   deploy-log lines authored by other features (those own their
   own redaction).

7. **Hand control back to FT-005.** On clean exit from steps 1–6,
   the hook returns; FT-005 proceeds to its per-service deploy-
   action trigger (FT-005 Behaviour §7) for this service. On any
   fail-fast, FT-005 short-circuits its own deploy-trigger step for
   this service and surfaces FT-007's symbol verbatim per FT-005
   I-12 / §"Error handling".

**Managed-field set (v1, ADR-003 §D5) on each Coolify env-var.**
FT-007 considers exactly the following fields **managed** on each
Coolify env-var record it upserts:

- `value` — the resolved string value from Aspire,
- `secret-flag` — Coolify's per-env-var \"is this a secret\" boolean,
  set from the Aspire parameter's `secret: true` / `secret: false`.

`scope` is fixed at service-scope by construction (we only ever call
the service-scope endpoint) and is therefore implicit. All other
Coolify env-var fields Coolify exposes (e.g. `is_build_time`,
`is_preview`, `description`) are **unmanaged** in v1: no GET-
comparison, no PATCH, no drift warning.

**Concurrency.** Per the pattern of FT-005, FT-007 v1 ships
sequential per-env-var iteration. Per-key upserts are independent
and idempotent; concurrency is permitted by the model and may be
added by a future feature without re-deciding. Across services,
FT-007 inherits FT-005's outer-loop ordering (sequential in graph
order) — FT-007 is not invoked in parallel for two services.

**Cancellation.** If the `CancellationToken` is cancelled between
steps 1–6 or between two key upserts in step 4, the hook exits with
FT-001's cancellation diagnostic (not an `E_…` symbol) and FT-005
honours the cancellation by not advancing to the deploy-trigger step.

**Catch-all (`E_ENVVAR_PHASE_UNEXPECTED`).** Any exception escaping
the hook body that is not classifiable as one of the two preceding
symbols (and is not a cancellation) is wrapped and surfaced as
`E_ENVVAR_PHASE_UNEXPECTED` with the inner exception's `Message`
appended, **after** that `Message` has been sentinel-scanned (a
sentinel in an inner exception message escalates to
`E_ENVVAR_SECRET_LEAKED`, not to `E_ENVVAR_PHASE_UNEXPECTED`). No
stack trace, no value content. Mirrors FT-002 / FT-005 catch-all
discipline.

### Invariants

- **I-1: hook fires exactly once per successfully-upserted service,
  between service upsert and deploy trigger.** Inherits FT-005 I-12.
  FT-007 is never invoked on a failed service-upsert, never invoked
  after the deploy-action trigger, never invoked twice for the same
  service in one deploy.
- **I-2: every env-var write is a name-keyed upsert at service
  scope.** GET-by-name precedes POST. PATCH targets a known key. No
  project-scope or environment-scope env-var endpoint is called by
  FT-007 under any condition in v1. (Asserted by request-trace
  inspection.)
- **I-3: no secret value appears in any FT-007-emitted log,
  diagnostic, or deploy-log line.** Every secret parameter value
  travels from Aspire's resolver into the `ICoolifyClient` request
  body and nowhere else FT-007 controls. Asserted by sentinel-grep
  on captured stdout / stderr / Aspire-structured-log across all
  TC scenarios (matches ADR-004 §"Test coverage" redaction
  assertion).
- **I-4: env-var key is the consuming-container-visible name,
  verbatim.** No case-folding, no prefix, no transform. (Confirmed
  A4.)
- **I-5: connection-string names with no FT-008-supplied value
  produce a deploy-log skip line and no Coolify call.** (Confirmed
  A3.) Asserted by request-trace inspection: zero env-var endpoint
  calls bear a key matching an unwired connection-string name.
- **I-6: orphan env-vars are left in place.** An env-var key that
  Coolify holds at service scope but that no current AppHost
  parameter reference produces is **not** deleted, not PATCHed,
  not GETted by FT-007. (Confirmed A5; matches FT-005's
  no-tear-down stance and ADR-003 §D6.)
- **I-7: managed-set discipline is honoured on PATCH.** Every PATCH
  body sent by FT-007 contains only the v1 managed set (`value`,
  `secret-flag`). Unmanaged fields are absent from the PATCH
  payload entirely (ADR-002 conservative serialisation).
- **I-8: aggregation discipline on per-key failures within a
  service.** The per-env-var loop attempts every key before
  surfacing failure; the diagnostic carries every failing key.
  (Confirmed A1; matches FT-005 I-10.)
- **I-9: a failing service's env-var aggregation prevents that
  service's deploy-trigger, but does not roll back its
  successfully-upserted env-vars or any prior service's state.**
  (Matches FT-005 I-9 and ADR-003 §D6.)
- **I-10: no persistent on-disk publisher state.** Asserted by
  filesystem-diff before/after a successful deploy that exercises
  FT-007.
- **I-11: idempotency on unchanged AppHost.** Running the same
  `aspire deploy --environment <env>` twice in a row produces zero
  net change in Coolify env-vars on the second run: every key takes
  the `unchanged` branch. (Composes with FT-005 I-6.)
- **I-12: the three `E_…` symbols are stable observable contract.**
  Their spellings appear verbatim as the first whitespace-delimited
  token on stderr. Changing any symbol is a breaking change.
- **I-13: sentinel-leak detection short-circuits before stderr
  write.** No FT-007-authored line containing an Aspire sentinel
  ever reaches stderr or the deploy log. (Confirmed A2.) Asserted
  by a regression test that injects a deliberately-leaked sentinel
  through a Coolify mock response excerpt and verifies the original
  line is suppressed and `E_ENVVAR_SECRET_LEAKED` is emitted
  instead.
- **I-14: sentinel scan scope is bounded.** The scan covers (a)
  FT-007's own diagnostic / log content and (b) Coolify response
  excerpts FT-007 would inline. It does **not** scan request
  bodies (encrypted-or-not by the client; never logged), and does
  **not** scan deploy-log lines authored by FT-005, FT-008, or
  other features (those own their own redaction). (Confirmed A2.)
- **I-15: secret-flag round-trips faithfully.** An Aspire parameter
  with `secret: true` lands as a Coolify env-var with the
  per-env-var secret flag set; with `secret: false`, the flag is
  clear. Asserted by Coolify-side inspection after a happy-path
  deploy.

### Error handling

The three `E_…` diagnostics enumerated above are the only error
paths this feature introduces. Beyond them:

- **Cancellation** → FT-001 cancellation diagnostic.
- **Aspire parameter-resolution failure** for a secret parameter →
  `E_ENVVAR_PHASE_UNEXPECTED` with the underlying error's `Message`
  (which Aspire's redaction has already scrubbed) sentinel-scanned
  before inclusion. For a non-secret parameter the same wrapping
  applies; the sentinel scan is still performed defensively.
- **Coolify response carries a sentinel substring** (e.g. because
  the operator pasted a real value into a non-secret env-var via
  the UI and that value happens to match an Aspire sentinel
  format) → `E_ENVVAR_SECRET_LEAKED`. The diagnostic does not
  echo the response excerpt; it only names the resource and
  service.
- **A failing key's response excerpt contains a sentinel** →
  the excerpt is dropped from the diagnostic; the key is still
  named; the diagnostic is `E_ENVVAR_SECRET_LEAKED`, not
  `E_ENVVAR_UPSERT_FAILED`. (SECRET_LEAKED dominates
  UPSERT_FAILED in precedence.)
- **Bug or unclassifiable exception** → `E_ENVVAR_PHASE_UNEXPECTED`
  with sentinel-scanned inner `Message`.

**Precedence (load-bearing):**

```
E_ENVVAR_SECRET_LEAKED
  > E_ENVVAR_UPSERT_FAILED
  > E_ENVVAR_PHASE_UNEXPECTED
```

A sentinel detection always wins: better to surface a redaction
regression than to print a value or to mask it as an unrelated
catch-all.

### Boundaries

- **In scope for FT-007:**
  - hooking into FT-005's per-service post-upsert / pre-trigger
    point (FT-005 I-12)
  - enumerating Aspire parameter references on the current resource
  - resolving each parameter's value through Aspire's standard API
    (the same surface FT-002 uses)
  - enumerating connection-string *names* the current resource
    consumes and skipping-with-log-line those without a value
  - upserting Coolify service-scope env-vars by name with the v1
    managed-field set (`value`, `secret-flag`)
  - the three `E_…` diagnostics with literal symbols, structured
    field blocks, and sentinel-scanned content
  - aggregation of per-key failures into one `E_ENVVAR_UPSERT_FAILED`
    per service
  - sentinel-scan of FT-007's own outgoing lines and Coolify
    response excerpts before they reach stderr
  - structured deploy-log emission attributed to the `deploy` phase
    (the `coolify-api` domain owns the actual endpoint; FT-007
    consumes it as a black box)
- **Out of scope for FT-007** (handled elsewhere or deferred):
  - emitting env-var values for connection-string names →
    **FT-008** (FT-007 reserves the keys via skip-with-log; FT-008
    fills them in)
  - `WithReference()`-derived service-to-service network wiring →
    **FT-008**
  - project-scope or environment-scope env-vars → **post-v1**
    (v1 keeps it tight at service scope, per the user instruction)
  - orphan env-var tear-down / GC → **post-v1** (`aspire coolify
    gc`); FT-007 leaves orphans (Confirmed A5)
  - the Coolify env-var endpoint path, request/response DTOs, the
    secret-flag field name, and the GET-by-name helper →
    **`coolify-api` domain, ADR-002**
  - the Aspire-side sentinel format and the redaction pipeline that
    places it on resolved secret values → **Aspire / ADR-004**;
    FT-007 only consumes the sentinel for detection
  - distinguishing 401 (token expired) from 403 (token lacks env-var
    permission) on env-var endpoints → both surface as
    `E_ENVVAR_UPSERT_FAILED` (matches ADR-004's "one opaque error
    path for auth")
  - retry / backoff on env-var upsert failures → single attempt
    per call (matches FT-002 / FT-005)
  - per-env-var concurrency → sequential v1; permitted by the model
  - TypeScript AppHost parity for any FT-007-introduced extension
    method (FT-007 introduces none — it is a hook implementation,
    not a public-API extension) → no parity work required
  - re-reading or validating the `(url, token)` configure-phase
    parameters → those are FT-002's; FT-007 consumes the
    `ICoolifyClient` only
  - drift detection or warn-and-overwrite on env-vars Coolify
    holds but the AppHost no longer references → I-6 (no GET,
    no PATCH, no warning on orphans)

## Out of scope

- **Project- and environment-scope env-vars.** v1 keeps everything
  at service scope. A future ADR may add cross-service env-var
  sharing via project/environment scope; FT-007 deliberately does
  not pre-build that path.
- **`WithReference()`-derived env-vars.** FT-008 owns the value
  side of connection-string env-vars; FT-007 names them and
  skip-with-logs when no value exists.
- **Orphan deletion.** Confirmed A5. No `aspire coolify gc` in v1.
- **Sentinel scan of request bodies, FT-005-authored deploy-log
  lines, or FT-008-authored output.** I-14. Each feature owns its
  own redaction surface; FT-007 only scans what FT-007 emits.
- **Retry / backoff on env-var upsert failures.** Single attempt
  per call; failure → aggregated fail-fast per service.
- **Per-env-var concurrency.** Sequential v1.
- **Plan / `--dry-run` for env-vars.** ADR-003 already defers.
- **Strict-mode refuse-to-deploy on env-var drift.** v1
  warn-and-overwrite via PATCH + `drift-overwritten` log line,
  matching ADR-003 §D5.
- **Persistent on-disk state.** Forbidden by ADR-003 §4 and I-10.
- **Distinguishing Aspire-side sentinel formats across Aspire
  versions.** FT-007 consumes whatever sentinel ADR-004's
  redaction discipline currently emits. An Aspire-side change to
  the sentinel format requires a coordinated update to FT-007's
  detector.
- **A standalone `aspire coolify envvars` / `aspire coolify
  whoami` command.** FT-007 is a hook inside `aspire deploy`'s
  deploy phase only; v1 exposes no standalone CLI verb.
