---
id: TC-027
title: native_containerregistry_path_tags_image_with_resource_address
type: exit-criteria
status: passing
validates:
  features:
  - FT-014
  adrs:
  - ADR-007
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~RegistryEdgeExitCriteriaTests.TC027
last-run: 2026-05-25T10:16:19.305655314+00:00
last-run-duration: 2.3s
---

## Description

Exit-criteria test for FT-014 / ADR-007 §Test coverage "Native primitive
happy path." Asserts that the Coolify publisher reads the image-push
target from the Aspire resource graph via `ContainerRegistryResource`
and the `WithContainerRegistry(...)` edge, and **never** from a
publisher-instance `(prefix, username, password)` triple, when the
AppHost uses only the native Aspire 13 primitives.

## Setup

- An AppHost that declares:
  - `var reg = builder.AddContainerRegistry("ghcr", "ghcr.io/acme");`
  - optional credentials attached to `reg` through whatever surface
    `ContainerRegistryResource` exposes (sentinel password)
  - two project workloads, each calling `.WithContainerRegistry(reg)`
  - `builder.AddDockerComposeEnvironment("env").WithCoolifyDeploy(url,
    token);`
  - **no call to `WithImageRegistry(...)` anywhere**
- A fake `ICoolifyClient` that records calls to `PrivateRegistries`.
- A fake Aspire image build/push pipeline that records the tags it
  was asked to build and the (tag, credentials) pairs it was asked to
  push.

## Steps

1. Run the publisher's deploy hook against the test AppHost.
2. Inspect the recorded build pipeline calls.
3. Inspect the recorded push pipeline calls.
4. Inspect the recorded `PrivateRegistries.UpsertAsync` calls.
5. Scan stderr for any `E_…` symbol.
6. Scan all log lines and intercepted HTTP for the sentinel password.

## Expected

- Build pipeline received exactly two calls, with tags
  `ghcr.io/acme/<workload1>:<apphost-version>` and
  `ghcr.io/acme/<workload2>:<apphost-version>`.
- Push pipeline received exactly two calls with those same two tags;
  no `:latest` tag was pushed.
- `PrivateRegistries.UpsertAsync` was invoked exactly once, with
  `host: "ghcr.io"` and `username: "<resolved>"` (when credentials
  were supplied); zero invocations when the registry resource was
  anonymous.
- No `E_…` symbol appeared on stderr.
- The sentinel password did not appear anywhere in logs, exception
  text, or intercepted HTTP request bodies (composes with FT-004
  I-3).
- No code path read a `(prefix, username, password)` triple from a
  field on `CoolifyDeployingPublisher` — asserted by source
  inspection that the triple-field is no longer declared on the
  publisher class.