---
id: FT-010
title: Managed Aspire dashboard opt-in — WithManagedDashboard() deploy-time upsert
phase: 1
status: complete
depends-on:
- FT-001
- FT-005
adrs:
- ADR-001
- ADR-002
- ADR-003
- ADR-004
- ADR-006
tests:
- TC-014
domains:
- aspire-publisher
- coolify-api
- managed-dashboard
domains-acknowledged: {}
---

## Description

FT-010 introduces the **optional managed Aspire dashboard** affordance the
brief flags as a "bonus" capability: a single chainable extension method,
`WithManagedDashboard(dashboardToken)`, that opts the AppHost into having a
`coolify-aspiredashboard` container upserted as a sibling service inside the
same Coolify project that FT-005 manages. The dashboard image is a separate
artefact (`ghcr.io/<org>/coolify-aspiredashboard:<version>`) built and
released out-of-band — this feature does **not** build, push, or version
that image; it only **references** a publisher-pinned image tag constant
and ensures the container exists in Coolify alongside the workload
services.

The shape mirrors Azure Container Apps' managed-dashboard pattern: the
upstream Aspire dashboard, bundled with a Coolify-aware resource provider
plugin, is given the three env-vars it needs to enumerate the workload
services from Coolify's API (`COOLIFY_API_URL`, `COOLIFY_API_TOKEN`,
`COOLIFY_PROJECT_UUID`) and Coolify auto-assigns it a domain. The
dashboard then talks to Coolify as a **separate audience** — a distinct
Aspire secret parameter, not the deploy token — per ADR-004's
audience-separation discipline.

The feature is **observability, not workload contract**. Every failure
inside the dashboard upsert path is a *warning, not fatal*: a missing
dashboard-token value, a non-2xx response from Coolify on the dashboard
service upsert, a transport failure, or a catch-all unexpected exception
all surface as `W_…` diagnostics on stderr, are logged at warning level
attributed to the `deploy` phase, and **do not** affect the `aspire deploy`
exit code. Exit code stays zero iff the workload deploy (FT-005) is
otherwise successful. This is the load-bearing contract of FT-010: the
dashboard never blocks the deploy.

The shape is dictated by ADR-001 (the dashboard lives in the **same**
Coolify project as the workload, in the **targeted** environment — never
in a separate project, never pre-created in sibling environments),
ADR-002 (the dashboard upsert and env-var writes go through the same
`ICoolifyClient` surface as every other write — no second client, no
direct `HttpClient`), ADR-003 (the upsert is name-keyed, idempotent, and
runs inside the existing `deploy` phase, after FT-005's per-service walk
and trigger loop, so a partial workload failure short-circuits before
the dashboard runs), and ADR-004 (the dashboard token is a separate
`IResourceBuilder<ParameterResource>` with `secret: true`, captured at
the call site as a typed handle — typo-safe, redaction-honoured, never
echoed).

`WithManagedDashboard(dashboardToken)` is **chainable on the result of
`WithCoolifyDeploy(...)`**, in the same chain as FT-003's
`WithImageRegistry(...)` and FT-005's `WithCoolifyDestination(...)`:

```csharp
var coolifyUrl       = builder.AddParameter("coolify-homelab-url");
var coolifyToken     = builder.AddParameter("coolify-homelab-token",      secret: true);
var coolifyDest      = builder.AddParameter("coolify-homelab-destination");
var dashboardToken   = builder.AddParameter("coolify-homelab-dashboard-token", secret: true);

builder.WithCoolifyDeploy(coolifyUrl, coolifyToken)
       .WithImageRegistry(prefix, user, pass)
       .WithCoolifyDestination(coolifyDest)
       .WithManagedDashboard(dashboardToken);
```

The method takes exactly one argument — the typed
`IResourceBuilder<ParameterResource>` dashboard-token handle — and no
other knobs. The dashboard image tag is a **publisher-pinned constant**
inside the publisher source (e.g. `ghcr.io/pksorensen/coolify-aspiredashboard:0.1.0`);
overriding the tag, port, FQDN, or any other dashboard configuration is
**out of scope for v1** and deferred to a later feature once the
dashboard image's release cadence is real.

