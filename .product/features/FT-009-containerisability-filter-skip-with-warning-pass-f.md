---
id: FT-009
title: Containerisability filter — skip-with-warning pass for non-containerisable Aspire resources
phase: 1
status: complete
depends-on:
- FT-001
adrs:
- ADR-001
- ADR-003
- ADR-004
- ADR-006
tests:
- TC-013
domains:
- aspire-publisher
- resource-mapping
domains-acknowledged: {}
---

## Description

FT-009 introduces the **containerisability filter**: a single, deterministic
pass that walks the Aspire resource graph once at the end of the `configure`
phase (or, equivalently, at the top of `build` before FT-003 begins) and
partitions every resource into two buckets — **containerisable** (passes
through to FT-003 / FT-004 / FT-005) and **non-containerisable** (skipped
with a warning, and never seen again by any downstream phase). The filter
publishes the canonical filtered enumeration once onto the publisher
context; FT-003, FT-004, and FT-005 read that enumeration verbatim and do
not re-walk or re-classify the graph.

This is the feature that ratifies ADR-001 §5 ("non-containerisable
resources skip-with-warning, not hard-fail") at the publisher-pipeline
layer. Per the brief's v1 framing — Coolify hosts containers, so v1 only
targets resources that can be expressed as containers — FT-009 is the
filter that turns a heterogeneous Aspire graph into the container-only
subgraph FT-003 / FT-004 / FT-005 are designed to consume. Crucially, the
deploy is **not** failed by the presence of non-containerisable resources;
they are emitted as one warning line per skipped resource and dropped from
the downstream enumeration. This preserves "`aspire deploy` just works"
ergonomics for the homelab developer profile the project targets.

FT-009 is deliberately **not** an extension method on the AppHost builder.
It is an internal publisher-pipeline step that runs unconditionally whenever
`WithCoolifyDeploy(...)` has registered the publisher (FT-001). There is no
opt-in, no opt-out, and no configuration knob in v1. The classification
rules are a fixed, small set; the reason vocabulary is a fixed four-element
enum (`parameter`, `azure-native`, `dev-only`, `unknown`); and the output
log shape is a single uniform line per skipped resource. New categories or
new rules require a feature amendment in a later release.

The filter is **idempotent on graph equality**: a second `aspire deploy`
against the same AppHost produces the same filtered enumeration in the
same order and emits the same set of warning lines for the same resources
in the same order. There are no error symbols introduced by this feature
— FT-009 cannot fail the deploy. The only diagnostic surface is the
warning lines and the structured filter-summary log entry.

ParameterResources referenced via `WithReference(...)` are skipped from
the downstream deploy enumeration by FT-009 (they are not containers and
therefore not services to deploy), but their *values* still flow into
service env-vars via FT-007's separate path. FT-009 does not coordinate
with FT-007; it simply removes the parameter from the deploy enumeration
and emits the uniform skip line. FT-007 picks up the parameter through
its own `WithReference()`-aware traversal and emits its own log lines
when it does. Keeping the two concerns in separate logs preserves
FT-009's output as a clean filter audit.

## Functional Specification

### Inputs

- The Aspire resource graph reachable from the running
  `DistributedApplication` instance, as exposed by the publisher driver
  on entry to the `configure` phase (or, equivalently, at the top of
  `build` — the contract is "before FT-003 begins"). FT-009 walks the
  graph in the same deterministic order the publisher driver enumerates
  resources; it does not impose its own ordering, but it does require
  the order to be stable across invocations against an unchanged
  AppHost (Aspire already guarantees this).
- The publisher context object owned by FT-001's spine, onto which
  FT-009 publishes the filtered enumeration for FT-003 / FT-004 / FT-005
  to read.
- The Aspire deploy `CancellationToken` for the in-flight invocation,
  propagated from FT-001's phase context. The filter pass is short
  (single graph walk, no I/O), but it honours cancellation at the
  per-resource boundary.

### Outputs

- **The canonical filtered enumeration** of containerisable resources,
  published exactly once onto the publisher context under a named
  channel (`ContainerisableResources` or equivalent — final field name
  chosen at implementation time). The enumeration preserves the input
  order with non-containerisable resources removed (it is a stable
  filter, not a re-sort). FT-003 / FT-004 / FT-005 read this channel
  verbatim. The enumeration is **read-only** after publication; no
  downstream phase mutates it.
- **One warning log line per skipped resource**, emitted to the
  Aspire-structured-log stream attributed to the `configure` phase
  (or `build`, depending on where the pass runs — the boundary is
  fixed at "before FT-003"), in the exact literal shape:

  ```
  skipped: <resource-name> (reason: <category>)
  ```

  where `<resource-name>` is the Aspire resource's `Name` property
  verbatim (matching FT-003 / FT-005 naming discipline — no
  case-folding, no slugification) and `<category>` is one of the four
  literal strings from the fixed enum (see Behaviour §Categories).
  The line is emitted at log level `warn`. No additional fields, no
  trailing punctuation, no Coolify-side context (FT-009 runs before
  any Coolify call).
- **One structured filter-summary log entry** at the end of the pass,
  attributed to the same phase, recording:
  - the total count of resources walked,
  - the count of containerisable resources passed through,
  - the count of skipped resources broken down by category
    (`parameter`, `azure-native`, `dev-only`, `unknown`).
  The summary is emitted at log level `info`. It exists for operator
  observability and for the idempotency exit-criteria test, which
  asserts identical summaries across two consecutive deploys.
- **No error symbols.** FT-009 does not introduce any `E_…` diagnostic.
  Every input graph produces a valid filtered enumeration (possibly
  empty) and exits the pass with a zero-impact result on the deploy
  exit code. An AppHost containing **only** non-containerisable
  resources produces an empty enumeration and FT-005 then upserts the
  destination / project / environment but iterates zero services — a
  successful, observable no-op deploy. This is by design (matches
  ADR-001 §5 "skip-with-warning, not hard-fail").

### State

- **No persistent state on disk.** Inherits ADR-003 §4 / FT-001 I-3.
- **No in-memory cross-deploy state.** Each invocation re-walks and
  re-classifies the graph from scratch.
- **In-memory state is bounded to the pass.** The filtered enumeration
  lives on the publisher context for the lifetime of the deploy
  invocation. Coolify-assigned IDs do not enter FT-009's scope (this
  feature runs before any Coolify call).

