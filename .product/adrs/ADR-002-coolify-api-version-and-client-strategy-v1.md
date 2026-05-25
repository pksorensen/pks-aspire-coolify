---
id: ADR-002
title: Coolify API version and client strategy (v1)
status: accepted
features:
- FT-001
- FT-002
- FT-004
- FT-005
- FT-006
- FT-007
- FT-008
- FT-010
- FT-013
- FT-016
supersedes: []
superseded-by: []
domains:
- coolify-api
scope: domain
content-hash: sha256:a53b00138a10f622bd247d21368c8f6b642ec5fc3cbf487e4b1f1c2e3dca781b
---

## Context

`pks-aspire-coolify` translates the Aspire resource graph into Coolify
objects via Coolify's REST API (ADR-001 fixes the mapping; this ADR fixes
the wire). Every other API-touching concern — idempotency state, secret
sync, drift detection, managed-dashboard provisioning, image-pull config —
sits on top of this client. The brief's "Known foundational decisions"
section explicitly carves out three questions:

1. **Which Coolify version do we pin a floor to?** Coolify is on its v4
   series at time of writing, and the v4 REST API is the only target we
   intend to support in v1. Older v3-era APIs are out of scope.
2. **Do we hand-write a thin typed REST wrapper over the endpoints we
   actually call, or do we generate a full client from Coolify's
   OpenAPI specification (NSwag, Kiota, OpenAPI Generator, ...)?**
3. **How do we absorb Coolify's occasionally-breaking API changes**
   without forcing a publisher release every time upstream ships a new
   minor?

These three are entangled: a generated client makes API drift catastrophic
(regeneration churn, broken builds on consumers); a hand-written client
makes drift cheap but puts the burden on us to keep the surface aligned.
The version-floor policy decides which drift we have to absorb in the first
place.

The publisher only touches a small subset of Coolify's API surface:
destinations (read, optional upsert), projects (upsert, get-by-name),
environments (upsert, get-by-name), applications/services/databases
(upsert, get, delete), environment variables (upsert at the appropriate
scope), and deploy actions (trigger, poll status). The full Coolify
OpenAPI surface is an order of magnitude larger — settings, teams, users,
SSH keys, server provisioning, webhooks, notifications, billing — none of
which the publisher will ever call.

## Decision

**v1 of `pks-aspire-coolify` ships a hand-written, thin, typed REST
wrapper over the subset of the Coolify v4 API the publisher actually
calls, with a version probe and tolerant deserialization. Concretely:**

1. **Target Coolify v4 only.** v3 and earlier are unsupported. The
   client speaks v4's REST endpoints (`/api/v1/...`) with bearer-token
   auth.
2. **Pin a minimum supported Coolify version as a source constant.** The
   client repo carries a `SupportedCoolifyVersions` constant (a single
   minimum-version string, e.g. `"4.0.0-beta.NNN"`) and a
   `SUPPORTED_COOLIFY_VERSIONS.md` table documenting which Coolify
   releases have been validated against which publisher releases. The
   concrete floor value is set when the first endpoint is wired and
   raised explicitly in a PR when upstream breakage forces it.
3. **Probe the Coolify version at the `configure` phase of every
   deploy.** Before any other API call, the publisher calls Coolify's
   version endpoint, compares against `SupportedCoolifyVersions`, and:
   - On match or above: proceed.
   - Below floor: fail fast with an actionable error naming the
     observed version, the required floor, and a link to the
     `SUPPORTED_COOLIFY_VERSIONS.md` table.
   - Endpoint missing or unreachable: fail fast with a "could not
     determine Coolify version" error rather than guessing.
4. **Hand-write the typed surface for the endpoints the publisher
   touches.** Each endpoint gets a method on a `CoolifyClient`
   interface, with explicit request and response record types. The
   surface is owned by us, not regenerated. No NSwag, no Kiota, no
   OpenAPI Generator step in the build.
5. **Deserialize tolerantly.** All response DTOs are records with
   nullable properties for everything the publisher does not strictly
   require, JSON deserialization is configured with
   `JsonSerializerOptions { PropertyNameCaseInsensitive = true }` and
   unknown fields are ignored by default (no `JsonUnmappedMemberHandling
   = Disallow`). Non-breaking additive changes upstream therefore do
   not break the client.
6. **Serialize conservatively.** Requests only send the fields the
   publisher actually sets; we do not echo-back unknown fields read from
   GET responses. This prevents a class of "I got 422 because I round-
   tripped a field Coolify now rejects" failures.
7. **Breaking-change handling has one explicit channel.** When upstream
   Coolify ships a breaking change to an endpoint we touch:
   - We raise `SupportedCoolifyVersions` to the new floor in a publisher
     release (semver: minor bump if endpoints we touch changed shape;
     patch bump if only error semantics changed).
   - The `SUPPORTED_COOLIFY_VERSIONS.md` table records the breaking
     change and the publisher release that absorbed it.
   - Older Coolify installs hitting the new client get the
     fail-fast version-probe error, not a confusing 4xx mid-deploy.
   - We do **not** maintain parallel branches per Coolify version.
