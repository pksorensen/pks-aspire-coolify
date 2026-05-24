---
id: TC-008
title: push_phase_and_configure_private_registry_upsert
type: exit-criteria
status: unimplemented
validates:
  features:
  - FT-004
  adrs: []
phase: 1
runner: custom
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~PushPhaseExitCriteria
---

## Purpose

Exit criteria for FT-004 (push phase + configure-phase Coolify
Private Registry upsert). Composes with TC-005 (ADR-005 scenario):
TC-005 pins the registry-side behaviour and credential-upsert keys;
TC-008 pins the four `E_…` symbols, the anonymous-vs-credentialled
branch, the aggregation discipline (I-9), and the
configure-phase-vs-push-phase boundary attribution.

## Fixture

Same AppHost shape as TC-007 (two containerisable resources
`web` and `worker`, `<apphost-version>=1.2.3-test`). The build phase
(FT-003) is assumed implemented and producing local image-cache
entries at the deterministic tags. Two registry harnesses available
as in TC-005 (authenticated `registry:2` + anonymous `registry:2`),
plus a Coolify v4 instance meeting ADR-002's floor with a valid
deploy token.

Sentinel literal
`SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_push_phase` for redaction.

## Assertions

### A. Anonymous-push path issues zero Coolify Private Registry
   calls (FT-004 I-1)

1. With `WithImageRegistry(prefix, …)` called with `credentials`
   omitted (anonymous), the configure-phase contribution issues
   **zero** GET / POST / PATCH against the Coolify
   `PrivateRegistries` endpoint group. A single debug-level
   structured log line records the skip.
2. The push phase succeeds against the anonymous registry; the
   service-upsert payload later seen on the deploy-phase trace
   (when FT-005 lands) carries no `registry-reference` field
   (asserted via request-body inspection or via a stub deploy
   harness; FT-004 I-15 cross-validated against FT-005).

### B. Credentialled path upserts exactly once and is idempotent
   (FT-004 I-2)

3. With `(username, password)` set and the Coolify Private
   Registries list initially empty for `(host, username)`, a
   first deploy issues exactly one `POST` carrying
   `host=<derived>`, `username=<value>`, the marker-suffix
   `managed-by: pks-aspire-coolify` on the record name, and zero
   `PATCH` calls.
4. A second identical deploy with unchanged password issues **zero**
   `POST` and **zero** `PATCH` against the same endpoint group.
5. Rotating `registry-password` and re-running issues **zero**
   `POST` and **exactly one** `PATCH` against the matching record.
6. Changing `username` (new tuple) issues exactly one `POST` for a
   second record; the original record is **not** deleted.

### C. Host derivation from prefix (FT-004 §Configure §3)

7. `prefix=ghcr.io/pksorensen/myapp` derives `host=ghcr.io`.
8. `prefix=registry.lan:5000/myapp` derives
   `host=registry.lan:5000`.

### D. Configure-phase upsert failure refuses to advance to build
   (FT-004 I-7) and leaves Coolify byte-identical on managed
   fields (FT-004 I-8)

9. With the Coolify `PrivateRegistries` endpoint forced to return
   500, deploy exits non-zero with stderr first-token
   `E_COOLIFY_REGISTRY_UPSERT_FAILED`; the diagnostic field block
   carries `registry: <host>`, `username: <value>`,
   `see: ADR-005 §D5`; the deploy log shows
   `configure: enter … configure: exit (failed)` with **no**
   `build: enter` line; a Coolify snapshot diff of the
   `private-registries` list before/after shows zero changes.
10. With the `(username, password)` pair resolving asymmetrically
    at runtime (one non-empty, one empty), the configure-phase
    contribution surfaces `E_COOLIFY_REGISTRY_UPSERT_FAILED` with
    a remediation pointing at ADR-005 §1's "credentials travel as
    a pair" rule. No Coolify mutation is attempted.

### E. Push uses FT-003's exact image tag (FT-004 I-5) and no
   `:latest` (FT-003 I-2 re-asserted)

11. The push-phase outbound trace for the happy-path deploy
    contains exactly two image-tag references, byte-identical to
    the tags FT-003 attached to the local image cache. Grepping
    the captured trace and the deploy log for `:latest` returns
    zero matches.

### F. Aggregated push failure classification and precedence
   (FT-004 I-9, §Behaviour §4)

12. With two of three resources' pushes failing with `401`
    (auth refused) and the third pushing successfully, deploy
    exits non-zero with stderr first-token
    `E_REGISTRY_AUTH_FAILED`; the diagnostic names **all** failing
    `(resource, tag)` pairs (auth-failure aggregation wins per
    FT-004 Behaviour §4).
13. With two of three resources' pushes failing with mixed errors
    (one `401` and one connection-refused) and the third pushing
    successfully, the surfaced symbol is `E_REGISTRY_AUTH_FAILED`
    (auth wins) and the non-auth failure is listed under a
    "and N additional non-auth failures" line in the diagnostic.
14. With all push failures non-auth (e.g. all `5xx`), the
    surfaced symbol is `E_IMAGE_PUSH_FAILED` and every failing
    pair appears in the diagnostic. Successfully-pushed siblings
    remain in the registry (not "unpushed").

### G. Push failure refuses to advance to deploy (FT-004 I-6)

15. On any of the push-failure cases (F §12–§14), no log line
    attributed to the `deploy` phase appears, and no Coolify
    application/service GET, POST, or PATCH is issued by the
    publisher.

### H. Phase-boundary attribution (FT-004 I-10)

16. Configure-phase upsert failure: log shows
    `configure: enter … configure: exit (failed)` with **no**
    `build: enter`.
17. Push-phase failure: log shows
    `push: enter … push: exit (failed)` with **no**
    `deploy: enter`.

### I. Redaction (FT-004 I-3)

18. With `password` set to the sentinel literal, grepping all
    captured stdout, stderr, Aspire structured logs, push-pipeline
    progress lines re-emitted into the deploy log, and any
    captured-and-rethrown exception messages returns **zero**
    matches for the sentinel across every scenario in B / D / F /
    G. The `username` value may appear in logs (non-secret); the
    password never may.

### J. Stable observable contract (FT-004 I-4)

19. The four `E_…` literals (`E_COOLIFY_REGISTRY_UPSERT_FAILED`,
    `E_REGISTRY_AUTH_FAILED`, `E_IMAGE_PUSH_FAILED`,
    `E_PUSH_PHASE_UNEXPECTED`) appear verbatim as the first
    whitespace-delimited token on stderr for the matching
    failure.

## Pass criteria

All nineteen assertion groups pass deterministically across at least
three consecutive runs against freshly reset registry harnesses and
a freshly reset Coolify instance. Any redaction failure (I §18) is a
hard fail.

## Out of scope

- Build-phase image production (TC-007).
- Deploy / verify (TC-009 / TC-010).
- The actual Coolify `/api/v1/...` paths and DTOs (ADR-002 +
  `coolify-api` domain).

## Validates

- FT-004 — Push phase + configure-phase Coolify Private Registry
  upsert.
- ADR-005 — Image registry strategy (v1).
- ADR-003 — Imperative deploy orchestration (v1).