The exit criteria are: (a) `WithManagedDashboard(dashboardToken)`
captures the typed handle on the publisher with FT-001/FT-003/FT-005-
equivalent null-handle / idempotency discipline; (b) when opted-in and
the workload deploy reaches the post-trigger point of the `deploy`
phase, the publisher upserts a single Coolify service named
`coolify-aspiredashboard` (or a publisher-defined fixed name) inside
the same project + targeted environment, bound to the resolved
destination, carrying the pinned image tag and the three required
env-vars; (c) the dashboard upsert is followed by its own Coolify
deploy-action trigger (so the dashboard rolls out alongside the
workload) and the resulting deploy-job handle is added to the list
handed to `verify` (FT-006); (d) any failure inside the dashboard path
is a `W_…` warning on stderr and leaves the workload deploy's exit
code unchanged; (e) the dashboard's URL — Coolify-assigned domain or
developer-pre-configured FQDN as reported by Coolify on the GET
response — is surfaced back to the developer in deploy output once
known; (f) when `WithManagedDashboard(...)` was never called, no
dashboard service is touched in Coolify under any code path.

## Functional Specification

### Inputs

- The `IResourceBuilder<ParameterResource>` dashboard-token handle
  captured on the `CoolifyDeployingPublisher` instance by a prior call
  to `WithManagedDashboard(dashboardToken)`. The handle is required;
  passing `null` throws `ArgumentNullException` at AppHost build time,
  naming the offending argument (matching FT-001 / FT-003 / FT-005
  null-handle discipline). The token parameter **must** be created with
  `secret: true` at the AppHost call site; FT-010 does not enforce the
  secret-flag (Aspire's parameter API does not currently expose it for
  inspection), but the redaction guarantee in §Invariants I-7 depends on
  the caller honouring it.
- The `(url, token, destination)` parameter handles previously captured
  by FT-001 (`WithCoolifyDeploy`) and FT-005 (`WithCoolifyDestination`),
  resolved to their string values during the deploy phase. FT-010 reads:
  - the **Coolify base URL string** to use as the `COOLIFY_API_URL`
    env-var on the dashboard container (taken verbatim from the
    publisher-configured URL parameter — same value the
    `ICoolifyClient` is constructed against),
  - the **resolved destination ID** from FT-005's step 2 — passed in
    the dashboard service's `destination-binding` managed field,
  - the **resolved project UUID** from FT-005's step 3 — used as
    `COOLIFY_PROJECT_UUID` on the dashboard container.
  FT-010 does **not** independently re-resolve any of these; it consumes
  them from the in-phase scope FT-005 already established.
- The `ICoolifyClient` instance constructed by FT-002 during configure,
  reused without modification.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context.

### Outputs

