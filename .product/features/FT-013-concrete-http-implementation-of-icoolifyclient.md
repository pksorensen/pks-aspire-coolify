---
id: FT-013
title: Concrete HTTP implementation of ICoolifyClient
phase: 1
status: complete
depends-on:
- FT-002
adrs:
- ADR-002
- ADR-004
- ADR-003
tests:
- TC-022
- TC-023
- TC-024
- TC-025
- TC-026
domains:
- coolify-api
domains-acknowledged:
  ADR-006: ADR-006 scopes Central Package Management to src/ and tests/ only with examples/ opt-out by placement. FT-013 adds code under src/Aspire.Hosting.Coolify and tests under tests/Aspire.Hosting.Coolify.Tests — both already governed by ADR-006's existing scope rule. No new placement decision is introduced.
  ADR-001: ADR-001 governs the Aspire-graph to Coolify-hierarchy mapping (which Aspire resource becomes which Coolify object). FT-013 is the wire-level HTTP client implementation; mapping decisions are entirely upstream of it (FT-005 and FT-009 own them and consume this client as a black box). The client exposes endpoint-group methods agnostic to which Aspire resource a caller is upserting.
---

## Description

v1 of `pks-aspire-coolify` (FT-001 through FT-012) wired up the entire
publisher pipeline — `WithCoolifyDeploy` extension, the five-phase
publisher skeleton, configure/build/push/deploy/verify phase bodies,
secrets/env-var sync, network wiring, containerisability filter,
managed-dashboard opt-in, TypeScript AppHost parity, and interactive
parameter prompting. Every one of those features depends on an
`ICoolifyClient` to actually talk to Coolify; v1 shipped only a
stub factory (`NotConfiguredCoolifyClientFactory`) that throws the
moment anyone calls a method on it. The end-to-end smoke test against
a real Coolify instance therefore hits `E_COOLIFY_UNREACHABLE` in the
`configure` phase — not because Coolify is unreachable, but because
the publisher never makes the HTTP call.

FT-013 fills that gap. It introduces a concrete HTTP implementation
of the `ICoolifyClient` interface that v1's features already consume
as a black box, and registers it via Aspire's DI in the
`WithCoolifyDeploy` extension so it supersedes the stub factory.

This is a property-only feature in the same shape FT-002 used for the
configure-phase probe: it specifies **what** the client must do
(every endpoint group covered, bearer header on every request, JSON
serialization shape, timeout behaviour, redaction discipline per
ADR-004) and **defers literal endpoint paths and Coolify v4 DTO
field names to implementation time**. The implementer (Pattern D)
discovers the exact REST paths from `coolify.io/docs/api-reference`
or by reading Coolify's OpenAPI document. FT-013 does not name a
single URL path.

The load-bearing ADRs are ADR-002 (hand-written thin REST wrapper,
tolerant deserialization, conservative serialization, version-probe
at configure time) and ADR-004 (bearer token via Aspire secret
parameter, never logged, never persisted).

## Functional Specification

### Inputs

- A resolved `(url, token)` pair, supplied by the `configure` phase
  (FT-002) via the client factory's construction call. The url is a
  non-empty string; the token is a non-empty opaque bearer string.
  FT-013 does not re-validate these — that is FT-002's contract —
  but it does take ownership: the resolved token string is held only
  on the `HttpClient`'s default `Authorization` header and inside
  the client instance's own scope, never on a static field, never
  in an exception message, never in a log line.
- The Aspire DI container in which the publisher runs. The factory
  registered by `WithCoolifyDeploy` is the **real** factory shipped
  by FT-013, superseding the v1 `NotConfiguredCoolifyClientFactory`
  stub. Registration is idempotent — registering twice does not
  produce two clients per deploy.
- The `CancellationToken` propagated from each phase. Every method
  on the client accepts a `CancellationToken` and honours it: a
  cancelled token aborts the in-flight HTTP request and surfaces
  as `OperationCanceledException` (not as a transport-failure
  exception). FT-002's classification of cancellation is unchanged.

### Outputs

