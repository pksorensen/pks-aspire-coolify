---
id: FT-012
title: Interactive parameter prompting in the configure phase
phase: 1
status: complete
depends-on:
- FT-002
adrs:
- ADR-004
- ADR-003
- ADR-001
- ADR-006
tests:
- TC-019
- TC-020
- TC-021
domains: []
domains-acknowledged: {}
---

## Description

FT-002 made the configure phase the single place where Coolify auth
parameters get resolved, and it committed to four fail-fast `E_…`
diagnostics whenever the token, url, or (via FT-004 / FT-005 / FT-010)
the registry-prefix / destination / dashboard-token parameters cannot
be turned into a usable value. End-to-end smoke testing has surfaced
that this is the wrong default for one specific situation: an
**interactive `aspire deploy`** run where the developer simply has not
set the parameter yet. The host pipeline (`aspire run`, `aspire deploy`)
already knows how to prompt the user for unset parameters; v1 of the
publisher short-circuits with `E_AUTH_TOKEN_MISSING` (or the equivalent
`UNREACHABLE` symbol for the url) before Aspire ever gets a chance to
ask.

FT-012 closes that gap **as a property of the configure phase**, not as
a particular API call. When the deploy is interactive AND the parameter
value is unset, the publisher MUST cause the canonical Aspire 13
parameter-prompt UX to fire (whatever mechanism the SDK exposes for
that — implementation detail, deferred the same way FT-002 deferred
the Coolify probe endpoint to the `coolify-api` client). When the
prompt completes successfully, the configure phase proceeds exactly as
if the value had been set in `dotnet user-secrets` from the start.
When the deploy is non-interactive (e.g. `--non-interactive`, CI, no
TTY — per whatever signal Aspire 13 surfaces), the existing fail-fast
behaviour is preserved verbatim: same `E_…` symbols, same precedence
rule, same stderr shape, same "no Coolify side effect on any fail-fast
path" invariant. CI environments must not start hanging on a prompt
that will never be answered.

This feature is **property-only**: it specifies *what* the configure
phase does in the interactive-and-unset case, not *which* Aspire 13
type or method it calls. The exact API surface is recorded in the
implementation comment / ADR-004 amendment at FT-012 implement time.

The same shape applies, by inheritance, to every other parameter
handle the publisher holds — `RegistryPrefix` (FT-004), `DestinationName`
(FT-005), `DashboardToken` (FT-010), and any future parameter — because
the prompt branch is keyed on *the parameter being unset at resolution
time*, not on which phase or sub-step happens to resolve it. FT-012
amends the resolution path in each of those features uniformly; it
does not add a separate prompting mechanism per parameter.

## Research findings (Aspire 13.3.5)

Full notes: `docs/aspire13-prompt-api-research.md`. Summary of the load-bearing
facts an implementer needs:

- **Root cause** — Aspire 13.1+ already ships a built-in pipeline step named
  `process-parameters` that calls `ParameterProcessor.InitializeParametersAsync(...,
  waitForResolution: true)`, which uses `IInteractionService.PromptInputsAsync`
  to prompt the user for every unresolved `ParameterResource` value, then
  completes each resource's `WaitForValueTcs` so subsequent `GetValueAsync`
  calls return the prompted value. **AppHost authors keep writing
  `builder.AddParameter("name")` exactly as today** — the framework does the
  prompting for free when `IInteractionService.IsAvailable` is true.
- **The bug** — `CoolifyBuilderExtensions.WithCoolifyDeploy` registers each
  `coolify-*` pipeline step with `dependsOn: previousStep` only (chaining
  within our own phases). It does **not** declare a dependency on Aspire's
  `WellKnownPipelineSteps.DeployPrereq` (which itself depends on
  `ProcessParameters`). Aspire's topological scheduler is therefore free to
  run `coolify-configure` before `process-parameters` — and does in practice,
  which is why `GetValueAsync` returns null and we fail-fast.