- **On opted-in success:** after FT-005's per-service trigger loop
  (step 7) completes successfully, the dashboard sub-phase issues
  exactly one additional service upsert (name `coolify-aspiredashboard`)
  + one additional deploy-action trigger inside the same project and
  the same targeted environment. After exit, Coolify holds one
  additional service alongside the N workload services, bound to the
  same destination, carrying:
  - `image`: the publisher-pinned constant
    (`ghcr.io/<org>/coolify-aspiredashboard:<pinned-version>`)
  - `destination-binding`: the destination ID from FT-005 step 2
  - env-vars `COOLIFY_API_URL`, `COOLIFY_API_TOKEN`,
    `COOLIFY_PROJECT_UUID` (env-var writes go through the same Coolify
    env-var endpoint surface FT-007 uses; FT-010 invokes that surface
    directly for the dashboard service only, since the dashboard is
    not in the Aspire graph and FT-007's hook does not run for it)
  - no other managed fields
  The dashboard's deploy-job handle is appended to the in-phase
  deploy-action handle list FT-005 collected, so `verify` (FT-006)
  polls it alongside the workload services.
- **Dashboard URL surfacing.** After the dashboard upsert succeeds,
  the publisher reads the FQDN field from the GET response on the
  upserted dashboard service (the same response that yielded the
  `serviceId` for the trigger call). When the response carries a
  non-empty FQDN (Coolify auto-assigned a domain or the developer
  pre-configured one in the Coolify UI), the publisher emits an
  Aspire-structured-log info line attributed to the `deploy` phase:

  ```
  managed-dashboard: url=https://<fqdn>
  ```

  When the FQDN field is empty / null (Coolify has not assigned a
  domain yet and no pre-configured FQDN exists), the publisher emits:

  ```
  managed-dashboard: url=<pending — check Coolify UI for assigned domain>
  ```

  Neither line is fatal under any condition; this is purely developer
  ergonomics.
- **On any warning-path failure:** the publisher prints a single
  diagnostic to stderr whose first whitespace-delimited token is one of
  the literal `W_…` symbols below, **continues the deploy**, and the
  process exit code is **unaffected** (zero iff the workload deploy
  was otherwise successful). The symbols are part of the observable
  contract and are matched as literal strings by exit-criteria tests:

  | Symbol                              | Stderr-visible literal              | Trigger                                                                              |
  |-------------------------------------|-------------------------------------|--------------------------------------------------------------------------------------|
  | `W_DASHBOARD_TOKEN_MISSING`         | `W_DASHBOARD_TOKEN_MISSING`         | Captured dashboard-token handle resolves to null/empty at deploy time                |
  | `W_DASHBOARD_UPSERT_FAILED`         | `W_DASHBOARD_UPSERT_FAILED`         | GET / POST / PATCH on the dashboard service endpoint returned non-2xx, threw, or timed out |
  | `W_DASHBOARD_ENVVAR_FAILED`         | `W_DASHBOARD_ENVVAR_FAILED`         | Any env-var write against the dashboard service returned non-2xx, threw, or timed out |
  | `W_DASHBOARD_TRIGGER_FAILED`        | `W_DASHBOARD_TRIGGER_FAILED`        | POST to the dashboard service's deploy-action endpoint returned non-2xx, threw, or timed out |
  | `W_DASHBOARD_UNEXPECTED`            | `W_DASHBOARD_UNEXPECTED`            | Catch-all for unclassifiable failures inside the dashboard sub-phase                 |

  All `W_…` diagnostics carry the same structured field-block shape
  as FT-005's `E_…` diagnostics (`project:`, `environment:`,
  `coolify:`, `see:`, `remediation:`), but with `severity: warning` on
  the first line and no remediation that asks the developer to re-run
  the deploy — the workload is already deployed, the dashboard is
  observability and can be retried by simply re-running `aspire deploy`
  later.

### State

- **No persistent state on disk authored by FT-010** (inherits ADR-003
  §4 and FT-001 invariant I-3). The publisher-pinned image tag is a
  source constant, not a file.
- **No in-memory cross-deploy state.** Each `aspire deploy` invocation
  re-evaluates whether the dashboard handle was captured and runs the
  sub-phase from scratch.
- **In-memory state is bounded to the dashboard sub-phase.** The
  dashboard service ID, the dashboard deploy-job handle, and the
  dashboard FQDN string live in the local scope of the sub-phase, are
  appended to FT-005's in-phase deploy-action handle list, and are
  discarded when the deploy phase exits.

### Behaviour

The dashboard sub-phase runs inside the existing `deploy` phase
(FT-005), strictly **after** FT-005 step 7 (per-service deploy-action
trigger) completes successfully. If FT-005 short-circuits with any
`E_…` symbol before step 7 (destination / project / environment /
service upsert failure, or workload deploy-trigger failure), the
dashboard sub-phase **does not execute** at all — the workload failed,
so there is nothing to dashboard. The sub-phase executes the following
steps in this exact order:

0. **(Registration-time, not deploy-time.) `WithManagedDashboard(dashboardToken)`
   extension.** FT-010 introduces a new extension method on the same
   builder surface FT-003 / FT-005 chain on (the result of
   `WithCoolifyDeploy(...)`), with signature:

   ```csharp
   public static <ReturnType> WithManagedDashboard(
       this <ReturnType> builder,
       IResourceBuilder<ParameterResource> dashboardToken);
   ```

   The method captures the typed handle on the `CoolifyDeployingPublisher`
   instance and sets an internal `dashboard-opted-in` flag to true.
   Passing `null` throws `ArgumentNullException` at AppHost build time,
   naming the offending argument. The method is idempotent with
   **last-call-wins** semantics (matching FT-003 `WithImageRegistry(...)`
   and FT-005 `WithCoolifyDestination(...)`): calling it twice replaces
   the captured handle with the second call's value but leaves the
   opted-in flag true. Calling it zero times leaves the flag false and
   the dashboard sub-phase skips entirely (no log line, no warning, no
   Coolify call — it is simply not part of the deploy).

