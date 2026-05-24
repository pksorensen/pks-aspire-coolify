---
id: FT-002
title: Configure phase — token resolution and combined version + auth probe
phase: 1
status: complete
depends-on:
- FT-001
adrs:
- ADR-002
- ADR-003
- ADR-004
- ADR-001
- ADR-006
tests:
- TC-002
- TC-004
- TC-006
domains:
- aspire-publisher
- auth
- coolify-api
domains-acknowledged: {}
---

## Description

FT-002 fills in the **configure phase** body that FT-001's skeleton left
as a no-op. It is the first feature that actually reads the `(url,
token)` parameter pair captured by `WithCoolifyDeploy(...)`, performs
the only network round-trip permitted in the configure phase, and
turns the result into one of four fail-fast diagnostics or a green
light for the rest of the pipeline.

Per ADR-003, configure is the prerequisite-check phase: it runs to
completion before `build` enters, and it is the only phase in which
the publisher is permitted to abort the deploy without leaving Coolify
half-converged (because no side-effecting work has begun yet). Per
ADR-002, the version probe lives in configure; per ADR-004, the auth
probe lives in configure too — FT-002 commits to the load-bearing
observation that **they are the same round-trip**, a single
authenticated GET against a low-cost Coolify endpoint whose response
satisfies both compatibility (ADR-002) and credential (ADR-004) checks
at once.

The exit criteria for this feature are entirely about (a) parameter
resolution through Aspire's standard `secret: true` mechanism, (b) the
combined version+auth probe being a single round-trip, (c) the four
diagnostic codes being printed verbatim to stderr in the order of the
fixed precedence rule, and (d) zero Coolify side-effects on any
fail-fast path. The actual HTTP client, endpoint path, and Coolify DTO
shape are ADR-002's concern and live in the `coolify-api` domain; this
feature consumes that client as a black box.

## Functional Specification

### Inputs

- The `IResourceBuilder<ParameterResource>` pair `(url, token)`
  captured on the `CoolifyDeployingPublisher` instance by FT-001's
  `WithCoolifyDeploy(url, token)` call. FT-002 reads these handles at
  the start of the `configure` phase — not earlier. Both handles are
  guaranteed non-null by FT-001's build-time check (FT-001 §Error
  handling, `ArgumentNullException`); FT-002 does not re-validate
  nullness.
- Aspire's parameter-resolution mechanism, used in its standard form:
  the publisher requests the resolved string value of each parameter
  through whatever API the `ParameterResource` exposes for consumers.
  FT-002 does not read `Environment` directly, does not open
  `secrets.json`, and does not invoke `dotnet user-secrets` itself —
  it lets Aspire resolve, exactly as ADR-004 §1 requires.
- An `ICoolifyClient` instance (or equivalent, named at implementation
  time) constructed from the resolved `(url, token)` pair. The client
  is owned by the `coolify-api` domain (ADR-002) and exposes a method
  that issues the combined version+auth probe — "one authenticated GET
  against a low-cost authenticated endpoint." The endpoint path itself
  belongs to the client; FT-002 does not name it.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context. Cancellation observed inside
  the configure phase aborts the deploy with the cancellation
  diagnostic already specified by FT-001 §Behaviour §6.

### Outputs

- **On success:** the configure phase exits normally and yields control
  to the `build` phase. The publisher has, by this point, established:
  (1) the token parameter resolved to a non-empty string value, (2)
  Coolify responded `200 OK` to the combined probe, (3) the version
  advertised by that response is at or above ADR-002's
  `SupportedCoolifyVersions` floor. No log line containing the
  literal token value is emitted at any verbosity. No file is written
  under the AppHost directory.
- **On any fail-fast path:** the publisher prints a single diagnostic
  to stderr whose first token (whitespace-delimited) is one of the
  four literal symbols below, exits non-zero, does not enter the
  `build` phase, and has not issued any Coolify call other than the
  single combined probe (and not even that, for the `MISSING` case).
  The four symbols are part of the **observable contract** and are
  matched as literal strings by TC-004 / TC-002 — they are not
  internal exception type names. They are:

  | Symbol                          | Stderr-visible literal           | Trigger                                                              |
  |---------------------------------|----------------------------------|----------------------------------------------------------------------|
  | `E_AUTH_TOKEN_MISSING`          | `E_AUTH_TOKEN_MISSING`           | Token parameter resolved to null or empty string                     |
  | `E_AUTH_TOKEN_INVALID`          | `E_AUTH_TOKEN_INVALID`           | Combined probe returned HTTP `401` or `403`                          |
  | `E_COOLIFY_VERSION_BELOW_FLOOR` | `E_COOLIFY_VERSION_BELOW_FLOOR`  | Combined probe returned `200 OK` but reported version < floor        |
  | `E_COOLIFY_UNREACHABLE`         | `E_COOLIFY_UNREACHABLE`          | Combined probe failed transport-level: `404`, `5xx`, timeout, refused, DNS, TLS |

