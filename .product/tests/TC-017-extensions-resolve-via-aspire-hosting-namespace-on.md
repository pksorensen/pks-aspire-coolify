---
id: TC-017
title: extensions_resolve_via_aspire_hosting_namespace_only
type: invariant
status: passing
validates:
  features:
  - FT-001
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~AspireHostingNamespaceConvention
last-run: 2026-05-24T21:11:21.896938792+00:00
last-run-duration: 2.2s
---

## Description

Reflection-based invariant test enforcing the Aspire convention that public
`With*` extension methods on `IDistributedApplicationBuilder` declared by
`Aspire.Hosting.Coolify` live in the **`Aspire.Hosting`** namespace, not
`Aspire.Hosting.Coolify`. AppHost projects already import `Aspire.Hosting`
via the implicit-usings convention, so users get every extension reachable
with no additional `using` directive — matching `Aspire.Hosting.Redis` →
`WithRedis()`, `Aspire.Hosting.Postgres` → `WithPostgres()`, etc.

## When

A consumer AppHost project references the `Aspire.Hosting.Coolify` package
and writes a one-liner deploy registration in `AppHost.cs` without adding
any explicit `using Aspire.Hosting.Coolify;` directive.

## Then

1. The `CoolifyBuilderExtensions` static class is declared in the
   `Aspire.Hosting` namespace.
2. Every public static extension method on `IDistributedApplicationBuilder`
   that this assembly exposes lives in the `Aspire.Hosting` namespace —
   not `Aspire.Hosting.Coolify`, not any other sub-namespace.
3. The set covers at minimum: `WithCoolifyDeploy`, `WithImageRegistry`,
   `WithCoolifyDestination`, `WithVerifyPolling`, `WithManagedDashboard`.
4. The non-public internals (`CoolifyDeployingPublisher`, `CoolifyPhase`,
   `ConfigureDiagnostic`, etc.) remain in `Aspire.Hosting.Coolify` — only
   the public extension surface is hoisted.

## Validates

- FT-001 §B (added) — public extension surface lives in `Aspire.Hosting`
  per Aspire's package convention.