1. **Opt-in gate.** At the dashboard sub-phase entry, check the
   `dashboard-opted-in` flag. If false → return immediately, no log
   output, no Coolify call. The remaining steps run only when opted-in.

2. **Resolve the dashboard token.** Read the captured handle's value
   through Aspire's parameter mechanism. If null or empty (after
   trimming), surface `W_DASHBOARD_TOKEN_MISSING` and **return** from
   the sub-phase. The workload deploy is unaffected. The diagnostic
   names the dashboard-token parameter and the two canonical ways to
   set it (`dotnet user-secrets set Parameters:<param-name>` and the
   `Parameters__<param-name>` env-var form), matching ADR-004 §5's
   E_AUTH_TOKEN_MISSING remediation shape but at warning severity.

3. **Dashboard service upsert (name-keyed inside the targeted
   environment).** Compose the dashboard service name from a
   publisher-defined fixed constant (`coolify-aspiredashboard` —
   intentionally distinct from any workload resource name; collisions
   with a user-named workload resource are out of scope and would
   surface as a Coolify-side rejection wrapped into
   `W_DASHBOARD_UPSERT_FAILED`). Inside the project + environment
   resolved by FT-005 steps 3–4, call
   `client.<group>.GetByNameAsync(projectId, environmentId,
   "coolify-aspiredashboard", cancellationToken)` with `<group>` =
   `Applications` (the dashboard is an application, not a service or
   database — single `kind` decision, fixed by this feature). Three
   outcomes mirror FT-005 step 5.ii but at warning severity:
   - **Found:** PATCH the managed set (`image`, `destination-binding`)
     if either has drifted, recording `drift-overwritten` warnings
     for any managed-field drift in the same shape FT-005 emits.
     Capture the `serviceId` and the `fqdn` field from the response.
   - **Not found:** POST to create with `image` =
     publisher-pinned constant, `destination-binding` = destination ID
     from FT-005 step 2, no other managed fields populated (Coolify
     auto-assigns a domain by its own policy, or the developer
     pre-configures one). Capture the `serviceId` from the response.
     The FQDN may be empty on this POST response and surface later.
   - **Non-2xx / transport failure:** surface `W_DASHBOARD_UPSERT_FAILED`
     and **return** from the sub-phase. The workload deploy is
     unaffected.

