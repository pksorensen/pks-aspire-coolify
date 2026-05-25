---
id: ADR-004
title: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
status: accepted
features:
- FT-001
- FT-002
- FT-003
- FT-004
- FT-005
- FT-007
- FT-008
- FT-009
- FT-010
- FT-011
- FT-012
- FT-013
supersedes: []
superseded-by: []
domains: []
scope: cross-cutting
content-hash: sha256:92bdd96218e081088fd6a9d2e9b6bd92d856c92f0422c904885ae25e56ef9f86
---

## Context

Coolify authenticates every REST call with a bearer **API token** issued from
the Coolify UI (Settings → API Tokens). ADR-002 fixes the wire — a thin
hand-written client over Coolify v4 — and ADR-003 fixes the orchestration —
an imperative `configure → build → push → deploy → verify` walk where the
client is invoked per-step. Neither says where the token comes from, who
owns it, or how the publisher resolves it at runtime.

The brief's "Known foundational decisions" entry lists the question
verbatim: *"Decide where the token lives (Aspire parameter? user-secrets?
env var? Aspire `AddParameter` with secret=true?), how multi-instance
setups are addressed (one developer deploying to multiple Coolify hosts),
and how the token rotates."*

Three properties have to hold simultaneously for v1:

1. **Per-instance scoping.** A single developer can plausibly run two or
   three Coolify instances (a homelab box, a VPS, a friend's lab) and want
   `aspire deploy --environment X` to target whichever is configured on the
   relevant `WithCoolifyDeploy()` call. The token surface must therefore be
   *per-deploy-target*, not a global ambient setting.
2. **Composes with Aspire's existing secret story.** Aspire AppHosts
   already have a first-class notion of secret parameters
   (`builder.AddParameter("name", secret: true)`) that resolves from
   `dotnet user-secrets` in dev, from environment variables / Azure
   Key Vault / external sources in published environments, and that
   participates in the resource graph (so the publisher can declare a
   dependency on it and Aspire knows to redact it in logs, telemetry, and
   dashboards). Reinventing a separate Coolify-flavoured secret channel
   would duplicate this machinery and bypass Aspire's redaction.
3. **No silent state on disk.** ADR-003 commits to *no persistent publisher
   state*; the auth model must not reintroduce a `.coolify/token` file, a
   `coolify.lock`, or anything else that tempts a user to commit a secret.

Coolify tokens are **opaque bearer strings**, long-lived until manually
revoked, with no refresh flow, no OIDC handshake, and no scope/audience
claim the publisher can validate offline. Rotation is "the user generates
a new one in Coolify and revokes the old one" — there is no programmatic
rotation API to call. Whatever channel we pick has to make
"replace the value" trivial.

## Decision

**v1 of `pks-aspire-coolify` resolves the Coolify API token exclusively
through Aspire's native secret-parameter mechanism, declared per
`WithCoolifyDeploy()` call, and consumed by the publisher at the start of
the `configure` phase. Concretely:**

1. **Token is an Aspire secret parameter.** Each `WithCoolifyDeploy(...)`
   invocation takes an `IResourceBuilder<ParameterResource>` for the
   token (created via `builder.AddParameter("coolify-<name>-token",
   secret: true)`). The publisher never reads `Environment` directly,
   never opens `secrets.json`, never calls `dotnet user-secrets` itself.
   It depends on the parameter resource and lets Aspire resolve it.

   ```csharp
   var token = builder.AddParameter("coolify-homelab-token", secret: true);
   var url   = builder.AddParameter("coolify-homelab-url"); // not secret

   builder.AddDockerComposeEnvironment("env")
          .WithCoolifyDeploy(url, token);
   ```

2. **Per-instance multiplicity is achieved by declaring multiple
   parameter pairs.** A developer with two Coolify instances declares
   two `(url, token)` parameter pairs and attaches a separate
   `WithCoolifyDeploy(...)` call (or a separate environment) per
   instance. There is no global `COOLIFY_TOKEN`, no
   "default instance" lookup, and no fallback chain. Identity is
   explicit at the call site.

3. **Local-developer source-of-truth is `dotnet user-secrets`.** This is
   the path Aspire already documents and tools (Rider / VS / VS Code)
   already integrate. The AppHost project gets a `UserSecretsId` (Aspire
   templates already do this), and the developer runs:

   ```bash
   dotnet user-secrets set "Parameters:coolify-homelab-token" "<token>"
   dotnet user-secrets set "Parameters:coolify-homelab-url"   "https://coolify.lan"
   ```

   We document this pattern; we do not implement it ourselves.

4. **CI / published source-of-truth is environment variables**, also via
   Aspire's existing parameter resolution. The standard mapping
   (`Parameters__coolify_homelab_token` → the parameter value) is
   Aspire's, not ours. The publisher inherits it for free by being a
   well-behaved parameter consumer.

5. **The `configure` phase resolves and validates the token.** Before
   ADR-002's version probe, the publisher:
   - resolves the `(url, token)` parameter pair through Aspire's
     standard mechanism;
   - **fails fast with E_AUTH_TOKEN_MISSING** if the token parameter is
     unset, naming the parameter (e.g. `coolify-homelab-token`) and the
     two canonical ways to set it
     (`dotnet user-secrets set Parameters:coolify-homelab-token ...`,
     or the `Parameters__coolify_homelab_token` env-var);
   - **issues a single authenticated probe** (Coolify's `GET /api/v1/version`
     with the bearer token) and **fails fast with E_AUTH_TOKEN_INVALID**
     on `401`/`403`, naming the URL and the parameter without ever
     echoing the token value.

   The version-probe and the auth-probe are the **same call** — one
   round-trip validates both compatibility (ADR-002) and credentials.

6. **The token is never logged, never written to disk, never echoed.**
   The publisher relies on Aspire's built-in redaction for
   `secret: true` parameters (which covers logs, dashboards, and the
   Aspire structured-log pipeline) and adds no logging of its own that
   could leak the value. The thin client (ADR-002) sets the
   `Authorization: Bearer …` header from the resolved parameter and
   does not retain the value beyond the `HttpClient` it owns.

7. **Rotation is "change the parameter value."** Because the parameter
   is resolved from `user-secrets` / env-var on every invocation, the
   user rotates by:
   - generating a new token in the Coolify UI,
   - `dotnet user-secrets set Parameters:coolify-homelab-token <new>`
     (or updating the CI secret),
   - revoking the old token in Coolify.

   The publisher caches nothing across runs; the next `aspire deploy`
   picks up the new value with no extra step. There is no
   token-rotation API on Coolify's side for the publisher to call, and
   we deliberately do not invent one.

8. **One `(url, token)` per `WithCoolifyDeploy(...)` call.** Both halves
   are required and travel together. The url is **not** a secret
   parameter; the token is. The publisher requires them as a pair so
   that no implicit cross-wiring (e.g. "use the homelab token against
   the VPS url") can occur from parameter-name collision.

## Rationale

- **Aspire's `AddParameter(secret: true)` is exactly the right shape.**
  It already gives us per-AppHost scoping, per-environment override,
  user-secrets in dev, env-vars in CI, Key Vault integration when
  needed, redaction in logs, and a typed handle the publisher can
  declare a dependency on. Every alternative listed below is some
  flavour of "reinvent this with worse defaults."
- **Per-instance scoping falls out of the parameter resource model.**
  Two `AddParameter` calls give two parameter resources give two
  independent secret values. Multi-instance support is not a feature
  we have to build; it is a property we inherit by not collapsing
  identity at the publisher layer.
- **`configure`-phase auth-probe matches ADR-003.** ADR-003 makes
  `configure` the place where prerequisites are checked before any
  side-effecting work begins. ADR-002 puts the version probe there.
  Auth is the same shape of prerequisite — cheap to check, ruinous to
  get wrong halfway through — so it goes in the same place, on the
  same round-trip.
- **No persistent state composes with ADR-003.** The publisher reads
  the token through Aspire, holds it in memory for the duration of one
  deploy, and forgets it. There is no `coolify-token.cache`, no
  encrypted blob in the AppHost directory, nothing to gitignore,
  nothing to leak.
- **Rotation-by-parameter-edit is honest.** Coolify tokens have no
  rotation protocol; pretending the publisher can "rotate" them would
  invite a misleading feature. Documenting that rotation is
  `user-secrets set` (or CI-secret update) + revoke-old-in-Coolify
  matches the actual capability of the platform.
- **One opaque error path for auth.** Coolify returns `401`/`403` for
  both "token unrecognised" and "token recognised but lacks
  permission." v1 does not try to distinguish — both surface as
  E_AUTH_TOKEN_INVALID with the same remediation ("regenerate the
  token in Coolify with API access"). When upstream gives us a finer
  signal, a future ADR can refine.
- **`(url, token)` as a required pair prevents accidental
  cross-wiring.** Naming conventions slip; if a user has parameters
  `coolify-homelab-url`, `coolify-homelab-token`, `coolify-vps-url`,
  `coolify-vps-token` and `WithCoolifyDeploy` accepted them by string
  name, a typo could send the homelab token to the VPS url. Requiring
  the typed parameter handles at the call site makes that class of
  mistake a compile error.

## Rejected alternatives

### (a) Single global `COOLIFY_TOKEN` environment variable

Read the token from a fixed env-var name (`COOLIFY_TOKEN`, optionally
paired with `COOLIFY_URL`) at publisher startup. No Aspire parameter
involvement.

**Rejected because:** it forecloses on the per-instance use case the
brief calls out (one developer, multiple Coolify hosts) — a single
process-wide env-var cannot encode "homelab token vs. VPS token vs.
friend's lab token" without inventing a naming convention that just
re-implements parameters, worse. It also bypasses Aspire's redaction
and parameter graph (so the token would not be masked in Aspire logs,
not surfaced in the Aspire dashboard as a managed input, and not
overridable per `aspire deploy --environment`). And it conditions
users to put secrets in `.envrc` / shell rc files, which is exactly
the leak vector `dotnet user-secrets` was created to remove.

### (b) Dedicated config file under the AppHost directory

A `coolify.toml` / `.coolify/credentials` file alongside `AppHost.cs`,
read directly by the publisher, holding `{url, token}` per named
instance.

**Rejected because:** it reintroduces persistent publisher state (in
direct contradiction to ADR-003), creates a file users will be
tempted to commit (and `git secrets`-style scanners will then have
to learn about), duplicates the user-secrets storage Aspire already
uses, and bypasses Aspire's parameter graph entirely. It also forces
the publisher to invent file-format versioning, schema migration, and
locking semantics — none of which we want to own.

### (c) OS keyring (libsecret / macOS Keychain / Windows DPAPI)

Resolve the token through a cross-platform secret-store abstraction,
prompting the user via OS-native UI on first use and persisting in the
OS keyring thereafter.

**Rejected because:** headless CI breaks immediately (no keyring
daemon, no interactive prompt), Linux servers in containers break
(no D-Bus / libsecret), and the dependency surface (a keyring crate
or P/Invoke per OS) is disproportionate to the value delivered.
`dotnet user-secrets` already solves the local-dev case with no
cross-platform pain, and CI is correctly served by env-vars; the
keyring path serves a niche neither audience asked for. If a future
user demands keyring storage, they can wire it under their own
parameter resolver — but it is not v1's job.

### (d) Hand-rolled `dotnet user-secrets` reader inside the publisher

Skip Aspire's parameter mechanism but read the same
`secrets.json` file directly, using a fixed key naming convention the
publisher owns.

**Rejected because:** it gets the storage right but bypasses every
other property `AddParameter(secret: true)` provides — no
participation in the resource graph, no Aspire-managed redaction in
logs/dashboard, no override-per-environment, no CI env-var fallback
without reimplementing it. It is "the worst of both worlds": tightly
coupled to Aspire's storage path *and* responsible for re-implementing
Aspire's resolution semantics. Use the parameter API or do not, but
do not split the difference.

### (e) Hardcoded token in `AppHost.cs` (or `appsettings.Development.json`)

Let the developer paste the token literal into source / settings JSON.

**Rejected because:** obvious. Listed only because every authoring
session should explicitly reject the literal-secret-in-source path so
no reviewer ever has to ask "what about just putting it in the file?"

### (f) Coolify-side OAuth / OIDC flow

Replace bearer tokens with an OAuth device-code or OIDC client-
credentials flow, with the publisher acting as an OAuth client.

**Rejected because:** Coolify v4 does not expose an OAuth/OIDC server
for its REST API. There is no flow to participate in. If Coolify ever
ships one, a future ADR supersedes this one.

## Test coverage

Exit-criteria test (see `TC-004`):

- **Happy path:** given a Coolify instance meeting ADR-002's version
  floor and an AppHost declaring
  `builder.AddParameter("coolify-homelab-token", secret: true)` +
  `builder.AddParameter("coolify-homelab-url")` with both values set in
  `dotnet user-secrets`, `aspire deploy` proceeds through `configure`
  without prompting and without writing any file under the AppHost
  directory.
- **Missing token:** with the url parameter set but the token
  parameter unset (no user-secret, no env-var), `aspire deploy` exits
  non-zero in `configure` before any Coolify resource is created, with
  an error matching `E_AUTH_TOKEN_MISSING` that names the parameter
  (`coolify-homelab-token`) and shows both remediation forms
  (the `dotnet user-secrets set` form and the
  `Parameters__coolify_homelab_token` env-var form).
- **Invalid token:** with both parameters set but the token value
  rejected by Coolify (`401`/`403` on `GET /api/v1/version`),
  `aspire deploy` exits non-zero in `configure` before any Coolify
  resource is created, with an error matching `E_AUTH_TOKEN_INVALID`
  that names the configured url and the parameter name — and that
  **does not** echo any portion of the token value into stdout, stderr,
  Aspire logs, or telemetry.
- **Redaction:** during a happy-path deploy, no Aspire log entry, no
  Aspire dashboard parameter display, and no thrown-exception message
  contains the literal token string. (Asserted by injecting a sentinel
  token value and grepping captured logs / exception traces.)
- **No persistent state:** after a successful deploy, the AppHost
  directory contains no new file written by the publisher relating to
  auth (no `coolify-token.cache`, no `.coolify/credentials`, no
  `coolify.lock`). The next deploy resolves the parameter freshly.
- **Multi-instance isolation:** an AppHost declaring two
  `WithCoolifyDeploy(...)` targets with distinct
  `(url, token)` parameter pairs resolves each pair independently;
  swapping the value of `coolify-homelab-token` does not affect a
  deploy targeting the `coolify-vps-*` pair, and vice versa.
- **Rotation:** after a successful deploy, replacing the token value
  via `dotnet user-secrets set Parameters:coolify-homelab-token <new>`
  and re-running `aspire deploy` succeeds without any other publisher
  action; replacing it with a value Coolify rejects fails as the
  "invalid token" case above.

## Consequences

- The `WithCoolifyDeploy(...)` extension's signature carries
  `IResourceBuilder<ParameterResource>` for both url and token. This is
  part of the public API surface and will be exercised by the
  TypeScript AppHost parity work; the generated TS module must surface
  the same two-parameter shape.
- The brief's foundational decision *"how the token rotates"* collapses
  to *"edit the parameter value; the publisher caches nothing."* No
  separate rotation ADR is required.
- Downstream ADRs (secrets-env sync, managed-dashboard auth) inherit
  "the deploy-time token is an Aspire secret parameter we already hold
  in memory during `configure`." The managed-dashboard ADR will still
  have to decide whether the dashboard reuses the same token at runtime
  or gets its own — that is a separate decision, but it starts from
  this one.
- Documentation must show the canonical `dotnet user-secrets set
  Parameters:...` invocation and the CI `Parameters__...` env-var
  shape, side-by-side, in the README and the first-run guide. The
  publisher's error messages already point at both forms so users who
  skip the docs still land on the right remediation.
- If Coolify ever ships per-scope tokens (read-only / deploy-only /
  admin), a future ADR can layer scope validation onto the
  `configure`-phase auth-probe without changing where the token lives.
- If Coolify ever ships OAuth/OIDC for its API, a superseding ADR
  reopens the storage question — opaque bearer tokens and OAuth access
  tokens have meaningfully different lifecycle properties.
