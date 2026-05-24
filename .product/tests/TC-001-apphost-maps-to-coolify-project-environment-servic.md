---
id: TC-001
title: apphost_maps_to_coolify_project_environment_service_hierarchy
type: scenario
status: passing
validates:
  features:
  - FT-005
  adrs:
  - ADR-001
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~DeployPhaseExitCriteria
last-run: 2026-05-24T17:56:50.291587884+00:00
last-run-duration: 2.4s
---

## Scenario

Given a representative Aspire AppHost named `SampleApp` with:

- two Aspire environments: `Development` and `Production`,
- one project resource (`api`, an ASP.NET project),
- one container resource (`redis`, a stock container image),
- one non-containerisable resource (e.g. `AddAzureKeyVault("kv")`).

And given a configured Coolify destination `homelab-prod` on a reachable
Coolify v4 instance.

## When

The developer runs:

```bash
aspire deploy --environment Production
```

against the configured Coolify instance, using
`WithCoolifyDeploy(destination: "homelab-prod")` in the AppHost.

## Then

The Coolify API reflects exactly the following state:

1. **Exactly one** Coolify project named `SampleApp` exists under the
   configured destination. No sibling projects are created.
2. **Exactly one** Coolify environment exists under the `SampleApp`
   project: `Production`. The declared-but-untargeted `Development`
   environment is **not** pre-created — environments are materialised
   lazily, only when a deploy actually targets them (ADR-001, clause 2).
3. Inside the `Production` environment, exactly two Coolify
   apps/services exist: one for `api` and one for `redis`. They are
   wired into the destination's network so `redis` is reachable from
   `api` via Coolify-internal DNS.
4. The deploy log contains a clearly-labelled **warning** referencing
   `kv` and the reason it was skipped (non-containerisable resource in
   v1). The deploy exit code is **0**.
5. The destination `homelab-prod` is the one configured on
   `WithCoolifyDeploy()`; no destination was derived from the AppHost
   graph.

## And then (idempotency)

Re-running the identical `aspire deploy --environment Production`
command:

- creates **zero** new Coolify projects,
- creates **zero** new Coolify environments (still just `Production`),
- creates **zero** new Coolify apps/services,
- upserts existing apps/services in place (image tag, env vars, port
  bindings, references may change but identities are stable),
- exits with code 0.

## And then (lazy materialisation of a second environment)

Subsequently running:

```bash
aspire deploy --environment Development
```

against the same AppHost:

- creates **zero** new Coolify projects (`SampleApp` is reused),
- creates **exactly one** new Coolify environment: `Development`,
- creates the `api` and `redis` apps/services inside `Development`
  without touching anything in `Production`,
- emits the same skip-with-warning for `kv`,
- exits with code 0.

After this second deploy, the `SampleApp` project contains exactly two
environments (`Production`, `Development`), each with its own
independent set of apps/services.

## Validates

- ADR-001 — Aspire-graph to Coolify-hierarchy mapping (v1)