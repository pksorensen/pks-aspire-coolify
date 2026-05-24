---
id: TC-014
title: managed_dashboard_optin_warnings_never_fail_workload
type: exit-criteria
status: passing
validates:
  features:
  - FT-010
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ManagedDashboardExitCriteria
last-run: 2026-05-24T18:42:34.143245448+00:00
last-run-duration: 2.4s
---

## Purpose

Exit criteria for FT-010 (managed Aspire dashboard opt-in —
`WithManagedDashboard(dashboardToken)` deploy-time upsert). Pins the
five `W_…` warning symbols (NOT `E_…` — dashboard never fails the
workload, I-1), the opt-in / opt-out / silent-when-absent contract
(I-5, I-6), the audience-separation invariant (I-4), the
same-project-same-targeted-environment placement (I-3), the
publisher-pinned image tag (I-8), and the FT-006 handoff with the
`dashboard` tag (I-10).

## Fixture

AppHost identical to TC-009 plus:

```csharp
var dashboardToken = builder.AddParameter(
    "coolify-homelab-dashboard-token", secret: true);

builder.WithCoolifyDeploy(coolifyUrl, coolifyToken)
       .WithImageRegistry(prefix, user, pass)
       .WithCoolifyDestination(coolifyDest)
       .WithManagedDashboard(dashboardToken);
```

Two sentinel token values used to prove audience separation:
- deploy token = `SENTINEL_DEPLOY_TOKEN_DO_NOT_LEAK_dashboard`
- dashboard token = `SENTINEL_DASHBOARD_TOKEN_DO_NOT_LEAK`

FT-001 / FT-002 / FT-005 implemented. FT-006 is either implemented
or stubbed to record handles + tags.

## Assertions

### A. Registration discipline (FT-010 §0)

1. `WithManagedDashboard(null)` throws `ArgumentNullException` at
   AppHost build time naming the offending argument.
2. Calling twice is **last-call-wins** on the token handle; the
   opt-in flag stays true.
3. Calling zero times leaves the flag false (assertion N below
   covers silent opt-out).

### B. Dashboard NEVER fails the workload (FT-010 I-1)

Five independent runs, each forcing exactly one warning-path
failure, all of which must result in zero workload impact:

4. **`W_DASHBOARD_TOKEN_MISSING`** — dashboard-token parameter
   unset; the workload deploy proceeds to completion; exit code
   is zero (workload succeeded). Stderr first-token literal is
   `W_DASHBOARD_TOKEN_MISSING`.
5. **`W_DASHBOARD_UPSERT_FAILED`** — Coolify forced to return
   500 on the dashboard service upsert; workload proceeds; exit
   code zero. Stderr first-token `W_DASHBOARD_UPSERT_FAILED`.
6. **`W_DASHBOARD_ENVVAR_FAILED`** — one of the three required
   env-var writes (COOLIFY_API_URL / COOLIFY_API_TOKEN /
   COOLIFY_PROJECT_UUID) forced to return non-2xx; workload
   proceeds; exit code zero. Stderr first-token
   `W_DASHBOARD_ENVVAR_FAILED`. Diagnostic names the failing
   var name(s); values never appear.
7. **`W_DASHBOARD_TRIGGER_FAILED`** — dashboard deploy-action
   trigger forced to return non-2xx; workload proceeds; exit
   code zero. Stderr first-token `W_DASHBOARD_TRIGGER_FAILED`.
   No dashboard handle is appended to the FT-006 list.
8. **`W_DASHBOARD_UNEXPECTED`** — synthetic exception thrown
   inside the dashboard sub-phase body; workload proceeds; exit
   code zero. Stderr first-token `W_DASHBOARD_UNEXPECTED`;
   inner `.Message` appended; no stack trace.

### C. Dashboard sub-phase runs only after clean workload deploy
   (FT-010 I-2)

9. With FT-005 forced to short-circuit with
   `E_COOLIFY_SERVICE_UPSERT_FAILED` on a workload resource, the
   dashboard sub-phase does NOT execute: outbound HTTP trace
   shows zero GET / POST / PATCH against the
   `coolify-aspiredashboard` service name. The deploy exits
   non-zero with the workload `E_…` symbol; no `W_…` from FT-010
   appears.

### D. Same project + targeted environment (FT-010 I-3)

10. On the happy path, Coolify-side inspection shows the
    dashboard service is upserted under FT-005's resolved
    `(projectId, environmentId)`. The dashboard does NOT appear
    in any sibling environment, does NOT appear in any other
    Coolify project.

### E. Audience separation honoured (FT-010 I-4)

11. With deploy token = `SENTINEL_DEPLOY_TOKEN_DO_NOT_LEAK_dashboard`
    and dashboard token = `SENTINEL_DASHBOARD_TOKEN_DO_NOT_LEAK`,
    inspecting the outgoing env-var write payload for
    `COOLIFY_API_TOKEN` on the dashboard service shows the value
    is the **dashboard** sentinel, NOT the deploy sentinel. The
    deploy sentinel never appears on any dashboard-attributed
    env-var write.

