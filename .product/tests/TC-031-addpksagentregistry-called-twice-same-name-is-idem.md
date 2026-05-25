---
id: TC-031
title: addpksagentregistry_called_twice_same_name_is_idempotent
type: scenario
status: passing
validates:
  features:
  - FT-015
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~PksAgentRegistry
last-run: 2026-05-25T10:16:40.178019705+00:00
last-run-duration: 2.2s
---

## Description

Validates FT-015 I-4 — calling `AddPksAgentRegistry(builder,
name)` more than once for the same `(builder, name)` pair does
not register duplicate resources on the builder, regardless of
whether the implementation chose last-call-wins or
no-op-after-first as its convergence strategy.

## Setup

- A bare `IDistributedApplicationBuilder`.
- A fixed `name` value (e.g. `"local-registry"`).

## Steps

1. Invoke `builder.AddPksAgentRegistry(name)` once.
2. Invoke `builder.AddPksAgentRegistry(name)` a second time
   against the same builder with the same name.
3. Build the application model.
4. Enumerate the application model's resources and count:
   - the number of `ContainerResource`s named `name`
   - the number of `ContainerRegistryResource`s named `name`

## Expected

- Exactly **one** `ContainerResource` named `name` is present
  in the application model.
- Exactly **one** `ContainerRegistryResource` named `name` is
  present in the application model.
- Neither call threw — both invocations returned a valid
  `(container, registry)` tuple.
- The second call's returned tuple refers to the same
  underlying resources as the first call's tuple.