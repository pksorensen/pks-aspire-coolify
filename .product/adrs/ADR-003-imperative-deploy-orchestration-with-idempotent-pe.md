---
id: ADR-003
title: Imperative deploy orchestration with idempotent per-resource upserts (v1)
status: accepted
features:
- FT-001
- FT-002
- FT-003
- FT-004
- FT-005
- FT-006
- FT-007
- FT-008
- FT-009
- FT-010
- FT-011
supersedes: []
superseded-by: []
domains:
- aspire-publisher
- coolify-api
- resource-mapping
- state
scope: cross-cutting
content-hash: sha256:a9b0e59852d2756a328eae30d3d5af756527f0ab67321c711180caf6c540d4a2
---

## Context

ADR-001 fixes the mapping from Aspire's resource graph onto Coolify's
`destination → project → environment → service` hierarchy, and commits to
**idempotency-by-name with lazy upsert**: identity is re-derived from the
AppHost name and the Aspire environment name on every deploy, and only
the environment being targeted is materialised. ADR-002 fixes the
client: a hand-written, thin, typed wrapper over the dozen Coolify v4
endpoints the publisher actually calls, with a version probe at the
start of every deploy.

What neither of those ADRs settles is the **shape of the deploy itself**:
when `aspire deploy --environment Production` runs, does the publisher
walk the Aspire graph and call Coolify endpoints in sequence (the
`aspire-ssh-deploy` model: `configure → build → push → deploy → verify`),
or does it assemble a full *desired-state* document describing every
project/environment/service it wants and submit that to Coolify as a
single transaction (or as a diff against what Coolify currently reports)?

The two shapes pull in different directions:

- **Aspire's own pipeline is imperative.** `aspire deploy` is decomposed
  into named phases (`configure`, `build`, `push`, `deploy`, `verify`),
  hooks run per-phase, exit codes and progress are reported per-phase.
  `aspire-ssh-deploy` — the project this one is consciously emulating —
  is imperative end-to-end.
- **Coolify's mental model is closer to declarative.** A Coolify project
  *is* the desired set of services; the UI shows you what exists; out-of-
  band edits accumulate. A pure imperative publisher that just \"calls
  endpoints\" can drift silently from what the AppHost graph says.

The choice has consequences for four downstream concerns the brief
explicitly raises:

1. **Idempotency.** ADR-001's name-keyed upsert already buys per-resource
   idempotency; what this ADR decides is whether *the deploy as a whole*
   is a sequence of idempotent steps (imperative) or a single converging
   apply (declarative).
2. **Drift detection.** A declarative model gets drift detection \"for
   free\" by virtue of diffing desired vs. observed; an imperative model
   has to opt into drift detection as a separate concern.
3. **Rollback / failed-deploy recovery.** Imperative deploys can fail
   partway through a multi-service graph and leave Coolify half-converged;
   a transactional declarative submit could (in principle) be all-or-
   nothing. In practice Coolify offers no single transactional submit
   endpoint, so the \"declarative\" shape has to be assembled from many
   calls anyway — but it could still defer side effects until a full
   plan exists.
4. **Compatibility with ADR-001's lazy-upsert.** Lazy upsert says
   sibling environments declared by the AppHost but not targeted by the
   current deploy are **not** pre-created on the Coolify side. A
   desired-state document that enumerates the whole AppHost would have
   to either lie about sibling environments or carry an explicit
   \"current target only\" filter — the imperative shape gets this
   invariant naturally.

The brief's \"Known foundational decisions\" entry lists three options
verbatim: (a) imperative — publisher calls Coolify endpoints in
sequence; (b) declarative — publisher builds a desired-state document,
diffs vs Coolify, submits only the changes; (c) hybrid. This ADR picks
one and records why.

## Decision

**v1 of `pks-aspire-coolify` adopts an imperative deploy orchestration,
with each step expressed as an idempotent name-keyed upsert against
Coolify. There is no global desired-state document, no plan/apply
separation, and no diff engine. Concretely:**

1. **The publisher implements Aspire's phase decomposition explicitly.**
   `configure → build → push → deploy → verify` is the v1 phase set,
   following `aspire-ssh-deploy`. Each phase runs to completion before
   the next begins; phase boundaries are observable in `aspire deploy`
   output.
2. **The `deploy` phase walks the Aspire graph in a deterministic
   order and issues one upsert per node.** The order is: destination
   (read or upsert) → project (upsert by AppHost name) → environment
   (upsert by Aspire env name, **only the targeted environment**, per
   ADR-001) → for each containerisable Aspire resource, upsert the
   matching app/service/database by resource name → for each resource,
   sync its environment variables → trigger Coolify's own deploy action
   for the affected services.
