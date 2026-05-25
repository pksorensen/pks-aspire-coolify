---
id: TC-035
title: Workload push tag targets the in-project registry resolved FQDN
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

Asserts invariants I-3 and I-4: after the prereq phase
resolves the in-project registry's FQDN, every workload
attached to that registry via `WithContainerRegistry(...)`
builds and pushes against tags shaped
`<fqdn>/<workload.Name>:<apphost-version>` exactly.

## Acceptance

Given an AppHost with a `pks-agent-registry` resource that
Coolify resolves to FQDN `reg.example.test` and two
workloads `api` and `worker` attached to it, the build
phase emits two tags `reg.example.test/api:<v>` and
`reg.example.test/worker:<v>` and the push phase pushes to
`reg.example.test`. No tag carries a stale placeholder
address.