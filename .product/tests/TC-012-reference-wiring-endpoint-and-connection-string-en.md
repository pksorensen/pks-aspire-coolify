---
id: TC-012
title: reference_wiring_endpoint_and_connection_string_envvars
type: exit-criteria
status: passing
validates:
  features:
  - FT-008
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ReferenceWiringExitCriteria
runner-timeout: 120
last-run: 2026-05-24T18:23:02.706366780+00:00
last-run-duration: 2.2s
---

## Purpose

Exit criteria for FT-008 (service-to-service network wiring — Aspire
`WithReference()` edges into Coolify service-scope env-vars). Pins
the three `E_…` symbols (plus the shared `E_ENVVAR_SECRET_LEAKED`
escalation), the AFTER-FT-007 / BEFORE-trigger invocation contract
(I-1), the single-writer-per-key partition with FT-007 (I-3), the
Aspire-consumer-side key-naming invariants (I-4), the Coolify-
internal-hostname resolution property (I-5), the rule-based
secret-flag policy (I-6), the target-resolved-against-in-phase-map
discipline (I-15), and the sentinel-leak precedence rule.

## Fixture

AppHost with three resources:

- `db` (Postgres container exposing a connection string),
- `api` (project) — calls
  `.WithReference(db)` (connection-string ref) and
  `.WithReference(cache)` (endpoint ref),
- `cache` (Redis container exposing an `http` endpoint at index 0
  and a `tcp` endpoint at index 0).

`api` therefore consumes one connection-string reference
(`ConnectionStrings__db`) and two endpoint references
(`services__cache__http__0`, `services__cache__tcp__0`).

The Postgres connection-string template contains a placeholder for a
secret parameter `db-password=SENTINEL_DB_PASSWORD_DO_NOT_LEAK_ref`
(`secret: true`) so the resolved value carries an Aspire sentinel
and FT-008 must mark the resulting env-var as Coolify-secret.

FT-001 / FT-002 / FT-005 / FT-007 are assumed implemented. FT-007
reserves the connection-string key with `envvar-skipped:
resource=api key=ConnectionStrings__db reason=awaiting-FT-008`
on its own log line for this fixture.

## Assertions

### A. Invocation contract — AFTER FT-007, BEFORE trigger
   (FT-008 I-1)

1. Hook-invocation ordering shows for each service: FT-007 hook
   returns successfully → THEN FT-008 hook fires → THEN FT-005's
   per-service deploy-action trigger fires. FT-008 is NEVER
   invoked before FT-007 returns for the same service.
2. FT-008 is not invoked for a service whose upsert failed, not
   invoked after the deploy-action trigger, not invoked twice for
   the same service in one deploy.

### B. Service-scope-only env-var writes through the same
   endpoint group FT-007 uses (FT-008 I-2)

3. Outbound HTTP trace shows every FT-008-attributed env-var call
   targets the same Coolify service-scope env-var endpoint group
   FT-007 uses (asserted by endpoint-group path equality across
   FT-007 and FT-008 traces). **Zero** project- or
   environment-scope calls. Every POST is preceded by a GET-by-name.

### C. Single-writer-per-key partition with FT-007
   (FT-008 I-3, FT-007 I-5)

4. Across the deploy of the fixture: the set of env-var keys
   written by FT-007 against the `api` service is
   {`app-greeting`, `api-key`} (parameter projections only), and
   the set of keys written by FT-008 is
   {`ConnectionStrings__db`, `services__cache__http__0`,
   `services__cache__tcp__0`}. The intersection is empty.
   FT-007 does NOT write any reference key; FT-008 does NOT write
   any parameter key. Asserted by request-trace inspection.
5. The `ConnectionStrings__db` key FT-008 writes is exactly the
   key FT-007 reserved with the `envvar-skipped: …
   reason=awaiting-FT-008` line.

### D. Key naming follows Aspire's consumer-side convention
   verbatim (FT-008 I-4)

6. Endpoint reference keys match the regex
   `^services__[A-Za-z0-9_-]+__[a-z]+__\d+$` exactly
   (double-underscores, target name verbatim, lowercase scheme,
   non-negative integer index). Specifically the fixture produces
   `services__cache__http__0` and `services__cache__tcp__0`
   (lowercase `cache`, NOT `Cache`).
7. Connection-string reference keys are
   `ConnectionStrings__<name>` (PascalCase prefix per Aspire
   convention, no transform on `<name>`).

### E. Coolify-internal hostname resolution property
   (FT-008 I-5)

8. The value written for every FT-008 env-var resolves the target
   service by its Coolify-internal hostname — i.e. the value
   contains the hostname token the destination's private network
   resolves to the target's container. Asserted indirectly by
   deploying the fixture, exec'ing into the `api` container, and
   running a TCP connectivity check against the value of
   `services__cache__tcp__0`: the connection succeeds. The exact
   format of the hostname token is NOT pinned by this test (it is
   implementation-defined against live Coolify v4 per FT-008 §Out
   of scope).

### F. Rule-based secret-flag policy (FT-008 I-6)

9. After a successful deploy, Coolify-side inspection shows:
   - `ConnectionStrings__db` carries the Coolify secret flag SET
     (because the resolved template contains an Aspire sentinel
     AND a secret parameter contributed),
   - `services__cache__http__0` carries the secret flag CLEAR
     (plain endpoint URL, no secret contribution),
   - `services__cache__tcp__0` carries the secret flag CLEAR.

