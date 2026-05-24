---
id: TC-011
title: envvar_sync_service_scope_redaction_and_skip_for_FT008
type: exit-criteria
status: passing
validates:
  features:
  - FT-007
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~EnvVarSyncExitCriteria
runner-timeout: 120
last-run: 2026-05-24T18:15:00.655396806+00:00
last-run-duration: 2.3s
---

## Purpose

Exit criteria for FT-007 (secret / env-var sync — Aspire parameters
and connection-string-named keys into Coolify service-scope env-vars).
Pins the three `E_…` symbols, the hook-fires-exactly-once-per-service
invariant (I-1), the service-scope-only contract (I-2), the no-secret-
leakage discipline (I-3, I-13, I-14), the single-writer-with-FT-008
key partition (I-5 / cross-validated by TC-012), the orphan-leave-in-
place stance (I-6), and the SECRET_LEAKED-dominates-UPSERT_FAILED
precedence.

## Fixture

AppHost with three resources:

- `api` (project) referencing two parameters:
  `app-greeting` (`secret: false`, value `"hello"`) and
  `api-key` (`secret: true`, value
  `SENTINEL_PARAM_DO_NOT_LEAK_envvar_sync`),
  plus a connection-string reference named `ConnectionStrings__db`
  that **FT-008 will supply** the value for (in v1 alone, FT-008
  has not landed, so the value is "no value" — see assertion C);
- `redis` (container) referencing no parameters and no
  connection-strings;
- `db` (database) referencing one parameter `db-password`
  (`secret: true`, value
  `SENTINEL_PARAM_DB_PASSWORD_DO_NOT_LEAK`).

Coolify v4 instance meeting ADR-002's floor; FT-001 / FT-002 /
FT-005 implemented. FT-008 is **either absent or a stub that always
returns "no value"** for connection-string lookups (the FT-007-alone
v1 world).

## Assertions

### A. Hook invocation contract (FT-007 I-1)

1. The FT-007 hook is invoked exactly once per successfully-upserted
   service, between the service upsert and FT-005's per-service
   deploy-action trigger. Asserted by hook-invocation counting:
   three services in the fixture → three invocations.
2. The hook is **not** invoked for a service whose upsert failed.
3. The hook is **not** invoked twice for the same service in one
   deploy.

### B. Service-scope-only env-var writes (FT-007 I-2)

4. Outbound HTTP trace shows every FT-007-attributed env-var call
   targets the service-scope env-var endpoint group keyed by the
   current `serviceId`. **Zero** project-scope or
   environment-scope env-var endpoint calls are issued.
5. Every `POST` is preceded by a `GET-by-name` for the same
   `(serviceId, key)` tuple.

### C. Connection-string skip-with-log when FT-008 absent
   (FT-007 §Behaviour §2, I-5)

6. The deploy log contains exactly one `envvar-skipped:
   resource=api key=ConnectionStrings__db
   reason=awaiting-FT-008` line. **Zero** outbound env-var
   endpoint calls bear the key `ConnectionStrings__db`.
7. The service upsert and the per-service deploy-trigger for `api`
   continue normally; the connection-string skip does not fail the
   deploy.

### D. Env-var key uses Aspire's consumer-side name verbatim
   (FT-007 I-4)

8. The Coolify-side env-var keys observed on the `api` service
   after a successful deploy are exactly `app-greeting`,
   `api-key`, and (for the connection-string slot, currently
   reserved for FT-008) nothing. No case-folding, no prefix
   transformation, no `APP_GREETING` style upper-snake conversion.

### E. Secret-flag round-trips faithfully (FT-007 I-15)

9. After a successful deploy, Coolify-side inspection shows the
   `api-key` env-var on the `api` service carries the Coolify
   secret flag SET, and the `app-greeting` env-var carries the
   secret flag CLEAR. Same for `db-password` on the `db`
   service (SET).

### F. Redaction across all surfaces (FT-007 I-3, I-13)

10. Grepping all captured stdout, stderr, Aspire structured logs,
    Aspire dashboard parameter snapshot, captured HttpClient
    request bodies' log-attribution side, and any
    captured-and-rethrown exception `.Message` strings for the
    literals `SENTINEL_PARAM_DO_NOT_LEAK_envvar_sync` and
    `SENTINEL_PARAM_DB_PASSWORD_DO_NOT_LEAK` returns **zero**
    matches across every assertion-group scenario.
