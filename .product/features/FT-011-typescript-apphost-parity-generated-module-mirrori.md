---
id: FT-011
title: TypeScript AppHost parity — generated module mirroring WithCoolifyDeploy / WithImageRegistry / WithCoolifyDestination / WithManagedDashboard
phase: 1
status: complete
depends-on:
- FT-001
- FT-003
- FT-005
- FT-010
adrs:
- ADR-001
- ADR-003
- ADR-004
tests:
- TC-015
domains:
- apphost-ts
domains-acknowledged: {}
---

## Description

FT-011 delivers **TypeScript AppHost parity** for the four publisher-
configuration extensions FT-001 / FT-003 / FT-005 / FT-010 introduce on
the C# side. It is the dedicated `apphost-ts` feature every other
feature in Phase 1 has deferred to (search "TypeScript AppHost parity"
in FT-001 / FT-003 / FT-005 / FT-010 — each one explicitly out-of-scopes
this work and points here).

The pattern is the one `aspire-ssh-deploy` set: an Aspire AppHost can be
authored in TypeScript instead of C#, and `aspire restore` writes a
typed TypeScript module exposing the same publisher-configuration
surface the C# extensions expose, so a TS AppHost reads exactly like the
C# one — same call sites, same chain shape, same handle discipline,
same null/missing-handle errors. FT-011 wires the `aspire restore`
generator hook that emits this module and fixes the module's public
contract.

The TS module is a **pure recording shim**: it captures the same
registration data the C# extensions capture (a handle reference and an
opt-in flag, per extension). It never resolves a parameter value, never
talks to Coolify, never builds an image, never runs a phase. When a TS
AppHost is `aspire deploy`-ed, the underlying .NET publisher (the
`CoolifyDeployingPublisher` from FT-001) still drives the actual deploy
— exactly as it does for a C# AppHost — and reads the recorded
registration data through the same in-publisher slots that
`WithCoolifyDeploy` / `WithImageRegistry` / `WithCoolifyDestination` /
`WithManagedDashboard` populate from C#. This mirrors how
`aspire-ssh-deploy`'s TS variant works: a TS AppHost still drives a
.NET publisher under the hood; the TS surface is authoring ergonomics,
not a reimplementation.

The shape is dictated by:

- **ADR-001 (mapping invariants).** The one-AppHost-one-Coolify-project
  mapping, lazy environment materialisation, name-keyed identity, and
  config-driven destination apply identically regardless of authoring
  surface. A TS-authored AppHost producing the same registration data
  as a C#-authored AppHost lands the same Coolify-side shape. FT-011
  must not introduce a TS-specific deviation from any ADR-001
  invariant.
- **FT-001 invariants I-1 through I-7** (skeleton contract: fixed
  phase order, no on-disk state, first-call-wins idempotency on
  `WithCoolifyDeploy(...)`, signature is load-bearing, token value
  never read). Inherited verbatim: the TS shim records the same
  `(url, token)` pair handle reference; first-call-wins applies; the
  TS shim never reads the token value (TypeScript types make this a
  compile-time impossibility for the shim — the handle type exposes
  no value-reading API to shim code).
- **FT-003 invariants** (`WithImageRegistry(prefix, [user, pass])`:
  required `prefix`, paired `(user, pass)`, last-call-wins, null /
  exactly-one-of-pair errors at AppHost build time, no `latest` tag,
  credentials captured but never dereferenced). Inherited.
- **FT-005 invariants** (`WithCoolifyDestination(name)`: required
  handle, last-call-wins, ArgumentNullException-equivalent on null at
  AppHost build time, destination handle resolved once at deploy
  time). Inherited.
- **FT-010 invariants** (`WithManagedDashboard(dashboardToken)`:
  required secret handle, last-call-wins, opt-in flag, dashboard never
  fails workload, audience-separation honoured). Inherited.

Everything FT-011 adds to the contract is **authoring ergonomics in
TypeScript** plus **the generator wiring that emits the shim**. No
behavioural extension is introduced. No new error symbol. No phase. No
HTTP call.