3. **Each upsert is a self-contained idempotent step.** Implementation
   pattern: GET-by-name → if absent, POST to create with our desired
   fields; if present, PATCH only the fields we own. ADR-002's
   conservative-serialization rule (only send fields we set) is what
   makes the PATCH safe.
4. **No persistent local desired-state document.** The publisher does
   not write a `desired.json` / `manifest.yaml` / `coolify.lock`
   alongside the AppHost. The Aspire graph in memory at deploy time
   *is* the desired state; it is reconstructed from `AppHost.cs` on
   every invocation. This composes with ADR-001 (identity is re-derived
   by name) and defers the \"idempotency state location\" foundational
   decision to a separate, much smaller ADR (\"we don't have one\").
5. **Drift is observed, not diffed.** During the `deploy` phase, for
   every resource the publisher manages, GET-by-name returns the
   currently-deployed shape. If a managed field disagrees with the
   AppHost's intent, v1 **overwrites** with the AppHost's value and
   **emits a `drift-overwritten` warning** in the deploy log naming the
   resource, the field, the old value (redacted if secret), and the new
   value. Unmanaged fields (those the publisher never writes) are left
   untouched — they cannot drift because the publisher has no opinion
   on them. There is no separate `aspire deploy --plan` or
   `aspire coolify drift` command in v1; drift surfaces inline.
6. **Rollback is `verify`-gated, not transactional.** The `verify`
   phase polls Coolify's deploy-action status for each service touched
   by the current deploy. If verification fails for any service, the
   publisher exits non-zero with a precise diagnostic (which service,
   which Coolify deploy-job URL, which timeout / error). The publisher
   does **not** attempt to roll back successfully-deployed siblings.
   Coolify retains the previous good version of each service through
   its own deploy history; recovery is \"re-run `aspire deploy` after
   fixing the AppHost\" or \"roll back in the Coolify UI.\" v1 documents
   this explicitly rather than pretending a multi-service rollback is
   safe.
7. **Order matters and is fixed.** The walk order above
   (destination → project → environment → services → env-vars →
   trigger-deploy) is part of the decision, not an implementation
   detail. It ensures parents exist before children, env-vars are
   written before the deploy action that reads them, and triggers run
   last so a failed config write never leaves a service mid-rolling-
   restart against bad config.
8. **Within a phase, sibling resources at the same level may be
   upserted concurrently.** Concurrency is a performance affordance,
   not a model change: each upsert is still self-contained and
   idempotent, and a partial failure across siblings still leaves each
   sibling in a well-defined state (either updated or unchanged). v1
   may ship sequential; the model permits concurrency without
   re-deciding.
9. **No retry-on-conflict bookkeeping in v1.** Because identity is
   name-keyed (ADR-001) and serialization is conservative (ADR-002),
   the publisher does not need optimistic-concurrency tokens, ETags,
   or version columns. The cost of this simplicity is that two
   simultaneous `aspire deploy` runs against the same Coolify project
   have undefined ordering. v1 documents \"don't do that\" and exits.

## Rationale

- **Matches the host pipeline.** `aspire deploy` is imperative; its
  phases, hooks, and exit-code semantics assume an imperative
  publisher. Adopting the same shape inside `pks-aspire-coolify` means
  there is no impedance layer between Aspire's pipeline and our work —
  phases line up, progress reporting lines up, hook insertion points
  line up. `aspire-ssh-deploy`, the project we are emulating, is
  imperative for exactly this reason.
- **Composes with ADR-001's lazy upsert naturally.** Lazy upsert says
  \"materialise only the targeted environment.\" An imperative walk
  that visits only the targeted environment's subtree gets this for
  free. A desired-state document over the full AppHost would either
  enumerate untargeted siblings (lying about what should exist) or
  carry an explicit filter (reintroducing the imperative \"only this
  one\" decision at document-assembly time). Imperative is the honest
  shape here.
- **Composes with ADR-002's thin client naturally.** The hand-written
  client exposes GET-by-name and POST/PATCH; an imperative orchestrator
  uses those endpoints directly. A diff engine would need a uniform
  resource abstraction that does not currently exist in the client and
  would have to be invented — adding a layer without adding value at
  v1 surface size.
- **Defers the idempotency-state-location ADR to a one-liner.** Because
  identity is re-derived by name (ADR-001) and the deploy is a sequence
  of name-keyed upserts (this ADR), there is no \"what UUIDs did
  Coolify assign last time\" to store. The brief's foundational
  decision \"idempotency state location\" collapses to \"none / always
  re-derive.\" That is a desirable reduction in scope.
- **Drift-as-warning is the right v1 trade.** A v1 user running Coolify
  for a homelab or side-project is much better served by \"your deploy
  succeeded, and by the way these three fields drifted and we
  overwrote them\" than by either silent overwrite (surprising) or a
  refuse-to-deploy mode (annoying for the common case of intentional
  out-of-band edits during debugging). The warning surface gives us a
  hook to upgrade later — a strict mode, a plan command, a per-field
  policy — without re-deciding the model.
- **Verify-gated, non-transactional rollback is honest.** Coolify does
  not expose a multi-service transactional apply, and even if it did,
  rollback of a partially-rolled-out container graph is genuinely
  ambiguous (do you roll back the services that succeeded? do you tear
  them down? do you leave them at the new version?). v1 picks the one
  answer with a precise semantic: each service either reached the new
  version or it didn't; the failure is reported precisely; recovery is
  Coolify-side. Pretending the publisher can do better would invite
  the worst kind of failure — a rollback that itself fails partway
  through.
- **No plan/apply in v1 is a deliberate scope guard.** A
  `terraform plan`-style two-phase workflow is a real cost: it doubles
  the client traffic per deploy, requires a serialisable plan format
  that we then have to keep stable across releases, and conditions
  users to expect refusal-to-apply on drift. The brief's target user
  wants `aspire deploy` to *just work*; v1 honours that. A future ADR
  can add `aspire deploy --plan` once we have evidence the absence
  hurts.
- **Fixed walk order is part of the contract, not an accident.**
  Documenting the order in the decision (not in code comments) makes
  hooks deterministic — a `before-deploy` hook always sees the parent
  resources already in place, an `after-verify` hook always sees the
  full graph live. It also lets downstream ADRs (secrets-env,
  managed-dashboard) reason about *when* their work happens relative
  to everyone else's.

## Rejected alternatives

### (a) Pure declarative — desired-state document with diff engine

The publisher assembles a complete `CoolifyDesiredState` document
from the Aspire graph (every project, environment, service, env-var,
expected deploy-action target), GETs the current Coolify state for the
project, computes a structural diff, and submits only the changes as
a sequence of API calls — terraform-style.

**Rejected because:** the value of declarative-with-diff is realised
when the underlying API supports transactional apply (so the diff
becomes the safe unit of work) or when the resource graph is too large
to walk in one pass. Coolify offers no transactional apply endpoint,
so the diff still degrades into a sequence of name-keyed upserts —
the same thing the imperative walk does, but with an extra
document-assembly and diff layer in front. Worse, the desired-state
document fights ADR-001's lazy-upsert invariant (it naturally wants to
enumerate the full AppHost, including untargeted environments) and
forces a stable serialised plan format we would have to maintain
across publisher releases. The supposed wins — \"free\" drift
detection, atomic apply — do not materialise; the costs are real.

### (b) Pure imperative without upsert semantics

A literal script: \"create project, create environment, create
service-A, create service-B, …,\" with no GET-then-POST-or-PATCH
discipline. On the second deploy the publisher would naively re-issue
creates and either rely on Coolify's 409s or duplicate resources.

**Rejected because:** it breaks ADR-001's idempotency-by-name
contract on the first re-deploy. Either the publisher would have to
remember Coolify-assigned UUIDs from the previous run (reintroducing
the persistent-state-file problem ADR-001 was designed to avoid) or
treat every deploy as a fresh provision (creating duplicates or
failing). The whole point of \"imperative\" in this ADR is the
*orchestration* shape, not the *step* shape; each step must still be
an upsert.

### (c) Two-phase plan-then-apply (terraform-style)

Split `aspire deploy` into `aspire deploy --plan` (which only reads
Coolify and prints a diff) and an `--apply` step that the user
explicitly confirms. Plan output is serialised to disk so apply
operates on a frozen snapshot of intent.

**Rejected because:** it imposes a workflow tax that does not match
the brief's target user. The whole pitch is \"`aspire deploy` just
works against my Coolify\"; introducing a confirm-the-plan step
collapses that into \"`aspire deploy --plan` then `aspire deploy
--apply`,\" which is exactly the friction the tool exists to remove.
It also requires us to ship and maintain a stable on-disk plan format
across publisher releases — a non-trivial compatibility surface — and
it raises the question of plan staleness (what happens when Coolify
state changes between plan and apply?) that has no clean answer at v1
scale. A future ADR can add `--plan` as an opt-in if usage shows it
is needed.

### (d) Server-side declarative submit via a Coolify manifest endpoint

Build a single Coolify manifest (analogous to a Docker-Compose file
or a Kubernetes manifest) and POST it to a hypothetical Coolify
endpoint that ingests the whole graph and converges server-side.

**Rejected because:** no such endpoint exists in Coolify v4 with
sufficient coverage of the resource types the publisher emits. Coolify
*does* accept docker-compose payloads for compose-stack applications,
but that is one specific resource type — it does not cover projects,
environments, env-var scopes, or the service/application/database
distinction. Building this shape on top of compose payloads would
re-collapse us toward `aspire-ssh-deploy`'s model (\"SCP a compose
file\") and forfeit the entire reason the brief picks Coolify over raw
compose. If Coolify ever ships a project-wide manifest endpoint, a
future ADR can revisit.

### (e) Hybrid — imperative orchestration plus a parallel cached desired-state file

Walk imperatively for the apply (as decided), but also write a
`coolify-desired.json` next to the AppHost on every successful deploy
so future deploys can diff old-desired vs. new-desired and reason
about \"what changed in the AppHost since last time.\"

**Rejected because:** the file is either gitignored (in which case CI
deploys never have it and the diff is useless) or committed (in which
case it becomes a second source of truth that can disagree with
`AppHost.cs`, with no clear winner). The information it would carry
is *already* in `AppHost.cs` and in git history; storing a derived
snapshot beside it adds drift surface without unlocking new
capability. v1 deliberately has no on-disk publisher state; the cost
of re-deriving from Coolify is one GET per resource and is paid
gladly.

## Test coverage

Exit-criteria test (see `TC-003`): given the representative AppHost
from TC-001 (two environments — `Development`, `Production` — and a
mix of resource types), against a Coolify instance that meets
ADR-002's version floor:

- **Phase shape:** `aspire deploy --environment Production` reports
  the five phases (`configure`, `build`, `push`, `deploy`, `verify`)
  in order, each phase completes before the next begins, and the
  deploy phase's first API call after `configure` is a GET-by-name
  on the project (no blind POST).
- **Per-step idempotency:** running the same `aspire deploy
  --environment Production` twice in a row produces zero net change
  in Coolify on the second run (no new project, no new environment,
  no new services, no new env-vars created), and the publisher's
  deploy-log shows that every upsert step took the \"already exists,
  PATCH with same fields → no-op\" branch.
- **Lazy environment is preserved:** after the Production-only
  deploy, the Coolify project has exactly one environment
  (`Production`); the declared-but-untargeted `Development`
  environment is not pre-created. (This re-asserts TC-001's invariant
  under this ADR's walk order.)
- **Drift-overwrite warning:** if a managed field on an existing
  Coolify service is changed out-of-band (e.g. via Coolify UI)
  between two `aspire deploy` runs, the second run overwrites the
  field with the AppHost's value and emits a `drift-overwritten`
  warning identifying the resource, the field, and the new value;
  the deploy exits zero. An *unmanaged* field changed out-of-band is
  left untouched and produces no warning.
- **Verify-gated failure is non-rolling-back:** if Coolify's deploy
  action for one of multiple services reports failure during the
  `verify` phase, `aspire deploy` exits non-zero with a diagnostic
  naming the failing service and the Coolify deploy-job URL; the
  other services that already succeeded are **not** torn down or
  reverted by the publisher. (This pins the documented non-
  transactional contract.)
- **No persistent publisher state:** after a successful deploy, the
  AppHost directory contains no new file written by the publisher
  (no `coolify.lock`, no `.coolify-state/`, no manifest). A
  subsequent deploy from a fresh clone of the same AppHost converges
  identically.

## Consequences

- The brief's foundational decision \"idempotency state location\"
  collapses to a one-line ADR: **none / always re-derive by name**.
  This ADR makes that collapse possible.
- The brief's foundational decision \"drift detection policy\" is
  partially answered here (v1: warn-and-overwrite on managed fields;
  ignore unmanaged fields; no separate drift command). A future ADR
  can add strict / refuse-to-deploy modes without re-deciding the
  orchestration shape.
- Downstream ADRs (secrets-env, image-flow, managed-dashboard) can
  assume \"my work runs inside a known phase, after these parents
  exist, before these triggers fire.\" The fixed walk order is part
  of the contract those ADRs depend on.
- Hooks into `aspire deploy`'s phase model line up 1:1 with our
  phases. Hook authors writing `before-deploy` / `after-verify`
  scripts see the same world the publisher does.
- Concurrent `aspire deploy` invocations against the same Coolify
  project are out of scope; v1 documents this rather than defending
  against it. If real-world usage shows a need (e.g. CI parallelism),
  a future ADR adds Coolify-side locking or client-side lease.
- If Coolify ever ships a project-wide transactional manifest
  endpoint, or if the publisher grows to cover graphs large enough
  that per-node upserts become latency-bound, this ADR is superseded
  by a new one — it does not require amendment, because the
  orchestration shape is a load-bearing decision, not a tunable.