11. A regression scenario that deliberately injects an Aspire
    sentinel into a Coolify mock response excerpt verifies the
    line containing the sentinel is suppressed and replaced with
    a fixed `E_ENVVAR_SECRET_LEAKED` diagnostic on stderr (FT-007
    Behaviour §6, I-13).

### G. Pre-flight sentinel scan on non-secret value
   (FT-007 §Behaviour §3)

12. With a deliberately-bugged Aspire parameter pipeline that
    stamps a sentinel onto a `secret: false` value, the hook
    surfaces `E_ENVVAR_SECRET_LEAKED` **before** issuing any
    Coolify env-var call (asserted by zero env-var endpoint
    requests prior to the diagnostic).

### H. Orphan env-vars left in place (FT-007 I-6)

13. After a successful deploy, manually add a Coolify-side env-var
    `orphan-key=value` on the `api` service via the Coolify UI.
    Re-run the deploy. **Zero** FT-007-attributed GET / PATCH /
    DELETE is issued against `orphan-key`; the value remains.

### I. Aggregated per-key failure (FT-007 I-8) and
   single-service scope of aggregation buckets (§State)

14. With two of three env-var POSTs for the `api` service forced
    to fail (non-2xx) and the third succeeding, the hook attempts
    all three keys before surfacing `E_ENVVAR_UPSERT_FAILED`; the
    diagnostic names **both** failing keys (values are NOT
    included); the third key's env-var remains in Coolify.
15. FT-005 sees FT-007's symbol and exits the deploy phase with
    `E_ENVVAR_UPSERT_FAILED` — NOT `E_DEPLOY_PHASE_UNEXPECTED`
    (FT-005 §"Error handling"). The per-service deploy-trigger
    for `api` is NOT executed; prior services' env-vars and
    successful upserts remain (no rollback).

### J. Idempotency on unchanged AppHost (FT-007 I-11)

16. Two consecutive deploys against an unchanged AppHost produce
    zero net change in Coolify env-vars on the second run; every
    key takes the `unchanged` branch in the deploy log.

### K. Managed-field discipline on PATCH (FT-007 I-7)

17. When a value or secret-flag drifts out-of-band and the deploy
    re-runs, the PATCH body sent contains only `value` and
    `secret-flag`. Unmanaged Coolify env-var fields
    (`is_build_time`, `is_preview`, `description`) are absent
    from the payload entirely.

### L. SECRET_LEAKED precedence dominance
   (FT-007 §"Error handling" precedence rule)

18. When a failing-key response excerpt contains a sentinel, the
    excerpt is dropped from the diagnostic, the key is still
    named, and the surfaced symbol is `E_ENVVAR_SECRET_LEAKED`
    (NOT `E_ENVVAR_UPSERT_FAILED`).

### M. No persistent on-disk state (FT-007 I-10)

19. Filesystem-diff of the AppHost directory before and after every
    scenario shows zero new files written by FT-007.

### N. Stable observable contract (FT-007 I-12)

20. The three `E_…` literals (`E_ENVVAR_UPSERT_FAILED`,
    `E_ENVVAR_SECRET_LEAKED`, `E_ENVVAR_PHASE_UNEXPECTED`) appear
    verbatim as the first whitespace-delimited token on stderr for
    the matching failure.

## Pass criteria

All twenty assertion groups pass deterministically across at least
three consecutive runs. Any redaction failure (F §10/§11 or G §12)
is a hard fail regardless of other results.

## Out of scope

- FT-008 (reference wiring) writing the connection-string values
  → TC-012.
- Project-scope / environment-scope env-vars (post-v1).
- Orphan GC (`aspire coolify gc`, post-v1).
- The actual Coolify env-var `/api/v1/...` path / DTO (ADR-002).

## Validates

- FT-007 — Secret / env-var sync — Aspire parameters and connection
  strings into Coolify service-scope env vars.
- ADR-002 — Coolify API version and client strategy (v1).
- ADR-003 — Imperative deploy orchestration (v1).
- ADR-004 — Coolify auth model (v1, redaction discipline).