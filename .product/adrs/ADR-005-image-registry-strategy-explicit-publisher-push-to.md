---
id: ADR-005
title: Image registry strategy — explicit publisher-push to a developer-chosen registry, with Coolify-side credential upsert (v1)
status: accepted
features:
- FT-001
- FT-003
- FT-004
supersedes: []
superseded-by: []
domains: []
scope: domain
content-hash: sha256:df9e8f2b6e509aa82a7fb2ce0a7906afba83b02a9c10e649dd31b9e1eed7894e
---

## Context

Coolify does not build container images — it pulls them. Every Aspire
resource that becomes a Coolify application/service (per ADR-001) has to
exist as a tagged image in some registry that the Coolify destination
host can reach at deploy time. ADR-003 commits to a fixed five-phase
deploy (`configure → build → push → deploy → verify`); `push` is one of
those phases, and this ADR fixes what it does. ADR-002 fixes the wire,
so any Coolify-side registry configuration we touch lands as one or two
new endpoints on the hand-written client. ADR-004 fixes the auth model
for the *Coolify token* and is explicit that the token is one specific
secret parameter — it says nothing about registry credentials, which
are a separate concern (different secret, different lifecycle, different
audience).

The brief's "Known foundational decisions" entry names three v1
candidates verbatim: **(a)** developer provides registry credentials,
publisher builds + pushes during `push`, Coolify pulls — registry-
agnostic; **(b)** Coolify-side registry mirror (Coolify can be
pre-configured with registries and the publisher targets one of those);
**(c)** hybrid where the publisher discovers Coolify's already-
configured registries and selects one automatically.

Three forces pull on this decision:

1. **The target user is a homelab / side-project developer.** Concretely
   that means two registry shapes dominate in practice: a public
   registry the developer already authenticates to elsewhere
   (`ghcr.io/<github-user>/...` is the modal case for .NET developers
   on GitHub), and a self-hosted registry running inside the homelab
   LAN (`registry:2` on `:5000`, often plain HTTP). Both must work in
   v1; neither can be assumed.
2. **Aspire already has a container-build pipeline.** Aspire's
   publishing infrastructure (used by `aspire-ssh-deploy`, by the
   Docker Compose publisher, by the Azure Container Apps publisher)
   builds container images from project resources in the `build`
   phase and pushes them in the `push` phase. Reinventing that
   pipeline would duplicate work and diverge from the host
   conventions; we should reuse it where the surface allows.
3. **Coolify *can* hold registry credentials on its side.** Coolify
   exposes a Private Registries concept (server-scoped credential
   entries the Coolify agent uses when pulling). For *private*
   images, those credentials have to exist somewhere Coolify can read
   — either the destination's docker daemon login state, or
   Coolify's own private-registry record. If the publisher pushes a
   private image and Coolify cannot pull it, the deploy fails late
   (during `verify`) with a confusing error.

The interaction with ADR-003's `push` phase is load-bearing: whatever
v1 picks must work as a step *inside* that phase, not as a parallel
out-of-band action.

The interaction with ADR-001's lazy-upsert mapping matters too: any
Coolify-side state we create (e.g. a Private Registry record) must be
idempotent-by-name, so re-deploys converge without duplicating
credential entries.

## Decision

**v1 of `pks-aspire-coolify` adopts a registry-agnostic, developer-
chosen, publisher-push model — option (a) — with one small Coolify-
side helper for private-registry credentials. The publisher does not
mirror, proxy, run, or magically select a registry on the developer's
behalf. Concretely:**

1. **Registry coordinates are declared explicitly on
   `WithCoolifyDeploy(...)`.** The extension grows a registry
   parameter triple — an image-prefix (e.g.
   `ghcr.io/pksorensen/myapp`), an optional username parameter, and
   an optional password/token parameter:

   ```csharp
   var coolifyUrl   = builder.AddParameter("coolify-homelab-url");
   var coolifyToken = builder.AddParameter("coolify-homelab-token", secret: true);

   var registryPrefix = builder.AddParameter("registry-prefix");        // ghcr.io/pksorensen/myapp
   var registryUser   = builder.AddParameter("registry-username");      // optional
   var registryPass   = builder.AddParameter("registry-password", secret: true); // optional

   builder.AddDockerComposeEnvironment("env")
          .WithCoolifyDeploy(coolifyUrl, coolifyToken)
          .WithImageRegistry(registryPrefix, registryUser, registryPass);
   ```

   Both username and password are optional and travel as a pair: either
   both are set (authenticated push and authenticated pull) or both are
   unset (anonymous push to a registry that accepts anonymous writes —
   the common homelab `registry:2` case).