### Behaviour

The filter pass executes the following steps in order, exactly once
per `aspire deploy` invocation, between the end of `configure` and the
start of `build`:

1. **Initialise tallies.** Reset four per-category counters
   (`parameter`, `azure-native`, `dev-only`, `unknown`) and the
   containerisable counter to zero. Initialise an empty list for the
   filtered enumeration.

2. **Walk the Aspire resource graph in publisher-driver order.** For
   each resource:

   1. **Cancellation check.** If the deploy `CancellationToken` has
      been signalled, abort the pass with FT-001's cancellation
      diagnostic (no `E_…` symbol). The publisher does not advance
      to `build`.

   2. **Classify the resource** by applying the rules below in
      first-match order (see Behaviour §Classification rules). Each
      resource matches exactly one outcome: either *containerisable*
      or *non-containerisable with category C ∈ {parameter,
      azure-native, dev-only, unknown}*.

   3. **If containerisable:** append the resource to the filtered
      enumeration and increment the containerisable counter. Emit no
      log line.

   4. **If non-containerisable:** emit the uniform skip line
      `skipped: <name> (reason: <category>)` at log level `warn`,
      increment the matching category counter. Do **not** add the
      resource to the filtered enumeration.

3. **Publish the filtered enumeration** onto the publisher context
   under the agreed channel. After publication the list is read-only.

4. **Emit the filter-summary log entry** with the five counts and
   exit the pass.

#### Categories (fixed enum, v1)

The reason vocabulary is exactly these four literal strings. No others
appear under any code path in v1; introducing a new category requires
a feature amendment.

| Category       | Literal       | Meaning                                                                                                |
|----------------|---------------|--------------------------------------------------------------------------------------------------------|
| `parameter`    | `parameter`   | Raw `ParameterResource` with no runtime / container representation. Its value may still flow as an env-var via FT-007. |
| `azure-native` | `azure-native`| Azure SDK resource (Key Vault, Storage, App Configuration, etc.) with no Aspire-side container shape.  |
| `dev-only`     | `dev-only`    | In-process or dev-time-lifetime resource (emulators, devcontainer-only services) not intended for deploy. |
| `unknown`      | `unknown`     | Anything else that has no detectable container representation. Catch-all so the classifier is total.   |

#### Classification rules (first-match order)

The classifier is intentionally small and conservative: the goal is to
let containers through and skip everything else with a precise reason.
Rules are evaluated in this order for each resource; the first match
wins.

