# Aspire 13.3.5 interactive parameter prompting — research for FT-012

Goal: make `aspire deploy` (interactive) prompt for unset `ParameterResource` values
instead of fail-fast with `E_AUTH_TOKEN_MISSING` in `CoolifyDeployingPublisher`.

## TL;DR

Aspire 13 already does this for us — but only if our publisher participates in the
pipeline correctly. In Aspire **13.1+** a built-in pipeline step
`process-parameters` runs *before* `DeployPrereq` / `BuildPrereq` / `PublishPrereq`
and calls `ParameterProcessor.InitializeParametersAsync(..., waitForResolution: true)`,
which uses `IInteractionService.PromptInputsAsync` to interactively prompt for any
unresolved parameter values, then completes each `ParameterResource.WaitForValueTcs`
so subsequent `GetValueAsync` calls return the user-supplied value.

The bug today is that our Coolify pipeline step (`CoolifyBuilderExtensions.cs:71`)
uses `requiredBy: WellKnownPipelineSteps.Deploy` only for the Verify phase and has
**no `dependsOn` on `DeployPrereq` / `ProcessParameters`**, so on some orderings
our step's `GetValueAsync` is reached before parameter prompting has run.
Two-line fix: depend on `WellKnownPipelineSteps.DeployPrereq` (and/or
`ProcessParameters` directly) — *or* call `IInteractionService.PromptInputAsync`
ourselves as a belt-and-braces fallback.

## Q1 — API surface

- **Interface:** `IInteractionService` in namespace `Aspire.Hosting`
  (file `src/Aspire.Hosting/IInteractionService.cs`).
- **Marked `[Experimental("ASPIREINTERACTION001")]`** — callers must suppress the
  diagnostic: `#pragma warning disable ASPIREINTERACTION001`.
- Methods relevant to a publisher:
  - `Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken ct = default)`
  - `Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken ct = default)`
  - `Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken ct = default)`
  - `bool IsAvailable { get; }`
- `InteractionInput` (same namespace) carries `Name`, `Label`, `InputType`
  (`Text | SecretText | Choice | Boolean | Number`), `Required`, `Placeholder`,
  `Value` (set on success).

## Q2 — detecting interactivity

`IInteractionService.IsAvailable` is the single source of truth. It is `true`
when either the dashboard is up OR the CLI is attached to a TTY (i.e. when the
user did *not* pass `--non-interactive`). The framework — not the caller — owns
this decision.

Pipeline contexts do NOT expose a separate `IsInteractive` flag; use
`IsAvailable` from the resolved `IInteractionService`.

## Q3 — behaviour under `--non-interactive`

- `IsAvailable` returns `false`.
- Calling `PromptInputAsync` / `PromptInputsAsync` when `IsAvailable == false`
  **throws** (per the Aspire docs page on the interaction service).
- During `aspire publish` / `aspire deploy`, only `PromptInputAsync` and
  `PromptInputsAsync` are allowed even when available; the message-box /
  confirmation / notification methods throw in CLI contexts.

Conclusion: always guard with `if (interactionService.IsAvailable) { … }` and
keep the existing fail-fast (`E_AUTH_TOKEN_MISSING`) on the `else` branch.

## Q4 — is the same API used by `aspire run` parameter prompts?

Yes. Both `aspire run` (dashboard form) and `aspire deploy` (CLI prompts)
go through `IInteractionService` → `ParameterProcessor.InitializeParametersAsync`
→ per-parameter `PromptInputsAsync`. The dashboard renders an HTML form;
the CLI renders sequential terminal prompts. Caller code is identical.

Source: `src/Aspire.Hosting/Orchestrator/ParameterProcessor.cs` —
`HandleUnresolvedParametersAsync` builds `InteractionInput[]` from the unresolved
`ParameterResource`s, calls `PromptInputsAsync`, then for each result calls
`parameterResource.WaitForValueTcs?.TrySetResult(inputValue)` so any pending
`GetValueAsync` await unblocks with the user-supplied value.

## Q5 — preferred fix and code sketch

**Preferred (minimal) fix — let the framework do it:** add a dependency on the
built-in parameter-prompting step when we register our Coolify pipeline step.
In `CoolifyBuilderExtensions.cs` (around line 71):

```csharp
pipeline.AddStep(
    name: $"coolify-{phase}",
    action: ctx => publisher.RunPhaseAsync(phase, ctx),
    dependsOn: new[] { WellKnownPipelineSteps.DeployPrereq },   // <-- new
    requiredBy: current == CoolifyPhase.Verify
        ? new[] { WellKnownPipelineSteps.Deploy }
        : null);
```