2. **Registry credentials are Aspire secret parameters, not the Coolify
   token.** Per ADR-004's separation of audiences: the Coolify token
   authenticates the publisher to Coolify; registry credentials
   authenticate the publisher to the registry *and* (re-used) Coolify
   to the registry. They are distinct values with distinct rotation
   cadences; the publisher must never substitute one for the other.

3. **`build` and `push` reuse Aspire's existing container-image
   pipeline.** The publisher does not invoke `docker build` /
   `docker push` directly. It hooks into the same image-build
   infrastructure the Docker Compose publisher and `aspire-ssh-deploy`
   use, supplies the registry prefix as the image-name root, and
   delegates execution. We own the *configuration* (what prefix, what
   tag scheme, what creds) and the *placement in the phase model*;
   Aspire owns the build itself.

4. **Image tag scheme is deterministic.** Each containerisable
   resource emits one image tag of shape
   `<registry-prefix>/<resource-name>:<apphost-version>`, where
   `<apphost-version>` is derived from the AppHost assembly's
   informational version (Aspire already exposes this). Tags are
   immutable per version; the publisher never reuses a tag for
   different content. A `latest` tag is **not** pushed in v1 — it is
   a known footgun for pull-on-restart semantics and adds nothing the
   versioned tag does not.

5. **When registry credentials are present, the publisher upserts a
   Coolify Private Registry record by `(url, username)`.** During
   `configure` — after the auth-probe from ADR-004 but before any
   image work — the publisher:
   - extracts the registry host from `registry-prefix` (the substring
     before the first `/`);
   - GETs Coolify's private-registries list;
   - if a record matching `(host, username)` exists, PATCHes the
     password if Aspire's parameter value differs from what Coolify
     reports holding (Coolify reports a presence/hash, never the
     value);
   - if no matching record exists, POSTs a new one;
   - tags the record with a stable marker (`managed-by:
     pks-aspire-coolify`) in its name suffix so drift detection
     (ADR-003) can distinguish ours from user-created ones.

   This is the *only* Coolify-side state this ADR creates, and it is
   name-keyed and idempotent in the ADR-003 sense.

6. **When credentials are absent, no Coolify-side registry record is
   created.** The deploy assumes the registry is publicly pullable
   (e.g. a public `ghcr.io` namespace, or an insecure intranet
   `registry:2` that Coolify's destination has in its
   `insecure-registries` list). Coolify pulls anonymously. We document
   this and do not try to detect "Coolify cannot pull anonymously" —
   the failure surfaces at `verify` with a precise Coolify-side error.

7. **No default registry. No magic discovery.** If
   `WithImageRegistry(...)` is not called, the `configure` phase fails
   fast with `E_REGISTRY_NOT_CONFIGURED`, naming the extension method
   and documenting the two canonical recipes:
   - **public homelab path:** `ghcr.io/<user>/<apphost-name>` with a
     PAT for `registry-password`;
   - **LAN path:** `registry.lan:5000/<apphost-name>` with no
     credentials, requiring the Coolify destination's docker daemon to
     list `registry.lan:5000` under `insecure-registries`.

   No default value is invented; no GitHub identity is sniffed; no
   "Coolify, what registries do you have?" auto-pick is performed
   (that is alternative (c), rejected below).

8. **The publisher does not run, spawn, mirror, or proxy a registry.**
   v1 is a publisher, not infrastructure. If a developer wants a local
   `registry:2` they run it themselves (or declare it as an Aspire
   container resource, which is the same thing). The publisher never
   `docker run`s a registry on the destination, never sets up pull-
   through caches, never installs a Coolify-side image mirror.

9. **Registry endpoint URLs are non-secret parameters; credentials are
   secret parameters.** ADR-004's redaction discipline applies to
   `registry-password` exactly as it does to `coolify-homelab-token`:
   never logged, never echoed, resolved through Aspire's parameter
   machinery, no caching beyond the in-memory client. Username and
   prefix are not secret and may appear in logs to aid diagnosis.

10. **The `push` phase fails fast on push errors and does not partial-
    deploy.** If pushing image N of M fails, the publisher exits
    non-zero with a precise diagnostic (which resource, which image
    tag, which registry, which underlying error) before entering the
    `deploy` phase. Coolify is never asked to pull an image that
    failed to push. This composes with ADR-003's verify-gated, non-
    transactional contract: the publisher refuses to half-deploy
    rather than letting Coolify fail to pull halfway through.

