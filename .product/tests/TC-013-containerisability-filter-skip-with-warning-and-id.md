---
id: TC-013
title: containerisability_filter_skip_with_warning_and_idempotency
type: exit-criteria
status: passing
validates:
  features:
  - FT-009
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ContainerisabilityFilterExitCriteria
last-run: 2026-05-24T18:31:53.860985674+00:00
last-run-duration: 2.3s
---

## Purpose

Exit criteria for FT-009 (containerisability filter — skip-with-
warning pass for non-containerisable Aspire resources). Pins the
total classifier (I-1), the stable filter property (I-2), the
fixed four-element reason vocabulary (I-3), the uniform skip-line
shape (I-4), idempotency on unchanged graph (I-5), the never-fails-
the-deploy contract (I-6), and the single-source-of-truth handoff to
FT-003 / FT-004 / FT-005 (I-7).

## Fixture

AppHost containing one of each kind so the classifier produces
exactly one of every outcome:

- `api` — `ProjectResource` (containerisable),
- `redis` — `ContainerResource` (containerisable),
- `kv` — `AddAzureKeyVault("kv")` (azure-native),
- `apiUrl` — `builder.AddParameter("api-url")` (parameter),
- `cosmosEmu` — an Aspire emulator annotated run-mode-only
  (dev-only),
- `mystery` — a synthetic `IResource` implementation with no
  container annotation, no Azure annotation, no emulator
  annotation (unknown).

FT-001 / FT-002 implemented; FT-003 / FT-004 / FT-005 either
implemented or stubbed to record what set they iterate over.

## Assertions

### A. Total classifier (FT-009 I-1)

1. Across the six fixture resources, exactly six classification
   outcomes are recorded: two containerisable
   (`api`, `redis`) and four non-containerisable (one per
   category). No resource is unclassified, no resource is
   double-classified.

### B. Stable filter — input-order preserved (FT-009 I-2)

2. The published filtered enumeration contains
   `[api, redis]` in publisher-driver order (the same order the
   classifier visited them). No reorder is performed.

### C. Fixed four-element reason vocabulary (FT-009 I-3)

3. Grepping all FT-009-attributed log lines for `reason:` values
   returns only the four literals `parameter`, `azure-native`,
   `dev-only`, `unknown`. No `database-only`, `keyvault`,
   `serverless`, or any other category appears.

### D. Uniform skip-line shape (FT-009 I-4)

4. Every FT-009 skip line matches the regex
   `^skipped: \S+ \(reason: (parameter|azure-native|dev-only|unknown)\)$`
   exactly. Specifically:
   - `skipped: kv (reason: azure-native)`
   - `skipped: api-url (reason: parameter)`
   - `skipped: cosmosEmu (reason: dev-only)`
   - `skipped: mystery (reason: unknown)`
   Resource names appear verbatim from `IResource.Name` (no
   case-folding). No trailing punctuation, no additional fields,
   no cross-feature inline notes.

### E. Filter-summary log entry

5. Exactly one structured filter-summary log line is emitted at
   the end of the pass, attributed to the filter's phase
   (configure or build per FT-009 §"Where the pass runs"),
   carrying counts: `walked=6`, `containerisable=2`,
   `parameter=1`, `azure-native=1`, `dev-only=1`, `unknown=1`.

### F. Idempotency on unchanged AppHost (FT-009 I-5)

6. Two consecutive `aspire deploy` invocations against the same
   AppHost emit byte-identical filter-summary entries (same
   counts) AND the same set of skip lines in the same order
   across both runs. Asserted by log-line diff after stripping
   non-FT-009 attributions.

### G. Filter never fails the deploy (FT-009 I-6, I-11)

7. The deploy invocation against the fixture exits zero on the
   happy path (workload `api` and `redis` deploy successfully).
   No `E_…` symbol is emitted by FT-009 under any code path
   (cancellation excepted, which is FT-001's contract).
8. An AppHost containing **only** non-containerisable resources
   (e.g. just `kv` and `apiUrl`) produces an empty filtered
   enumeration; FT-005 upserts the destination / project /
   environment and iterates zero services; deploy exits zero. No
   `E_…` symbol from FT-005 either.

### H. Single source of truth for downstream phases (FT-009 I-7)

9. With FT-003 / FT-004 / FT-005 implemented (or stubbed to
   record their iteration set), each of the three downstream
   phases iterates exactly `[api, redis]` — the published
   filtered enumeration. The non-containerisable resources
   (`kv`, `apiUrl`, `cosmosEmu`, `mystery`) are absent from
   every downstream iteration set.
10. No downstream phase re-walks the graph or re-classifies; the
    filter pass runs exactly once per deploy invocation.

### I. No Coolify call from FT-009 (FT-009 I-8)

11. Outbound HTTP trace attributed to FT-009 shows zero
    requests against any endpoint group during the filter pass.
    No filesystem write either (no `coolify.lock`, no filter
    cache).

### J. No opt-in / opt-out surface (FT-009 I-9)

12. There is no `WithoutContainerisabilityFilter()` extension
    method, no `WithCoolifyDeploy(filter: false)` overload, and
    no environment variable that disables the filter. Asserted by
    public-API inspection.

### K. Annotation-based dev-only classification (FT-009 I-10)

13. Adding a new emulator resource annotated as run-mode-only
    (without modifying FT-009's source code) results in the
    resource being classified as `dev-only` in the next deploy.
    The classifier picks up annotations, not a hard-coded type
    allowlist.

### L. First-match rule order (FT-009 §Classification rules)

14. A hybrid resource that is BOTH a `ContainerResource` AND
    carries an Azure annotation (synthetic; ContainerResource
    wins per rule 1) is classified as **containerisable**, not
    `azure-native`. Asserted by adding such a synthetic resource
    and observing it appears in the filtered enumeration with no
    skip line.

### M. Cancellation honoured at per-resource boundary
   (FT-009 §Behaviour §2.i)

15. Triggering cancellation between two resource classifications
    aborts the pass with FT-001's cancellation diagnostic (no
    `E_…` symbol from FT-009); the publisher does not advance to
    `build`.

### N. No persistent state on disk

16. Filesystem-diff before/after the deploy shows zero new files
    written by FT-009.

## Pass criteria

All sixteen assertion groups pass deterministically across at least
three consecutive runs.

## Out of scope

- Coolify-side state — the filter runs before any Coolify call.
- Pluggable / extensible classifier (post-v1).
- Hard-fail mode for non-containerisable (rejected by ADR-001 §5).
- Cross-feature coordination on skip lines (FT-007 owns its own
  log surface for parameter-as-env-var flows).
- TypeScript AppHost parity (TC-015 / FT-011).

## Validates

- FT-009 — Containerisability filter — skip-with-warning pass for
  non-containerisable Aspire resources.
- ADR-001 — Aspire-graph to Coolify-hierarchy mapping (v1).
- ADR-003 — Imperative deploy orchestration (v1).