1. **Container / ContainerResource passthrough.** If the resource is a
   container resource (i.e. it carries a container image annotation,
   is a `ContainerResource`, or is a `ProjectResource` that Aspire's
   build pipeline produces a container image for), classify as
   **containerisable** and pass it through. This is the only path
   that adds the resource to the filtered enumeration.

2. **ParameterResource → `parameter`.** If the resource is a raw
   `ParameterResource` (no runtime, no container representation),
   classify as non-containerisable with category `parameter`.

3. **Azure-native → `azure-native`.** If the resource's CLR type
   originates from `Aspire.Hosting.Azure.*` (or carries an
   Azure-resource annotation) and is not also a container resource,
   classify as non-containerisable with category `azure-native`.
   (The exact detection — namespace prefix vs. marker annotation —
   is an implementation choice, but it must match every Azure-SDK
   resource Aspire 10 ships in v1.)

4. **Dev-time-only / emulator → `dev-only`.** If the resource is
   annotated as dev-time-only, in-process, or is a known Aspire
   emulator type (e.g. an in-memory emulator that exists only to
   satisfy a connection-string at `dotnet run` time and has no
   deployed counterpart), classify as non-containerisable with
   category `dev-only`. The detection rule is annotation-based —
   v1 uses Aspire's "run-mode-only" / "emulator" annotations rather
   than a type allowlist, so future emulators are picked up
   automatically without amending FT-009.

5. **Fallthrough → `unknown`.** Any resource that does not match
   rules 1–4 is classified as non-containerisable with category
   `unknown`. This makes the classifier total — every resource
   produces exactly one outcome — and gives the developer a precise
   "we don't know what to do with this, but we didn't fail your
   deploy" signal.

The rules above produce a partition: every resource is classified
exactly once, and rule 1 is the only one that adds to the filtered
enumeration.

#### Where the pass runs

The filter pass runs at the **boundary between configure and build**.
Concretely: it is the last step of `configure` (after FT-002's token
resolution and auth probe succeed) or the first step of `build`
(before FT-003 begins iterating). Either placement satisfies the
contract "the filtered enumeration is available to FT-003 / FT-004 /
FT-005." The boundary is fixed because:

- FT-003 and FT-004 must not waste work building or pushing images
  for resources that would be dropped at deploy.
- FT-005's per-resource service-upsert loop assumes every input is
  containerisable (FT-005 §Boundaries explicitly defers this
  filter).
- FT-002's auth probe must have succeeded first, so that a failing
  filter pass never masks an auth failure (and vice versa — a
  failed auth probe means the filter pass never runs).

### Invariants

- **I-1: classifier is total.** Every Aspire resource visited
  produces exactly one classification outcome (containerisable or
  one of the four non-containerisable categories). No resource is
  unclassified, no resource is double-classified.
- **I-2: filtered enumeration is a stable filter.** The output
  enumeration preserves the input order with non-containerisable
  resources removed. The classifier never reorders the surviving
  resources.
- **I-3: the four categories are the only categories.** No log line
  emitted by FT-009 uses any `reason:` value other than the four
  literal strings `parameter`, `azure-native`, `dev-only`,
  `unknown`. Asserted by log-grep on an exit-criteria fixture.
- **I-4: uniform skip-line shape.** Every skip line matches exactly
  the regex `^skipped: \S+ \(reason: (parameter|azure-native|dev-only|unknown)\)$`
  with the resource name verbatim from `IResource.Name`. No
  additional fields, no inline cross-feature notes, no trailing
  punctuation.
- **I-5: idempotency on unchanged AppHost.** Two consecutive
  `aspire deploy` invocations against the same AppHost produce
  byte-identical filter-summary entries (same counts) and the same
  set of skip lines in the same order. Asserted by log diff.