The exit criteria are: (a) `aspire restore` on a project that depends
on the Coolify publisher writes a typed TypeScript module exposing
exactly the four functions below, with the camelCase names, the
parameter-handle-typed signatures, and the inherited idempotency rules
wired into the shim's capture logic; (b) a TS AppHost calling those
functions records the same registration data the C# extensions record,
into the same publisher-side slots, so the .NET publisher driving the
deploy from a TS AppHost sees behaviour identical to a C#-authored
equivalent; (c) typed handle discipline prevents missing-handle and
wrong-shape bugs at TypeScript compile time, and the emitted shim
adds runtime checks as backstop for the residual cases TS types
cannot prevent (e.g. `undefined` arriving through `any` boundaries);
(d) no TS-side reimplementation of phases, Coolify client, image
flow, or any other behaviour escapes into the emitted module — the
module is structurally a recording shim and grep-able as such.

## Functional Specification

### Inputs

- A TypeScript Aspire AppHost project that takes a dependency on the
  Coolify publisher (the C# publisher assembly is referenced by the
  generated TS AppHost's underlying .NET project — this is the
  `aspire restore` mechanism the brief's Inspiration section
  describes, the same one `aspire-ssh-deploy` uses).
- An `aspire restore` invocation against that project. `aspire
  restore` discovers the Coolify publisher and invokes the generator
  hook this feature registers. No TS-AppHost-specific configuration
  knobs are introduced by FT-011 — the hook is unconditional once the
  publisher is referenced.
- A TS AppHost author's call site, matching the camelCase shape
  below:

  ```ts
  import { distributedApplication } from "@aspire/hosting";
  import {
    withCoolifyDeploy,
    withImageRegistry,
    withCoolifyDestination,
    withManagedDashboard,
  } from "./aspire.generated/coolify"; // emitted by aspire restore

  const builder = distributedApplication.createBuilder(process.argv);

  const coolifyUrl       = builder.addParameter("coolify-homelab-url");
  const coolifyToken     = builder.addParameter("coolify-homelab-token",      { secret: true });
  const registryPrefix   = builder.addParameter("registry-prefix");
  const registryUser     = builder.addParameter("registry-username");
  const registryPass     = builder.addParameter("registry-password",          { secret: true });
  const coolifyDest      = builder.addParameter("coolify-homelab-destination");
  const dashboardToken   = builder.addParameter("coolify-homelab-dashboard-token", { secret: true });

  withCoolifyDeploy(builder, coolifyUrl, coolifyToken);
  withImageRegistry(builder, registryPrefix, [registryUser, registryPass]);
  withCoolifyDestination(builder, coolifyDest);
  withManagedDashboard(builder, dashboardToken);

  // ... addProject / addContainer / addParameter calls ...
  builder.build().run();
  ```

  Exact module path (`./aspire.generated/coolify` above) and import
  style are the Aspire-TS-AppHost project convention as established by
  `aspire restore`; FT-011 emits into whatever path `aspire restore`'s
  generated-modules layout designates for publisher-emitted modules.
  The functions are exported as named exports; the module emits no
  default export.

### Outputs

- **A generated TypeScript module written by `aspire restore`** into
  the AppHost project's generated-modules directory, containing
  exactly the four exported functions described below, their type
  declarations, and the minimal supporting types for the
  parameter-resource handle reference. The module is regenerated on
  every `aspire restore` invocation and is not intended for manual
  edit (the file carries an `// auto-generated by aspire restore;
  do not edit` header line). The module's contract — the set of
  exports, their signatures, their idempotency, and their capture
  behaviour — is the **primary observable surface** of FT-011.
- **Four functions exported from the generated module**, with the
  signatures, idempotency, and null/missing-handle discipline
  detailed in §Behaviour. They are pure recording shims: each
  function (a) validates its arguments at runtime as the backstop to
  TS types, (b) records a handle reference (or pair of handle
  references) into the same publisher-side slot the corresponding C#
  extension writes to, and (c) returns the builder (or chainable
  result) for chaining, matching the C# chain shape.
- **The recorded registration data is read by the underlying .NET
  publisher** (`CoolifyDeployingPublisher` from FT-001) at
  `aspire deploy` time, through the same publisher-instance slots
  used by the C# extensions. FT-011 must use the same
  publisher-instance API the C# extensions use; it does not
  introduce a second registration channel.

### State

- **No persistent state on disk authored by FT-011** beyond the
  generated module file itself, which is `aspire restore`'s output
  artefact and lives where `aspire restore`'s generated-modules
  layout places it (typically under the AppHost project's
  `aspire.generated/` or equivalent). The shim writes nothing at
  runtime. The publisher-side recorded data inherits FT-001 I-3's
  no-persistent-state invariant (it lives in the publisher instance
  for the lifetime of the AppHost process only).