4. **Env-var writes.** Issue three env-var writes against the
   dashboard service (whose `serviceId` was captured in step 3),
   through the same Coolify env-var endpoint surface FT-007 uses
   against workload services. Each is a name-keyed upsert (GET-by-name
   on `(serviceId, env-name)` → POST-if-absent / PATCH-if-present):
   - `COOLIFY_API_URL` = the resolved Coolify base URL string
     (FT-001's captured URL parameter, resolved by FT-002)
   - `COOLIFY_API_TOKEN` = the resolved dashboard-token value from
     step 2 (**never** the deploy token — this is the audience-
     separation guarantee from ADR-004)
   - `COOLIFY_PROJECT_UUID` = the project UUID captured by FT-005
     step 3 on the project GET/POST response
   On any non-2xx / transport failure for any of the three writes,
   accumulate the failing var-name into a bucket; after attempting all
   three (aggregation discipline matching FT-005 I-10), surface
   `W_DASHBOARD_ENVVAR_FAILED` naming the failed var-names (the
   *values* are never echoed — token redaction and URL non-secret-but-
   noisy are both handled by the diagnostic emitter), and **return**
   from the sub-phase. The workload deploy is unaffected. Note: the
   token-value redaction is enforced by the structured-log emitter
   reading the `secret: true` flag on the dashboard-token parameter
   resource — same path Aspire uses for the deploy token.

5. **Dashboard deploy-action trigger.** Call
   `client.Applications.TriggerDeployAsync(serviceId, cancellationToken)`
   for the dashboard service. Fire-and-confirm-accepted, same shape
   as FT-005 step 7. On 2xx, append the returned deploy-job handle to
   FT-005's in-phase deploy-action handle list so `verify` (FT-006)
   polls it alongside workload services. On non-2xx / transport
   failure, surface `W_DASHBOARD_TRIGGER_FAILED` and **return** from
   the sub-phase. The workload deploy is unaffected; FT-006 will not
   poll a handle that was never appended.

6. **FQDN surfacing.** Re-issue a single GET against the dashboard
   service (or use the GET response cached from step 3 if the
   service was found-and-PATCHed and the FQDN field was populated
   there) to read the current FQDN. Emit the
   `managed-dashboard: url=…` info line described in §Outputs.
   Failure to read FQDN is not a separate warning — the line just
   prints the `pending` form. This step is best-effort and never
   surfaces a `W_…`.

7. **Exit the sub-phase normally.** Return control to FT-005, which
   then completes the `deploy` phase boundary and yields to `verify`
   (FT-006).

**Catch-all (`W_DASHBOARD_UNEXPECTED`).** Any exception escaping the
sub-phase body that is not classifiable as one of the four preceding
symbols (and is not a cancellation) is wrapped and surfaced as
`W_DASHBOARD_UNEXPECTED` with the inner exception's `Message`
appended (no stack trace, no secret content). The sub-phase returns;
the workload deploy is unaffected. Mirrors FT-005's
`E_DEPLOY_PHASE_UNEXPECTED` discipline but at warning severity.

**Cancellation.** If the deploy `CancellationToken` is cancelled
inside the dashboard sub-phase, the publisher honours it the same
way FT-005 does — exit with FT-001's cancellation diagnostic (not a
`W_…` symbol), no further dashboard work, no further workload work.
This is the one path where the dashboard *can* abort the deploy: a
cancellation is a developer intent, not a dashboard failure.

### Invariants