- **I-6: filter never fails the deploy.** No code path in FT-009
  emits an `E_…` symbol, sets a non-zero exit code, or aborts the
  pipeline (cancellation excepted, which is FT-001's contract).
- **I-7: filtered enumeration is the single source of truth for
  downstream phases.** FT-003, FT-004, and FT-005 read the published
  channel verbatim. They do not re-walk or re-classify the graph.
  Asserted at integration-test level: a deliberately-injected
  Azure-native resource appears in FT-009's skip log exactly once
  and is absent from FT-003's build set, FT-004's push set, and
  FT-005's upsert set.
- **I-8: no Coolify call originates from FT-009.** The pass is
  pure-local: it does not call `ICoolifyClient`, does not make any
  HTTP request, does not touch the filesystem, does not read any
  Aspire parameter value. Asserted by request-trace inspection on
  the filter phase.
- **I-9: no opt-out / opt-in surface.** The filter runs
  unconditionally whenever `WithCoolifyDeploy(...)` has registered
  the publisher. There is no `WithoutContainerisabilityFilter()`,
  no `WithCoolifyDeploy(filter: false)`, no environment variable.
  This is part of ADR-001 §5's "non-containerisable resources
  skip-with-warning" contract at v1.
- **I-10: classifier is annotation-based for `dev-only`.** The rule
  for `dev-only` looks at Aspire's run-mode / emulator annotations,
  not a hard-coded type allowlist, so future emulators inherit the
  classification without amending FT-009.
- **I-11: empty filtered enumeration is a successful deploy.** An
  AppHost containing only non-containerisable resources produces an
  empty enumeration and the deploy exits zero (after FT-005 upserts
  the destination / project / environment and triggers zero
  services). No `E_…` symbol fires.

### Error handling

FT-009 introduces **no** `E_…` diagnostics. The pass either runs to
completion (every input AppHost graph is classifiable, by I-1) or is
cancelled via the Aspire deploy `CancellationToken`. A cancellation
between two resource classifications surfaces as FT-001's cancellation
diagnostic and the publisher does not advance to `build`. There is no
catch-all `E_FILTER_PASS_UNEXPECTED` — the classifier is pure-local,
side-effect-free, and total, so any exception escaping the pass is a
bug, not a contract.

If a downstream feature (FT-003 / FT-004 / FT-005) reads the published
enumeration and finds it absent (i.e. the publisher context channel
does not exist), that is a wiring bug in FT-001's spine, not an FT-009
diagnostic. FT-009's contract is "publish the channel exactly once on
the success path," and the success path is the only path.

### Boundaries

- **In scope for FT-009:**
  - the one-shot containerisability filter pass at the
    configure/build boundary
  - the five-outcome classifier (containerisable + four
    non-containerisable categories) with fixed first-match rules
  - publication of the filtered enumeration on the publisher
    context for FT-003 / FT-004 / FT-005 to consume
  - the uniform `skipped: <name> (reason: <category>)` warning line
    per skipped resource
  - the structured filter-summary log entry at end of pass
  - cancellation honouring at the per-resource boundary
  - idempotency on unchanged AppHost
- **Out of scope for FT-009** (handled elsewhere or deferred):
  - any Coolify API call → none originate here
  - resolution of `ParameterResource` values → FT-007 (env-var sync)
  - `WithReference()` traversal → FT-008
  - building / pushing / upserting containerisable resources →
    FT-003 / FT-004 / FT-005
  - opt-in / opt-out surface for the filter → none in v1
  - pluggable mapping for non-containerisable resources → post-v1
    (ADR-001 §5 defers this)
  - cross-feature inline notes on skip lines (e.g.
    "flows-as-env-var") → FT-007 owns its own log lines
  - new reason categories beyond the fixed four → feature amendment
  - TypeScript AppHost parity → `apphost-ts` domain feature
  - drift detection on previously-skipped resources between deploys
    → not a v1 concern (the filter is stateless and re-classifies
    from scratch each invocation)

## Out of scope

- **Coolify-side interaction.** FT-009 runs before any Coolify call.
  No HTTP, no `ICoolifyClient`, no destination / project / environment
  / service touch. Asserted by I-8.
- **Pluggable / extensible classifier.** v1's classifier is fixed.
  No `IContainerisabilityRule` plugin interface, no DI-injected
  classifier override, no per-AppHost rule extension. Post-v1.
- **Hard-fail mode for non-containerisable resources.** Already
  rejected by ADR-001 §5; not reopened here.
- **Cross-feature coordination on skip lines.** FT-007's
  parameter-as-env-var flow is its own log surface. FT-009's skip
  line is uniform regardless of whether the skipped resource is
  referenced downstream.
- **Drift detection on the skipped set across deploys.** The filter
  is stateless and re-runs from scratch every invocation; "this
  resource was non-containerisable last deploy and is now
  containerisable" is not a concept FT-009 tracks.
- **Per-resource concurrency.** The pass is sequential and trivially
  fast (no I/O); parallelisation would not materially help and is
  not implemented.
- **Configurable category vocabulary.** The four literals are part
  of the observable contract (I-3, I-4). Adding `database-only`,
  `keyvault`, `serverless`, or any other category is a feature
  amendment, not a v1 knob.
- **Persistent state of any kind.** Forbidden by ADR-003 §4 and the
  publisher's no-on-disk-state invariant.