- A working `ICoolifyClient` implementation that v1's existing
  feature bodies consume without code changes. The client exposes
  the same method set the interface already declares, covering the
  following endpoint groups (named here by **purpose**, not by URL
  path):

  | Endpoint group        | Consumed by | Purpose                                                                 |
  |-----------------------|-------------|-------------------------------------------------------------------------|
  | `version` probe       | FT-002      | Single authenticated GET; returns Coolify version + 200/401/403/5xx/etc |
  | `destinations`        | FT-005      | List / get-by-name (deploy walk picks the destination from the pair)    |
  | `projects`            | FT-005      | List / get-by-name / upsert                                             |
  | `environments`        | FT-005      | List / get-by-name / upsert (scoped to a project)                       |
  | `services` (apps/dbs) | FT-005      | List / get-by-name / upsert / delete (scoped to an environment)         |
  | `private-registries`  | FT-004      | List / get-by-name / upsert (push-phase credential upsert per ADR-005)  |
  | `deploy-jobs`         | FT-006      | Trigger deploy + poll job-handle until terminal state                   |
  | `env-vars`            | FT-007, 008 | List / upsert / delete at service scope                                 |
  | `applications`        | FT-010      | Upsert / get / delete (managed-dashboard deploy-time upsert)            |

  Each group is one file under the `coolify-api` domain
  (per ADR-002 §Consequences). FT-013 does not prescribe filenames;
  it prescribes that **every interface method declared at the end of
  v1 has a real HTTP-backed body**, and that no method falls back to
  the stub. The stub remains in the codebase only as the
  publisher's "not yet configured" sentinel — once `WithCoolifyDeploy`
  has run, the DI container resolves to the real factory.

- DI wiring: `WithCoolifyDeploy(url, token)` registers the real
  factory in Aspire's service collection. After this call, any
  consumer requesting `ICoolifyClient` (or `ICoolifyClientFactory`,
  per the v1 shape) receives the FT-013 implementation. Consumers
  that never call `WithCoolifyDeploy` continue to see the v1 stub —
  that is unchanged.

### State

- **In-memory only.** The client owns one `HttpClient` instance per
  `(url, token)` pair for the duration of one `aspire deploy`
  invocation. The bearer header is set once at construction. No
  static fields, no process-global cache.
- **No persistent state on disk.** Inherits ADR-003 §4 and ADR-004 §7.
  No token cache, no response cache, no `.coolify/` directory.
- **No connection pooling across deploys.** A new deploy constructs
  a new client. Aspire's `IHttpClientFactory` may be used internally
  for socket-handler pooling, but the `HttpClient` instance carrying
  the bearer header is scoped to one deploy and disposed at the end
  of `verify`.

### Behaviour

The HTTP client implementation must satisfy the following properties
on **every** method call (version probe, destinations, projects,
environments, services, private-registries, deploy-jobs, env-vars,
applications):

1. **Bearer header on every request.** The `Authorization: Bearer <token>`
   header is set on the underlying `HttpClient` such that it is
   present on every outbound request issued by the client, without
   any per-call code having to add it. The token value is taken from
   the `(url, token)` pair passed to the factory at configure time
   (ADR-004 §1, §6).
2. **Base address from the url parameter.** The url resolved by
   FT-002 is used as the `HttpClient.BaseAddress`. Per-endpoint
   methods compose relative paths against it. The url is not echoed
   back into request bodies.
3. **JSON shape per ADR-002.**
   - Deserialization uses `JsonSerializerOptions` with
     `PropertyNameCaseInsensitive = true`, default
     `JsonUnmappedMemberHandling` (i.e. unknown members ignored —
     not `Disallow`), and nullable properties for everything the
     publisher does not strictly require.
   - Serialization omits any field the publisher did not explicitly
     set on the outgoing request object. The client does **not**
     round-trip unknown fields read from a prior GET into a
     subsequent PATCH/POST body.
