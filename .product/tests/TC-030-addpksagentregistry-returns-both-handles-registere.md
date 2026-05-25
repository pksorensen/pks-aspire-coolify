---
id: TC-030
title: addpksagentregistry_returns_both_handles_registered_on_builder
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

Validates FT-015 I-1 / I-2 / I-3 — `AddPksAgentRegistry(builder,
name)` returns a tuple whose **both** handles are live and
registered on the argument builder, with the container resource
configured for port 5000 + bind-mount `./.data/<name>/` and the
registry resource carrying `Address = "localhost:5000"`.

## Setup

- A bare `IDistributedApplicationBuilder` produced by
  `DistributedApplication.CreateBuilder(args: [])`.
- The `name` argument set to a deterministic value (e.g.
  `"local-registry"`).

## Steps

1. Invoke
   `var (container, registry) = builder.AddPksAgentRegistry(name);`
2. Build the application model (without running it).
3. Enumerate the application model's resources.
4. Inspect the `ContainerResource` named `name`:
   - image is `ghcr.io/pksorensen/pks-agent-registry`
   - it has a bind-mount annotation with source `./.data/<name>`
     (or its absolute equivalent) and target
     `/var/lib/registry`
   - it has an HTTP endpoint annotation with port `5000` and
     target port `5000`
5. Inspect the `ContainerRegistryResource` named `name`:
   - its `Address` property is the string `"localhost:5000"`
6. Assert the returned tuple's handles refer to the same two
   resources discovered in steps 4 and 5.

## Expected

- The tuple's first element is a non-null
  `IResourceBuilder<ContainerResource>` whose underlying
  resource matches all assertions in step 4.
- The tuple's second element is a non-null
  `IResourceBuilder<ContainerRegistryResource>` whose underlying
  resource matches all assertions in step 5.
- Both resources are reachable from the application model's
  resource enumeration — they were actually registered on the
  builder, not just constructed and returned.