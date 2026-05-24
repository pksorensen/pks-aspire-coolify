# pks-aspire-coolify

An [Aspire](https://learn.microsoft.com/dotnet/aspire/) hosting extension that publishes an AppHost to a [Coolify](https://coolify.io) instance. One AppHost ⇒ one Coolify project; each Aspire environment ⇒ one Coolify environment; each containerisable Aspire resource ⇒ one Coolify app/service. v1 is push-based: the publisher builds + pushes container images, then upserts the project, environments, and services via Coolify's REST API and triggers per-service deploys.

## Quick start

Add a `ProjectReference` (or, post-pack, a `PackageReference`) to `Aspire.Hosting.Coolify` from your AppHost, then wire it up:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var coolifyUrl   = builder.AddParameter("coolify-url");
var coolifyToken = builder.AddParameter("coolify-token", secret: true);
var registry     = builder.AddParameter("registry-prefix");

var api = builder.AddProject<Projects.MyApp_Api>("api");

builder
    .WithCoolifyDeploy(coolifyUrl, coolifyToken)
    .WithImageRegistry(registry)
    .WithCoolifyDestination("homelab");

builder.Build().Run();
```

No `using Aspire.Hosting.Coolify;` directive needed — the extensions live in the `Aspire.Hosting` namespace per the Aspire ecosystem convention (matches `WithRedis`, `WithPostgres`, etc.).

Then run a real deploy:

```bash
aspire deploy -e Production
```

The publisher walks five fixed phases in order: **configure → build → push → deploy → verify**. The configure phase fails fast with one of four diagnostic symbols if anything is wrong, before any side-effecting work:

| Symbol | Cause |
|---|---|
| `E_AUTH_TOKEN_MISSING` | `coolify-token` parameter is unset |
| `E_AUTH_TOKEN_INVALID` | Coolify returned 401/403 |
| `E_COOLIFY_VERSION_BELOW_FLOOR` | Coolify version is older than ADR-002's floor |
| `E_COOLIFY_UNREACHABLE` | Transport error / 5xx / timeout |

## Example AppHost

See [`examples/HelloWorldApp/`](./examples/HelloWorldApp) for a minimal end-to-end smoke test — Aspire AppHost + a minimal API + Coolify wiring. Build it with `dotnet build examples/HelloWorldApp/AppHost/`. To exercise the configure-phase fail-fast diagnostics without a real Coolify, run:

```bash
aspire deploy --apphost examples/HelloWorldApp/AppHost/HelloWorldApp.AppHost.csproj \
              --non-interactive --nologo -e Production
```

You should see the pipeline register all five `coolify-*` steps, hit `E_AUTH_TOKEN_MISSING` at configure (no token set), and refuse to advance to subsequent phases.

## Project structure

```
src/Aspire.Hosting.Coolify/             # The hosting extension package (Aspire 13, net10.0)
tests/Aspire.Hosting.Coolify.Tests/     # xUnit tests (205 tests, all green)
examples/HelloWorldApp/                 # End-to-end smoke test
  ├── AppHost/                          # The Aspire AppHost — calls WithCoolifyDeploy
  └── Api/                              # Minimal API workload
.product/                               # product-cli typed knowledge graph
  ├── adrs/                             # 6 accepted ADRs
  ├── features/                         # 11 complete features
  └── tests/                            # 18 test criteria
docs/
  ├── brief.md                          # PRD-style vision document
  ├── product-cli-notes.md              # methodology reference
  ├── post-v1-ideas.md                  # in-project registry + source-build alternatives
  └── qa-pass-2026-05-24.md             # Pattern H QA artifact
```

## Decisions (ADRs)

| ID | Decision |
|---|---|
| ADR-001 | Aspire→Coolify hierarchy mapping (one AppHost = one project) |
| ADR-002 | Coolify API version + hand-written thin REST client |
| ADR-003 | Imperative deploy with name-keyed upserts |
| ADR-004 | Auth: `AddParameter(secret: true)` typed handles |
| ADR-005 | Image registry: developer-chosen, publisher-push |
| ADR-006 | Central Package Management scope (src/ + tests/ only) |

`product adr show ADR-XXX` for the full text + rationale + rejected alternatives.

## Status

- 11 / 11 features complete
- 205 xUnit tests passing
- Phase 1 OPEN per `product status`
- `product graph check && product gap check && product drift check` — all clean
- See `docs/qa-pass-2026-05-24.md` for the methodology QA audit trail