## Rationale

- **Registry-agnostic matches the actual user population.** The two
  modal registries for the brief's target user (`ghcr.io` and
  self-hosted `registry:2`) have nothing in common beyond "speaks the
  OCI registry HTTP API." Any model that privileges one over the
  other (Coolify-side mirror, hybrid auto-detect, hardcoded default)
  would either rule the other out or invent an abstraction over both
  that we then have to maintain. The OCI registry API *is* that
  abstraction; we use it as-is.
- **Explicit configuration over magical discovery composes with
  ADR-004.** ADR-004 deliberately rejected ambient `COOLIFY_TOKEN`
  env-vars and global default lookups in favour of declared-on-the-
  call-site parameters. Registry config follows the same shape for the
  same reasons: identity is explicit, multi-instance is naturally
  supported, redaction is inherited from Aspire, and there is no
  fallback chain to debug when the wrong value shows up.
- **Reusing Aspire's build pipeline is non-negotiable.** Aspire owns
  the relationship between project-resource → container-image (base
  images, build context, layer caching, SDK version pinning); a
  parallel implementation would diverge on every Aspire SDK update.
  Hooking into the existing infrastructure means we inherit every
  improvement upstream ships.
- **Coolify-side credential upsert is the smallest helper that makes
  private images work end-to-end.** The alternative is documenting
  "after first deploy, go into the Coolify UI and add your registry
  credentials by hand" — which violates the brief's "just works"
  pitch and reintroduces a manual step on every fresh Coolify
  destination. One name-keyed upsert costs us two endpoints on the
  hand-written client (per ADR-002's "thin wrapper over endpoints we
  actually call") and buys a deploy that works first-try on a new
  Coolify install.
- **`(url, username)` keying for the Private Registry record is the
  honest identity.** Coolify allows multiple records per host (one per
  service account), so `host` alone is not unique. Re-deriving by
  `(host, username)` matches ADR-001's idempotency-by-name discipline
  for a 2-tuple instead of a 1-tuple, with no behaviour change.
- **No `latest` tag is the right v1 trade.** `latest` causes
  surprising pull-on-restart behaviour in Coolify (services restart
  silently when the tag moves), defeats reproducibility, and tempts
  users to skip version bumps. Deterministic per-version tags give
  Coolify's own rollback (and ADR-003's verify-gated failure
  diagnostics) something concrete to point at.
- **No registry default is a deliberate scope guard.** Picking *any*
  default — `ghcr.io/<user>` with auto-detected GitHub identity, an
  in-Coolify auto-spun registry, a `docker.io` namespace — privileges
  one user population over the others, requires identity discovery we
  have no business doing, and creates a dependency on an external
  service the user did not opt into. Failing fast with a precisely-
  worded error and two documented recipes is friendlier than a magic
  default that mysteriously pushes to the wrong place.
- **`E_REGISTRY_NOT_CONFIGURED` mirrors ADR-004's
  `E_AUTH_TOKEN_MISSING`.** Same shape (named extension method,
  canonical remediation forms), same phase (`configure`, before any
  side-effecting work), same redaction discipline. Error-message
  consistency across the publisher is a UX concern this ADR pays into.

## Rejected alternatives

### (a-variant) Registry-agnostic, but with a hardcoded default registry

Same as the chosen decision, but if `WithImageRegistry(...)` is
omitted, default to `ghcr.io/<github-user>/<apphost-name>` after
discovering the GitHub user from `gh auth status`, `git config
user.email`, or a `GITHUB_USER` env-var.

**Rejected because:** identity discovery is heuristic by nature
(`gh auth status` works on dev machines but not in CI; `git config
user.email` is wrong for shared mailbox commits; `GITHUB_USER` is an
unwritten convention). Each fallback layer adds a "why did it push to
*that* namespace?" debugging surface. The decision document already
shows the two canonical recipes; making them mandatory keeps the
mental model honest. If a future ADR wants to add an opt-in default,
it can — but the floor must be "explicit or fail."

### (b) Coolify-side registry mirror as the v1 default

The publisher assumes Coolify is pre-configured with a Private
Registry record and pushes to *that* registry's URL, fetched from
Coolify on first deploy. The developer's only registry-related
configuration is "which named record in Coolify to use."