- **I-1: dashboard never fails the workload.** No code path in FT-010
  may cause the `aspire deploy` exit code to be non-zero. The only
  exception is cooperative cancellation (which is FT-001's exit, not
  FT-010's). Asserted by injecting a forced-fail at each of the five
  warning-path steps and verifying exit code zero plus a single `W_…`
  on stderr in each case.
- **I-2: dashboard sub-phase runs only after a clean workload deploy.**
  If FT-005 short-circuits with any `E_…` symbol, the dashboard
  sub-phase does not execute. Asserted by forcing an
  `E_COOLIFY_SERVICE_UPSERT_FAILED` on a workload resource and
  verifying no GET / POST / PATCH against the dashboard service name
  is emitted on the request trace.
- **I-3: dashboard lives in the same project + targeted environment.**
  The dashboard service is upserted under FT-005's resolved
  `(projectId, environmentId)` exactly, never in a sibling
  environment, never in a separate project. Asserted by deploy-log +
  Coolify-side service-list inspection.
- **I-4: audience separation honoured.** The `COOLIFY_API_TOKEN`
  env-var on the dashboard service is sourced from the dashboard-
  token parameter, not from FT-001's deploy-token parameter. Asserted
  by injecting two sentinel values (`DEPLOY-SENTINEL` and
  `DASHBOARD-SENTINEL`) into the two parameters and inspecting the
  outgoing env-var write payload — only `DASHBOARD-SENTINEL` appears.
- **I-5: `WithManagedDashboard(...)` is last-call-wins idempotent.**
  Calling it N times leaves the dashboard opted-in with the
  most-recent handle. Calling it zero times leaves the dashboard not
  opted-in.
- **I-6: opt-out is silent.** When `WithManagedDashboard(...)` was
  never called, the deploy emits zero log lines, zero `W_…` symbols,
  and zero Coolify calls related to the dashboard. The deploy output
  is byte-identical to a publisher build with this feature absent.
- **I-7: dashboard token is never logged.** The dashboard-token value
  is redacted from logs, telemetry, exception messages, and stderr by
  Aspire's `secret: true` parameter redaction — the same machinery
  ADR-004 §6 relies on for the deploy token. Asserted by injecting a
  sentinel dashboard-token value and grepping captured logs /
  exception traces.
- **I-8: image tag is a publisher-pinned constant in v1.** No code
  path in this feature reads the image tag from a parameter, env-var,
  config file, or any other runtime source. The constant is defined
  in publisher source and changes only via a publisher release.
- **I-9: name-keyed upsert and managed-field discipline mirror
  FT-005.** The dashboard service is GET-by-name then POST-or-PATCH;
  PATCH bodies contain only `image` and `destination-binding`. Other
  Coolify fields on the dashboard service (FQDN, port, healthcheck,
  restart policy) are unmanaged in v1.
- **I-10: dashboard deploy-job handle is verified alongside workload.**
  When the dashboard trigger succeeds, its handle is in the list
  `verify` (FT-006) polls. When the dashboard trigger does not
  succeed (warning path), no handle is appended and `verify` polls
  only the workload handles. Verification failure on the dashboard
  handle alone is **not** a workload failure — this is FT-006's
  concern to honour, but FT-010 sets the contract by tagging the
  handle as `dashboard` in the in-phase list. (A future ADR / feature
  may refine how `verify` treats dashboard-tagged handles; v1 leaves
  the tag in place and lets FT-006 decide.)
- **I-11: phase boundary preserved.** The dashboard sub-phase
  contributes log lines under the `deploy` phase attribution; it does
  not emit a separate `dashboard: enter / dashboard: exit` boundary.
  This keeps the five-phase ADR-003 contract intact.
- **I-12: no persistent state on disk.** No file is written by
  FT-010's code under any code path. Asserted by filesystem-diff
  before and after both a successful-opt-in and a warning-path
  deploy.
- **I-13: warning symbols are stable observable contract.** The five
  `W_…` spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any symbol is a
  breaking change to the publisher's CLI contract and requires a new
  ADR.

### Error handling

The five `W_…` diagnostics enumerated above are the only error paths
this feature introduces. Beyond them:

- **Cancellation between or during steps** → FT-001 cancellation
  diagnostic, propagated by FT-005's outer phase body. FT-010 does
  not wrap cancellation in a `W_…`.
- **Null dashboard-token handle at the call site of
  `WithManagedDashboard(...)`** → `ArgumentNullException` thrown at
  AppHost build time, naming the offending argument. Same discipline
  as FT-001 / FT-003 / FT-005.
- **Dashboard-token parameter was created without `secret: true`** →
  not detected by FT-010 (Aspire's parameter API does not currently
  expose the flag for read-back). Documented in §Boundaries as a
  caller responsibility; the redaction guarantee in I-7 depends on
  the caller honouring it.
- **Workload deploy short-circuited before step 7** → dashboard
  sub-phase silently does not run; no `W_…`, no log line.
- **`verify` (FT-006) reports failure on the dashboard deploy-job
  handle while all workload handles succeed** → handled in FT-006
  per the `dashboard` tag described in I-10; FT-010 does not own
  that path.

### Boundaries

- **In scope for FT-010:**
  - the `WithManagedDashboard(dashboardToken)` extension (introduced
    here, fixed signature, last-call-wins idempotency,
    `ArgumentNullException` on null handle)
  - capturing the dashboard-token handle and the `dashboard-opted-in`
    flag on the publisher instance
  - the dashboard sub-phase body inside FT-005's `deploy` phase,
    running strictly after FT-005 step 7 success
  - name-keyed GET-then-POST-or-PATCH discipline on the single
    `coolify-aspiredashboard` Coolify application
  - the three env-var writes (`COOLIFY_API_URL`, `COOLIFY_API_TOKEN`,
    `COOLIFY_PROJECT_UUID`) on the dashboard service, sourced
    respectively from FT-001's URL parameter, FT-010's separate
    dashboard-token parameter, and FT-005's resolved project UUID
  - the dashboard deploy-action trigger and handle appending to
    FT-005's in-phase deploy-action list with `dashboard` tag
  - dashboard FQDN surfacing (`managed-dashboard: url=…`)
  - the five `W_…` warning diagnostics with literal symbols,
    structured field blocks, zero secret content, and warning-not-
    fatal severity
  - the publisher-pinned image tag constant (defined in publisher
    source, e.g. `ghcr.io/pksorensen/coolify-aspiredashboard:0.1.0`)
- **Out of scope for FT-010** (handled elsewhere or deferred):
  - building, publishing, or versioning the
    `coolify-aspiredashboard` image itself → separate sub-project /
    repo; this feature only references a publisher-pinned constant
  - the Coolify-aware resource provider plugin bundled inside the
    dashboard image → separate sub-project / repo
  - overriding the dashboard image tag, port, FQDN, or any other
    dashboard configuration from the AppHost → deferred to a later
    feature once the dashboard image release cadence is real
  - the `WithCoolifyDeploy(...)` / `WithImageRegistry(...)` /
    `WithCoolifyDestination(...)` extensions → FT-001 / FT-003 /
    FT-005
  - configure-phase token resolution and version+auth probe for the
    deploy token → FT-002 (FT-010 reuses the same parameter-
    resolution mechanism for its dashboard token at deploy time, not
    configure time, because dashboard-token missing is a warning not
    a fatal)
  - the workload-side `deploy` phase walk and per-service triggers
    → FT-005
  - the workload-side env-var sync (`WithReference` + parameters)
    → FT-007 / FT-008 (FT-010 calls the same Coolify env-var
    endpoints directly for the dashboard service only, since the
    dashboard is not in the Aspire graph and FT-007's hook does not
    fire for it)
  - `verify` phase polling of the dashboard deploy-job handle, and
    the policy for treating dashboard verification failure as
    non-fatal → FT-006 honouring the `dashboard` tag
  - validating that the dashboard-token parameter was created with
    `secret: true` → caller responsibility; I-7 depends on it
  - claim-marker / managed-by tagging to distinguish a publisher-
    upserted dashboard from a hand-created service of the same name
    → not in v1 (mirrors ADR-001's stance on workload services)
  - TypeScript AppHost parity for `WithManagedDashboard(...)` →
    `apphost-ts` domain's feature

## Out of scope

- **Building / shipping the dashboard image.** FT-010 references a
  publisher-pinned constant tag; the image is built and released in a
  separate sub-project / repo. Image release cadence, signing, SBOM,
  base-image choice, and the Coolify-aware resource provider plugin
  implementation are not this feature's concerns.
- **Dashboard configuration knobs.** No image-tag override, no port
  override, no FQDN override, no env-var-passthrough beyond the three
  required vars. A later feature revisits this once the dashboard
  image's release cadence is real.
- **`verify`-phase policy for dashboard verification failure.** FT-006
  reads the `dashboard` tag on the deploy-job handle and decides
  whether a dashboard-only verification failure is a warning or fatal.
  FT-010 sets the tag; the policy is FT-006's.
- **Tear-down of the dashboard service when `WithManagedDashboard(...)`
  is removed from `AppHost.cs`.** Aspire-level removal of the chained
  extension call leaves the previously-upserted dashboard service
  orphaned in Coolify (same orphan-on-remove semantics FT-005 §Out of
  scope flags for workload resources). A future `aspire coolify gc`
  is out of v1.
- **Multi-dashboard support.** Exactly one dashboard service per
  AppHost in v1. Calling `WithManagedDashboard(...)` twice is last-
  call-wins on the token handle; it does not register a second
  dashboard.
- **Dashboard runtime concerns** (auth UX, sign-in flow, RBAC,
  multi-tenant viewer auth on the dashboard's own surface). The
  dashboard image bundles whatever upstream Aspire dashboard ships
  with; FT-010 only wires its env-vars at deploy time.
- **Persistent state on disk** of any kind. Forbidden by ADR-003 §4
  and I-12.
- **Retry / backoff on dashboard upsert, env-var, or trigger
  failures.** Single attempt; failure → warning + skip. Re-run
  `aspire deploy` later to retry.
- **Detecting that the dashboard-token parameter was created without
  `secret: true`.** Caller responsibility. Documented in I-7 and
  §Error handling.
- **TypeScript AppHost parity** for `WithManagedDashboard(...)` →
  `apphost-ts` feature.
