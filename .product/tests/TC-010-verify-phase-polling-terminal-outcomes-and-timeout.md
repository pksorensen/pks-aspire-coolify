---
id: TC-010
title: verify_phase_polling_terminal_outcomes_and_timeout
type: exit-criteria
status: unimplemented
validates:
  features:
  - FT-006
  adrs: []
phase: 1
runner: custom
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~VerifyPhaseExitCriteria
---

## Purpose

Exit criteria for FT-006 (verify phase — poll Coolify deploy-action
handles to terminal outcome or overall timeout). Pins the three
`E_…` symbols, the verify-is-the-gate invariant (I-1), the
non-rolling-back read-only contract (I-2), the phase-level timeout
bound (I-3), the aggregation discipline (I-4), the 60-second
per-poll cap invariant under `WithVerifyPolling(...)` (I-9), and the
`E_VERIFY_TIMEOUT` > `E_VERIFY_FAILED` precedence on mixed outcomes
(I-11).

## Fixture

AppHost identical to TC-009. FT-001 / FT-002 / FT-005 are assumed
implemented and produce an in-phase deploy-action handle list with
three handles (`api`, `redis`, plus a third stub service for
multi-handle aggregation scenarios). Coolify v4 instance meeting
ADR-002's floor. The `client.DeployJobs.GetStatusAsync(handle, …)`
surface is implemented (or stubbed) per ADR-002.

## Assertions

### A. `WithVerifyPolling(...)` registration discipline
   (FT-006 §0)

1. Calling `WithVerifyPolling(null, timeout)` or
   `WithVerifyPolling(interval, null)` throws
   `ArgumentNullException` at AppHost build time naming the
   offending argument.
2. Calling with `TimeSpan.Zero` or a negative `TimeSpan` for either
   argument throws `ArgumentOutOfRangeException` at AppHost build
   time naming the offending argument.
3. Calling twice is **last-call-wins** (FT-006 §0): the verify
   phase uses the second pair's values.
4. Calling zero times leaves the v1 defaults in place (5s initial
   interval, 10min total timeout — asserted indirectly by
   observing default cadence in B §5).

### B. Happy path — all handles succeed (FT-006 I-1)

5. With every handle reaching `succeeded` on its first poll, the
   verify phase exits normally with `verify: enter … verify: exit
   (ok)` and exit code zero. Exactly one `verify-progress` log
   line per handle is emitted (the transition from unobserved to
   `succeeded`).
6. The first poll per handle occurs at or after the configured
   `interval`; subsequent re-polls (when a handle is still
   non-terminal) occur at doubling intervals capped at 60s
   regardless of the configured `interval` value.

### C. Empty handle list short-circuits (FT-006 I-8)

7. Given an empty deploy-action handle list from FT-005 (e.g. an
   AppHost with zero containerisable resources after FT-009 filter),
   verify exits normally with `verify: enter … verify: exit (ok)`,
   zero `verify-progress` lines, and **zero** outbound HTTP
   requests.

### D. Per-handle terminal-failure aggregation
   (FT-006 I-4, §Behaviour §3.iv)

8. With two of three handles reaching terminal `failed` (or
   equivalent non-success state per ADR-002 enumeration) and the
   third reaching `succeeded`, deploy exits non-zero with stderr
   first-token `E_VERIFY_FAILED`. The diagnostic names **both**
   failing handles with their `(resource, deploy-job-url,
   terminal-state)` tuples; the successful handle is NOT named in
   the diagnostic field block (only offending services).
9. Successfully-deployed siblings remain in Coolify; **zero**
   DELETE / PATCH / POST is issued by FT-006 against any
   Coolify endpoint (verify is read-only — FT-006 I-2).

### E. Timeout (FT-006 I-3)

10. With every handle stuck in a non-terminal state, deploy exits
    non-zero with stderr first-token `E_VERIFY_TIMEOUT` no later
    than `timeout + (one in-flight GET round-trip)` after
    `verify: enter`. The diagnostic field block carries `elapsed:`
    and lists every still-pending `(resource, deploy-job-url,
    last-observed-state)` tuple.