**Rejected because:** it inverts the failure mode. The brief's user
runs `aspire deploy` against a fresh Coolify install with no
preconfigured registries — option (b) makes that the unhappy path,
requiring the user to set up Coolify-side registry state *before*
they can use the publisher. It also couples our public surface to
Coolify's Private Registry CRUD endpoints (which are minor v4
endpoints, less stable than the project/service core), and forecloses
on registries Coolify *does not* know about (a colleague's private
ghcr.io repo accessed via PAT, where the user already has `docker
login` cached locally but never told Coolify). The chosen decision
*can* upsert a record on Coolify's side when creds are present —
which gives us option (b)'s end-state benefit without making it a
precondition.

### (c) Hybrid: discover Coolify's configured registries and auto-pick one

On first deploy, GET Coolify's registries list, pick a registry by
heuristic (first registered? matching destination's network? marked
"default"?), and push there. If the registry is private, derive
credentials from Coolify's stored values (via an admin-scoped
read endpoint).

**Rejected because:** Coolify does not expose stored registry
passwords via its API (correctly — it should not), so the publisher
cannot push to a Coolify-known registry without the developer
*re-supplying* the credentials anyway. The "discovery" half therefore
buys nothing the developer didn't already have to provide. Worse, the
"auto-pick" heuristic is exactly the kind of magic that produces
"why did it push there?" support tickets. Hybrid here is two bad
ideas reinforcing each other.

### (d) Bypass registries entirely: SCP locally-built images to the Coolify destination and `docker load`

Skip the registry round-trip. The publisher builds images locally,
saves them as tar archives, SCPs them to the Coolify destination,
and runs `docker load` followed by a Coolify deploy action that
references the now-local image.

**Rejected because:** it reproduces `aspire-ssh-deploy`'s SSH-coupled
model inside a tool whose entire premise is "use Coolify's API, not
SSH." It requires the publisher to know the destination's SSH
endpoint (which Coolify *abstracts away* — that is one of Coolify's
selling points), bypasses Coolify's image-management story
(Coolify cannot then re-pull on restart, cannot show the image source
in its UI, cannot use the same image for a rollback), and fails on
multi-host destinations or destinations the publisher cannot SSH to
directly (Coolify Cloud, hardened homelabs). It also competes with
ADR-003's contract that the deploy proceeds entirely through Coolify's
API surface.

### (e) Auto-spin a `registry:2` inside Coolify on first deploy

If no registry is configured, the publisher creates a `registry:2`
service inside the Coolify project (or destination), points its own
push at that registry, and configures Coolify to pull from
`localhost:5000`.