- **Primary fix (one line per step)** — add `WellKnownPipelineSteps.DeployPrereq`
  to the `dependsOn` array when registering every `coolify-*` step (or at
  minimum `coolify-configure`, since the rest already chain off it). This is
  the canonical Aspire 13 pattern and matches what the Azure publishers do.
  Code sketch:

  ```csharp
  pipeline.AddStep(
      name: stepName,
      action: ctx => publisher.RunPhaseAsync(current, ctx),
      dependsOn: previousStep is null
          ? new[] { WellKnownPipelineSteps.DeployPrereq }
          : new[] { previousStep, WellKnownPipelineSteps.DeployPrereq },
      requiredBy: requiredBy);
  ```

- **Belt-and-braces fallback** (optional but recommended) — after
  `GetValueAsync` returns and value is still empty, call
  `IInteractionService.PromptInputAsync` ourselves, gated on
  `IInteractionService.IsAvailable`. Covers Aspire 13.0 consumers and
  unusual `ValueProvider`s. Requires `#pragma warning disable
  ASPIREINTERACTION001` because the interface is `[Experimental(...)]`.
  Use `InputType.SecretText` when `Token.Resource.Secret == true` so the
  CLI disables echo / the dashboard renders masked input.
- **Non-interactive contract preserved** — `IInteractionService.IsAvailable`
  returns `false` when `aspire deploy --non-interactive` is used; calling
  `PromptInputAsync` in that mode throws. So our fallback is guarded by
  `IsAvailable`, and the existing `E_AUTH_TOKEN_MISSING` /
  `E_COOLIFY_UNREACHABLE` / etc. fail-fast paths fire unchanged when CI runs
  with no value set.
- **Source pointers** (Aspire main branch):
  - `src/Aspire.Hosting/IInteractionService.cs` — interface
  - `src/Aspire.Hosting/Orchestrator/ParameterProcessor.cs` —
    `HandleUnresolvedParametersAsync` is the canonical implementation pattern
  - `src/Aspire.Hosting/Pipeline/WellKnownPipelineSteps.cs` — step name
    constants
  - `src/Aspire.Hosting/Pipeline/DistributedApplicationPipeline.cs` —
    `process-parameters` step registration (PR dotnet/aspire#13041, 13.1)
  - Docs: <https://aspire.dev/extensibility/interaction-service/>

## Functional Specification

### Inputs

- The same `(url, token)` parameter handles FT-002 reads, plus
  `RegistryPrefix` (FT-004), `DestinationName` (FT-005), and
  `DashboardToken` (FT-010) — the full set of `IResourceBuilder<ParameterResource>`
  handles captured on `CoolifyDeployingPublisher` at builder time.
- The Aspire-provided **interactivity signal** for the current deploy
  invocation. FT-012 does not name the property; it requires only that
  the publisher consult whatever signal Aspire 13 exposes for "is
  `--non-interactive` in effect / is there a TTY / is the user
  reachable for a prompt." The signal is a boolean for FT-012's
  purposes: interactive (`true`) or non-interactive (`false`).
- The Aspire-provided **parameter-prompt mechanism** for the current
  deploy invocation. FT-012 does not name the API; it requires only
  that the publisher cause the canonical prompt to fire for the
  unset parameter when interactivity is `true`, and that the prompt
  honours Aspire's existing `secret: true` redaction for secret
  parameters (so the token is masked as the user types and never
  echoed afterwards).
- The Aspire deploy `CancellationToken`, propagated from FT-001's
  phase context.

### Outputs

- **On the interactive-and-unset path:** the publisher causes a prompt
  to fire for the unset parameter, waits for it, and on a non-empty
  reply proceeds with that value as if it had been resolved from
  `dotnet user-secrets`. No `E_…` diagnostic is emitted. The
  configure phase continues to step 2 (url) → step 3 (client) → step 4
  (combined version + auth probe) → step 5 (version-floor check), per
  FT-002 §Behaviour.
- **On the non-interactive-and-unset path:** behaviour is unchanged
  from FT-002 §Behaviour — the matching `E_…` symbol is written to
  stderr in the existing format, the publisher exits non-zero before
  `build` enters, no Coolify side effect occurs. The four symbols
  remain stable observable contract (ADR-004 I-5, FT-002 I-5).
- **On the interactive-and-set path** (user already has the value
  in user-secrets / env-var): no prompt fires; behaviour is identical
  to today's FT-002 happy path.
- **On a cancelled prompt** (Ctrl-C, stdin closed mid-prompt): the
  configure phase exits with FT-001's cancellation diagnostic, not
  with an `E_…` symbol. (Cancellation is the user actively aborting,
  not "the value is absent.")

### State

- **No persistent on-disk state introduced by FT-012.** Inherits
  ADR-003 §4 and FT-002 §State. The publisher itself writes nothing.
  If Aspire's parameter-prompt machinery persists the prompted value
  back to user-secrets (the way `aspire run`'s parameter prompts
  do), that persistence is **Aspire's**, not FT-012's; the
  publisher takes no opinion and does not duplicate the write. The
  next deploy invocation resolves the parameter freshly through the
  same machinery, which composes correctly with either persistence
  choice the host makes.
- **No in-memory cross-deploy state.** A prompt that fires during one
  `aspire deploy` invocation has no effect on the next invocation's
  prompt decision other than via Aspire's own resolution path.
- **At most one prompt per parameter per deploy** (I-3 below): once a
  value has been obtained for a given parameter handle inside a
  single deploy invocation, subsequent phases that need the same
  parameter reuse the resolved value (today: via the
  `ICoolifyClient` instance for token, via the captured handle's
  resolution result for url / registry-prefix / destination /
  dashboard-token). No second prompt for the same handle.

### Behaviour

The configure phase's parameter-resolution step (FT-002 §Behaviour
step 1 for token, step 2 for url; FT-004's analogous step for
registry-prefix; FT-005's for destination; FT-010's for
dashboard-token) is amended as follows. The change applies
uniformly to every parameter handle the publisher holds.

