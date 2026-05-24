# pks-aspire-coolify — Project Brief (PRD)

## Vision

`aspire-coolify-deploy` is an Aspire publishing/hosting extension that makes
"deploy this AppHost to my Coolify instance" a one-line developer experience.
It mirrors what `aspire-ssh-deploy` does for raw SSH+Docker-Compose hosts, but
targets [Coolify](https://coolify.io) — a self-hosted PaaS that already models
destinations, projects, environments, and services as first-class concepts.
Where `aspire-ssh-deploy` SCPs a compose file and runs `docker compose up`,
this project translates the Aspire resource graph into Coolify's API objects
so that deploys are managed declaratively by Coolify (with its own UI, logs,
restarts, secrets, and network grouping) rather than as opaque compose runs.
The target user is a .NET developer who already runs Coolify for their
side-projects or homelab and wants `aspire deploy` to "just work" against it.

**v1 framing (locked in during brief review):**

- **One AppHost = one Coolify project.** The AppHost's identity becomes the
  Coolify project; Aspire environments become Coolify environments inside it;
  each Aspire resource becomes an app/service in those environments.
- **Container-only in v1.** Coolify hosts containers, so v1 only targets
  Aspire resources that can be expressed as containers. Resources that don't
  containerise (Azure-native, dev-time emulators, etc.) are skipped with a
  warning rather than hard-failing the deploy.
- **Deploy flow:** ensure the configured destination exists → ensure the
  project exists (create if missing) → for each Aspire environment, ensure a
  Coolify environment exists → for each Aspire resource, upsert it as an
  app/service in the correct environment.
- **Bonus: managed Aspire dashboard.** Mirror Azure Container Apps'
  managed-dashboard pattern: ship a `coolify-aspiredashboard` container that
  bundles the Aspire dashboard with a Coolify-aware resource provider, and
  optionally deploy it into the project so users get a one-click dashboard
  pointing at the running services. The Aspire dashboard already accepts a
  pluggable resource provider; the provider implementation talks to the
  Coolify API to enumerate the project's containers.

## Inspiration

This project is directly inspired by
[`davidfowl/aspire-ssh-deploy`](https://github.com/davidfowl/aspire-ssh-deploy),
which establishes the pattern we want to emulate:

- A single extension method (`WithSshDeploySupport()`) is chained onto an
  Aspire Docker Compose environment in `AppHost.cs`:

  ```csharp
  builder.AddDockerComposeEnvironment("env").WithSshDeploySupport();
  ```

- The extension plugs into Aspire's `aspire deploy` publishing pipeline as a
  multi-phase orchestrator: **configure** (gather SSH endpoint, registry
  creds, target path) → **build** (build container images locally, in
  parallel) → **push** (to the configured registry) → **deploy** (SCP the
  compose manifest to the host and run `docker compose up`) → **verify**
  (health checks + metadata extraction).
- A parallel TypeScript AppHost variant is provided through Aspire's
  generated-modules system (`aspire restore` writes a typed module).
- The repo ships both C# and TypeScript AppHost samples to demonstrate the
  pattern end-to-end.

The pattern we are emulating, in one line: **a single hosting-package
extension method that hooks the Aspire deploy pipeline and translates the
Aspire resource graph into a target platform's native concepts.** The
substitution we make is "raw SSH + compose" → "Coolify API + Coolify's own
project/env/service model".

## Goals

- A single hosting-package extension (working name `AddCoolifyDeploy()` /
  `WithCoolifyDeploy()`) that an Aspire AppHost can chain onto an environment
  or destination resource and have `aspire deploy` push the full graph into a
  named Coolify instance.
- Map an Aspire AppHost cleanly onto Coolify's four-level hierarchy
  (destination → project → environment → service/application/database) so
  a developer's mental model from `AppHost.cs` survives the round-trip.
- First-class secret / environment-variable sync from Aspire parameters into
  Coolify environments.
- Idempotent re-deploys: running `aspire deploy` twice should converge, not
  duplicate projects or services.
- Work against a self-hosted Coolify (HTTPS + API token) with zero Coolify-
  side configuration beyond creating the destination.
- TypeScript AppHost parity, following the same generated-module convention
  used by `aspire-ssh-deploy`.

## Non-goals

- Replacing Coolify's UI: provisioning, server bootstrapping, SSH key
  management, and TLS certs stay in Coolify.
- Becoming a generic PaaS publisher: Kubernetes, Nomad, ECS, Fly, Render are
  explicitly out of scope — those have their own Aspire publishers or are
  fundamentally different models.
- Building or hosting a container registry: image push uses whatever registry
  the developer / Coolify already trusts.
- Managing Coolify itself (upgrades, backups, multi-tenant admin).
- Long-running runtime control plane: we are a publisher, not an operator.
  Once Coolify owns the resources, runtime concerns (restart, scale, logs)
  belong to Coolify.
- **Non-container Aspire resources in v1.** Coolify is a container host;
  resources that don't have a container representation (Azure SDK resources,
  in-process emulators, raw `Parameter`-only resources without a runtime)
  are skip-with-warning, not a deploy failure. Pluggable mapping for these
  is post-v1.
- **Multiple AppHosts into one Coolify project.** v1 keeps the
  one-AppHost-one-project invariant. Multi-AppHost composition is post-v1.

## Domains

One-line description of each candidate domain. Names and exact set will be
finalised during `product author` discovery.

- `coolify-api` — typed HTTP client over Coolify's REST API (destinations,
  projects, environments, services, applications, databases, env vars,
  deploy actions).