`DeployPrereq` itself `dependsOn` `WellKnownPipelineSteps.ProcessParameters`
(Aspire 13.1+, see PR dotnet/aspire#13041), so by the time our step runs every
`ParameterResource.GetValueAsync` will already have been resolved — either from
config / user-secrets or from an interactive prompt — and return the value
synchronously. **No publisher code change required**.

**Belt-and-braces fallback** — if the resolved value is still empty (e.g. user
is on Aspire 13.0 or runs an apphost without the ProcessParameters step),
prompt inline. Drop this into `CoolifyDeployingPublisher` where the current
`GetValueAsync` + null-check lives (e.g. line 419):

```csharp
#pragma warning disable ASPIREINTERACTION001
var interaction = context.ServiceProvider.GetRequiredService<IInteractionService>();
var tokenValue = await Token.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false);

if (string.IsNullOrEmpty(tokenValue) && interaction.IsAvailable)
{
    var input = new InteractionInput
    {
        Name      = Token.Resource.Name,
        Label     = "Coolify API token",
        InputType = InputType.SecretText,     // masked / no-echo
        Required  = true,
        Placeholder = "coolify_pat_…",
    };
    var result = await interaction.PromptInputAsync(
        title: "Coolify deploy: missing parameter",
        message: $"Parameter '{Token.Resource.Name}' is required.",
        input: input,
        cancellationToken: cancellationToken).ConfigureAwait(false);

    if (result is { Canceled: false, Data: { Value: { Length: > 0 } v } })
    {
        tokenValue = v;
        Token.Resource.WaitForValueTcs?.TrySetResult(v); // unblock other awaiters
    }
}

if (string.IsNullOrEmpty(tokenValue))
{
    throw new InvalidOperationException("E_AUTH_TOKEN_MISSING …");
}
#pragma warning restore ASPIREINTERACTION001
```

`context.ServiceProvider` is reachable from `PipelineStepContext`
(`CoolifyDeployingPublisher.cs:285`). `IInteractionService` is registered by
`Aspire.Hosting`'s default `IDistributedApplicationBuilder` wiring — no extra
DI registration needed.

## Q6 — secrets

Use `InputType.SecretText`. In the dashboard this renders as a masked
`<input type="password">`; in the CLI it disables echo (no characters printed
while typing). All persistence behaviour (user-secrets save offer) is handled
by Aspire — we just set the InputType.

`ParameterResource.Secret == true` is the canonical signal; we should derive
`InputType` from that:

```csharp
InputType = Token.Resource.Secret ? InputType.SecretText : InputType.Text,
```

## Q7 — source pointers (Aspire main branch)

| Concern | File |
|---|---|
| `IInteractionService` interface | `src/Aspire.Hosting/IInteractionService.cs` |
| Default impl (CLI + dashboard transports) | `src/Aspire.Hosting/InteractionService.cs` |
| Parameter resolution + prompt orchestration | `src/Aspire.Hosting/Orchestrator/ParameterProcessor.cs` (`InitializeParametersAsync`, `HandleUnresolvedParametersAsync`, `ApplyParameterValueAsync`) |
| `ParameterResource.WaitForValueTcs` / `GetValueAsync` | `src/Aspire.Hosting/ApplicationModel/ParameterResource.cs` |
| Pipeline step constants (`ProcessParameters`, `DeployPrereq`, …) | `src/Aspire.Hosting/Pipeline/WellKnownPipelineSteps.cs` |
| Registration of `process-parameters` → required-by Deploy/Build/PublishPrereq | `src/Aspire.Hosting/Pipeline/DistributedApplicationPipeline.cs` (PR dotnet/aspire#13041, milestone 13.1) |
| Docs page | <https://aspire.dev/extensibility/interaction-service/> |
| Diagnostic ID | `ASPIREINTERACTION001` — <https://aspire.dev/diagnostics/aspireinteraction001/> |

## Recommendation for FT-012

1. **Primary fix** — wire `dependsOn: WellKnownPipelineSteps.DeployPrereq` (or
   `ProcessParameters` directly, but `DeployPrereq` is the documented contract)
   in `CoolifyBuilderExtensions.AddStep`. This is the canonical Aspire 13
   pattern and makes our publisher behave identically to the Azure publishers.
2. **Secondary fix** — keep the existing `string.IsNullOrEmpty` check and add
   the `IInteractionService.PromptInputAsync` fallback shown in Q5 for
   defence-in-depth (covers Aspire 13.0 consumers and any param whose
   ValueProvider produces empty without a `WaitForValueTcs`).
3. Surface `--non-interactive` behaviour in TC-020: `IsAvailable == false` →
   `PromptInputAsync` not called → existing fail-fast preserved.

DONE
