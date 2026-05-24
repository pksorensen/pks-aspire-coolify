---
id: TC-006
title: configure_phase_exit_criteria_diagnostics_and_no_side_effects
type: exit-criteria
status: unimplemented
validates:
  features:
  - FT-002
  adrs: []
phase: 1
runner: custom
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ConfigurePhaseExitCriteria
---

## Purpose

Exit criteria for FT-002 (configure phase — token resolution + combined
version+auth probe). Composes with TC-002 (ADR-002 client/probe scenario)
and TC-004 (ADR-004 token-per-instance scenario): TC-002 / TC-004 pin
client and auth behaviour against real fixtures; TC-006 pins the exact
shape and precedence of FT-002's four `E_…` symbols, the I-1 "at most
one round-trip per deploy" invariant, and the zero-side-effect guarantee
on every fail-fast path that FT-002's §Invariants names.

Closes W003 on FT-002 (currently only scenario-typed TCs are linked).

## Fixture

Representative AppHost identical to TC-002 / TC-004 (one project, one
container, one non-containerisable, two declared Aspire environments).
Coolify v4 instance reachable at a parameterised URL with a valid
bearer token. Sentinel token literal `SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9`
re-used from TC-004 §4 for redaction assertions.

The publisher under test is the FT-001 skeleton + FT-002 configure-phase
body wired together. No other phase bodies (FT-003+) are required to be
implemented for this TC — `build` / `push` / `deploy` / `verify` may be
no-op skeleton shells.

## Assertions

### A. Single round-trip (FT-002 I-1)

1. On a happy-path `aspire deploy --environment Production` the
   captured outbound HTTP trace shows **exactly one** authenticated
   request originating from the `configure` phase. That request is a
   `GET` carrying `Authorization: Bearer …`. No second probe / "double
   check" request appears.

### B. Four `E_…` symbols — precedence rule (FT-002 §Behaviour
   precedence; I-5; I-6)

The precedence is asserted by four independent runs, each targeted at
forcing exactly one outcome and verifying the matching literal appears
as the first whitespace-delimited token on stderr:

2. **`E_AUTH_TOKEN_MISSING`** — token parameter unset; deploy exits
   non-zero; **zero** outbound HTTP requests are issued (the token
   check short-circuits before any network I/O); stderr first-token
   matches `E_AUTH_TOKEN_MISSING`; remediation block shows both the
   `dotnet user-secrets set Parameters:<name>` and
   `Parameters__<name>` forms.
3. **`E_AUTH_TOKEN_INVALID`** — token set, Coolify returns `401`
   (and a separate run with `403`); both runs exit non-zero with
   stderr first-token `E_AUTH_TOKEN_INVALID`; the structured field
   block names the resolved URL and the `coolify-*-token` parameter
   name; **no** distinguishing between `401` and `403` appears in
   the symbol (ADR-004 "one opaque error path for auth").
4. **`E_COOLIFY_VERSION_BELOW_FLOOR`** — Coolify returns `200 OK`
   with a version string strictly below the
   `SupportedCoolifyVersions` floor; deploy exits non-zero; stderr
   first-token `E_COOLIFY_VERSION_BELOW_FLOOR`; the structured
   field block carries `observed: <version>`, `required: >= <floor>`,
   and `see: SUPPORTED_COOLIFY_VERSIONS.md`.
5. **`E_COOLIFY_UNREACHABLE`** — exercised four ways in independent
   runs: probe returns `404`; probe returns `5xx`; probe times out;
   probe connection refused. Each run exits non-zero with stderr
   first-token `E_COOLIFY_UNREACHABLE`. Additionally the
   "`200 OK` with unparseable version body" case also routes here
   (FT-002 §"Error handling": "could not determine Coolify
   version → `E_COOLIFY_UNREACHABLE`"). No run misclassifies as
   `VERSION_BELOW_FLOOR`.

### C. Phase-boundary observability (FT-002 I-7)

6. On every fail-fast run in B (2–5) the deploy log shows
   `configure: enter … configure: exit (failed)` with **no**
   `build: enter` line. On the happy-path run in A (1) the deploy
   log shows `configure: enter … configure: exit (ok)` followed by
   `build: enter`.

### D. Zero Coolify side-effect on every fail-fast (FT-002 I-2)

7. For each of scenarios 2, 3, 4, and 5, a Coolify-side snapshot
   diff (project list, environment list, application/service list,
   environment-variable list, private-registries list) taken
   immediately before and immediately after the deploy invocation
   shows **zero** changes. Specifically: the `MISSING` and
   `UNREACHABLE-refused` cases do not even issue HTTP requests; the
   `INVALID`, `VERSION_BELOW_FLOOR`, and `UNREACHABLE-{404,5xx,timeout}`
   cases issue the single probe and nothing else.

### E. Redaction (FT-002 I-3)

8. With token value `SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9`, running each
   scenario in B (2–5) — and the happy path in A (1) — and grepping
   captured stdout, stderr, Aspire structured logs, the dashboard
   parameter snapshot, and any thrown-and-handled exception
   `.Message` strings for the literal sentinel returns **zero**
   matches across all five runs.

### F. Token resolution happens exactly once (FT-002 I-4)

9. Outbound HTTP traces across the happy-path run carry an
   `Authorization` header whose bearer matches the resolved value
   on every request the publisher issues from `configure` onwards;
   no intra-deploy re-resolution call against Aspire's parameter
   API is observed (asserted by counting calls to the parameter-
   resolution surface — exactly one for the token, exactly one for
   the URL, per deploy).

### G. Diagnostic shape is the stable observable contract (FT-002
   I-5)

10. The stderr first-token spellings in B (2–5) match the four
    literals exactly (uppercase, underscores, no trailing
    punctuation). Each diagnostic carries the structured field
    block FT-002 §Outputs prescribes — `parameter:` for MISSING /
    INVALID, `url:` for INVALID / VERSION_BELOW_FLOOR /
    UNREACHABLE, `observed: / required: / see:` for
    VERSION_BELOW_FLOOR, and a `remediation:` block where FT-002
    requires one.

## Pass criteria

All ten assertion groups pass deterministically across at least three
consecutive runs against a freshly reset Coolify instance. Any
redaction failure (E §8) is a hard fail regardless of other results.

## Out of scope

- Build / push / deploy / verify phase behaviour (TC-003 / TC-005
  / per-phase TCs).
- The Coolify endpoint path, DTO shape, or `SupportedCoolifyVersions`
  constant value (ADR-002 + `coolify-api` domain).
- The `aspire coolify whoami` standalone CLI verb — explicitly out of
  v1 (FT-002 §Out of scope).

## Validates

- FT-002 — Configure phase — token resolution and combined
  version + auth probe.
- ADR-002 — Coolify API version and client strategy (v1).
- ADR-004 — Coolify auth model (v1).
