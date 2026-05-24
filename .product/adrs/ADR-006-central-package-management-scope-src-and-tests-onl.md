---
id: ADR-006
title: Central Package Management scope — src/ and tests/ only, examples/ opt-out by placement (v1)
status: accepted
features: []
supersedes: []
superseded-by: []
domains: []
scope: cross-cutting
content-hash: sha256:ade4519a636b465cab479f73d91873e090cb873de05f39238782d0293b80cf6e
amendments:
- date: 2026-05-24T21:11:53Z
  reason: Add body-embedded source-files block so drift check can discover governed Directory.Packages.props files (path-3 skips .props extensions; front-matter source-files struct is not read by drift body extractor — both are upstream product-cli bugs).
  previous-hash: sha256:e79a2c68528ef171b5d356caa03a5db600c9888b1c3e3eaa41a7c4b8c3fcc28e
---

## Context

FT-001 introduced a repo-root `Directory.Packages.props` to enable Central Package
Management (CPM) for the `Aspire.Hosting.Coolify` extension package and its xUnit
test project. CPM is desirable here because:

- The extension targets Aspire 13 (`net10.0`) and pulls in a non-trivial set of
  `Aspire.Hosting.*`, `Microsoft.Extensions.*`, and HTTP/JSON dependencies that
  must stay version-aligned across `src/` and `tests/` to avoid binding-redirect
  surprises and duplicate transitive resolutions.
- A single `PackageVersion` ledger makes Dependabot / Renovate sweeps and Aspire
  SDK upgrades a one-file change, which matters because Aspire's preview cadence
  has historically shipped breaking version bumps inside a minor.
- Pack-time reproducibility for a NuGet-published extension benefits from
  pinned, centrally-declared versions rather than scattered `Version=` strings.

The bleed problem: MSBuild's `Directory.Packages.props` discovery walks **up**
the directory tree from each csproj and stops at the first hit. A file at the
repo root therefore captures **every** nested project — including subtrees that
are not part of the production build:

- `examples/HelloWorldApp/` (added for FT-001 smoke-testing) was scaffolded from
  `dotnet new webapi`, which emits `<PackageReference Include="..." Version="..." />`.
  CPM forbids inline `Version=` attributes and the example failed to restore.
- Any future `docs/walkthroughs/**`, `samples/**`, or one-off reproduction
  projects will hit the same wall.
- Most importantly: examples exist to model **what a third-party consumer of the
  NuGet package experiences**. A real downstream consumer will not have our
  `Directory.Packages.props` in their tree. Forcing CPM onto `examples/` makes
  the examples behave unlike real consumers — eroding their value as smoke
  tests for the published-package experience.

## Decision

1. **CPM applies to `src/` and `tests/` only.** These are the production
   extension code and its xUnit harness — the two trees whose dependency graph
   we own and must keep version-aligned.
2. **Place two `Directory.Packages.props` files, one per managed subtree:**
   - `src/Directory.Packages.props` — authoritative `PackageVersion` ledger for
     the extension and any future production sub-packages.
   - `tests/Directory.Packages.props` — ledger for test-only dependencies
     (xUnit, FluentAssertions, Aspire.Hosting.Testing, Playwright, etc.).
     May duplicate a subset of `src/`'s versions or pin test-only packages
     independently; the two files are siblings, not a parent/child.
3. **No `Directory.Packages.props` at the repo root.** The root remains CPM-free
   so that any subtree which does not opt in (by placing its own file) walks up
   the tree, finds none, and behaves as a standard un-managed MSBuild project.
4. **`examples/**` and any future `docs/**`, `samples/**`, walkthrough trees do
   not get a `Directory.Packages.props`.** They use plain
   `<PackageReference Include="..." Version="..." />` exactly as a third-party
   consumer's project would. This is a feature, not a workaround.
5. **`ManagePackageVersionsCentrally` is set implicitly via the presence of the
   props file** — no explicit `<PropertyGroup>` toggle in individual csprojs.
   The two managed subtrees are CPM-on by virtue of file placement; everything
   else is CPM-off by absence.

## Rationale

- **Discovery semantics match intent.** MSBuild's upward walk + first-hit rule
  is exactly the mechanism we need: file placement directly encodes scope. No
  per-project opt-out flags, no `<ManagePackageVersionsCentrally>false</…>`
  scattered across example csprojs, no MSBuild conditions.