8. **No runtime feature detection beyond the version probe.** The client
   does not introspect Coolify's capabilities endpoint, does not branch
   on "if version >= X, use endpoint Y else fallback to Z." A single
   minimum-version floor is the only compatibility dimension.
9. **The client lives in a single project**, exposed only via the
   `coolify-api` domain. Other domains (publisher, resource-mapping,
   state, secrets-env) depend on the `CoolifyClient` interface, never on
   `HttpClient` or raw JSON. This keeps the wire concern in one place
   for the (inevitable) day the API changes shape.

## Rationale

- **Hand-written matches the actual surface area we need.** The
  publisher touches roughly a dozen endpoints. A generated client would
  emit hundreds of methods, models, and enums we never call, polluting
  IntelliSense, slowing builds, and obscuring the actual contract
  between publisher and platform. A thin wrapper makes the *real*
  contract — what we send, what we read — legible in one file per
  endpoint group.
- **Tolerant deserialization absorbs the common case of upstream
  change.** The dominant pattern in actively developed PaaS APIs is
  additive, non-breaking change: new optional fields, new endpoints, new
  enum values. Configuring the deserializer to ignore unknown fields
  means 90%+ of upstream Coolify releases require zero work on our side.
- **Version probe absorbs the uncommon case explicitly.** Genuine
  breaking changes do happen; the probe surfaces them as a clear "your
  Coolify is too old / our publisher is too old" error at the start of
  a deploy, instead of as a cryptic 4xx halfway through provisioning
  half the graph. Fail-fast at `configure` aligns with Aspire's pipeline
  phases and produces an actionable diagnostic.
- **A single floor — no per-version branching — is a deliberate
  simplicity guard.** Compatibility matrices are a well-known
  maintenance tarpit. By committing to "one floor, raise it explicitly
  in a PR," we keep the client mentally fits-in-head and force every
  upstream-driven change to be reviewed deliberately.
- **Owning the surface keeps Coolify-specific quirks centralized.**
  Coolify's REST API has minor inconsistencies (envelope shapes that
  vary by endpoint, fields that are sometimes strings and sometimes
  IDs, deploy-action endpoints that return 202 with a job URL). A
  hand-written client localises the workarounds; a generated client
  forces them either into post-generation patches (fragile) or into
  every caller (leaky).
- **Idempotency-by-name (from ADR-001) does not require UUID round-
  trips.** Because ADR-001 commits to deriving Coolify identity from
  AppHost and environment names, the client only needs GET-by-name
  endpoints and POST/PATCH upserts. It does **not** need an exhaustive
  generated model of every Coolify resource type — only the fields
  needed to identify and reconcile what we manage.
- **No build-time codegen step removes a class of contributor friction.**
  A generated client would require either checking generated code into
  the repo (review noise) or making `dotnet build` depend on having the
  Coolify OpenAPI document fetched at build time (network coupling,
  cache-poisoning risk, CI complexity). Hand-written code has none of
  these problems.

## Rejected alternatives

### (a) Generate a full client from Coolify's OpenAPI (NSwag / Kiota / OpenAPI Generator)

Wire NSwag (or Kiota, or OpenAPI Generator) into the build, point it at
the Coolify OpenAPI document, and consume the generated `CoolifyClient`
directly from the publisher.

**Rejected because:** the generated surface would be an order of
magnitude larger than the surface we actually use, and Coolify's
OpenAPI document is itself a moving target whose quality (correctness
of types, completeness of error shapes, accuracy of optional vs.
required) we do not control. Every upstream change — including purely
additive, non-breaking ones — would force a regeneration and a noisy
diff in the repo. Codegen also conflates "the API exists" with "we
support it"; we would have to either prune the generated surface (which
defeats the purpose) or expose endpoints we do not test. Finally, the
generated client's idiomatic shape (often `Task<ApiResponse<T>>` with
strongly-typed exceptions for every documented status) tends to fight
Aspire's `Task`/`HttpRequestException` idioms, requiring an adapter
layer that erodes the supposed savings.

### (b) Hybrid: generate models, hand-write methods

Use codegen to produce the request/response DTO records from the
OpenAPI document, and hand-write the actual HTTP methods on top of
them.