For each parameter handle, in the same order each owning feature
already specifies:

1. **Resolve the parameter through Aspire's standard mechanism**
   (unchanged from today).
2. **If the resolved value is non-null and non-empty after trimming
   surrounding whitespace**, proceed with that value — no prompt,
   no diagnostic. (Unchanged happy path.)
3. **If the resolved value is null or empty, consult the Aspire
   interactivity signal:**
   - **Interactive (signal = true):** request the canonical Aspire 13
     parameter prompt for this parameter and await its result.
     - If the prompt yields a non-empty value, treat that value as
       the resolution result and proceed as in step 2 — the
       configure phase advances to its next step (e.g. for token →
       url resolution → client construction → combined probe).
     - If the prompt yields an empty value (user pressed Enter on a
       blank prompt), treat as unset and emit the matching `E_…`
       symbol per the pre-FT-012 fail-fast path. (We do not loop;
       the user has answered "no value.")
     - If the prompt is cancelled (Ctrl-C, stdin EOF, deploy
       `CancellationToken` fires while the prompt is awaiting),
       exit with FT-001's cancellation diagnostic — not with an
       `E_…` symbol.
   - **Non-interactive (signal = false):** behaviour is exactly
     what FT-002 / FT-004 / FT-005 / FT-010 already specify for
     the unset case — emit the matching `E_…` symbol verbatim,
     exit non-zero, no Coolify side effect. **No prompt is
     requested.** (I-1 below.)
4. **The prompted value flows into the same downstream consumer**
   the resolved value would have flowed into. For the token, that
   is the `ICoolifyClient`'s `Authorization: Bearer …` header
   (FT-002 §Behaviour step 3). For url, the client base address.
   For registry-prefix, FT-004's image-tag computation. For
   destination, FT-005's destination resolution. For
   dashboard-token, FT-010's dashboard sub-phase. **FT-012
   introduces no new propagation channel** — the prompted value
   reuses the existing wiring.

