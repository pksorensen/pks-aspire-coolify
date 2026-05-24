---
id: TC-003
title: deploy_model_imperative_orchestration_v1
type: exit-criteria
status: passing
validates:
  features:
  - FT-001
  adrs:
  - ADR-003
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~CoolifyDeployTests
last-run: 2026-05-24T21:02:41.819910977+00:00
last-run-duration: 2.3s
---

## Purpose

Exit criteria for ADR-003 (imperative deploy orchestration with
idempotent per-resource upserts). Pins the orchestration shape, the
walk order, the per-step idempotency contract, the drift-overwrite
warning, the verify-gated non-rolling-back failure semantics, and the
absence of any persistent publisher state on disk.

Composes with TC-001 (mapping invariants under ADR-001) and TC-002
(version-probe and client behaviour under ADR-002); this TC layers the
orchestration assertions on top of the same representative AppHost.

## Fixture

Representative AppHost (same as TC-001):

- Two declared Aspire environments: `Development`, `Production`.
- One project resource (containerisable).
- One container resource.
- One non-containerisable Azure-native resource (expected
  skip-with-warning per ADR-001 / TC-001).

A reachable Coolify v4 instance whose version meets the
`SupportedCoolifyVersions` floor from ADR-002, with a configured
destination on the `WithCoolifyDeploy()` extension.

## Assertions

### Phase shape

1. `aspire deploy --environment Production` reports the five phases
   in order: `configure`, `build`, `push`, `deploy`, `verify`.
2. Each phase completes (success or failure) before the next phase
   begins; phase boundaries appear in the deploy output.
3. The first Coolify API call issued during `deploy` (after
   `configure`'s version probe from TC-002) is a `GET` on the project
   by name — not a blind `POST` / `PUT`.

### Per-step idempotency

4. A second invocation of `aspire deploy --environment Production`
   immediately after the first produces zero net change in Coolify:
   - no new project created,
   - no new environment created,
   - no new application / service / database created,
   - no new environment-variable rows created.
5. The deploy log of the second invocation shows, for each managed
   resource, a `GET → present → PATCH (no-op)` branch (or equivalent
   marker), not a `POST (create)` branch.

### Lazy-environment invariant under the walk order

6. After the Production-only deploy (first or second invocation),
   the Coolify project contains exactly one environment:
   `Production`. The declared-but-untargeted `Development`
   environment is not present.
7. A subsequent `aspire deploy --environment Development` creates
   the `Development` environment lazily and does not disturb
   `Production`'s services or env-vars.

### Drift overwrite (managed fields)

8. Setup: after a successful Production deploy, modify a *managed*
   field on one of the deployed Coolify services out-of-band (e.g.
   change its image tag or a publisher-written env-var via the
   Coolify UI / API).
9. A subsequent `aspire deploy --environment Production`:
   - overwrites the changed field with the AppHost's value,
   - emits a `drift-overwritten` warning in the deploy log naming
     the resource, the field, and (for non-secret fields) the
     observed-vs-desired values; secret fields are redacted,
   - exits with code `0`.

### Drift ignore (unmanaged fields)

10. Setup: after a successful Production deploy, modify an *unmanaged*
    field on one of the deployed Coolify services out-of-band (a
    field the publisher never writes — e.g. a Coolify-UI-only
    annotation, a domain alias the publisher does not own).
11. A subsequent `aspire deploy --environment Production` leaves the
    unmanaged field untouched and emits **no** drift warning for it.

### Verify-gated, non-rolling-back failure

12. Setup: arrange for Coolify's deploy action to fail for exactly
    one of the deployed services (e.g. by pointing it at an
    unreachable image), with the other services succeeding.
13. `aspire deploy --environment Production`:
    - exits non-zero,
    - emits a diagnostic naming the failing service and the Coolify
      deploy-job URL / error,
    - does **not** tear down, revert, or roll back the services that
      succeeded.
14. After the failure, the Coolify project still contains the
    successfully-deployed services in their new state; rollback (if
    desired) is the user's responsibility via Coolify's own deploy
    history.

### No persistent publisher state

15. After a successful `aspire deploy --environment Production`, the
    AppHost directory contains no new files written by the publisher
    (no `coolify.lock`, no `.coolify-state/`, no
    `coolify-desired.json`, no manifest).
16. Cloning the AppHost into a fresh working directory and running
    `aspire deploy --environment Production` against the same
    Coolify instance produces the same converged state, with the
    same per-step `GET → present → PATCH (no-op)` log shape as
    assertion (5).

## Out of scope

- Concurrent `aspire deploy` invocations against the same Coolify
  project — explicitly undefined in ADR-003 for v1.
- `aspire deploy --plan` semantics — no plan/apply split in v1.
- Strict / refuse-to-deploy drift modes — v1 is warn-and-overwrite
  on managed fields only.
- Transactional multi-service rollback — v1 deliberately does not
  attempt this.

## Status

Unimplemented. Becomes implementable once the publisher's phase
scaffold and the `coolify-api` client wrap-up exist; runner
configuration will be added at that point.