- `aspire-publisher` — the Aspire publishing-pipeline hook itself: the
  `WithCoolifyDeploy()` extension, the multi-phase publisher implementation,
  and integration with `aspire deploy`.
- `resource-mapping` — translation layer between Aspire's resource graph
  (projects, containers, parameters, references) and Coolify's
  destination/project/environment/service hierarchy.
- `auth` — Coolify API-token discovery, storage, and per-instance
  configuration (env var, user-secrets, Aspire parameter binding).
- `state` — local idempotency state: what was deployed last time, what UUIDs
  Coolify assigned to which Aspire resource, so re-deploys converge.
- `image-flow` — building container images locally and pushing to whichever
  registry Coolify will pull from (delegates to existing Aspire compose-build
  where possible).
- `secrets-env` — flowing Aspire parameters and connection strings into
  Coolify environment-variable scope without leaking them to logs.
- `apphost-ts` — TypeScript AppHost parity via the `aspire restore`
  generated-module convention.
- `managed-dashboard` — the optional `coolify-aspiredashboard` container
  (Aspire dashboard + Coolify-aware resource provider) and the publisher
  logic that opts it into the project.

(Final set will be refined during `product author feature` discovery — some of
these may collapse into one another, or split further as the Coolify API
surface is mapped in detail.)

## Sketched capability areas

These are the rough shapes the feature graph will grow into. They are
deliberately not numbered — `product author` will name them, decompose them,
and assign FT-XXX during its clarifying-question step.

**Coolify destination/project/environment/service mapping.** Decide how an
Aspire AppHost name and environment name map onto a Coolify project +
environment, and how each Aspire resource (project, container, database)
becomes a Coolify service, application, or database. The Coolify destination
is likely chosen by config rather than derived.

**Container image build + push flow.** Reuse Aspire's existing container-
image build infrastructure where possible; ensure the resulting images land
in a registry Coolify can pull from. Configurable registry endpoint and
auth; sensible defaults for "developer + Coolify on the same LAN".

**Secret / environment-variable sync.** Translate Aspire parameters,
connection strings, and resource references into Coolify environment
variables at the right scope (project, environment, or service), masking
their values in any pipeline output.

**Service-to-service network wiring inside a Coolify destination.** Coolify
groups services by destination's network; Aspire's `WithReference()` edges
need to become DNS / env-var wiring that resolves inside that network. The
Aspire reference semantics ("service A depends on service B") must produce
the right env vars on A pointing at B's Coolify-internal hostname.

**Incremental deploy / drift detection.** Re-running `aspire deploy` should
diff the desired graph against what Coolify reports it currently has and
apply only the delta. Includes detecting out-of-band edits made in the
Coolify UI and deciding the policy (warn? overwrite? refuse?).

**Rollback / failed-deploy recovery.** When a deploy partway through a
multi-service graph fails (push failed, Coolify API returned 500, health
check timed out), the publisher needs a deterministic recovery story:
abort and leave Coolify untouched, abort and rollback partial state, or
mark the deploy failed and surface it to `aspire deploy`'s exit code.

**TypeScript AppHost parity.** Generated module emitted by `aspire restore`
mirroring the C# `WithCoolifyDeploy()` surface, following the same pattern
`aspire-ssh-deploy` uses.