**Rejected because:** it conjures a managed resource the user never
declared in `AppHost.cs`, creating a ghost service in the Coolify UI
whose presence and lifecycle the user did not opt into. It also
silently introduces persistent storage (the registry's image store)
that becomes a backup/upgrade concern. The pattern is "infrastructure
as a side effect of a deploy," which is exactly the kind of magic
ADR-003's no-persistent-state discipline and ADR-001's no-config-
knobs discipline argue against. If a user wants a local registry,
they can declare it as an Aspire container resource — same outcome,
no magic.

### (f) `latest` tag pushed alongside the versioned tag, Coolify pulls `latest`

Push `<prefix>/<resource>:<version>` *and* `<prefix>/<resource>:latest`,
configure Coolify to pull `latest`, and let restart-after-push roll
services to the new image automatically.

**Rejected because:** it pushes side-effect-on-restart semantics into
the system. A Coolify service that restarts for an unrelated reason
(host reboot, OOM, manual restart) would silently jump to whatever
`latest` happens to point at — which may not match the last
`aspire deploy`'s intent. It also makes ADR-003's verify-gated
diagnostic fuzzier ("the service rolled, but to what?"). Per-version
immutable tags are the only model where "the deploy is the only
thing that changes what runs" holds.

### (g) Registry coordinates supplied via Coolify token's metadata / instance config

Treat the registry as a property of the Coolify *instance* rather
than a property of the deploy, fetching it from Coolify on connect.

**Rejected because:** the registry a workload pushes to is a
property of *the workload*, not the platform — two different
AppHosts deploying to the same Coolify can legitimately push to
different registries (one to `ghcr.io/teamA/...`, one to the
homelab `registry:2`). Binding registry identity to Coolify instance
identity collapses that fan-out and forces an unnecessary
coordination problem at the platform level. ADR-004's per-call-site
parameter scoping already gives us the right shape; reuse it.

## Test coverage

Exit-criteria test (see `TC-005`):

- **Push happy path, authenticated:** given an AppHost with two
  containerisable resources and `WithImageRegistry(prefix, user, pass)`
  set against a reachable registry, the `push` phase pushes exactly
  two image tags of shape `<prefix>/<resource>:<apphost-version>`,
  no `latest` tag is pushed, and the registry's contents after
  `push` match exactly the set of tags emitted.
- **Push happy path, anonymous:** with no `registry-username` /
  `registry-password` parameters, against a registry that accepts
  anonymous writes (homelab `registry:2`), the `push` phase
  succeeds and produces the same per-resource tags. No Coolify
  Private Registry record is created.
- **Coolify-side registry upsert (create):** with credentials
  configured and no matching `(host, username)` record on the
  Coolify side, the `configure` phase POSTs exactly one Private
  Registry record, tagged with the `managed-by: pks-aspire-coolify`
  suffix.
- **Coolify-side registry upsert (idempotent):** re-running the
  same deploy issues no new POST and either no PATCH (if the
  password parameter is unchanged) or exactly one PATCH (if it
  changed). No duplicate records appear on the Coolify side.
- **Missing registry config:** with `WithCoolifyDeploy(...)`
  configured but `WithImageRegistry(...)` omitted, `aspire deploy`
  exits non-zero in `configure` before any build, push, or Coolify
  resource is created, with an error matching
  `E_REGISTRY_NOT_CONFIGURED` that names the extension method and
  shows both canonical recipes (the `ghcr.io/...` form and the
  `registry.lan:5000/...` form).
- **Push failure does not advance to deploy:** with a registry URL
  that returns 401/403/connection-refused on push, the `push` phase
  exits non-zero with a diagnostic naming the failing resource,
  image tag, registry host, and underlying error; the `deploy`
  phase is never entered; no Coolify application/service is created
  or updated.
- **Redaction:** during a happy-path deploy with sentinel values for
  `registry-password`, no Aspire log entry, dashboard parameter
  display, exception trace, or push-progress line contains the
  literal password. (Asserted by injecting a sentinel and grepping
  captured output, matching ADR-004's redaction test shape.)
- **No `latest` ever:** under no v1 code path does the publisher
  emit a `<prefix>/<resource>:latest` push. Asserted by inspecting
  registry contents after `push` and by a grep for `:latest` in
  push-phase logs.
- **Reused build pipeline:** the publisher does not directly invoke
  `docker build` / `docker push`; the `build` and `push` phases
  delegate to Aspire's container-image infrastructure (asserted by
  hook / process-tree inspection in the test harness, mirroring
  the pattern `aspire-ssh-deploy`'s test suite uses).

## Consequences

- The `WithCoolifyDeploy(...)` surface gains a companion
  `WithImageRegistry(prefix, [username, password])` extension. Both
  username and password are optional and travel as a pair. This is
  part of the public API and must be mirrored in the TypeScript
  AppHost parity work.
- The hand-written client (ADR-002) gains two endpoints under a new
  `PrivateRegistriesApi.cs`: list, and upsert (POST or PATCH). These
  are name-keyed by `(host, username)` per ADR-001's idempotency
  discipline. `SUPPORTED_COOLIFY_VERSIONS.md` records the minimum
  Coolify version that exposes these endpoints in a usable shape.
- The brief's foundational decision *"image registry strategy"* is
  fully answered here. The brief's related concern *"sensible default
  for the homelab user"* is answered by *documentation + fail-fast
  error*, not by a hardcoded default.
- Downstream ADRs (managed-dashboard packaging in particular)
  inherit "images live at a developer-declared registry; Coolify
  pulls them; private registries have a Coolify-side credential
  record the publisher manages." The managed-dashboard ADR can
  decide separately whether the dashboard image lives at the same
  developer-declared registry or at a public ghcr.io maintained by
  the project — that is a *distribution* decision, distinct from
  this *workload* decision.
- The Coolify-side credential record we create is the *only*
  Coolify-side configuration this ADR introduces. Drift on it
  (e.g. a user editing the password in the Coolify UI) is handled
  per ADR-003: warn-and-overwrite on managed fields (the password
  presence/hash), leave unmanaged fields untouched (the record's
  display name unless we wrote it).
- If a future Coolify version exposes a stored-credential reuse path
  ("push using this Coolify-managed registry's creds") that does not
  require the developer to re-supply the password, a future ADR can
  add it as an *additional* mode — but it does not retroactively
  invalidate the explicit-parameter shape, which remains the
  registry-agnostic floor.
- If Aspire's container-build infrastructure exposes a richer
  extension point for publishers in a future SDK release, the `build`
  and `push` integration here can deepen without re-deciding the
  registry model. The model is the load-bearing decision; the
  integration point is implementation.
