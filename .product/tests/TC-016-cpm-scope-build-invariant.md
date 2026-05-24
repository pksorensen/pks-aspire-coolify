---
id: TC-016
title: cpm_scope_build_invariant
type: invariant
status: unimplemented
validates:
  features: []
  adrs:
  - ADR-006
phase: 1
---

## Description

Build-system invariant test enforcing the Central Package Management (CPM)
scope decided in ADR-006: CPM is on for `src/` and `tests/` (via sibling
`Directory.Packages.props` files) and off everywhere else (no root file, no
file under `examples/**`).

## Assertions

1. `src/Directory.Packages.props` exists at exactly that path.
2. `tests/Directory.Packages.props` exists at exactly that path.
3. No `Directory.Packages.props` file exists at the repository root.
4. No `Directory.Packages.props` file exists anywhere under `examples/**`.
5. Every `*.csproj` under `src/**` and `tests/**` uses bare
   `<PackageReference Include="..." />` form — no `Version=` attribute on
   any PackageReference element (CPM forbids it; presence indicates either
   stale scaffolding or accidental CPM-bypass).
6. A representative `examples/**` project (e.g. `examples/HelloWorldApp/`)
   restores successfully with inline `<PackageReference … Version="…" />`,
   confirming that examples behave as un-managed consumer-style projects.

## Rationale

The failure mode this guards against is silent re-introduction of a
root-level `Directory.Packages.props` (or one under `examples/`) during a
future refactor, which would break the consumer-simulation property of the
examples subtree and re-trigger the FT-001 smoke-test scaffold failure.

## Mechanism

Implemented as an xUnit test in `tests/Aspire.Hosting.Coolify.Tests/` that
walks the repo tree from the solution root, opens each csproj as XML, and
asserts the file-placement and PackageReference rules above. The
`examples/HelloWorldApp` restore check is an `Aspire.Hosting.Testing` or
shell-driven `dotnet restore` invocation on the example project file.