**Rejected because:** it preserves most of the downsides of full
codegen (build-time dependency on the OpenAPI doc, regeneration churn,
inability to deviate from upstream's chosen field names) without
delivering the supposed upside (reduced hand-written code), because
the dozen DTOs the publisher actually needs are *trivial* to hand-write
and very stable in shape. The "save typing" argument does not survive
contact with reality at this surface size. It also makes tolerant
deserialization harder to enforce uniformly: every regeneration is a
chance for someone to accidentally mark a field required.

### (c) Adopt an existing community Coolify .NET SDK

Defer the client question by depending on a third-party Coolify .NET
client package, if one exists, or by translating one of the
community-maintained Node/Python clients.

**Rejected because:** at the time of this ADR there is no
production-grade, actively maintained .NET client for Coolify v4 that
we trust to anchor a publisher on. Even if one existed, taking a
dependency on it would put Coolify-version compatibility, breaking-
change response time, and security-patch cadence outside our control —
unacceptable for a tool whose entire value proposition is "`aspire
deploy` just works." Wrapping a Node/Python client (e.g. via JS-interop
or process-shelling) is strictly worse on every dimension. If a
high-quality community SDK appears later, a future ADR can revisit.

### (d) No version pin, "best effort" floating compatibility

Don't probe Coolify's version at all; just call the endpoints and let
HTTP errors surface to the user.

**Rejected because:** Aspire deploys are multi-step and partially
side-effecting. A version mismatch that surfaces as a 4xx on the
seventh API call leaves Coolify in a half-deployed state and the user
with a cryptic error pointing at the wrong cause ("400 Bad Request on
/api/v1/applications" vs. "your Coolify is older than v4.0.0-X").
Floating compatibility also makes user bug reports unactionable: we
cannot tell whether a failure is our bug, a Coolify regression, or a
version mismatch. The cost of one extra HTTP call at the start of each
deploy is trivial; the diagnostic clarity it buys is enormous.

### (e) Pin to a single exact Coolify version (not a floor)

Hard-pin to one specific Coolify release (e.g. `v4.0.0-beta.418`
exactly) and reject every other version — newer or older — at the
version probe.

**Rejected because:** Coolify users update on their own cadence and
will routinely run a Coolify newer than the one we tested last. An
exact-pin policy would make the publisher unusable for anyone not on
the exact tested release, forcing a publisher patch release every time
upstream ships a Coolify update — even for purely additive,
non-breaking changes. A *minimum* version floor combined with tolerant
deserialization correctly models the actual compatibility relation
(forward-compatible across additive changes, explicitly raised across
breaking ones).

### (f) Generate a client at install time on the consumer's machine

Ship a build step that, when a consumer adds `WithCoolifyDeploy()` to
their AppHost, fetches Coolify's OpenAPI document from their configured
Coolify instance and generates a client tailored to *their* Coolify
version.

**Rejected because:** it puts a network call and a codegen step in the
consumer's restore/build path, which is hostile to CI environments,
offline development, and reproducible builds. It also makes the
publisher's behaviour depend on the OpenAPI document of whatever
Coolify the user happened to point at, defeating any guarantee we make
about "this publisher release behaves the same way for everyone."

## Test coverage

Exit-criteria test (see `TC-002`):

- **Version-probe happy path:** given a Coolify instance reporting a
  version at or above `SupportedCoolifyVersions`, the `configure` phase
  succeeds without surfacing the version probe to the user.
- **Version-probe below-floor:** given a Coolify instance reporting a
  version below the floor, `aspire deploy` exits non-zero before any
  resource is created in Coolify, with an error message naming the
  observed version, the required floor, and pointing at the
  `SUPPORTED_COOLIFY_VERSIONS.md` table.
- **Version-probe unreachable:** given a Coolify instance whose version
  endpoint returns 404 / 5xx / connection refused, `aspire deploy`
  exits non-zero before any resource is created, with an error message
  distinguishing "could not determine Coolify version" from "version too
  old."
- **Tolerant deserialization, additive case:** given a Coolify response
  carrying additional fields not present in our DTOs, deserialization
  succeeds, those fields are ignored, and the publisher proceeds. (No
  `JsonException` is thrown for unknown members.)
- **Conservative serialization:** given a GET response carrying a field
  the publisher did not write, a subsequent PATCH/POST issued by the
  publisher does not echo that field back in the request body.
- **No build-time codegen artefact:** the repo contains no
  `*.g.cs` / `Generated/` directory backed by an OpenAPI document, and
  `dotnet build` succeeds with no network access. This is asserted at
  CI level (a `find`-based check) rather than as a runtime scenario.

## Consequences

- Every endpoint group lives in one hand-maintained file per resource
  type (`ProjectsApi.cs`, `EnvironmentsApi.cs`, `ApplicationsApi.cs`,
  etc.) under the `coolify-api` domain. Adding a new endpoint is a
  reviewed, deliberate change.
- The `SUPPORTED_COOLIFY_VERSIONS.md` table is a load-bearing document
  for consumer support: every PR that touches `SupportedCoolifyVersions`
  must also update the table.
- Future ADRs (idempotency state, drift detection, managed-dashboard
  packaging) can assume "we have a typed client that fails fast on
  version mismatch and tolerates additive change." They do not need to
  carry their own compatibility story.
- If Coolify's API ever undergoes a shape-level overhaul (e.g. a v5
  with substantially different envelope conventions), this ADR is
  superseded by a new one that re-decides hand-write vs. codegen in
  light of the new surface — it does not require an amendment.
- Consumers running Coolify older than the floor get a clear "upgrade
  your Coolify" message instead of a half-deployed graph. This is a
  deliberate UX trade: faster to diagnose, costlier when the user
  cannot upgrade. v1 accepts that trade.
