---
id: TC-009
title: deploy_phase_walk_and_idempotent_upserts
type: exit-criteria
status: unimplemented
validates:
  features:
  - FT-005
  adrs: []
phase: 1
runner: custom
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~DeployPhaseExitCriteria
---

## Purpose

Exit criteria for FT-005 (deploy phase — Aspire-graph walk +
idempotent name-keyed upserts + per-service deploy-action trigger +
`WithCoolifyDestination(...)` capture). Composes with TC-001 (ADR-001
mapping scenario, which exercises the same walk against a live
Coolify): TC-001 pins the observable Coolify-side hierarchy
invariants; TC-009 pins FT-005's six `E_…` symbols, the fixed walk
order (I-1), the managed-field discipline (I-4), the drift
warn-and-overwrite behaviour (I-5), the targeted-environment-only
materialisation (I-3), and the FT-007 / FT-008 hook-point invocation
contract (I-12).

## Fixture

AppHost with two containerisable resources (`api` project, `redis`
container), two declared Aspire environments (`Development`,
`Production`), and the four publisher-configuration extensions
chained:

```csharp
builder.WithCoolifyDeploy(coolifyUrl, coolifyToken)
       .WithImageRegistry(prefix, user, pass)
       .WithCoolifyDestination(coolifyDest);
```

Coolify v4 instance meeting ADR-002's floor. Destination
`homelab-prod` either pre-existing or programmatically upsertable.
FT-001 / FT-002 / FT-003 / FT-004 are assumed implemented. FT-007
and FT-008 hooks are installed as **no-op stubs** that record their
invocation count and the `(projectId, environmentId, serviceId)`
arguments they receive — the actual env-var work is TC-011 / TC-012.

## Assertions

### A. `WithCoolifyDestination(...)` registration discipline
   (FT-005 §0)

1. Calling `WithCoolifyDestination(null)` throws
   `ArgumentNullException` at AppHost build time naming the
   offending argument.
2. Calling twice is **last-call-wins**: the deploy phase targets the
   second destination handle.
3. Omitting the call entirely → deploy exits non-zero with stderr
   first-token `E_COOLIFY_DESTINATION_UPSERT_FAILED` and a
   remediation pointing at ADR-001 D4. No Coolify call beyond
   FT-002's configure probe is observed.

### B. Fixed walk order (FT-005 I-1)

4. On the happy-path deploy, the outbound HTTP request trace shows
   in this exact order: destination GET/upsert → project GET/upsert
   → environment GET/upsert (for the targeted env only) → for each
   resource: service GET/upsert → FT-007 hook invocation →
   FT-008 hook invocation → service deploy-action trigger. No
   reordering, no interleaving across hierarchy levels.

### C. Name-keyed upsert (FT-005 I-2)

5. Every `POST` issued by FT-005 on a managed endpoint is preceded
   by a `GET` on the same name-key within the same deploy
   invocation. No blind `POST` appears in the trace.

### D. Targeted environment only (FT-005 I-3)

6. After `aspire deploy --environment Production`, Coolify holds
   exactly one environment under the project: `Production`. The
   declared-but-untargeted `Development` environment receives
   **zero** GET / POST / PATCH calls from FT-005.
7. A subsequent `aspire deploy --environment Development` lazily
   upserts `Development` and leaves `Production`'s services
   untouched.

### E. Managed-field discipline on PATCH (FT-005 I-4)

8. Every PATCH body sent by FT-005 on application / service /
   database endpoints contains only fields from the v1 managed set
   (`image`, `registry-reference`, `destination-binding`).
   Unmanaged fields (ports, domains, healthcheck, restart-policy,
   build args, volumes, env-vars) are absent from the PATCH
   payload entirely. Asserted by request-body inspection on a
   re-deploy after an out-of-band edit.

### F. Drift overwrite on managed, drift ignore on unmanaged
   (FT-005 I-5)

9. After a successful deploy, change a managed field (`image` →
   point at an arbitrary other tag) AND an unmanaged field
   (`restart-policy`) on one Coolify service out-of-band. Re-run
   the same `aspire deploy --environment Production`:
   - exactly one `drift-overwritten` warning line is emitted
     naming the resource and the `image` field;
   - PATCH body contains only `image` (and the other managed
     fields if they ALSO drifted, which they did not in this
     scenario);
   - the post-deploy state of `restart-policy` matches the
     out-of-band value (unmanaged → untouched);
   - deploy exits zero.

