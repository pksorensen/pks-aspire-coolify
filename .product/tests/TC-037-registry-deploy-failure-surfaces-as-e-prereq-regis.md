---
id: TC-037
title: Registry deploy failure surfaces as E_PREREQ_REGISTRY_DEPLOY_FAILED and halts before build
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

Asserts invariant I-6 and the error contract for
`E_PREREQ_REGISTRY_DEPLOY_FAILED`.

## Acceptance

With `IApplicationsApi` configured to return non-2xx on the
registry-Application's create / upsert / deploy-trigger call
(or to drive the polled deploy-action to a terminal `failed`
state), the prereq phase exits with
`E_PREREQ_REGISTRY_DEPLOY_FAILED` as the first whitespace-
delimited token on stderr. The structured field block names
the registry resource, the project UUID, and (when reachable)
the Application UUID and deploy-action UUID. The
`coolify-build` phase does not run; no sibling workload image
is built; no `coolify-push` HTTP calls are observed.