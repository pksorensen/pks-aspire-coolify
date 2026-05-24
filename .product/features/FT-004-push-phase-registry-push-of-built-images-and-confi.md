---
id: FT-004
title: Push phase — registry push of built images and configure-phase Coolify Private Registry upsert
phase: 1
status: complete
depends-on:
- FT-001
- FT-003
adrs:
- ADR-002
- ADR-003
- ADR-004
- ADR-005
- ADR-001
- ADR-006
tests:
- TC-005
- TC-008
domains:
- aspire-publisher
- coolify-api
- image-flow
domains-acknowledged: {}
---

## Description

FT-004 fills in the **push phase** body that FT-001's skeleton left as a
no-op, and additionally contributes one small step to the **configure
phase**: the Coolify-side Private Registry record upsert mandated by
ADR-005 §D5. Together these two touch-points complete the registry
half of the deploy: configure ensures Coolify holds the credentials it
will need to *pull* (when the registry is private), and push moves the
image bytes FT-003 produced from the developer's local cache to the
registry Coolify will pull from.

Per ADR-003, `push` runs after `build` (FT-003) and before `deploy`
(FT-005). The push phase is the third of the five fixed phases, the
*first* phase that talks to a network destination other than Coolify,
and the first phase whose success or failure is observable by an
external party (the registry's namespace, after a successful push,
contains the freshly tagged images). The Coolify-side Private Registry
upsert lives in `configure` rather than `push` because ADR-005 §D5
fixes it there: prerequisite-side state is established up-front, before
any image bytes move, so that a misconfigured Coolify-side record fails
fast at the same gate as `E_AUTH_TOKEN_*` / `E_COOLIFY_VERSION_BELOW_FLOOR`
rather than mid-push.

FT-004 reads — for the first time in the pipeline — the `username` and
`password` registry-credential parameter handles that FT-003's
`WithImageRegistry(prefix, username, password)` extension captured but
did not consume. ADR-004's redaction discipline applies to
`registry-password` exactly as it does to `coolify-<name>-token`:
never logged, never echoed, resolved through Aspire's parameter
machinery, no caching beyond the in-memory client. Username and prefix
are not secret and may appear in logs to aid diagnosis.

