---
id: TC-034
title: Prereq phase is a no-op when no in-project registry exists
type: absence
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

Asserts invariant I-2: an AppHost that uses only external
registries (e.g. `ghcr.io/...` via `AddContainerRegistry`) sees
the `coolify-prereq` phase as a no-op — zero Coolify-side
calls, zero log differences attributable to the phase, and
none of the three FT-016 symbols emitted.

## Acceptance

Run a deploy against an AppHost whose resource graph contains
no `PksAgentRegistryResource`. Assert that the prereq phase
issues zero HTTP calls to Coolify, that
`E_PREREQ_REGISTRY_DEPLOY_FAILED`,
`E_PREREQ_REGISTRY_UNREACHABLE`, and
`W_REGISTRY_FQDN_FALLBACK` do not appear in stderr, and that
`coolify-build` runs immediately after `coolify-configure`
with no observable interleaved phase body.