---
id: TC-036
title: Workload Application carries Private-Registry attachment for the in-project registry
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

Asserts invariant I-5: each sibling workload's Coolify
Application body, as emitted by `coolify-deploy` (FT-005),
carries an attachment to the in-project registry's Coolify
Private-Registry record. Property-only on the exact field
name — the test asserts the attachment is present and keyed
by the in-project registry's Private-Registry UUID, not the
JSON spelling.

## Acceptance

With `IApplicationsApi` mocked to capture the JSON payload of
the workload Application's create / update call, the captured
payload contains a field whose value equals the in-project
registry's Private-Registry UUID recorded during the prereq
phase. The same UUID appears for every workload attached to
the same in-project registry.