**Precedence rule is preserved.** The four `E_…` symbols and their
precedence (ADR-004 I-5, FT-002 §Behaviour "Precedence rule") are
unchanged. The interactive-prompt branch fires *before* the
matching `E_…` decision, so on the interactive path no `E_…` is
emitted in the first place; on the non-interactive path the
existing precedence applies verbatim.

**Phase boundary is preserved.** Whether the publisher prompts or
fails-fast, all of it happens **inside** the configure phase
observed by FT-001's phase-boundary logging. A successful prompt
followed by a successful probe shows as `configure: enter …
configure: exit (ok)`; a non-interactive fail-fast shows as
`configure: enter … configure: exit (failed)` with no `build:
enter` line. (FT-002 I-7 still holds.)

**Redaction is preserved.** For secret parameters (token,
dashboard-token), the publisher relies on Aspire's existing
`secret: true` prompt UX to mask user input and to never echo the
entered value. FT-012 adds no logging of the prompted value, no
inclusion of it in any FT-012-authored message, and no write of it
to disk. The sentinel-grep assertion from TC-004 §4 (no occurrence
of the literal token in stdout / stderr / Aspire logs / exception
traces / dashboard parameter display) holds verbatim for prompted
values too.

### Invariants

- **I-1: non-interactive fail-fast is preserved.** When the Aspire
  interactivity signal reports `false` (the canonical signal for
  `--non-interactive`, no TTY, CI) AND the parameter value is
  unset, the publisher MUST emit the same `E_…` symbol with the
  same stderr shape and the same precedence the pre-FT-012 spec
  required. CI deploys MUST NOT hang on a prompt that will never
  be answered. This invariant is what TC-020 pins.
- **I-2: prompted secret values are never logged.** For any
  parameter declared with `secret: true` (token,
  dashboard-token), no FT-012 code path may write the prompted
  value to stdout, stderr, the Aspire structured-log pipeline,
  the Aspire dashboard parameter display, an exception message,
  or any file. Redaction inherits from Aspire's `secret: true`
  prompt UX plus FT-012 emitting no value-bearing messages of its
  own. Asserted by sentinel-grep in TC-019.
- **I-3: at most one prompt per parameter per deploy.** Within a
  single `aspire deploy` invocation, the canonical Aspire prompt
  for a given parameter handle fires **at most once**, regardless
  of how many phases (configure, build, push, deploy, verify)
  reference the same handle. Subsequent phases consume the
  already-resolved value through the existing propagation channels
  (e.g. the `ICoolifyClient` instance for the token). A re-prompt
  in a later phase is a bug.
- **I-4: the interactive branch fires only when both conditions
  hold simultaneously** — interactivity = true AND resolved value
  is null/empty. Any other combination (interactive + set,
  non-interactive + set, non-interactive + unset) MUST NOT cause a
  prompt to be requested.
- **I-5: no new observable symbols.** FT-012 introduces no new
  `E_…` or `W_…` symbols on either path. The interactive happy
  path is silent on the diagnostic surface; the non-interactive
  path reuses the four pre-existing `E_…` symbols verbatim.
- **I-6: the prompt is property, not API.** FT-012 specifies the
  *observable contract* (a prompt happens, the user's reply is
  consumed, no logging leak); it does not specify which Aspire 13
  type or method realises the prompt. The implementer records the
  exact API surface in the ADR-004 amendment at implement time.
- **I-7: phase boundary holds.** Both branches resolve entirely
  inside the configure phase as observed by FT-001's phase-boundary
  logging. No prompt may straddle a phase boundary; no fail-fast
  may leak into `build`.
- **I-8: no Coolify side effect on the prompt-cancelled path.**
  Cancelling a prompt mid-input MUST leave the Coolify instance
  byte-identical to its pre-deploy state, identical to today's
  `E_AUTH_TOKEN_MISSING` invariant (FT-002 I-2). The cancellation
  diagnostic exits the configure phase before any side-effecting
  step.

### Error handling