### G. Target identity resolved against FT-005's in-phase map,
   not Coolify (FT-008 I-15)

10. Outbound HTTP trace for FT-008-attributed code paths shows
    zero GET calls against Coolify whose purpose is to discover
    target `serviceId` or hostname; both are read from FT-005's
    in-phase upserted-services map.

### H. `WithReference(parameter)` skipped silently
   (FT-008 §Behaviour §1; FT-007 owns parameter refs)

11. Adding a `.WithReference(someParameter)` edge on `api` (a
    parameter resource) produces zero FT-008-authored log lines
    about that edge and zero FT-008 env-var calls; FT-007's
    parameter-projection path handles it. Asserted by partitioning
    log lines by feature attribution.

### I. Target-not-deployed defensive symbol
   (FT-008 §Behaviour §2, E_REFERENCE_TARGET_NOT_DEPLOYED)

12. With FT-005's walk order deliberately re-ordered so that
    `api` is processed before `cache` (a regression scenario, not
    a normal v1 deploy), FT-008's lookup for `cache` against the
    in-phase map returns absent; the hook surfaces
    `E_REFERENCE_TARGET_NOT_DEPLOYED` naming `cache`; FT-005
    short-circuits the per-service trigger for `api`.

### J. Aggregated per-key failure (FT-008 I-8)

13. With two of three FT-008-owned env-var POSTs forced to fail
    non-2xx, the hook attempts every key before surfacing
    `E_REFERENCE_ENVVAR_UPSERT_FAILED`; the diagnostic names
    **both** failing keys (values are NOT included); the third
    key's env-var remains in Coolify (FT-008 I-9 — no rollback).

### K. Precedence — TARGET_NOT_DEPLOYED > UPSERT_FAILED > UNEXPECTED
   (FT-008 §"Sentinel-leak precedence")

14. In a scenario combining a missing target AND a failing
    env-var upsert in the same service hook, the surfaced symbol
    is `E_REFERENCE_TARGET_NOT_DEPLOYED` (and the env-var work for
    the missing-target ref was never attempted in any case).

### L. Sentinel-leak escalation uses the SHARED FT-007 symbol
   (FT-008 I-7, §"Sentinel-leak precedence")

15. With a Coolify mock response excerpt for an FT-008 env-var
    write deliberately containing an Aspire sentinel, the line
    containing the sentinel is suppressed and the surfaced symbol
    is `E_ENVVAR_SECRET_LEAKED` (the SAME literal FT-007 uses,
    NOT a bifurcated `E_REFERENCE_SECRET_LEAKED`).

### M. Redaction across all FT-008 surfaces

16. Grepping all captured stdout, stderr, Aspire structured logs,
    HttpClient request-body-log-attribution side, and exception
    `.Message` strings for the literal
    `SENTINEL_DB_PASSWORD_DO_NOT_LEAK_ref` returns zero matches
    across every scenario.

### N. Orphan reference env-vars left in place (FT-008 I-13)

17. Removing `.WithReference(cache)` from `api`'s AppHost
    definition and re-deploying produces zero FT-008-attributed
    GET / PATCH / DELETE against `services__cache__http__0` or
    `services__cache__tcp__0`; those keys remain on the Coolify
    service.

### O. Idempotency on unchanged AppHost (FT-008 I-12)

18. Two consecutive deploys against an unchanged AppHost produce
    zero net change in FT-008-written env-vars on the second run;
    every key takes the `unchanged` branch.

### P. Managed-field discipline on PATCH (FT-008 I-10)

19. When a value or secret-flag drifts out-of-band on an FT-008-
    written key and the deploy re-runs, the PATCH body sent
    contains only `value` and `secret-flag`. Unmanaged Coolify
    env-var fields are absent from the payload entirely.

### Q. No persistent on-disk state (FT-008 I-11)

20. Filesystem-diff before/after every scenario shows zero new
    files written by FT-008.

### R. Stable observable contract (FT-008 I-14)

21. The three FT-008 `E_…` literals
    (`E_REFERENCE_TARGET_NOT_DEPLOYED`,
     `E_REFERENCE_ENVVAR_UPSERT_FAILED`,
     `E_REFERENCE_PHASE_UNEXPECTED`) appear verbatim as the first
    whitespace-delimited token on stderr; the shared
    `E_ENVVAR_SECRET_LEAKED` literal is byte-identical to
    FT-007's.

## Pass criteria

All twenty-one assertion groups pass deterministically across at
least three consecutive runs against a freshly reset Coolify
instance. Any redaction failure (L §15 or M §16) is a hard fail.

## Out of scope

- Parameter projections (TC-011 / FT-007).
- Cross-destination references (post-v1).
- The exact slug/service-name format of the Coolify-internal
  hostname (implementation against live Coolify v4 per FT-008 I-5).
- The actual Coolify env-var `/api/v1/...` path / DTO (ADR-002).

## Validates

- FT-008 — Service-to-service network wiring — Aspire
  `WithReference()` edges into Coolify intra-destination env-vars.
- ADR-001 — Aspire-graph to Coolify-hierarchy mapping (v1).
- ADR-002 — Coolify API version and client strategy (v1).
- ADR-003 — Imperative deploy orchestration (v1).
- ADR-004 — Coolify auth model (v1, redaction discipline).