**Anonymous push (no credentials) is a first-class supported path.**
When the `username` / `password` pair is omitted (per ADR-005 §1 they
travel as a pair: both set, or both unset), FT-004 skips *both* the
configure-phase Coolify Private Registry upsert *and* any registry-
auth handling in push. The registry is assumed publicly pullable
(public `ghcr.io` namespace, or a homelab `registry:2` listed in the
Coolify destination's `insecure-registries`). Failures to pull
anonymously surface later, at `verify` (FT-006), with a precise
Coolify-side error — they are not FT-004's concern.

The exit criteria are: (a) when credentials are present, the configure
phase upserts exactly one Coolify Private Registry record keyed by
`(host, username)` per ADR-005 §D5, with `host` derived from the
prefix's leading segment; (b) when credentials are absent, no Coolify
Private Registry call is issued at all; (c) the push phase moves
every image FT-003 built (one per containerisable resource) to the
configured registry, using deterministic per-resource tags
`<registry-prefix>/<resource-name>:<apphost-version>` exactly as FT-003
emitted them, with no tag mutation; (d) on any push failure across the
N resources, the publisher fails the phase, does not advance to
`deploy`, and surfaces `E_IMAGE_PUSH_FAILED` (or the more specific
`E_REGISTRY_AUTH_FAILED` on 401/403) with the failing resource name(s)
and tag(s); (e) on any configure-phase upsert failure (Coolify
reachable but POST/PATCH errored), the publisher fails configure with
`E_COOLIFY_REGISTRY_UPSERT_FAILED`, does not advance to `build`, and
leaves the Coolify instance byte-identical to its pre-deploy state on
all fields the publisher attempted to write.

This feature reuses Aspire's existing container-image push pipeline
(ADR-005 §D3) rather than invoking `docker push` directly, mirroring
how FT-003 reuses the build pipeline.

## Functional Specification

### Inputs

- The `IResourceBuilder<ParameterResource>` triple `(prefix, username,
  password)` captured on the `CoolifyDeployingPublisher` instance by
  FT-003's `WithImageRegistry(...)` extension. FT-004 reads all three:
  `prefix` (to derive the registry host for the Coolify-side record
  key, and to pass to the push pipeline), `username` and `password`
  (for both the Coolify-side upsert and the registry-side push auth).
  `prefix` is required; `username` and `password` are jointly optional
  and travel as a pair (ADR-005 §1).
- The `ICoolifyClient` instance constructed by FT-002 during the
  configure phase. FT-004's configure-phase upsert step issues calls
  through `client.PrivateRegistries` — a new endpoint group on the
  hand-written client owned by ADR-002 (`coolify-api` domain).
  FT-004 names only the property (`PrivateRegistries`); the underlying
  `/api/v1/...` path and DTO shape belong to ADR-002.
- The local image-cache entries produced by FT-003: one per
  containerisable resource, each carrying the deterministic tag
  `<prefix>/<resource-name>:<apphost-version>`. FT-004 enumerates the
  same resource set the build phase iterated (received from the
  publisher driver — FT-004 does not re-walk the Aspire graph).
- Aspire's existing container-image **push** infrastructure — the same
  surface the Docker Compose publisher and `aspire-ssh-deploy` use to
  push a locally-tagged image to a registry, with credentials supplied
  in whatever form Aspire's pipeline accepts. FT-004 consumes it as a
  black box: it supplies (image-tag, registry-credentials) and
  delegates execution. The exact entry point is named at implementation
  time against the Aspire SDK version pinned by FT-001's source files.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context.

### Outputs

- **On success (push phase):** the push phase exits normally and yields
  control to the `deploy` phase (FT-005). After exit, the configured
  registry's namespace contains exactly one image per containerisable
  resource at the deterministic tag, and no `latest` tag is present
  (ADR-005 §D4 / §D6 — re-asserted at the push layer as FT-003's I-2
  is re-asserted here). The publisher has emitted, per resource, an
  Aspire-structured-log entry attributed to the `push` phase recording
  the resource name, the full image tag, and the registry host (no
  credentials).
- **On success (configure-phase upsert step), credentials present:**
  exactly one Coolify Private Registry record exists matching
  `(host, username)`, tagged with the `managed-by:
  pks-aspire-coolify` marker per ADR-005 §D5. Either one POST (create)
  or zero/one PATCH (update password when changed) was issued. No
  duplicate records appear. Configure proceeds to `build`.
- **On success (configure-phase upsert step), credentials absent:**
  zero Coolify Private Registry calls were issued. Configure proceeds
  to `build`.
- **On any fail-fast path:** the publisher prints a single diagnostic
  to stderr whose first whitespace-delimited token is one of the four
  literal symbols below, exits non-zero, does not enter the *next*
  phase (push fails → no `deploy`; configure-upsert fails → no
  `build`), and (for `E_COOLIFY_REGISTRY_UPSERT_FAILED`) leaves the
  Coolify instance byte-identical to its pre-deploy state on fields
  the publisher attempted to write. The four symbols are part of the
  **observable contract** and are matched as literal strings by
  FT-004's exit-criteria tests — they are not internal exception type
  names. They are:

  | Symbol                              | Stderr-visible literal              | Phase     | Trigger                                                                  |
  |-------------------------------------|-------------------------------------|-----------|--------------------------------------------------------------------------|
  | `E_COOLIFY_REGISTRY_UPSERT_FAILED`  | `E_COOLIFY_REGISTRY_UPSERT_FAILED`  | configure | Coolify reachable but `PrivateRegistries` POST/PATCH returned non-2xx     |
  | `E_REGISTRY_AUTH_FAILED`            | `E_REGISTRY_AUTH_FAILED`            | push      | Registry returned 401 or 403 on push for any resource                    |
  | `E_IMAGE_PUSH_FAILED`               | `E_IMAGE_PUSH_FAILED`               | push      | Any other push failure for any resource (5xx, timeout, refused, DNS, TLS, manifest reject) |
  | `E_PUSH_PHASE_UNEXPECTED`           | `E_PUSH_PHASE_UNEXPECTED`           | push      | Catch-all for unclassifiable failures inside the push phase body         |

  All four diagnostics carry the same structured field block shape as
  FT-002 / FT-003:

  ```
  <E_SYMBOL>: <one-line human description>
    resource(s): <aspire-resource-name>[, …]      (for IMAGE_PUSH_FAILED / REGISTRY_AUTH_FAILED / PUSH_PHASE_UNEXPECTED)
    tag(s):      <prefix>/<resource>:<version>[, …]  (for IMAGE_PUSH_FAILED / REGISTRY_AUTH_FAILED)
    registry:    <host>                           (for IMAGE_PUSH_FAILED / REGISTRY_AUTH_FAILED / COOLIFY_REGISTRY_UPSERT_FAILED)
    username:    <resolved-username>              (for REGISTRY_AUTH_FAILED / COOLIFY_REGISTRY_UPSERT_FAILED — never the password)
    see:         ADR-005 §D5                      (for COOLIFY_REGISTRY_UPSERT_FAILED)
    remediation:
      verify the registry-username / registry-password Aspire parameters     (for REGISTRY_AUTH_FAILED)
      verify network reach to <host> from the AppHost build machine          (for IMAGE_PUSH_FAILED)
      check Coolify's Private Registries view for stale or duplicate records (for COOLIFY_REGISTRY_UPSERT_FAILED)
  ```

  The first whitespace-delimited token on the first line is the literal
  `E_…` symbol; this is what TC-007 (and any future push-phase tests)
  grep for.

### State

- **No persistent state on disk authored by FT-004** (inherits
  ADR-003 §4 and FT-001 invariant I-3). The only externally-observable
  artefacts FT-004 produces are (a) the image-tag entries that appear
  in the *registry's* storage as a result of the push, and (b) the
  Coolify-side Private Registry record (when credentials are present).
  Neither is a file under the AppHost directory; neither is written by
  FT-004 directly to local disk.
- **In-memory state is bounded to the configure-phase upsert step and
  the push phase.** The resolved `username` and `password` strings
  live in the local scope of the upsert step (where they are handed
  to `client.PrivateRegistries.UpsertAsync(...)`) and in the local
  scope of the push phase body (where they are handed to Aspire's
  push pipeline). They are **not** stored as fields on the publisher
  and **not** captured in any closure that outlives the phase that
  read them. After push exits, the resolved password string is out of
  scope.
- **No probe-result or upsert-result cache across deploys.** Every
  `aspire deploy` invocation runs the upsert check fresh (GET-then-
  POST-or-PATCH against Coolify). This is the same discipline FT-002
  applies to the version+auth probe.
- **The Coolify Private Registry record itself is server-side state**,
  upserted name-keyed by `(host, username)` per ADR-001's idempotency
  discipline. FT-004 does not delete records on tear-down (there is
  no tear-down in v1; ADR-003 §6 is verify-gated and non-transactional)
  and does not garbage-collect stale records the publisher previously
  created — drift on the record is handled per ADR-003 (warn-and-
  overwrite on the password field; leave unmanaged fields untouched).

### Behaviour

FT-004 contributes work to two phases. The configure-phase contribution
runs **after** FT-002's auth probe and **before** the configure phase
exits to `build`. The push-phase contribution runs as the entire body
of the push phase.

#### Configure-phase contribution — Coolify Private Registry upsert

These steps run sequentially after FT-002's combined version+auth
probe has succeeded. If any step fails, configure exits non-zero with
`E_COOLIFY_REGISTRY_UPSERT_FAILED` and does not enter `build`.

1. **Check whether registry credentials are configured.** Look at the
   publisher's captured `(username, password)` handles. If
   `WithImageRegistry(...)` was called with both omitted (anonymous-
   push path), **skip this entire step**: no resolution, no Coolify
   call, no log entry beyond a single structured "anonymous registry
   push — Coolify-side upsert skipped" debug-level line. Configure
   proceeds to `build` as if FT-004 contributed nothing.

2. **Resolve `(username, password)`.** Request the resolved string
   values of the two parameter handles through Aspire's standard
   parameter-resolution API (matching FT-002 §Behaviour §1 for the
   Coolify token). If exactly one resolves to a non-empty value and
   the other resolves to null/empty, treat as
   `E_COOLIFY_REGISTRY_UPSERT_FAILED` with a remediation message
   pointing at ADR-005 §1's "credentials travel as a pair" rule.
   (FT-003 §Behaviour §0 already throws `ArgumentException` at AppHost
   build time when this asymmetry exists at the call site; the runtime
   check here is a defence in depth for cases where the parameter
   values themselves are mismatched.)

3. **Derive the registry host.** Read the `prefix` parameter (already
   resolved during FT-003's build-phase pre-flight is not assumed —
   FT-004 resolves freshly here, matching FT-002's I-4 "exactly once
   per deploy" pattern applied per-phase). The `host` is the substring
   of `prefix` before the first `/`. Example: `ghcr.io/pksorensen/myapp`
   → `ghcr.io`. Example: `registry.lan:5000/myapp` → `registry.lan:5000`.
   No validation beyond "non-empty"; the registry's own response on
   push will surface bad hosts.

4. **Issue the Private Registry upsert.** Call
   `client.PrivateRegistries.UpsertAsync(host, username, password,
   cancellationToken)` (final method name chosen against ADR-002's
   client surface at implementation time). The client is expected to
   implement GET-by-`(host, username)` → POST-if-absent /
   PATCH-if-present-and-password-changed, tagging new records with
   the `managed-by: pks-aspire-coolify` suffix per ADR-005 §D5. The
   client is the single owner of the `/api/v1/...` path, the DTO
   shape, the marker-suffix placement, and the password-change
   detection (Coolify reports a presence/hash, never the value, so
   the comparison is hash-based or always-PATCH; either is acceptable
   and is ADR-002's concern, not FT-004's).

5. **Classify the result.** Success → continue. Any non-2xx response,
   thrown `HttpRequestException`, timeout, or other client-surfaced
   error → fail-fast `E_COOLIFY_REGISTRY_UPSERT_FAILED` with the
   structured field block (registry host, username, ADR pointer). The
   diagnostic includes the underlying error's `Message` (no stack
   trace, no password content).

#### Push-phase contribution — registry push of built images

These steps run as the entire body of the push phase. If any step
fails for any resource, push exits non-zero with the matching
`E_…` symbol and does not enter `deploy`.

1. **Enumerate the resource set to push.** Push receives the same
   resource set FT-003's build phase iterated, from the publisher
   driver. FT-004 does not re-walk the Aspire graph, does not
   re-classify containerisable-vs-not (FT-009 owns that filter; FT-004
   assumes the set is the same one build operated on), and does not
   re-derive image tags (each tag was computed once during build per
   FT-003 §Behaviour §4 and is reused here verbatim).

2. **Resolve registry credentials (or confirm anonymous).** If
   `WithImageRegistry(...)` was called with `(username, password)`
   present, resolve both through Aspire's standard parameter API.
   If absent, the push proceeds anonymously — no resolution, no
   credential argument passed to the push pipeline beyond what
   Aspire's pipeline does by default for an anonymous push.

3. **For each resource in the enumeration, push.** For every resource:
   1. Read the resource's image tag — the deterministic
      `<prefix>/<resource.Name>:<apphost-version>` string that FT-003
      attached to the local image. FT-004 does not recompute the tag
      from `prefix` + `resource.Name` + version; it reads what build
      emitted so that any divergence between build's tag and push's
      tag is impossible by construction.
   2. Invoke Aspire's container-image **push** pipeline for this
      image tag, passing the resolved registry credentials (or
      passing the anonymous indicator). The pipeline's existing
      semantics for retry, progress reporting, manifest format,
      and protocol negotiation are inherited unchanged.
   3. If the pipeline call returns a 401 or 403 (registry refused
      the push for auth reasons), accumulate the (resource, tag)
      pair into the `REGISTRY_AUTH_FAILED` bucket.
   4. If the pipeline call returns any other failure (5xx, request
      timeout, connection-refused, DNS-resolution-failed,
      TLS-handshake-failed, manifest rejected, blob upload aborted),
      accumulate the (resource, tag) pair into the `IMAGE_PUSH_FAILED`
      bucket. The diagnostic later names *all* failing resources, not
      only the first.
   5. On success, emit a single Aspire-structured-log entry attributed
      to the push phase recording the resource name, the full image
      tag, and the registry host. No credentials are emitted.

4. **Aggregate failures and exit.** After every resource has been
   attempted (push does **not** short-circuit on the first failure —
   it attempts all N so the diagnostic can name the full set, which
   is more useful than \"the first one failed\"):
   - If the `REGISTRY_AUTH_FAILED` bucket is non-empty, fail-fast
     `E_REGISTRY_AUTH_FAILED` with all failing (resource, tag) pairs.
     Auth failures take precedence in the diagnostic because they
     are usually a single config error producing N symptoms; surfacing
     them together prevents a misleading "push for resource-7 failed"
     when the real cause is "the registry-password parameter is wrong
     and *all* pushes 401'd."
   - Else if the `IMAGE_PUSH_FAILED` bucket is non-empty, fail-fast
     `E_IMAGE_PUSH_FAILED` with all failing (resource, tag) pairs.
   - Else exit the push phase normally.

5. **Successfully-pushed siblings are not "unpushed" on failure.**
   The images that did push live at the registry, untouched. This
   matches ADR-003 §6's verify-gated, non-transactional rollback
   contract and FT-003 §Behaviour §4.iii's discipline for build
   failures: the publisher refuses to advance to `deploy` after a
   push failure, but does not pretend it can undo registry writes.
   Tags are immutable per ADR-005 §D4, so the partial push leaves
   the registry in a recoverable state for the next deploy
   (which will re-push the failing tags after the underlying cause
   is fixed; tags that already exist at the target will be no-ops
   for the pipeline).

**Concurrency.** Push ships sequential in v1, matching FT-003 §Behaviour
"Concurrency." The model permits per-resource concurrency (each push
is independent and idempotent against the registry), and a future
feature may parallelise without re-deciding. Within FT-004 the
per-resource loop runs one resource at a time, in graph enumeration
order, and the deploy log reflects that order.

**Cancellation.** If the deploy `CancellationToken` is cancelled
between any of the configure-phase upsert steps, between push steps,
or between two resource pushes, the affected phase exits with FT-001's
cancellation diagnostic (not an `E_…` symbol) and does not enter the
next phase. A cancellation observed *inside* Aspire's push pipeline
mid-upload is propagated by the pipeline; FT-004 re-emits it as a
cancellation exit, not as `E_IMAGE_PUSH_FAILED`.

**Catch-all (`E_PUSH_PHASE_UNEXPECTED`).** Any exception escaping the
push phase that is not classifiable as `E_REGISTRY_AUTH_FAILED` or
`E_IMAGE_PUSH_FAILED` (and is not a cancellation) is wrapped and
surfaced as `E_PUSH_PHASE_UNEXPECTED` with the inner exception's
`Message` appended (no stack trace, no secret content). This mirrors
FT-002's `E_COOLIFY_UNREACHABLE` catch-all and FT-003's
`E_BUILD_PHASE_UNEXPECTED`: better a precise fail-fast than entering
`deploy` in an unknown state.

### Invariants

- **I-1: anonymous-push mode issues zero Coolify Private Registry
  calls.** When `WithImageRegistry(...)` is called with both
  `username` and `password` omitted, the configure-phase contribution
  performs no GET, POST, or PATCH against `client.PrivateRegistries`.
  Asserted by intercepting outbound HTTP during a happy-path
  anonymous deploy and asserting zero requests to the `PrivateRegistries`
  endpoint group.
- **I-2: credentialled mode upserts exactly one Private Registry
  record per `(host, username)` pair, idempotently.** Re-running the
  same deploy with unchanged credentials produces zero net change on
  the Coolify side (no new record, no spurious PATCH) on the second
  run. Re-running with a *changed* password produces exactly one
  PATCH. Re-running with a *different username* produces a new record
  (a different `(host, username)` tuple, per ADR-005's keying rule).
- **I-3: the resolved password string is never logged, echoed, or
  written to disk.** This includes diagnostic messages, exception
  messages, Aspire structured-log fields, the Aspire dashboard
  parameter display, any push-pipeline progress lines re-emitted into
  the deploy log, and any captured-and-rethrown exception chain. The
  username is *not* secret and may appear in logs; the password
  is. Asserted by sentinel-grep in TC-007 (mirrors FT-002 §I-3 and
  ADR-005 test-coverage's "Redaction" item).
- **I-4: the four `E_…` symbols are stable observable contract.**
  Their spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any symbol is
  a breaking change requiring a new ADR.
- **I-5: push uses the exact image tag FT-003 emitted.** FT-004 does
  not recompute the tag from `(prefix, resource.Name, apphost-version)`;
  it reads the tag attached to the local image-cache entry by FT-003.
  This makes any drift between build's tag and push's tag impossible.
  No `latest` tag is pushed under any code path (re-asserts FT-003 §I-2
  at the push layer).
- **I-6: push fails refuse to advance to `deploy`.** Asserted by
  forcing one resource's push to fail and verifying that no log line
  attributed to the `deploy` phase appears, and that no Coolify
  application/service GET, POST, or PATCH is issued.
- **I-7: configure-phase upsert failure refuses to advance to `build`.**
  Asserted by forcing the `client.PrivateRegistries.UpsertAsync(...)`
  call to return non-2xx and verifying that no log line attributed
  to the `build` phase appears, no Aspire image-pipeline call is
  made, and the only Coolify-side mutation attempted was the failed
  upsert itself.
- **I-8: configure-phase upsert failure leaves Coolify byte-identical
  to its pre-deploy state on managed fields.** If the POST/PATCH
  fails after Coolify has accepted bytes but before it commits, FT-004
  does not retry, does not roll forward to a different shape, and
  does not invent a compensating delete. The failure is surfaced
  verbatim with the upsert's response body excerpt in the diagnostic.
  (Asserted by a snapshot diff of Coolify's `private-registries`
  list before and after a forced-failure scenario.)
- **I-9: push attempts every resource before reporting failure.**
  The per-resource push loop does not short-circuit on the first
  failure. The diagnostic for `E_IMAGE_PUSH_FAILED` /
  `E_REGISTRY_AUTH_FAILED` carries *all* failing (resource, tag)
  pairs from the run, not just the first. Asserted by forcing two
  of three resources to fail and verifying both appear in the
  stderr diagnostic.
- **I-10: every fail-fast exit emits the phase boundary.** FT-001's
  phase-boundary logging shows `configure: enter … configure: exit
  (failed)` for `E_COOLIFY_REGISTRY_UPSERT_FAILED` (with no `build:
  enter`), and `push: enter … push: exit (failed)` for the three
  push-phase symbols (with no `deploy: enter`). Asserted by deploy-
  log scraping in TC-007.
- **I-11: the credential handles are dereferenced only inside the
  phases that need them.** The configure-phase upsert step resolves
  `(username, password)` only when about to call
  `client.PrivateRegistries.UpsertAsync(...)`; the push phase
  resolves them only when about to invoke Aspire's push pipeline.
  Neither phase stores the resolved strings on the publisher
  instance, and the resolved values go out of scope as the phase
  exits. (Composes with FT-003 §I-6 "captured but never
  dereferenced" — FT-004 is the dereferencing feature.)

### Error handling

The four `E_…` diagnostics enumerated above are the only error paths
this feature introduces. Beyond them:

- **Cancellation between or during steps** → FT-001 cancellation
  diagnostic (not an `E_…` symbol).
- **`(username, password)` resolution asymmetry at runtime** (one
  resolves non-empty, the other null/empty) → treated as
  `E_COOLIFY_REGISTRY_UPSERT_FAILED` in configure with a remediation
  message pointing at ADR-005 §1's "credentials travel as a pair"
  rule. If the asymmetry is detected later in push (defence in depth),
  same classification but at the push phase boundary — though
  `E_PUSH_PHASE_UNEXPECTED` is also acceptable for this case, since
  configure should have already caught it.
- **`client.PrivateRegistries.UpsertAsync(...)` throws an
  unclassifiable exception type** (e.g. a bug in the client itself)
  → `E_COOLIFY_REGISTRY_UPSERT_FAILED` with the inner `Message`
  appended. This is the configure-phase catch-all bucket.
- **Aspire's push pipeline returns success for a tag, but the tag
  does not appear in the registry's storage on a subsequent
  read** → out of scope for FT-004's defensive verification (FT-003
  defends against this for the local cache because the local cache
  is cheap to query; the registry is not, and `verify` (FT-006) will
  catch the deeper failure via Coolify's own pull). FT-004 trusts the
  push pipeline's reported result.
- **Mixed-failure ordering.** If the push loop encounters both 401/403
  failures and other-failures across different resources, the
  diagnostic classifies as `E_REGISTRY_AUTH_FAILED` (auth failures
  take precedence per Behaviour §4 — they almost always indicate a
  single root cause). The non-auth failures are still listed in the
  diagnostic's `resource(s)` and `tag(s)` fields under a "and N
  additional non-auth failures" line so the user sees the full
  picture.
- **`registry-prefix` resolves to null/empty at FT-004's
  configure-phase contribution start time.** Already an
  `E_REGISTRY_NOT_CONFIGURED` from FT-003 §Behaviour §1 — but FT-003's
  check fires at the start of the *build* phase. Since FT-004's
  configure-phase step runs *before* build, FT-004 must check `prefix`
  too: if `WithImageRegistry(...)` was not called, FT-004 simply
  skips its configure-phase contribution (the missing-prefix case
  is FT-003's to surface at the build-phase boundary, with the
  same `E_REGISTRY_NOT_CONFIGURED` diagnostic). FT-004 does not
  invent a parallel "registry not configured" symbol.

### Boundaries

- **In scope for FT-004:**
  - the configure-phase Coolify Private Registry upsert step (per
    ADR-005 §D5, anchored after FT-002's auth probe, before `build`)
  - resolving the `(username, password)` parameter handles (which
    FT-003 captured but did not consume)
  - deriving the registry host from the prefix's leading segment
  - issuing the upsert through `client.PrivateRegistries.UpsertAsync(...)`
    on the existing `ICoolifyClient` (ADR-002 owns the endpoint paths
    and DTO shape)
  - the push-phase body: per-resource iteration, invocation of
    Aspire's push pipeline with resolved credentials (or anonymous),
    aggregated failure classification, structured diagnostics
  - the anonymous-push path: skipping both the configure-phase upsert
    and any push-side credential handling
  - the four `E_…` diagnostics with literal symbols, structured field
    blocks, and zero secret content
  - cancellation honouring at phase boundaries and as propagated by
    the push pipeline
  - re-emission of push-pipeline progress under the `push` phase
    attribution
- **Out of scope for FT-004** (handled elsewhere or deferred):
  - the `WithImageRegistry(...)` extension itself (signature, handle
    capture, last-call-wins idempotency, ArgumentException on
    `(username XOR password)` at the call site) → **FT-003**
  - the actual `/api/v1/...` paths, request/response DTOs, marker-
    suffix placement, and password-change detection for the
    Private Registries endpoint group → **ADR-002 + the
    `coolify-api` domain's client**
  - the build-phase image production → **FT-003**
  - the deploy phase: Aspire-graph walk, resource-to-Coolify-object
    upserts, env-var sync, Coolify deploy-action trigger → **FT-005**
  - the verify phase: polling Coolify's deploy-action status,
    surfacing "Coolify failed to pull the image" → **FT-006**
  - filtering the resource set to containerisable resources → **FT-009**
  - TypeScript AppHost parity for the registry-credential parameters
    (which already live on `WithImageRegistry(...)`, owned by FT-003)
    → **the `apphost-ts` domain's feature**
  - drift detection on the Coolify Private Registry record beyond
    ADR-003's warn-and-overwrite — no separate `aspire coolify
    drift` command, no refuse-to-deploy strict mode → **deferred**
  - GC of stale Private Registry records the publisher previously
    created (e.g. when a prefix's host changes between deploys, the
    old `(host, username)` record is *left behind* rather than
    deleted) → **deferred; ADR-003 §6 verify-gated, non-transactional
    contract permits this**
  - retry / backoff on push or upsert failures → single attempt,
    matches FT-002's transport-failure discipline and FT-003's
    image-pipeline failure discipline

## Out of scope

- **Tearing down or rolling back successfully-pushed images.** Tags
  are immutable per ADR-005 §D4; partial push failure leaves the
  registry with whatever subset succeeded, untouched. The next deploy
  re-pushes the failing tags; the already-pushed tags are no-ops at
  the pipeline level.
- **Multi-architecture / multi-platform manifest list pushes.** FT-003
  emits one image tag per resource for the local build host's
  architecture; FT-004 pushes that tag as-is. Cross-arch is a later
  concern.
- **Image signing, attestation, SBOM upload during push.** If Aspire's
  push pipeline grows these affordances they appear here by
  inheritance; FT-004 does not configure them.
- **Authenticating Coolify-the-puller to the registry by means other
  than the `PrivateRegistries` record.** Per-destination `docker
  login` state, host-level `~/.docker/config.json`, Kubernetes
  pull-secret style mechanisms are out of v1 — ADR-005 §D5 chose
  the `PrivateRegistries` record as the one channel and FT-004
  honours that.
- **Distinguishing "registry rejected the manifest" from "registry
  refused the blob upload" inside `E_IMAGE_PUSH_FAILED`.** Both
  bucket together as transport-level push failures. A future ADR
  may refine; v1 accepts the single bucket.
- **Distinguishing 401 from 403 in `E_REGISTRY_AUTH_FAILED`.** Mirrors
  ADR-004's "one opaque error path for auth" rationale. Both surface
  as the same symbol with the same remediation ("verify the
  registry-username / registry-password Aspire parameters").
- **A `--dry-run` mode for the push phase.** No such mode in v1;
  push always pushes or fails fast. Plan/apply separation was
  rejected by ADR-003 §Rationale.
- **Pre-flight reachability check of the registry before pushing.**
  No HEAD-to-`/v2/` probe; the first push attempt *is* the
  reachability check. Avoids the same "double round-trip" footgun
  FT-002 §I-1 calls out for the Coolify probe.
- **Reading any value from the Coolify Private Registry record's
  response beyond "did it succeed."** FT-004 does not surface Coolify-
  assigned record IDs, does not propagate the record's display name,
  does not feed any field of the response forward to `deploy` or
  `verify`. The upsert is fire-and-confirm.
- **Coolify-side registry record deletion.** No tear-down in v1.
  Stale records accumulate; users can remove them in the Coolify UI.
  A future feature may add `aspire coolify gc` semantics; not v1.
- **Affecting the `latest` tag.** I-5 re-asserts FT-003 §I-2: no
  `latest` push, no `latest` tag operation of any kind.
