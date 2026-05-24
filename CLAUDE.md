# pks-aspire-coolify — implementer guide

This project is bootstrapped with `product-cli`. The `.product/` directory holds the typed knowledge graph (ADRs, features, TCs); the `src/` and `tests/` trees hold the .NET implementation.

## ADR ↔ code linkage (mandatory)

For **any file you create or substantially edit** that implements a linked ADR, add a comment near the top (after the using-directives, before the namespace or first declaration) in this exact form — language-appropriate comment syntax:

```csharp
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
```

Multiple ADRs on one file → multiple comment lines, one per ADR:

```csharp
// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
```

This lets `product drift check`'s pattern-based discovery find which files govern which ADRs without us maintaining `source-files:` on the ADR front-matter by hand. **Do not run `product adr source-files add`** — that's the path-1 workflow we're avoiding.

To look up the right ADR title summary: `product adr show ADR-XXX | head -3`.

## Solution layout

- `src/Aspire.Hosting.Coolify/` — main extension package (Aspire 13, net10.0, packs as NuGet)
- `tests/Aspire.Hosting.Coolify.Tests/` — xUnit unit tests
- Eventual integration tests will live in `tests/Aspire.Hosting.Coolify.IntegrationTests/`

## Aspire 13 notes

`IDistributedApplicationPublisher` is obsolete in Aspire 13. The current surface is `IDistributedApplicationPipeline.AddStep(name, action, dependsOn, requiredBy)` — see FT-001's `CoolifyBuilderExtensions.cs` for the canonical wiring pattern. Use the same pattern when wiring new pipeline steps from FT-002 onward.

## When implementing a feature

1. Run `dotnet build && dotnet test` after every substantive change.
2. Keep changes scoped to the feature's spec — don't touch unrelated invariants from other features.
3. When you're done, the test suite should be green; report the file list + final test output.
