---
id: TC-028
title: withimageregistry_shim_synthesises_native_edge_and_emits_obsolete_warning
type: exit-criteria
status: passing
validates:
  features:
  - FT-014
  adrs:
  - ADR-007
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~RegistryEdgeExitCriteriaTests.TC028
last-run: 2026-05-25T10:16:19.305655314+00:00
last-run-duration: 2.4s
---

## Description

Exit-criteria test for FT-014 / ADR-007 §Test coverage "Shim
source-compat." Asserts that the deprecated `WithImageRegistry(prefix,
username, password)` extension still compiles and deploys with
byte-identical observable behaviour against ADR-005's v1.x reference,
and that exactly one `[CS0618]`-style obsolete warning is emitted at
the call site.

## Setup

- An AppHost compiled against the v1.x surface:
  - `builder.AddDockerComposeEnvironment("env")
       .WithCoolifyDeploy(url, token)
       .WithImageRegistry("ghcr.io/legacy", user, pass);`
  - two project workloads added, neither with an explicit
    `WithContainerRegistry(...)` call
- A fake `ICoolifyClient` and fake build/push pipelines as in TC-027.
- A snapshot of the v1.x reference deploy: tag set, push set,
  Coolify Private Registry upsert request body.
- A compiler diagnostics collector capturing `[CS0618]`-equivalent
  warnings emitted during AppHost compilation.

## Steps

1. Compile the AppHost; collect compiler diagnostics.
2. Run the publisher's deploy hook against the compiled AppHost.
3. Compare the recorded build / push / upsert calls against the
   v1.x snapshot.
4. Inspect the resource graph for a `ContainerRegistryResource`
   whose name matches the shim's deterministic synthesis pattern
   (e.g. `coolify-legacy-<hash>`) and whose `Address` equals
   `"ghcr.io/legacy"`.
5. Inspect the two workloads for a `WithContainerRegistry(...)`
   edge pointing at the synthesised registry resource.

## Expected

- Exactly one `[CS0618]`-style obsolete warning was emitted, with
  text referencing `AddContainerRegistry` + `WithContainerRegistry`
  and citing ADR-007, attributed to the `WithImageRegistry(...)`
  call site.
- Build pipeline tags, push pipeline tags, and the
  `PrivateRegistries.UpsertAsync(host: "ghcr.io", username: user,
  ...)` invocation match the v1.x snapshot byte-for-byte (modulo
  the username string and the password content, which are
  parameter-driven).
- The resource graph contains exactly one `ContainerRegistryResource`
  named `coolify-legacy-<hash>` with `Address == "ghcr.io/legacy"`.
- Each of the two workloads has a `WithContainerRegistry(...)`
  edge pointing at that synthesised registry resource (FT-014 I-2).
- No `E_…` symbol appeared on stderr.
- Calling `WithImageRegistry("ghcr.io/legacy", ...)` a second time
  with the same prefix on the same builder does not create a
  second registry resource (FT-014 I-8 / ADR-007 §Decision §4.1
  "repeated calls converge").