- **No in-memory state in the shim beyond a single per-call write
  to the publisher slot.** The shim does not maintain its own
  collection of registrations — the publisher instance is the single
  source of truth, exactly as it is for C#. Calling a shim function
  twice writes to the publisher slot twice, and the publisher slot's
  idempotency rule (first-call-wins or last-call-wins per function)
  applies regardless of which authoring surface made the call.

### Behaviour

The TS module exposes exactly four functions. Each function's
contract — signature, idempotency, null/missing-handle errors —
mirrors its C# counterpart exactly. The shim implementation for
each function is the same three steps:

1. Validate arguments at runtime (TS types prevent most bugs at
   compile time; the runtime check is the backstop for `any` /
   `unknown` boundaries and for callers running un-typechecked JS).
2. Resolve the publisher-instance for the given builder (the
   recording-shim equivalent of the C# extensions' "find or attach
   the `CoolifyDeployingPublisher` to this builder" step). Same slot,
   regardless of authoring surface.
3. Write the captured handle reference(s) into the slot, honouring
   the function's idempotency rule.

**The shim never resolves a handle to its value.** TypeScript types
expose no value-reading API on the parameter-handle type to the shim,
and the shim implementation does not call any value-reading API even
if one existed. This is the TS-side enforcement of FT-001 I-7 (token
value never read) and FT-003 I-6 (credentials never dereferenced).

#### 0. (Generator-time, not runtime.) `aspire restore` generator hook

FT-011 registers a generator hook with `aspire restore`'s
generated-modules system. The hook fires for every AppHost project
that references the Coolify publisher assembly. On fire, the hook
emits a single TypeScript file containing the four functions, the
type declarations for the handle reference type, and the
`// auto-generated by aspire restore; do not edit` header. The
emitted file is byte-identical for a given publisher version (no
timestamps, no machine-specific paths in the file body), so that
checked-in generated files do not produce gratuitous diffs across
developers. The hook does not write anywhere outside the
generated-modules directory `aspire restore` designates.

#### 1. `withCoolifyDeploy(builder, url, token)`

Signature:

```ts
function withCoolifyDeploy(
  builder: IDistributedApplicationBuilder,
  url:     IResourceHandle<ParameterResource>,
  token:   IResourceHandle<ParameterResource>,
): IDistributedApplicationBuilder;
```

(The exact TS type names for the builder and handle types mirror
whatever Aspire's TS AppHost surface exposes — the names above are
placeholders for the canonical Aspire-TS types. FT-011's contract is
"the same types the rest of the Aspire-TS AppHost surface uses for
builders and parameter handles," not specific symbol names.)

- `builder`, `url`, and `token` are required. The TS function
  signature marks them non-optional; the shim's runtime backstop
  throws on `null` / `undefined` for any of the three, naming the
  offending argument. Mirrors FT-001's `ArgumentNullException` at
  AppHost build time.
- The shim writes the `(url, token)` handle-reference pair into the
  publisher's `(url, token)` slot.
- **Idempotency: first-call-wins.** Mirrors FT-001 I-4. Calling
  `withCoolifyDeploy(...)` twice on the same builder leaves the
  first pair in the slot; the second call is a silent no-op (it
  does not throw, it does not write). Calling it twice with
  different pairs is also first-call-wins; multi-target deploys are
  out of scope (FT-001 §Behaviour §1).
- The shim does not read `url` or `token` values. The publisher
  reads `token` (via `FT-002`) during configure phase, same as for a
  C# AppHost.
- Returns the builder, for optional chaining symmetry with the C#
  surface (though chaining four separate top-level function calls
  reads more idiomatically in TS than a method chain — both shapes
  work).

#### 2. `withImageRegistry(builder, prefix, credentials?)`

Signature:

```ts
function withImageRegistry(
  builder:      IDistributedApplicationBuilder,
  prefix:       IResourceHandle<ParameterResource>,
  credentials?: [IResourceHandle<ParameterResource>, IResourceHandle<ParameterResource>],
): IDistributedApplicationBuilder;
```

- `builder` and `prefix` are required. `credentials` is optional and,
  when present, is a tuple of `(username, password)` handles. The
  tuple form (rather than two separate optional parameters) is the TS
  expression of FT-003's "credentials travel as a pair" invariant
  (ADR-005 §1) — TypeScript types make it structurally impossible to
  pass exactly one of the pair, which is the TS-side enforcement of
  FT-003's `ArgumentException` on "exactly one of username/password
  is non-null." Runtime backstop: throws on `null` / `undefined`
  `prefix`, throws on a `credentials` tuple where either element is
  `null` / `undefined`.
- The shim writes `(prefix, username, password)` (with `username` and
  `password` `null` when `credentials` is omitted) into the
  publisher's `(prefix, username, password)` slot.
- **Idempotency: last-call-wins.** Mirrors FT-003 I-8. Calling
  `withImageRegistry(...)` twice replaces the previously-captured
  triple with the new one.
- The shim does not read `prefix`, `username`, or `password` values.
  The publisher reads `prefix` (via FT-003 build phase) and
  `username` / `password` (via FT-004 push phase), same as for a C#
  AppHost.
- Returns the builder.

#### 3. `withCoolifyDestination(builder, name)`

Signature:

```ts
function withCoolifyDestination(
  builder: IDistributedApplicationBuilder,
  name:    IResourceHandle<ParameterResource>,
): IDistributedApplicationBuilder;
```

- `builder` and `name` are required. Runtime backstop throws on
  `null` / `undefined` for either, naming the offending argument.
  Mirrors FT-005's `ArgumentNullException`.
- The shim writes the `name` handle reference into the publisher's
  destination-name slot.
- **Idempotency: last-call-wins.** Mirrors FT-005 §Behaviour §0.
- The shim does not read `name`. The publisher resolves `name` once
  at the top of the deploy phase (FT-005 step 1), same as for a C#
  AppHost.
- Returns the builder.

#### 4. `withManagedDashboard(builder, dashboardToken)`

Signature:

```ts
function withManagedDashboard(
  builder:        IDistributedApplicationBuilder,
  dashboardToken: IResourceHandle<ParameterResource>,
): IDistributedApplicationBuilder;
```

- `builder` and `dashboardToken` are required. Runtime backstop
  throws on `null` / `undefined` for either, naming the offending
  argument. Mirrors FT-010's `ArgumentNullException`.
- The shim writes the `dashboardToken` handle reference into the
  publisher's dashboard-token slot and sets the
  `dashboard-opted-in` flag to true. (FT-010 §Behaviour §0.)
- **Idempotency: last-call-wins** on the handle; the opt-in flag
  remains true. Calling zero times leaves the flag false and the
  dashboard sub-phase skips entirely on deploy — same opt-out-is-
  silent contract as FT-010 I-6.
- The shim does not read `dashboardToken`. The publisher resolves
  it at deploy time, same as for a C# AppHost. The redaction
  guarantee (FT-010 I-7) is honoured by the publisher's
  `secret: true` parameter redaction — the shim does not touch the
  value at all.
- Returns the builder.

### Invariants

- **I-1: emitted module's public contract matches the four
  signatures above exactly.** Function names, argument order,
  argument types (parameter-handle-typed, not raw-string), tuple
  shape for `withImageRegistry`'s credentials, return type. Any
  deviation (e.g. string-overload, swapped argument order,
  raw-string fallback) is a breaking change requiring a new ADR.
- **I-2: every shim function is a pure recording shim.** No HTTP
  call, no Coolify-client construction, no parameter-value
  resolution, no image-pipeline call, no file I/O at runtime, no
  side effect beyond writing to the publisher slot. Asserted by
  unit-test inspection of the emitted module's source: the
  function bodies contain only argument validation and a single
  slot-write call.
- **I-3: idempotency rules mirror C# exactly per function.**
  `withCoolifyDeploy`: first-call-wins. `withImageRegistry`,
  `withCoolifyDestination`, `withManagedDashboard`: last-call-wins.
  Asserted by calling each function twice with distinguishable
  handle references and inspecting the publisher slot.
- **I-4: ADR-001 invariants apply identically.** A TS AppHost
  driving the publisher produces the same Coolify-side shape as a
  C# AppHost with equivalent registration data. No TS-specific
  deviation from one-AppHost-one-project, lazy-environment-
  materialisation, name-keyed identity, or config-driven
  destination is introduced anywhere in this feature.
- **I-5: no handle value is read by the shim.** Neither
  compile-time-visible API nor runtime call resolves
  `url` / `token` / `prefix` / `username` / `password` / `name` /
  `dashboardToken` to their string values inside any shim function.
  Asserted by injecting sentinel values at the TS AppHost layer and
  verifying the sentinels never reach the shim's argument-
  validation log lines, the publisher slot's debug surface, or any
  emitted stderr.
- **I-6: null / undefined handle is rejected at runtime.** Even
  with TS types enforcing non-nullability at compile time, the
  shim's runtime backstop throws when a handle argument is `null`
  or `undefined`, naming the offending argument. Asserted by
  calling each shim function from un-typechecked JS with explicit
  `null` / `undefined`.
- **I-7: `withImageRegistry`'s credentials tuple is shape-
  enforced.** Passing a one-element tuple, or a two-element tuple
  with `null` / `undefined` in either slot, throws at runtime
  (TypeScript types prevent the one-element case at compile time;
  the runtime backstop covers both). Mirrors FT-003's
  `ArgumentException`.
- **I-8: idempotency on multi-call is observable through the
  publisher slot, not through the shim.** The shim does not
  maintain its own per-builder record of "have I been called
  before for this function" — it delegates that to the publisher
  slot's existing idempotency check (the same check the C#
  extension delegates to). Asserted by code-review of the emitted
  shim source.
- **I-9: emitted module is byte-stable for a given publisher
  version.** No timestamps, no machine-specific paths, no `os.tmpdir()`
  derivations leak into the file body. Re-running `aspire restore`
  twice on the same project against the same publisher version
  produces identical bytes. Asserted by SHA-256 comparison across
  two consecutive `aspire restore` invocations.
- **I-10: generator hook writes nowhere outside the designated
  generated-modules directory.** Asserted by filesystem-diff on
  `aspire restore` invocation: only the designated file is
  created / updated.
- **I-11: no TS-side reimplementation of phases, Coolify client,
  or image flow appears in the emitted module.** The emitted file
  contains only the four function definitions, the supporting type
  declarations, and the file header. Asserted by inspection: zero
  `fetch(`, zero `import("http")`, zero `child_process`, zero
  `docker`, zero image-tag composition, zero phase-name string
  literals (`"configure"`, `"build"`, `"push"`, `"deploy"`,
  `"verify"`) appear in the module body.
- **I-12: emitted module declares no default export.** All four
  functions are named exports. Asserted by parsing the module's
  exports table.

### Error handling

- **Null / undefined handle at a shim call site (after compile
  time)** → the shim throws a TS `Error` whose `.message` names the
  offending argument and points to the corresponding C# extension
  for the rationale (e.g. `withCoolifyDeploy: 'token' argument
  must be a parameter handle; got undefined. See FT-001.`). Mirrors
  the C# `ArgumentNullException` discipline at runtime.
- **Wrong tuple shape on `withImageRegistry`'s `credentials`
  argument** → TS types prevent the one-element form at compile
  time; the runtime backstop throws on `null` / `undefined` inside
  a two-element tuple, naming the offending tuple position
  (`credentials[0]` or `credentials[1]`). Mirrors FT-003's
  `ArgumentException`.
- **Calling a shim function before `withCoolifyDeploy`** → the
  publisher slot exists on the publisher instance, which is
  attached by `withCoolifyDeploy`'s slot-write step. If
  `withImageRegistry` / `withCoolifyDestination` /
  `withManagedDashboard` is called first, the shim either
  attaches the publisher itself (matching whatever discipline the
  C# extensions follow when chained off the builder rather than
  off the `WithCoolifyDeploy(...)` result) or throws a precise
  error telling the developer to call `withCoolifyDeploy(...)`
  first. The exact ordering policy mirrors the C# extensions'
  ordering policy — FT-011 must not introduce a TS-specific
  ordering constraint. The emitted shim source documents the
  inherited policy in a comment block.
- **Generator hook fails mid-write** → `aspire restore` reports
  the failure through its own error-surfacing channel; FT-011 does
  not introduce a new error symbol. The hook is atomic per
  emitted file (write-to-temp-then-rename) so a partial file is
  never observed by a subsequent build.
- **TS AppHost runs without `aspire restore` having been run** →
  the generated module is absent; the TS AppHost fails to compile
  with a standard TS module-not-found error. Not an FT-011 concern
  beyond ensuring the module path is the conventional one.
- **TS AppHost author writes the module path manually instead of
  importing from the generated path** → not supported; the module
  is generated and any hand-written equivalent is the developer's
  responsibility. FT-011 does not police hand-written
  equivalents.

### Boundaries

- **In scope for FT-011:**
  - the `aspire restore` generator hook that emits the TS module
    for any AppHost project referencing the Coolify publisher
  - the emitted TypeScript module's public contract: four named
    exports (`withCoolifyDeploy`, `withImageRegistry`,
    `withCoolifyDestination`, `withManagedDashboard`), camelCase,
    parameter-handle-typed signatures, tuple shape for
    credentials, no default export, no other exports
  - per-function idempotency rules matching C# exactly (first-
    call-wins for `withCoolifyDeploy`, last-call-wins for the
    other three)
  - runtime null/undefined backstop validation in every shim
    function, with error messages that point back to the
    corresponding C# extension's rationale
  - byte-stable emitted-file contract (no timestamps, no
    machine-specific paths in the body)
  - the auto-generated header line and the do-not-edit comment
  - writing the recorded data into the same publisher-instance
    slots the C# extensions write into (single source of truth
    on the publisher)