### State

- **No persistent state on disk** (inherits ADR-003 §4 and FT-001
  invariant I-3). No `coolify-token.cache`, no `.coolify/credentials`,
  no `coolify.lock`, no probe-result cache between invocations.
- **In-memory state is bounded to the configure phase.** The resolved
  token string lives only in the local scope of the configure-phase
  body and in the `Authorization: Bearer …` header set on the
  `HttpClient` owned by the `coolify-api` client. It is **not**
  stored as a field on the publisher, **not** captured in a closure
  that outlives configure, and **not** returned from the configure
  callback to later phases. Downstream phases (`build`, `push`,
  `deploy`, `verify`) receive credentials only through the
  `ICoolifyClient` instance itself, which is the single owner of the
  bearer header for the rest of the deploy.
- **The configure phase exposes one boolean-shaped result** to the
  outer publisher driver: "proceed" or "fail-fast with diagnostic X."
  No probe payload, no version string, no token, no url, is propagated
  in plaintext to subsequent phases. (The version observed in the
  probe response may be passed forward as a structured value for
  later-feature use — e.g. capability flags — but is never required
  by FT-002 itself.)

### Behaviour

The configure phase body executes the following steps in this exact
order. Each step is a hard gate: if it fails, the publisher exits
non-zero with the matching diagnostic and does not run any
subsequent step.

1. **Resolve the token parameter.** Request the resolved value of the
   `token` `ParameterResource` through Aspire's standard parameter-
   resolution API. If the resolved value is `null` or the empty
   string (after trimming surrounding whitespace), fail-fast with
   `E_AUTH_TOKEN_MISSING`. **No round-trip to Coolify is performed
   for this case.** This is the highest-precedence failure: if the
   token is missing, the URL's reachability is not even examined.

2. **Resolve the url parameter.** Request the resolved value of the
   `url` `ParameterResource` (which is *not* a secret parameter per
   ADR-004 §8). If null/empty, treat as `E_COOLIFY_UNREACHABLE`
   (precedence-equivalent to a transport failure: we cannot make the
   probe call without a target). The url value is not redacted in
   diagnostics — it is part of the user-visible identity of the
   target. (A separate ADR may later add url validation; v1 does
   only the null/empty check.)

3. **Construct the `ICoolifyClient`.** Hand the resolved `(url,
   token)` pair to the `coolify-api` domain's client factory. The
   client takes ownership of the token: it sets the
   `Authorization: Bearer …` header on its internal `HttpClient`
   and the resolved token string goes out of FT-002's lexical
   scope.

4. **Issue the combined version + auth probe.** Call the client's
   probe method — "one authenticated GET against a low-cost
   authenticated endpoint." This is a single round-trip. The probe
   either:
   - returns a structured response carrying a Coolify version
     string (success path → continue to step 5), or
   - throws / returns an HTTP-status-tagged failure that the
     configure body classifies as exactly one of:
     - `401` or `403` → fail-fast `E_AUTH_TOKEN_INVALID`,
     - `404`, any `5xx`, request-timeout, connection-refused,
       DNS-resolution-failed, TLS-handshake-failed, or any other
       transport-level fault that prevented a usable response →
       fail-fast `E_COOLIFY_UNREACHABLE`.

5. **Compare the reported version against `SupportedCoolifyVersions`.**
   The floor constant is owned by the `coolify-api` domain (ADR-002).
   If the observed version is strictly below the floor, fail-fast
   `E_COOLIFY_VERSION_BELOW_FLOOR`. The comparison is delegated to
   the client (or to a small SemVer helper inside the
   `coolify-api` domain); FT-002 only consumes the boolean result.

6. **Hand off the `ICoolifyClient` to subsequent phases.** The
   client (which now holds the validated bearer) is the only
   credential channel for `build`, `push`, `deploy`, `verify`. The
   configure phase exits normally.

**Diagnostic content** — every fail-fast stderr message is composed
as:

```
<E_SYMBOL>: <one-line human description>
  parameter: coolify-<name>-token     (for MISSING / INVALID)
  url:       <resolved-url>           (for INVALID / VERSION_BELOW_FLOOR / UNREACHABLE)
  observed:  <observed-version>       (for VERSION_BELOW_FLOOR)
  required:  >= <floor-version>       (for VERSION_BELOW_FLOOR)
  see:       SUPPORTED_COOLIFY_VERSIONS.md   (for VERSION_BELOW_FLOOR)
  remediation:
    dotnet user-secrets set Parameters:coolify-<name>-token <value>
    or set Parameters__coolify_<name>_token=<value>            (for MISSING)
```