**Managed Aspire dashboard inside the project.** Optional opt-in
(`WithManagedDashboard()`-style) that, on deploy, ensures a
`coolify-aspiredashboard` container exists in the same Coolify project as
the workload. The container bundles the upstream Aspire dashboard with a
Coolify resource-provider plugin so it lists the project's running
containers, logs, and (where possible) traces. Conceptually parallel to
Azure Container Apps' managed Aspire dashboard. Includes: building/
publishing the dashboard image, wiring its env vars (Coolify API base +
token, project UUID), and surfacing its URL back to the deploying
developer.

## Known foundational decisions

These are the load-bearing decisions that the project will need to make
early. Each will become its own ADR via `product author adr`.

**Aspire-graph → Coolify-hierarchy mapping.** (Settled by brief review,
will still be authored as an ADR to record rationale and rejected
alternatives.) **v1 convention: one AppHost = one Coolify project; each
Aspire environment = one Coolify environment inside that project; each
containerisable Aspire resource = one app/service inside the appropriate
environment.** The destination is chosen by config and assumed to exist (or
upserted lazily). Non-containerisable resources skip-with-warning. The ADR
still needs to capture the rejected alternatives (developer-pick mapping,
multi-AppHost-per-project, one-environment-per-AppHost) and the rationale.

**Coolify API version and surface.** Coolify ships REST endpoints and is on
v4 at time of writing; we must pin a minimum version, decide whether to
generate a client from their OpenAPI or hand-write a thin wrapper, and decide
how to handle their (occasionally breaking) API changes.

**Imperative vs. declarative deploy model.** Two valid shapes: (a)
imperative — the publisher calls Coolify endpoints in sequence, like a
script; (b) declarative — the publisher builds a desired-state document,
diffs against Coolify, and submits only the changes. Aspire's deploy pipeline
is imperative, but Coolify's own model is more declarative — this choice
affects idempotency, drift detection, and rollback.

**Auth model.** Coolify uses bearer API tokens. Decide where the token lives
(Aspire parameter? user-secrets? env var? Aspire `AddParameter` with
secret=true?), how multi-instance setups are addressed (one developer
deploying to multiple Coolify hosts), and how the token rotates.

**Image registry strategy.** Coolify needs to pull images from somewhere.
Options: developer provides registry creds (we just push), Coolify-side
registry (developer points us at theirs), or hybrid. The chosen default
will heavily influence the first-run experience.

**Idempotency state location.** To converge re-deploys we need to remember
the UUIDs Coolify assigned to each Aspire resource on the previous deploy.
Decide: store in the AppHost directory (committed? gitignored?), in Aspire's
publishing manifest, or always re-derive from Coolify by name lookup.

**Managed-dashboard packaging strategy.** How is the
`coolify-aspiredashboard` image built and distributed? Options: (a) we
publish it to a public registry (ghcr.io/pksorensen/...) and the deploy
just references it; (b) we build it locally during `aspire deploy` and
push to whatever registry the workload uses; (c) we publish a base image
and let users layer in their own resource provider. Also: how does the
bundled Aspire-dashboard resource provider authenticate to Coolify
(reuse the deploy token? separate scoped token? OIDC?), and what's the
upgrade story when upstream Aspire dashboard releases a new version?

## Open questions for the authoring session

Things we deliberately leave unanswered for `product author` discovery:

- Does `WithCoolifyDeploy()` attach to a Docker-Compose environment (like
  `aspire-ssh-deploy`) or to a new Coolify-flavoured environment resource?
- How does an Aspire resource's domain/hostname surface in Coolify — do we
  pre-create FQDNs, leave it to Coolify's auto-domain feature, or take
  hostnames from Aspire endpoint config?
- Are Coolify "services" (one-click apps) ever a target, or do we only
  produce "applications" (git/docker/compose-deployed)?
- How do we handle Aspire resources Coolify doesn't model natively (e.g.
  Azure-specific resources, dev-time emulators)? Skip-with-warning,
  hard-error, or pluggable mapping?
- What does verification look like — call Coolify's health endpoint per
  service, or wait for Coolify's own deploy job to report success?
- Multi-environment workflow: is `aspire deploy --environment Staging` a
  first-class concept that maps to a Coolify environment of the same name?
- Do we follow `aspire-ssh-deploy`'s exact phase decomposition
  (configure / build / push / deploy / verify), or does the Coolify model
  collapse some phases (e.g. Coolify pulls and runs, so "deploy" becomes
  "tell Coolify to redeploy")?
