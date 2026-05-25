---
id: TC-029
title: workload_without_any_registry_edge_fails_fast_with_e_registry_not_configured
type: exit-criteria
status: passing
validates:
  features:
  - FT-014
  adrs:
  - ADR-007
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~RegistryEdgeExitCriteriaTests.TC029
last-run: 2026-05-25T10:16:19.305655314+00:00
last-run-duration: 2.3s
---

## Description

Exit-criteria test for FT-014 / ADR-007 §Test coverage
"`E_REGISTRY_NOT_CONFIGURED` triggered by missing edge." Asserts that
a containerisable workload with neither an explicit
`WithContainerRegistry(...)` edge nor a shim-synthesised edge fails
fast at the start of the build phase with the literal symbol
`E_REGISTRY_NOT_CONFIGURED`, names the offending workload, and
points the user at both the native and the deprecated shim shape in
the remediation block.

## Setup

- An AppHost that declares:
  - `builder.AddDockerComposeEnvironment("env").WithCoolifyDeploy(
    url, token);`
  - one project workload added with **no**
    `WithContainerRegistry(...)` call
  - **no** call to `WithImageRegistry(...)` (so the shim never runs
    and never synthesises an edge for this workload)
- Fakes as in TC-027.

## Steps

1. Run the publisher's deploy hook against the test AppHost.
2. Capture stderr and the publisher's exit code.
3. Scan the deploy log for phase-boundary lines.
4. Inspect the fake build pipeline for any invocations.
5. Inspect the fake push pipeline for any invocations.
6. Inspect the fake `ICoolifyClient` for any
   `PrivateRegistries.UpsertAsync` invocations.

## Expected

- Publisher exited non-zero.
- The first whitespace-delimited token on the first line of stderr
  is the literal `E_REGISTRY_NOT_CONFIGURED` (composes with FT-003
  I-10).
- The structured field block names the offending workload's Aspire
  resource name in a `resource:` line.
- The remediation block contains both:
  - the native shape (`AddContainerRegistry(...)` +
    `WithContainerRegistry(...)`), and
  - the deprecated shim shape (`WithImageRegistry(prefix, [user,
    pass])`),
  and cites ADR-007 in the `see:` line.
- The deploy log shows `build: enter … build: exit (failed)` and
  no `push: enter` (FT-003 I-9).
- The build pipeline was never invoked.
- The push pipeline was never invoked.
- `PrivateRegistries.UpsertAsync` was never invoked (the failure
  fires before the upsert step has had a chance to run for this
  workload — and even if configure ran first, no registry edge
  exists to produce a `(host, username)` pair).