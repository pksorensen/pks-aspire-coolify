---
id: TC-033
title: Pipeline phase order places prereq between configure and build
type: invariant
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

Asserts the hard invariant I-1 of FT-016: the new
`coolify-prereq` phase runs strictly after `coolify-configure`
and strictly before `coolify-build`, and no workload image is
built before every in-project registry has cleared the prereq
phase.

## Acceptance

The publisher's phase enumeration / step graph, inspected at
AppHost build time, yields the order
`configure → prereq → build → push → deploy → verify`. A test
harness recording phase-entry callbacks observes prereq's
entry before build's entry on every run that includes at
least one in-project registry.