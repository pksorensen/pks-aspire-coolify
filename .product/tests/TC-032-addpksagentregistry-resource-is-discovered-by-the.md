---
id: TC-032
title: AddPksAgentRegistry resource is discovered by the publisher
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
last-run-duration: 2.5s
---

## Description

Asserts that the `coolify-prereq` phase enumerates every
`PksAgentRegistryResource` declared via `AddPksAgentRegistry(...)`
(FT-015) in the resource graph and treats each as an in-project
registry requiring provisioning before `coolify-build`.

## Acceptance

Given an AppHost that calls `AddPksAgentRegistry("reg")` once and
attaches at least one workload via `WithContainerRegistry(reg)`,
the `coolify-prereq` phase visits exactly one in-project
registry. Given an AppHost with two `AddPksAgentRegistry(...)`
calls, the phase visits exactly two. Given an AppHost with zero
such calls, the phase visits zero (composes with TC-034).