### G. Idempotency on unchanged AppHost (FT-005 I-6)

10. Two consecutive `aspire deploy --environment Production`
    invocations against an unchanged AppHost produce zero new
    destinations / projects / environments / services on the
    second run. The deploy log on the second run shows every
    upsert step taking the `unchanged` branch.

### H. Service-upsert and trigger aggregation (FT-005 I-10)

11. With the second of three resources' service-upsert forced to
    fail (non-2xx), the loop attempts all three resources before
    surfacing `E_COOLIFY_SERVICE_UPSERT_FAILED`; the diagnostic
    names **all** failing `(resource, tag, response-excerpt)`
    tuples; the deploy-trigger step is NOT executed (zero trigger
    POSTs observed); previously-upserted siblings remain in
    Coolify (not torn down — FT-005 I-9).
12. With service upserts all succeeding but two of three deploy-
    action triggers forced to return non-2xx, the surfaced symbol
    is `E_COOLIFY_DEPLOY_TRIGGER_FAILED` and the diagnostic names
    both failing services. Verify phase is not entered.

### I. FT-007 / FT-008 hook-point contract (FT-005 I-12)

13. On the happy-path deploy, each hook is invoked exactly once
    per successfully-upserted service, between service upsert
    success and the per-service deploy-action trigger. Hooks
    receive the in-phase `(projectId, environmentId, serviceId)`.
14. On a service-upsert failure for one resource, the hooks are
    **not** invoked for that resource (no `serviceId`); they are
    invoked for the resources whose upsert succeeded earlier in
    the loop.
15. If a hook surfaces a fail-fast diagnostic (e.g.
    `E_ENVVAR_UPSERT_FAILED`), the deploy phase exits non-zero
    with **that** symbol — not `E_DEPLOY_PHASE_UNEXPECTED`; the
    deploy-trigger step for that service is not executed.

### J. No env-var writes from FT-005 (FT-005 I-14)

16. Outbound HTTP trace attributed to FT-005-owned code paths
    shows zero requests against the env-var endpoint group.

### K. Anonymous-push propagation (FT-005 I-15)

17. When FT-004 ran in anonymous-push mode, the service-upsert
    POST/PATCH payload omits the `registry-reference` field
    entirely (not `null`, not `""`). Asserted by request-body
    inspection.

### L. No persistent on-disk publisher state (FT-005 I-7) and no
   UUID propagation (FT-005 I-8)

18. A filesystem-diff of the AppHost directory before and after a
    successful deploy shows zero new files written by FT-005.
19. Across two consecutive deploys, no Coolify-assigned UUID
    captured in deploy A is reused from a cache in deploy B; the
    second deploy re-resolves every ID by GET-by-name.

### M. Phase-boundary observability (FT-005 I-13)

20. On every fail-fast in A / H / I, the log shows
    `deploy: enter … deploy: exit (failed)` with **no**
    `verify: enter`. On the happy path, the log shows
    `deploy: enter … deploy: exit (ok)` followed by
    `verify: enter`.

### N. Stable observable contract (FT-005 I-11)

21. The six `E_…` literals
    (`E_COOLIFY_DESTINATION_UPSERT_FAILED`,
     `E_COOLIFY_PROJECT_UPSERT_FAILED`,
     `E_COOLIFY_ENVIRONMENT_UPSERT_FAILED`,
     `E_COOLIFY_SERVICE_UPSERT_FAILED`,
     `E_COOLIFY_DEPLOY_TRIGGER_FAILED`,
     `E_DEPLOY_PHASE_UNEXPECTED`) appear verbatim as the first
    whitespace-delimited token on stderr for the matching failure.

## Pass criteria

All twenty-one assertion groups pass deterministically across at
least three consecutive runs against a freshly reset Coolify
instance.

## Out of scope

- Env-var sync detail (TC-011 / FT-007).
- Reference wiring detail (TC-012 / FT-008).
- Verify polling (TC-010 / FT-006).
- The containerisability classifier (TC-013 / FT-009).
- The actual Coolify `/api/v1/...` paths / DTOs (ADR-002).

## Validates

- FT-005 — Deploy phase — Aspire-graph walk and idempotent Coolify
  upserts with deploy trigger.
- ADR-001 — Aspire-graph to Coolify-hierarchy mapping (v1).
- ADR-003 — Imperative deploy orchestration (v1).