- **Out of scope for FT-011** (handled elsewhere or deferred):
  - TS-side reimplementation of any phase (configure / build /
    push / deploy / verify) — the .NET publisher runs the phases
    regardless of authoring surface
  - TS-side Coolify client of any kind — `ICoolifyClient` is
    C#-only; the shim never touches HTTP
  - TS-side image build or registry push — the .NET publisher
    drives Aspire's image pipeline regardless of authoring
    surface
  - TS-side parameter-value resolution — handles are recorded
    by reference; values are read by the publisher, not the
    shim
  - TS-side parameter-secret redaction — inherited from the
    publisher's `secret: true` parameter redaction; the shim
    never logs handle values, so there is nothing for the shim
    to redact
  - any new error symbol (no `E_…` or `W_…` introduced by
    FT-011 — all behavioural error symbols stay in their
    respective C# features)
  - TS-side equivalents of FT-007 / FT-008 hook points — those
    are publisher-internal extension points, not part of the
    authoring surface
  - multi-target `withCoolifyDeploy` on a single TS builder —
    inherits FT-001's first-call-wins no-op-on-second-call
    contract; multi-target is post-v1 for both surfaces
  - non-TS authoring-surface parity (Python, F#, etc.)
  - changes to ADR-001's mapping invariants for TS-authored
    AppHosts — invariants apply identically regardless of
    surface
  - `aspire restore` generator-modules infrastructure itself
    — FT-011 consumes it as a black box (it is part of the
    Aspire SDK), exactly as `aspire-ssh-deploy`'s TS variant
    does

## Out of scope

- **TS-side reimplementation of any behavioural surface.** Phases,
  Coolify client, image flow, parameter-value resolution, env-var
  writes, drift detection — all stay 100% in C#. The TS shim is a
  recording surface only.
- **New error symbols.** Every `E_…` and `W_…` in the publisher
  stays exactly where its feature put it. FT-011 surfaces TS
  runtime errors via standard `Error` instances; it does not
  introduce a stable observable error-symbol contract of its own.
- **Multi-target `withCoolifyDeploy` on a single TS builder.**
  First-call-wins applies; second-call-with-different-handles is a
  silent no-op. Multi-target deploys remain post-v1 for both
  authoring surfaces.
- **Hand-written or developer-customised TS module.** The module
  is generated by `aspire restore` and is regenerated on every
  invocation. Manual edits are not preserved and not supported.
- **Non-TypeScript authoring surfaces.** Python, F#, generated-from-
  schema bindings, etc. are out of v1.
- **Persistent state on disk authored by the shim at runtime.**
  Forbidden — inherits FT-001 I-3. The only file FT-011 produces
  is the `aspire restore`-time generated module, which is build
  artefact, not runtime state.
- **TS-side ordering enforcement different from C#.** If C#
  permits calling `withImageRegistry` before `withCoolifyDeploy`
  on the same builder (via attach-the-publisher-if-missing), the
  TS shim permits the same; if C# requires `WithCoolifyDeploy(...)`
  first, the TS shim requires the same. FT-011 mirrors the C#
  policy verbatim and does not invent a TS-specific rule.
- **TS-side equivalents of FT-007 / FT-008 hook points.** Those
  are internal to the publisher and not part of any AppHost
  authoring surface.
- **Generator-hook configuration knobs.** The hook fires
  unconditionally for any AppHost project that references the
  Coolify publisher; there is no opt-in / opt-out / module-path-
  override in v1.