The first whitespace-delimited token on the first line is the
literal `E_…` symbol; this is what TC-002 / TC-004 grep for. The
parameter name (`coolify-<name>-token`) is derived from the
`ParameterResource.Name` of the token handle FT-001 captured.

**Cancellation.** If the deploy `CancellationToken` is cancelled
between steps 1–5, the configure phase exits with FT-001's
cancellation diagnostic (not one of the four `E_…` symbols) and
does not enter `build`.

**Precedence rule (load-bearing).** The four diagnostics are
strictly ordered by the step at which they can fire:

```
E_AUTH_TOKEN_MISSING
  > E_AUTH_TOKEN_INVALID
  > E_COOLIFY_VERSION_BELOW_FLOOR
  > E_COOLIFY_UNREACHABLE
```

The `MISSING` case short-circuits before any network I/O; the other
three are distinguishable only after the probe has been attempted
(and `INVALID` and `UNREACHABLE` are mutually exclusive in any single
deploy because they are different classifications of the same probe's
outcome). `VERSION_BELOW_FLOOR` can only fire on a `200 OK` response,
so it cannot collide with `INVALID` or `UNREACHABLE`. The precedence
is therefore not a tie-break rule among simultaneously-possible
outcomes; it is a statement that the steps are ordered and the
earliest applicable failure wins.

### Invariants

- **I-1: configure issues at most one Coolify round-trip per deploy.**
  The version probe and the auth probe are the *same* call. FT-002
  must not issue a second call to "double-check" either dimension.
  (Asserted by intercepting outbound HTTP and counting requests during
  a happy-path configure.)
- **I-2: no Coolify resource is created, modified, or deleted on any
  fail-fast path.** All four `E_…` exits leave the Coolify instance
  byte-identical to its pre-deploy state. (Asserted by Coolify-side
  snapshot diff in TC-004 scenarios 2 & 3 and TC-002 below-floor /
  unreachable scenarios.)
- **I-3: the resolved token string is never logged, echoed, or
  written to disk.** This includes diagnostic messages, exception
  messages, Aspire structured-log fields, the Aspire dashboard
  parameter display, and any captured-and-rethrown exception chain.
  Redaction is achieved by (a) inheriting Aspire's `secret: true`
  parameter redaction for the parameter resource, and (b) never
  including the resolved string in any FT-002-authored message.
  (Asserted by sentinel-grep in TC-004 scenario 4.)
- **I-4: token resolution happens exactly once per deploy.** The
  parameter is resolved at the start of configure; the resolved
  value is handed to the `ICoolifyClient` constructor; FT-002 does
  not re-resolve it later in configure and does not re-resolve it
  for subsequent phases. Subsequent phases reuse the client. (This
  composes with ADR-004's rotation story: a new deploy invocation
  picks up the new value because every invocation resolves freshly;
  within one invocation there is no staleness window.)
- **I-5: the four E_… symbols are stable observable contract.**
  Their spellings (exact uppercase, underscores, no trailing
  punctuation) appear verbatim as the first whitespace-delimited
  token on stderr for the matching failure. Changing any of the
  four symbols is a breaking change to the publisher's CLI
  contract and requires a new ADR.
- **I-6: precedence is honoured.** No code path may issue the probe
  before the token-missing check, may classify a `401/403` as
  `VERSION_BELOW_FLOOR`, may classify a `200 OK` with an unknown
  version field as `UNREACHABLE`, or may classify a transport
  failure as `INVALID`. Each `E_…` symbol corresponds to exactly
  the trigger in the table above; no overlap is permitted.
- **I-7: configure-phase boundary is honoured.** Every fail-fast
  exit happens **inside** the configure phase as observed by
  FT-001's phase-boundary logging — i.e. the deploy log shows
  `configure: enter … configure: exit (failed)` with no `build:
  enter` line. TC-004 §2/§3 asserts the phase boundary.

### Error handling

The four `E_…` diagnostics enumerated above are the only error
paths this feature introduces. Beyond them:

- **Cancellation between steps** → FT-001 cancellation diagnostic
  (not an `E_…` symbol).
- **Bug in the `ICoolifyClient` itself** (e.g. an unexpected
  exception type that doesn't classify cleanly into `INVALID` /
  `UNREACHABLE`) is treated as `E_COOLIFY_UNREACHABLE` with the
  inner exception's `Message` appended (no stack trace, no token
  content). This is the catch-all bucket: it is better to
  conservatively fail-fast on configure than to enter `build` in
  an unknown state. (A future ADR may refine; v1 accepts the
  catch-all.)