- **Aspire prompt subsystem itself throws** (e.g. the prompt API
  faults because the host is in a degenerate state — neither
  cleanly interactive nor cleanly non-interactive): treat as
  non-interactive with the matching `E_…` symbol. Better to
  preserve the CI contract (fail-fast, never hang) than to
  ambiguously enter `build`.
- **Empty reply to a prompt:** treated as "value remains unset" →
  matching `E_…` symbol. Not a special case.
- **Cancellation during prompt:** FT-001 cancellation diagnostic
  (not an `E_…`).
- **Aspire interactivity signal is itself unavailable** (older
  SDK / future API change): treat as non-interactive (CI-safe
  default). Implementer documents the minimum Aspire version that
  exposes the signal in the ADR-004 amendment.

### Boundaries

- **In scope for FT-012:**
  - amending the parameter-resolution step in the configure phase
    (and the analogous steps in FT-004 / FT-005 / FT-010 that
    resolve parameter handles owned by the publisher) so that the
    interactive-and-unset case prompts the user via Aspire's
    canonical mechanism
  - preserving the non-interactive fail-fast behaviour verbatim
    (I-1) — same `E_…` symbols, same stderr shape, same precedence
  - preserving redaction (I-2) for secret parameters' prompted
    values
  - the "at most one prompt per parameter per deploy" invariant
    (I-3)
  - the cancellation semantics (FT-001 cancellation diagnostic,
    not an `E_…`)
  - amending ADR-004 with a §5a sub-section recording the
    interactive-prompt branch and the chosen Aspire 13 API at
    implement time
- **Out of scope for FT-012** (deferred or owned elsewhere):
  - naming the specific Aspire 13 type / method that realises the
    prompt (implementer discovery, recorded in the ADR-004
    amendment) — see I-6
  - naming the specific Aspire 13 property / context field that
    surfaces the interactivity signal (same — implementer
    discovery, ADR-004 amendment)
  - persistence of prompted values back to user-secrets — this is
    Aspire's choice as the host of the prompt; FT-012 takes no
    opinion and writes nothing itself
  - prompting for parameters not owned by `WithCoolifyDeploy` /
    `WithImageRegistry` / `WithCoolifyDestination` /
    `WithManagedDashboard` — those are Aspire's responsibility
    via the standard `AddParameter` UX
  - prompt UI flourishes (prefilled defaults, validation regex,
    multi-line input) — out of v1; the prompt is whatever
    Aspire's standard parameter-prompt UX does
  - retry-on-bad-value loops — a single prompt; an empty reply
    becomes the matching `E_…` symbol
  - a separate `aspire coolify configure` interactive wizard
    command — v1 piggybacks on `aspire deploy`'s existing prompt
    UX only
  - changing the four `E_…` symbols (ADR-004 I-5 stays stable)

## Out of scope

- **Aspire-version pinning for the prompt API.** When the
  implementer picks the canonical Aspire 13 mechanism, the
  ADR-004 amendment records the minimum supported version. FT-012
  itself does not pin a version; if the chosen API is in Aspire
  13.x, the package's existing Aspire 13 pin (per the project's
  `CLAUDE.md`) is sufficient.
- **Multi-step parameter wizards** ("ask me for url, then ask me
  for token, then ask me to pick a destination from a discovered
  list"). v1 prompts independently for each unset parameter in
  the order each owning feature already resolves them.
- **Prompt-during-rotation flows.** Rotation is still "edit the
  parameter value; the publisher caches nothing" (ADR-004 §7).
  FT-012 does not introduce a "rotate now?" prompt.
- **A `--prompt-for-missing=false` opt-out separate from
  `--non-interactive`.** The interactivity signal is the sole
  control; a deploy is either interactive (prompts allowed) or
  non-interactive (fail-fast). No third mode.
- **Caching prompted values across deploys inside the
  publisher.** Persistence (if any) is Aspire's via its standard
  parameter machinery; the publisher itself stores nothing.
- **Prompting for non-publisher parameters.** Other Aspire
  resources that happen to be unset are Aspire's concern; FT-012
  is scoped to the five parameter handles the publisher holds.
