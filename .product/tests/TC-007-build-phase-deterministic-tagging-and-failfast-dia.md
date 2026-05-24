---
id: TC-007
title: build_phase_deterministic_tagging_and_failfast_diagnostics
type: exit-criteria
status: unimplemented
validates:
  features:
  - FT-003
  adrs: []
phase: 1
runner: custom
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~BuildPhaseExitCriteria
---

## Purpose

Exit criteria for FT-003 (build phase — Aspire image pipeline +
deterministic per-resource tagging + `WithImageRegistry(...)` capture).
Pins the four `E_…` symbols, the deterministic-tag invariant (I-1),
the no-`latest` prohibition (I-2), the "credentials captured but never
dereferenced" invariant (I-6), and the "build failure refuses to
advance to push" invariant (I-7).

## Fixture

AppHost with two containerisable resources (`web` project, `worker`
container) and one non-containerisable resource (e.g.
`AddAzureKeyVault("kv")`). For FT-003's exit-criteria the
non-containerisable resource is assumed already filtered out by
FT-009 in scenarios that exercise success; scenarios that exercise
build-pipeline failure inject a resource the Aspire image pipeline
will refuse to build.

`AssemblyInformationalVersionAttribute` is set to `1.2.3-test` on
the AppHost assembly except where a scenario explicitly removes it.

A reachable but **uncalled** Coolify v4 instance and a local image
store the publisher can write tagged images to.

Sentinel password literal
`SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_build_phase` used in the
credential-not-dereferenced assertion.

## Assertions

### A. `WithImageRegistry(...)` registration discipline (FT-003 §0)

1. Calling `WithImageRegistry(null, user, pass)` throws
   `ArgumentNullException` at AppHost build time naming `prefix`.
2. Calling `WithImageRegistry(prefix, user, null)` (XOR pair) throws
   `ArgumentException` at AppHost build time naming the offending
   argument; same for `(prefix, null, pass)`.
3. Calling `WithImageRegistry(...)` twice with different
   `(prefix, user, pass)` triples is **last-call-wins** (FT-003 I-8):
   the build phase tags against the second prefix. Asserted by
   inspecting the local image cache.

### B. Pre-walk gates produce the right `E_…` symbol (FT-003 §1–§2)

4. AppHost omits `WithImageRegistry(...)` entirely → deploy exits
   non-zero, stderr first-token `E_REGISTRY_NOT_CONFIGURED`, zero
   build work attempted, zero Coolify call, zero image tag emitted
   to the local cache; diagnostic carries `see: ADR-005 §1` and the
   `builder.WithCoolifyDeploy(…).WithImageRegistry(…)` remediation.
5. AppHost references the publisher correctly but the AppHost
   assembly has no `AssemblyInformationalVersionAttribute` (or its
   value is empty/whitespace) → deploy exits non-zero, stderr
   first-token `E_APPHOST_VERSION_MISSING`; diagnostic names the
   AppHost assembly simple name and shows the
   `[<Assembly: AssemblyInformationalVersion("…")>]` remediation
   form. **No fallback** to git SHA, timestamp, or assembly
   `Version` is observed.

### C. Deterministic tag shape (FT-003 I-1) and no `:latest`
   (FT-003 I-2)

6. Happy path with `prefix=auth-reg.test:5000/myapp` and
   `<apphost-version>=1.2.3-test`: after the build phase completes
   the local image store contains exactly two tags —
   `auth-reg.test:5000/myapp/web:1.2.3-test` and
   `auth-reg.test:5000/myapp/worker:1.2.3-test`. No other tag for
   either repo exists. **Specifically** no tag ending in `:latest`
   under any code path; grepping the deploy log across the run for
   the literal `:latest` returns zero matches.
7. The tag for each resource uses `IResource.Name` verbatim (no
   case-folding, no slugification). Asserted by adding a resource
   named `Mixed-Case_Worker_2` and observing the tag is
   `…/Mixed-Case_Worker_2:1.2.3-test`.

### D. AppHost version is read exactly once (FT-003 I-3)

8. Instrumenting the AppHost assembly's
   `AssemblyInformationalVersionAttribute` read surface, exactly
   one read is observed during a happy-path build phase, regardless
   of the number of resources iterated.

### E. No Coolify call and no registry push from FT-003
   (FT-003 I-4, I-5)

9. Outbound HTTP trace during the happy-path build phase shows
   **zero** requests to the registry host and **zero** requests to
   any Coolify endpoint. Build is local-cache-only.

### F. Credentials captured but never dereferenced (FT-003 I-6)

10. With `password` parameter value set to
    `SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_build_phase`, grepping
    captured stdout, stderr, Aspire structured logs, and any
    HttpClient request bodies / headers observed during the build
    phase returns **zero** matches for the sentinel literal.
    Additionally, no call to the password parameter's value-resolution
    API is observed during the build phase.

### G. Image-pipeline failure surfaces as `E_IMAGE_BUILD_FAILED`
   and refuses to advance to push (FT-003 I-7)

11. With one resource's container image pipeline forced to fail
    (e.g. a `Dockerfile` that exits non-zero), deploy exits
    non-zero; stderr first-token `E_IMAGE_BUILD_FAILED`; the
    diagnostic names the failing resource and the computed tag.
    Earlier-iterated resources whose builds succeeded **remain** in
    the local image cache (not "unbuilt"). No log line attributed
    to the `push` phase appears in the deploy output. No registry
    or Coolify HTTP request is observed.
12. "Pipeline returns success but the local cache has no entry for
    the computed tag" is treated as `E_IMAGE_BUILD_FAILED`
    (defensive verification).

### H. Catch-all (FT-003 §Behaviour catch-all)

13. An exception escaping the build-phase body that does not
    classify as the three preceding symbols (and is not a
    cancellation) surfaces as `E_BUILD_PHASE_UNEXPECTED` with the
    inner exception's `Message` appended; no stack trace appears in
    the diagnostic.

### I. Phase-boundary observability (FT-003 I-9)

14. On every fail-fast in B / G / H the deploy log shows
    `build: enter … build: exit (failed)` with no `push: enter`
    line. On the happy path the log shows
    `build: enter … build: exit (ok)` followed by `push: enter`.

### J. Stable observable contract (FT-003 I-10)

15. The four `E_…` literals (`E_REGISTRY_NOT_CONFIGURED`,
    `E_APPHOST_VERSION_MISSING`, `E_IMAGE_BUILD_FAILED`,
    `E_BUILD_PHASE_UNEXPECTED`) appear verbatim as the first
    whitespace-delimited token on stderr for the matching failure.

## Pass criteria

All fifteen assertion groups pass deterministically across at least
three consecutive runs. Any credential-leak detection (F §10) is a
hard fail regardless of other results.

## Out of scope

- Push, Coolify Private Registry upsert, deploy, verify (TC-008 /
  TC-009 / TC-010).
- The containerisability classifier (TC-013).
- Aspire image-pipeline internals (Aspire SDK).

## Validates

- FT-003 — Build phase — Aspire image pipeline + deterministic
  per-resource tagging.
- ADR-005 — Image registry strategy (v1).
- ADR-003 — Imperative deploy orchestration (v1).