### F. Mixed-outcome precedence (FT-006 I-11)

11. With one handle reaching `failed` and another still
    non-terminal at the configured `timeout`, the surfaced symbol
    is `E_VERIFY_TIMEOUT` (not `E_VERIFY_FAILED`); the failing
    handle's terminal state is still recorded in its
    `verify-progress` line.

### G. 60-second cap invariant under `WithVerifyPolling(...)`
   (FT-006 I-9)

12. With `interval: TimeSpan.FromMinutes(5)`, observed polls after
    the **first** occur at most 60 seconds apart per handle. (The
    first poll is allowed to wait the full configured interval;
    subsequent polls under exponential backoff are capped at 60s.)

### H. Transient transport failure tolerance
   (FT-006 §Error handling)

13. A single 502 response from `GetStatusAsync` mid-loop is treated
    as "state unchanged, sleep and retry" — it does NOT surface
    `E_VERIFY_PHASE_UNEXPECTED`. The loop continues until terminal
    state or `timeout`.

### I. 404 on a handle is `failed`, not unexpected
   (FT-006 §Error handling)

14. A handle whose `GetStatusAsync` returns 404 (the trigger said
    the job was accepted but the job has vanished) is accumulated
    into the `VERIFY_FAILED` bucket with the 404 status in the
    `last-observed` field; the surfaced symbol is `E_VERIFY_FAILED`
    (or `E_VERIFY_TIMEOUT` per F precedence if other handles also
    time out).

### J. No endpoint outside the deploy-job-status group is
   contacted (FT-006 I-7)

15. Outbound HTTP trace during the verify phase shows zero
    requests against project / environment / service / destination
    / registry / env-var endpoints; every request targets the
    deploy-job-status group and is a GET.

### K. Deploy-job URL composition is path-free (FT-006 I-12)

16. The deploy-job URL surfaced in `E_VERIFY_FAILED` /
    `E_VERIFY_TIMEOUT` diagnostics is composed via the
    `coolify-api` client (e.g. `client.DeployJobs.GetHumanUrl(handle)`)
    and contains no FT-006-authored hard-coded path fragment
    (asserted by inspecting FT-006's source — no string literal
    matching `"/projects/"` / `"/deployments/"`).

### L. No persistent on-disk publisher state (FT-006 I-6)

17. Filesystem-diff of the AppHost directory before and after each
    verify-phase exit (success, failed, timeout) shows zero new
    files.

### M. Cancellation pre-empts both sleep and timeout
   (FT-006 §Cancellation)

18. Triggering cancellation mid-sleep exits the publisher with
    FT-001's cancellation diagnostic (NOT an `E_…` symbol),
    immediately, before either the configured interval or the
    configured timeout elapses.

### N. Phase-boundary observability (FT-006 I-10)

19. Every fail-fast exit emits `verify: enter … verify: exit
    (failed)`. Successful exit emits
    `verify: enter … verify: exit (ok)`.

### O. Stable observable contract (FT-006 I-5)

20. The three `E_…` literals (`E_VERIFY_FAILED`, `E_VERIFY_TIMEOUT`,
    `E_VERIFY_PHASE_UNEXPECTED`) appear verbatim as the first
    whitespace-delimited token on stderr for the matching failure.

## Pass criteria

All twenty assertion groups pass deterministically across at least
three consecutive runs against a freshly reset Coolify instance.

## Out of scope

- Deploy-phase upserts and trigger semantics (TC-009 / FT-005).
- Per-service health probing on the deployed surface (post-v1).
- Automatic retry of failed deploy-actions (post-v1).
- The actual Coolify deploy-job-status `/api/v1/...` paths / DTOs
  (ADR-002).

## Validates

- FT-006 — Verify phase — poll Coolify deploy-action handles until
  each per-service deploy completes.
- ADR-002 — Coolify API version and client strategy (v1).
- ADR-003 — Imperative deploy orchestration (v1).
