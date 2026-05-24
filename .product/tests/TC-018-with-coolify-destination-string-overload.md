---
id: TC-018
title: with_coolify_destination_string_overload
type: scenario
status: passing
validates:
  features:
  - FT-005
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~CoolifyDestinationStringOverload
last-run: 2026-05-24T21:06:42.959575930+00:00
last-run-duration: 2.5s
---

## Scenario

`WithCoolifyDestination(string)` overload accepts a literal destination name
at the call site — suitable for the common homelab case where a single Coolify
destination doesn't vary per environment and is never secret. Last-call-wins
discipline holds across both overloads (handle + string), with exactly one
source active at deploy time.

## Then

1. `DestinationLiteralName` is the verbatim string passed at the call site;
   `DestinationName` is null when the string overload was the last call.
2. After a handle-overload call, `DestinationName` is set and
   `DestinationLiteralName` is null.
3. Deploy-phase resolution prefers `DestinationLiteralName` when set, skipping
   parameter resolution entirely.
4. `WithCoolifyDestination((string)null!)` throws `ArgumentNullException`;
   empty / whitespace strings throw `ArgumentException`.
5. `WithCoolifyDestination("homelab")` before `WithCoolifyDeploy` throws
   `InvalidOperationException` matching the handle overload's shape.

## Validates

- FT-005 §0 (amended) — string overload of `WithCoolifyDestination`.