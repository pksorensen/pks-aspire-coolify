---
id: ADR-001
title: Aspire-graph to Coolify-hierarchy mapping (v1)
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
supersedes: []
superseded-by: []
domains: []
scope: cross-cutting
content-hash: sha256:d6b27e3dc2c8863f0a3b74afd8662c99dafcf7ba40d54228f4495963168c91b3
---

## Context

`pks-aspire-coolify` is an Aspire publishing/hosting extension that takes the
resource graph produced by an Aspire AppHost and pushes it into a self-hosted
[Coolify](https://coolify.io) instance via Coolify's REST API. Coolify models
the world as a four-level hierarchy:

```
destination  →  project  →  environment  →  service / application / database
```

Aspire models the world as:

```
AppHost (DistributedApplication)
   └── environment(s) (e.g. Development, Staging, Production)
         └── resources (projects, containers, parameters, references, ...)
```

The two shapes are structurally similar but not identical, and every other
decision in the project (idempotency state, secret scoping, network wiring,
managed-dashboard placement, drift detection, naming collisions) depends on
how we collapse one onto the other. Picking the mapping is the load-bearing
foundational decision: it locks the mental model a developer keeps in their
head when they read `AppHost.cs` and then opens the Coolify UI.

The brief (`docs/brief.md`, "v1 framing" and "Known foundational decisions")
settled the shape during brief review. This ADR exists to record the
rationale and rejected alternatives, not to relitigate the choice.

## Decision

**v1 of `pks-aspire-coolify` adopts the following mapping convention,
without configuration knobs:**

1. **One AppHost = one Coolify project.** The Aspire AppHost's identity
   (its `DistributedApplication` builder name, i.e. the AppHost project /
   assembly name) is the canonical identity of the Coolify project. The
   publisher upserts a Coolify project with that name on first deploy and
   reuses it on every subsequent deploy.
2. **Each Aspire environment = one Coolify environment inside that project,
   materialised lazily.** The Aspire environment name (`Development`,
   `Staging`, `Production`, or whatever `aspire deploy --environment X`
   resolves to) becomes a Coolify environment of the same name inside the
   AppHost's project. **Only the environment being deployed is upserted on
   each invocation** — sibling environments that the AppHost declares but
   that the current deploy is not targeting are **not** pre-created. Each
   environment first comes into existence on the deploy that targets it,
   and subsequent deploys to the same environment upsert it in place.
3. **Each containerisable Aspire resource = one app/service in the
   appropriate environment.** Project resources, container resources, and
   database resources that have a container representation become Coolify
   applications / services / databases in the environment matching the
   active deploy.
4. **Destination is chosen by config, not derived.** The publisher takes the
   Coolify destination (server + network) as configuration on the
   `WithCoolifyDeploy()` extension. The destination is assumed to exist; if
   absent, the publisher upserts it lazily where Coolify's API allows, or
   fails with an actionable error otherwise. The destination is **not**
   inferred from the AppHost graph.
5. **Non-containerisable resources skip-with-warning.** Aspire resources
   that have no container representation in v1 (Azure-native SDK resources,
   in-process dev emulators, parameter-only pseudo-resources with no runtime,
   etc.) are emitted as warnings in the deploy output and skipped. They do
   **not** fail the deploy. Pluggable mapping for these is explicitly
   post-v1.
6. **The convention is not configurable in v1.** There is no
   `WithCoolifyProjectName("X")` override, no per-resource "deploy into
   project Y / environment Z" knob, and no multi-AppHost composition. The
   one-AppHost-one-project invariant is load-bearing for idempotency and
   for the developer's mental model.

## Rationale

- **Mirrors the developer's mental model.** A .NET developer who writes
  `AppHost.cs` already thinks of it as "my application" — singular. Coolify
  users already think of a project as "one app's worth of services."
  Collapsing the two so they coincide means the AppHost code and the
  Coolify UI describe the same thing at the same level of granularity.
- **Mirrors Aspire's own environment semantics.** Aspire already treats
  `--environment Staging` as a first-class concept and Coolify already has
  environments as a first-class concept under projects. Mapping them 1:1
  preserves the meaning of `aspire deploy --environment Staging` end-to-end
  without inventing a new vocabulary.
- **Lazy environment materialisation matches `aspire deploy` semantics.**
  `aspire deploy` is invoked one environment at a time; pre-creating all
  declared Aspire environments on the Coolify side would produce empty
  shell environments that suggest deploys exist where none do, confusing
  the Coolify UI and complicating drift detection. Lazy upsert means the
  Coolify project's environment list reflects what has actually been
  deployed, not what could hypothetically be deployed.
- **Makes idempotency tractable.** With a deterministic
  `(AppHost name) → project` and `(Aspire env name) → environment` mapping,
  re-deploys can converge by name lookup alone. The publisher does not need
  to remember Coolify-assigned UUIDs to find what it deployed last time —
  it can always re-derive them. This radically simplifies the state-location
  decision (a separate ADR).
- **Matches Coolify's own grouping semantics.** Coolify scopes its
  internal network and its env-var inheritance at the project/environment
  level. Mapping one AppHost to one project means the services Aspire
  considers "the same application" land in the same Coolify network, and
  their `WithReference()` edges resolve via Coolify's intra-project DNS
  naturally rather than needing cross-project wiring.
- **Skip-with-warning beats hard-fail for non-containerisable resources.**
  Real-world AppHosts mix Azure-native resources, emulators, and parameter
  resources alongside containers. Hard-failing the deploy because the graph
  references `AddAzureKeyVault(...)` would make the tool unusable for the
  exact developer profile we target (a hobbyist or homelab dev who wants
  `aspire deploy` to "just work"). A warning preserves the deploy, surfaces
  the gap, and leaves room for a post-v1 pluggable mapping.
- **Config-driven destination decouples "where" from "what."** The Aspire
  graph describes *what* to deploy; the developer's Coolify configuration
  describes *where*. Conflating them (e.g. deriving the destination from
  resource metadata) would either require Coolify-specific annotations
  inside AppHost.cs (leaky abstraction) or guess heuristically (brittle).
- **No configuration knobs in v1 is a deliberate scope guard.** Every knob
  added at v1 ossifies into a compatibility surface. Locking the convention
  lets us ship, get real usage, and add knobs only where evidence forces
  them.

## Rejected alternatives

### (a) Developer-pick mapping

Let the developer manually annotate each Aspire resource with the Coolify
project / environment / service it should land in (e.g.
`.WithCoolifyPlacement(project: "X", environment: "Y")`).

**Rejected because:** it duplicates information already present in the
AppHost graph (resource identity, environment, references), turns every
AppHost into a Coolify-specific dialect, and breaks the
one-extension-method ergonomic that `aspire-ssh-deploy` set as the bar.
It also blocks idempotency-by-name (because placement is now arbitrary
per-resource) and forces the developer to learn Coolify's hierarchy in
order to write Aspire code — exactly the friction this tool exists to
remove.

### (b) Multi-AppHost-per-project

Allow several AppHosts to deploy into the same Coolify project as a way
of composing larger systems out of multiple AppHosts.

**Rejected because:** it dissolves the one-AppHost-one-project invariant
that makes idempotency by name lookup work. Two AppHosts deploying into
the same project would race on shared resource names, fight over env-var
scope at the project level, and create ambiguous ownership of
out-of-band edits. The brief's non-goals explicitly defer multi-AppHost
composition to post-v1; this ADR ratifies that.

### (c) One-Coolify-environment-per-AppHost (collapse Aspire environments)

Map the AppHost to a single Coolify environment (e.g. always `production`)
and ignore Aspire's environment dimension, so a Staging deploy and a
Production deploy overwrite each other inside the same Coolify
environment.

**Rejected because:** it throws away `aspire deploy --environment X`
semantics, which is a first-class Aspire concept and the primary way
developers stage releases. It would also force developers who want
Staging vs Production to create two AppHosts (or two Coolify projects),
which is exactly the multi-AppHost-per-project pattern alternative (b)
already rejected. The cost of supporting environments natively is
trivial — Coolify already models them — so collapsing them is
gratuitous loss of fidelity.

### (d) One-Coolify-project-per-Aspire-environment (flip the hierarchy)

Invert the chosen mapping: each Aspire environment becomes its own
top-level Coolify project (e.g. `myapp-staging`, `myapp-production`),
and there is no shared project grouping in Coolify.

**Rejected because:** it shatters the "this is one application" mental
model on the Coolify side. A developer looking at Coolify's project list
would see N entries per AppHost (one per environment) with no native
grouping, defeating the primary UX benefit of using Coolify in the
first place. It also duplicates project-level configuration (env-vars,
secrets, webhooks, destination) across environments, and makes
"promote Staging to Production" semantically a cross-project operation
in Coolify rather than a same-project environment promotion — losing
Coolify's native promotion affordances.

## Test coverage

Exit-criteria test (see `TC-001`): given a representative AppHost with
two environments (`Development`, `Production`) and a mix of resource
types (one project resource, one container resource, one Azure-native
resource that does not containerise), `aspire deploy --environment
Production` against a Coolify instance produces:

- exactly one Coolify project named after the AppHost,
- exactly one Coolify environment under that project — `Production` —
  because environments are materialised lazily and only the targeted
  environment is upserted (the declared-but-untargeted `Development`
  environment is **not** pre-created on the Coolify side),
- one Coolify app/service per containerisable Aspire resource inside the
  `Production` environment,
- a deploy-log warning for the non-containerisable resource and a
  zero-exit-code deploy (no hard failure),
- a destination chosen from configuration (not derived from the graph).

A second invocation of the same `aspire deploy --environment Production`
is idempotent: no new projects, environments, or services are created;
existing ones are upserted in place. A subsequent `aspire deploy
--environment Development` then lazily upserts the `Development`
environment and its resources without disturbing `Production`.

## Consequences

- All downstream ADRs (idempotency state, secret scoping, image registry,
  network wiring, managed-dashboard placement) inherit this mapping as a
  fixed point.
- Multi-AppHost composition and pluggable non-container resource mapping
  are explicitly post-v1 work and will require either a new ADR (if the
  invariants here change) or a superseding ADR.
- The publisher must validate the AppHost name and environment names
  against Coolify's naming constraints early in the `configure` phase and
  fail with an actionable error if they collide with existing
  non-managed Coolify projects/environments.
- Because environments are materialised lazily, the Coolify project's
  environment list is an accurate record of what has actually been
  deployed, not what the AppHost declares — useful for drift detection
  and for the managed-dashboard's enumeration logic.