### F. Image tag is a publisher-pinned constant (FT-010 I-8)

12. The image tag observed on the dashboard service upsert is
    the publisher-pinned constant
    (e.g. `ghcr.io/<org>/coolify-aspiredashboard:<pinned-version>`).
    No code path reads the tag from a parameter, env-var, config
    file, or any other runtime source. Asserted by source-level
    inspection of FT-010 + happy-path trace.

### G. Three required env-vars on the dashboard service
   (FT-010 §Behaviour §4)

13. After a successful opt-in deploy, Coolify-side inspection of
    the dashboard service shows exactly the three env-vars:
    `COOLIFY_API_URL` = the configured Coolify base URL string,
    `COOLIFY_API_TOKEN` = the resolved dashboard-token value
    (secret-flag SET), and `COOLIFY_PROJECT_UUID` = the project
    UUID captured by FT-005 step 3.

### H. Name-keyed upsert + managed-field discipline (FT-010 I-9)

14. The dashboard service upsert follows GET-by-name then
    POST-or-PATCH. PATCH bodies contain only `image` and
    `destination-binding`; unmanaged fields (FQDN, port,
    healthcheck, restart policy) are absent from the PATCH
    payload entirely.

### I. Aggregated env-var failure attempts all three
   (FT-010 §Behaviour §4)

15. With two of three env-var writes forced to fail and the
    third succeeding, the diagnostic
    `W_DASHBOARD_ENVVAR_FAILED` names BOTH failing var-names;
    workload still exits zero.

### J. FQDN surfacing (FT-010 §Outputs)

16. After a successful upsert, when Coolify reports a non-empty
    FQDN, the deploy log contains a `managed-dashboard:
    url=https://<fqdn>` line.
17. When Coolify reports an empty / null FQDN, the deploy log
    contains a `managed-dashboard: url=<pending — check Coolify
    UI for assigned domain>` line. Neither line affects exit
    code.

### K. Dashboard handle appended with `dashboard` tag to FT-006
   list (FT-010 I-10)

18. On a successful dashboard trigger, the dashboard's
    deploy-job handle is appended to FT-005's in-phase
    deploy-action handle list with a `dashboard` tag.
19. On a failed dashboard trigger
    (`W_DASHBOARD_TRIGGER_FAILED`), no handle is appended to the
    list; FT-006 polls only the workload handles.

### L. Last-call-wins idempotency (FT-010 I-5)

20. Calling `WithManagedDashboard(...)` twice with different
    token handles leaves the second handle in effect; happy-path
    deploy uses the second token (asserted via sentinel
    distinction).

### M. Token redaction (FT-010 I-7)

21. Grepping captured stdout, stderr, Aspire structured logs,
    dashboard parameter snapshot, and any
    captured-and-rethrown exception `.Message` strings for the
    literal `SENTINEL_DASHBOARD_TOKEN_DO_NOT_LEAK` returns zero
    matches across every scenario in B / D / E / G / I / L.

### N. Opt-out is silent (FT-010 I-6)

22. An otherwise-identical AppHost that never calls
    `WithManagedDashboard(...)` produces a deploy log
    byte-identical (after stripping timestamps) to a deploy log
    from a publisher build with FT-010 absent: zero
    dashboard-attributed log lines, zero `W_…` symbols, zero
    Coolify calls against the `coolify-aspiredashboard` service
    name.

### O. Phase boundary preserved (FT-010 I-11)

23. The dashboard sub-phase contributes log lines under the
    `deploy` phase attribution. No `dashboard: enter / dashboard:
    exit` boundary line appears; the five-phase ADR-003 contract
    is intact.

### P. No persistent state on disk (FT-010 I-12)

24. Filesystem-diff before/after every scenario (success and all
    five warning-paths) shows zero new files written by FT-010.

### Q. Stable observable contract (FT-010 I-13)

25. The five `W_…` literals appear verbatim as the first
    whitespace-delimited token on stderr for the matching
    failure; all carry `severity: warning` on the first line per
    FT-010 §Outputs.

## Pass criteria

All twenty-five assertion groups pass deterministically across at
least three consecutive runs. Any token-leak detection (M §21) is a
hard fail. Any workload-impact failure (B §4–§8 producing non-zero
exit code) is a hard fail.

## Out of scope

- Building / publishing the `coolify-aspiredashboard` image itself.
- The Coolify-aware resource provider plugin inside the dashboard
  image.
- Dashboard configuration knobs (image-tag override, port, FQDN,
  env-var passthrough) — deferred to a later feature.
- `verify`-phase policy for dashboard verification failure — FT-006
  reads the `dashboard` tag and decides; FT-010 only sets the tag.
- Tear-down of the dashboard service when opt-in is removed (no GC
  in v1).
- TypeScript AppHost parity (TC-015 / FT-011).

## Validates

- FT-010 — Managed Aspire dashboard opt-in —
  `WithManagedDashboard()` deploy-time upsert.
- ADR-001 — Aspire-graph to Coolify-hierarchy mapping (v1).
- ADR-003 — Imperative deploy orchestration (v1).
- ADR-004 — Coolify auth model (v1, audience separation).