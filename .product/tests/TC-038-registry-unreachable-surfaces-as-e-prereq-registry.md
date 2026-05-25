---
id: TC-038
title: Registry unreachable surfaces as E_PREREQ_REGISTRY_UNREACHABLE and halts before build
type: exit-criteria
status: passing
validates:
  features:
  - FT-016
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~PrereqPhaseExitCriteria
last-run: 2026-05-25T15:47:56.997430725+00:00
last-run-duration: 2.4s
---

## Description

Asserts invariant I-7 and the error contract for
`E_PREREQ_REGISTRY_UNREACHABLE`.

## Acceptance

With the registry-Application deploy succeeding but the
resolved FQDN's `/v2/` probe configured (in the test harness)
to return 5xx / connection-refused for the entire probe
budget, the prereq phase exits with
`E_PREREQ_REGISTRY_UNREACHABLE` as the first whitespace-
delimited token on stderr. The structured field block names
the registry resource, the resolved FQDN, the probe URL,
the elapsed time, and the last network-level error. The
`coolify-build` phase does not run.