- **Aspire parameter-resolution failure** (e.g. the resolver
  throws because of a misconfigured user-secrets store) is
  surfaced as `E_AUTH_TOKEN_MISSING` if it pertains to the token
  parameter, or `E_COOLIFY_UNREACHABLE` if it pertains to the url
  parameter. The diagnostic includes the underlying error's
  `Message` (which Aspire's own redaction has already scrubbed
  for secret parameters).
- **The probe returns `200 OK` but the response body cannot be
  parsed to extract a version string** is treated as
  `E_COOLIFY_UNREACHABLE` (we did not get a usable answer to our
  question), not as `VERSION_BELOW_FLOOR`. This matches ADR-002's
  "could not determine Coolify version" exit and TC-002's
  unreachable scenario.

### Boundaries

- **In scope for FT-002:**
  - reading the `(url, token)` parameter handles captured by FT-001
  - resolving both parameters through Aspire's standard mechanism
  - constructing the `ICoolifyClient` from the resolved pair
  - issuing the single combined version + auth probe via the client
  - classifying the result into success or one of the four `E_…`
    diagnostics, in the precedence order specified
  - writing the four diagnostic messages to stderr with the literal
    symbols, the structured fields above, and zero token content
  - ensuring no Coolify side-effect occurs on any fail-fast path
  - ensuring no persistent on-disk state is written
  - honouring the cancellation token between steps
- **Out of scope for FT-002** (handled elsewhere or deferred):
  - the `WithCoolifyDeploy(...)` extension, publisher registration,
    phase shells, idempotency at the builder level → **FT-001**
  - the `ICoolifyClient` itself, the endpoint path of the probe,
    the `SupportedCoolifyVersions` floor constant, the
    `SUPPORTED_COOLIFY_VERSIONS.md` table, tolerant
    deserialization, conservative serialization →
    **`coolify-api` domain, ADR-002**
  - the `build`, `push`, `deploy`, `verify` phase bodies →
    later features
  - the Aspire-graph walk, resource-to-Coolify-object mapping →
    **ADR-001 / future FT**
  - the image build/push flow → **ADR-005 / future FT**
  - env-var / secrets sync into Coolify → **future FT**
  - the managed dashboard, TypeScript AppHost parity, drift
    detection, multi-AppHost composition → out of v1
  - distinguishing `401` ("token unrecognised") from `403`
    ("token recognised but lacks permission") — both surface as
    `E_AUTH_TOKEN_INVALID` per ADR-004's rationale "one opaque
    error path for auth"

## Out of scope

- **Refresh / rotation flow.** Coolify tokens are opaque bearers with
  no refresh endpoint (ADR-004). FT-002 does nothing to "rotate"
  tokens; it simply resolves whatever value Aspire hands it on the
  current invocation. The user-facing rotation story
  (`dotnet user-secrets set …` + revoke-in-Coolify) is documented in
  ADR-004 §7 and surfaces in FT-002 only via the `MISSING` diagnostic's
  remediation block.
- **Distinguishing `401` from `403`.** Both → `E_AUTH_TOKEN_INVALID`.
  ADR-004 Rationale §"One opaque error path for auth."
- **Per-scope / per-permission validation of the token.** ADR-004
  Consequences anticipates this as a future ADR; v1 only checks "can
  the token reach an authenticated endpoint at all."
- **Caching the probe result across deploys.** Every `aspire deploy`
  invocation runs the probe afresh. There is no `.coolify/probe.cache`
  or in-process static field — this is part of the no-persistent-state
  invariant.
- **Probing more than one endpoint.** I-1: exactly one round-trip per
  configure. If a future feature needs additional configure-phase
  checks (e.g. destination existence), a later FT extends configure
  explicitly; FT-002 does not preemptively add hooks.
- **Validating the url parameter beyond null/empty.** Schema/format
  validation, HTTPS enforcement, port-range checks are deferred. An
  unparseable url surfaces via the probe attempt as
  `E_COOLIFY_UNREACHABLE`.
- **Surfacing the probe's response payload to later phases as a
  parsed Coolify-capabilities object.** FT-002 only needs the
  version string for the floor comparison. Capability-flag plumbing
  is a separate concern.
- **A separate `aspire coolify check` / `aspire coolify whoami`
  command.** The probe runs as part of `aspire deploy`'s configure
  phase only; v1 does not expose it as a standalone CLI verb.
- **Retry / backoff on transport failures.** A single attempt; any
  failure → `E_COOLIFY_UNREACHABLE`. Retries inside configure would
  delay diagnostics and conflict with the "fail-fast" framing.