- **Examples remain faithful consumer simulations.** A new contributor running
  `dotnet new webapi -o examples/Foo` followed by `dotnet add package
  Aspire.Hosting.Coolify` gets the same experience a downstream user gets. If
  that path breaks, it breaks for our users too — which is the entire point of
  keeping examples in-repo.
- **Two-file split (src vs tests) keeps test-only churn out of the production
  ledger.** Bumping Playwright or FluentAssertions does not touch the file that
  governs the shipped package, and a `git log src/Directory.Packages.props`
  gives a clean history of production dependency changes.
- **Zero per-csproj boilerplate.** Existing csprojs in `src/` and `tests/`
  continue to use bare `<PackageReference Include="..." />` (no Version) and
  pick up the new sibling props file automatically after the root file is
  removed and the new ones are added.
- **Future-proof for new subtrees.** Adding `docs/walkthroughs/` or
  `tools/codegen/` requires no ADR amendment — they are CPM-off by default,
  matching consumer ergonomics.

## Rejected alternatives

1. **Root `Directory.Packages.props` with per-project opt-out in `examples/`.**
   Each example csproj would need
   `<ManagePackageVersionsCentrally>false</…>` plus inline `Version=` on every
   PackageReference. Workable but viral: every new example, walkthrough, or
   sample needs the opt-out boilerplate, and any contributor who forgets it
   gets a confusing CPM error at restore time. Also fails the
   "examples mirror consumer experience" test — a real consumer does not write
   that property.

2. **Root `Directory.Packages.props` plus an `examples/Directory.Build.props`
   override that unsets `ManagePackageVersionsCentrally`.** Less viral than #1
   but still has the bleed-by-default failure mode for any new top-level
   subtree (docs/, samples/, tools/). Also creates non-obvious interaction
   between two MSBuild import files — debugging CPM behaviour in an example
   would require knowing about both files.

3. **No CPM anywhere; inline `Version=` on every PackageReference.** Loses
   single-point version alignment between `src/` and `tests/`, makes Aspire SDK
   bumps an N-file sweep, and risks transitive version drift that has bitten
   Aspire-preview projects elsewhere. Premature de-optimisation for an
   actively-versioned dependency surface.

4. **Generate a `Directory.Packages.props` per subtree from a single source via
   an MSBuild target or build script.** Solves alignment but introduces
   tooling that contributors must learn and run, defeats `dotnet restore`
   ergonomics, and provides no benefit over two hand-maintained sibling files
   given the small number of managed subtrees (two, today; unlikely to grow).

5. **Use a `Directory.Build.props` at the root that conditionally enables CPM
   based on `$(MSBuildProjectDirectory)` matching `src/**` or `tests/**`.**
   Achieves the same scope but encodes it in a path-matching condition that
   is fragile to repo reorganisation and harder to discover than the
   "the file is there → CPM is on" rule that MSBuild already provides for free.

## Governed source files

<!-- Body-embedded source-files block: product-cli drift check reads this from
the body (not the YAML front-matter struct), and its pattern-discovery path
skips non-code extensions like .props. This block IS the source-of-truth that
drift consults for ADR-006. -->

source-files:
- src/Directory.Packages.props
- tests/Directory.Packages.props

## Test coverage

- **TC-016 `cpm_scope_build_invariant`** — invariant test asserting:
  1. `src/Directory.Packages.props` exists and sets
     `ManagePackageVersionsCentrally=true`.
  2. `tests/Directory.Packages.props` exists and sets
     `ManagePackageVersionsCentrally=true`.
  3. No `Directory.Packages.props` exists at the repo root.
  4. No csproj under `src/**` or `tests/**` contains a `<PackageReference>`
     element with a `Version=` attribute (CPM forbids it).
  5. A clean `dotnet restore` of an `examples/HelloWorldApp` style project —
     scaffolded from `dotnet new webapi` with inline `Version=` references —
     succeeds, demonstrating examples remain CPM-free.

The invariant runs as part of the standard `dotnet test` pass for the
extension test project and additionally as a structural file-tree check so
that an example breaking due to a stray root `Directory.Packages.props`
fails the build immediately rather than at first contributor encounter.