4. **Timeouts.** Every request has a bounded timeout. The default
   timeout is set on the `HttpClient.Timeout` property (a single
   client-wide value is sufficient for v1; per-call overrides may
   be added later without changing this feature's contract). On
   timeout the request surfaces as a transport-failure exception
   (see §Error handling).
5. **CancellationToken honoured.** Every method accepts a
   `CancellationToken` and passes it to the underlying HTTP call.
   A cancellation surfaces as `OperationCanceledException` (or
   `TaskCanceledException`), not as a transport-failure exception,
   so FT-002's cancellation diagnostic stays distinct from
   `E_COOLIFY_UNREACHABLE`.
6. **Version probe is the canonical single round-trip.** The probe
   method issues one authenticated GET against Coolify's version
   endpoint (whatever path that resolves to under Coolify v4 — the
   implementer chooses), parses the response into a structured
   value carrying at least the Coolify version string, and returns.
   It never retries inside FT-013; retry policy is the caller's
   concern (FT-002 explicitly does not retry).
7. **Idempotent upserts.** For every endpoint group exposing an
   upsert (`projects`, `environments`, `services`, `private-registries`,
   `applications`, `env-vars`), the client method exposes a single
   `Upsert*` operation that the caller can invoke without first
   checking existence. The client may internally do a `GET → POST/PATCH`
   sequence or rely on a Coolify-side upsert endpoint; the choice is
   the implementer's, but the externally observed property is
   `Upsert(x); Upsert(x);` produces the same Coolify-side state as
   `Upsert(x);` alone (the same property FT-005 and ADR-003 already
   require of the orchestration layer).
8. **Deploy-job polling shape.** The `deploy-jobs` group exposes
   (a) a trigger method that returns a job handle, and (b) a
   poll-status method that the caller invokes in a loop. FT-013
   does **not** embed the polling loop itself — the loop, backoff,
   and timeout policy are FT-006's concern.
9. **Redaction discipline (ADR-004 §6, §I-3 of FT-002).** The
   client implementation does not log the token, does not include
   the token in any exception message it constructs, does not
   include the token in any request body it serializes, and does
   not include the token in any response object it returns. If the
   implementation logs HTTP request lines at all (e.g. for
   debugging), it logs them through a channel that strips the
   `Authorization` header. Aspire's built-in `secret: true`
   redaction covers the parameter-resource side; FT-013 must not
   re-introduce a leak on the consumer side.

### Invariants

- **I-1: Bearer header on every outbound request.** No method on
  the client issues an HTTP call without an `Authorization: Bearer …`
  header. Asserted by intercepting outbound HTTP in TC-025 and
  inspecting the header on every captured request.
- **I-2: Token never appears in request bodies.** No JSON request
  body serialized by the client contains the token string as a
  field value or as part of a field value. Asserted by sentinel-grep
  in TC-026.
- **I-3: Token never appears in log lines or exception text.** No
  log entry written by the client and no exception thrown or
  rethrown by the client contains the token string. Asserted by
  sentinel-grep in TC-026 against captured logs and exception
  `ToString()`.
- **I-4: Tolerant deserialization.** A response containing JSON
  fields not modelled on the publisher DTO deserializes without
  throwing `JsonException`. Asserted by TC-024 with a synthetic
  response carrying extra fields.
- **I-5: Conservative serialization.** A request body issued after
  a prior GET contains only fields the publisher explicitly set;
  unknown fields read from the GET are not echoed back. Inherits
  TC-002's existing assertion; FT-013 must not regress it.
- **I-6: Transport failures classify uniformly.** Connection refused,
  DNS failure, TLS handshake failure, request timeout, and any
  `5xx` response surface to the caller as the **same** typed
  exception shape (or the same well-known abstraction), such that
  FT-002's configure-phase classifier can map them to
  `E_COOLIFY_UNREACHABLE` with a single catch. Asserted by TC-023.
- **I-7: Auth failures classify separately.** `401` and `403`
  responses surface as a distinguishable shape from I-6, such that
  FT-002 can map them to `E_AUTH_TOKEN_INVALID`. The two shapes
  are mutually exclusive — a transport failure does not present
  itself as an auth failure or vice versa.
- **I-8: Single client instance per deploy.** Per `(url, token)`
  pair per `aspire deploy` invocation, exactly one `ICoolifyClient`
  is constructed and shared across phases. Asserted by counting
  factory invocations during a happy-path deploy.
- **I-9: Stub supersession is total.** After `WithCoolifyDeploy`
  has registered the real factory in the DI container, no consumer
  resolves to `NotConfiguredCoolifyClientFactory`. Asserted by
  resolving `ICoolifyClientFactory` from the post-`WithCoolifyDeploy`
  DI scope and inspecting the concrete type.

### Error handling

- **HTTP `401` / `403`** → auth-failure exception shape (caller
  classifies as `E_AUTH_TOKEN_INVALID` per ADR-004 §5 and FT-002 §4).
- **HTTP `404`** on a path the publisher believes should exist →
  surfaced as a not-found shape distinct from transport failure;
  callers may treat as "resource does not exist" (for upsert
  pre-checks) or as `E_COOLIFY_UNREACHABLE` (for the version probe,
  per FT-002 §4 and ADR-002 §test coverage). The client itself
  does not collapse these — it returns the structured outcome and
  lets the caller decide.
- **HTTP `5xx`, request timeout, connection refused, DNS failure,
  TLS handshake failure, any other transport-level fault** →
  uniform transport-failure exception shape (caller classifies as
  `E_COOLIFY_UNREACHABLE`).
- **HTTP `200 OK` with a body that cannot be deserialized into the
  expected DTO** (e.g. malformed JSON, missing required field) →
  surfaced as a deserialization-failure shape distinct from
  transport failure. FT-002 already maps this to
  `E_COOLIFY_UNREACHABLE` (per FT-002 §Error handling); FT-013
  must preserve that mapping by not silently swallowing the parse
  error.
- **Cancellation** → `OperationCanceledException` /
  `TaskCanceledException` propagates unchanged. Not classified
  as a transport failure.
- **Token value in exception messages** → forbidden. Any exception
  the client constructs that wraps an underlying HTTP error must
  scrub the `Authorization` header from any captured request
  context before including it in the exception's `Message` or
  inner-exception chain.

### Boundaries

- **In scope for FT-013:**
  - A concrete HTTP-backed `ICoolifyClient` implementation
    covering every endpoint group declared by v1's interface
    (version, destinations, projects, environments, services,
    private-registries, deploy-jobs, env-vars, applications).
  - Bearer header wiring from the resolved token parameter.
  - JSON serialization options per ADR-002 §5–§6 (tolerant
    deserialization, conservative serialization).
  - A bounded request timeout and cancellation propagation.
  - The uniform error-shape classification described above.
  - Registration of the real factory via Aspire's DI inside
    `WithCoolifyDeploy(url, token)`, superseding the v1 stub.
  - Redaction discipline (token never logged, never in bodies,
    never in exception text).
- **Out of scope for FT-013** (deferred to implementer or future FTs):
  - The literal endpoint path strings under `/api/v1/...`. The
    implementer discovers these from `coolify.io/docs/api-reference`
    or Coolify's OpenAPI document. Same property-only shape FT-002
    used for the version-probe endpoint.
  - The Coolify v4 DTO field names. Each endpoint group's request
    and response record types are hand-written at implementation
    time against the live API; FT-013 only constrains their JSON
    options and field-set discipline.
  - Retry / backoff inside the client. Single attempt per call;
    callers (FT-006 specifically) own their own polling/backoff
    loops.
  - Per-call timeout overrides. v1 ships a single client-wide
    `HttpClient.Timeout`. Future FTs may add per-call overrides
    without amending this one.
  - Capability flags / feature detection beyond the version
    string (ADR-002 §8).
  - Caching response payloads between deploys.
  - HTTP request/response logging at trace verbosity. If added
    later, the implementer must preserve I-3 (no token in logs).
  - Updating the `NotConfiguredCoolifyClientFactory` stub itself
    — it stays as the pre-`WithCoolifyDeploy` sentinel.

## Out of scope

- **Generated client from OpenAPI.** ADR-002 §Rejected (a)–(b) and
  §test coverage "no build-time codegen" forbid this. FT-013 is
  hand-written; no NSwag, no Kiota, no OpenAPI Generator.
- **Distinguishing `401` from `403`.** Both surface to the caller
  as the same auth-failure shape; ADR-004 §Rejected and FT-002 §6
  fix this at the publisher contract.
- **A standalone `aspire coolify ping` / `whoami` CLI verb.** The
  version probe is exposed only through the client method consumed
  by FT-002's configure phase.
- **Token rotation flow.** ADR-004 §7: rotation is "edit the
  parameter value, run `aspire deploy` again." FT-013 caches
  nothing.
- **Multi-instance fan-out inside one deploy.** One `(url, token)`
  pair per `WithCoolifyDeploy` call. Multiple instances are handled
  at the AppHost level by multiple `WithCoolifyDeploy` calls
  (ADR-004 §2); each gets its own client.
- **Webhook / notification / billing / team / SSH-key endpoints.**
  ADR-002 §Context lists these as outside the publisher's surface.
  FT-013 does not wire them.
- **Coolify v3 compatibility.** ADR-002 §1: v4 only.
- **Updating `SupportedCoolifyVersions` floor.** That constant
  lives in the `coolify-api` domain (ADR-002 §2) and is raised
  via explicit PR when upstream breakage forces it; FT-013 does
  not pick a new floor.
