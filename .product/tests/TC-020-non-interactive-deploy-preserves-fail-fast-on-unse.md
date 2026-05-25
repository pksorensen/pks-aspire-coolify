---
id: TC-020
title: non_interactive_deploy_preserves_fail_fast_on_unset_parameter
type: scenario
status: passing
validates:
  features:
  - FT-012
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~InteractiveParameterPromptExitCriteria
runner-timeout: 120
last-run: 2026-05-25T06:34:08.220328421+00:00
last-run-duration: 2.6s
---

# TC-020 — Non-interactive deploy preserves fail-fast on unset parameter

Validates **FT-012 I-1** (non-interactive fail-fast preservation).
This is the load-bearing CI contract: when `aspire deploy` runs in
non-interactive mode (whatever signal the Aspire 13 SDK exposes —
typically `--non-interactive`, no TTY, or a CI environment) AND a
required parameter handle resolves to an empty value, the publisher
MUST emit the same `E_…` symbol with the same stderr shape and the
same precedence as the pre-FT-012 spec. **CI MUST NOT hang on a prompt
that will never be answered.**

This TC re-asserts the four FT-002 / ADR-004 fail-fast scenarios under
the explicit non-interactive signal, so the introduction of FT-012's
interactive branch cannot silently regress them.

## Setup

Same AppHost as TC-004:

```csharp
var url   = builder.AddParameter("coolify-homelab-url");
var token = builder.AddParameter("coolify-homelab-token", secret: true);

builder.AddDockerComposeEnvironment("homelab")
       .WithCoolifyDeploy(url, token);
```

Test driver runs `aspire deploy --environment homelab` in
**non-interactive mode** (passes whatever flag / sets whatever env-var
the Aspire 13 SDK uses to signal `--non-interactive`; intercepts the
same signal the publisher consults so the test stays API-agnostic).

Every scenario asserts an **upper bound** on wall-clock time
(`< 30s`) to detect "deploy hung waiting for a prompt that will never
arrive."

## Scenarios

### 1. Non-interactive + unset token → E_AUTH_TOKEN_MISSING, no prompt, no hang

- Given the url parameter is set, the token parameter is unset,
- and the deploy is non-interactive,
- when `aspire deploy` runs,
- then the process exits non-zero **within 30s** (no prompt-wait
  hang),
- and stderr matches `E_AUTH_TOKEN_MISSING` (literal first
  whitespace-delimited token; FT-002 I-5 + ADR-004 I-5 unchanged),
- and the error message names the parameter
  `coolify-homelab-token` and shows both remediation forms
  (`dotnet user-secrets set Parameters:coolify-homelab-token …`
  and `Parameters__coolify_homelab_token=…`),
- and **zero** parameter prompts are observed (FT-012 I-1: no
  prompt in non-interactive mode),
- and no Coolify resource is created (FT-002 I-2).

### 2. Non-interactive + unset url → E_COOLIFY_UNREACHABLE, no prompt, no hang

- Given the token parameter is set, the url parameter is unset,
- and the deploy is non-interactive,
- when `aspire deploy` runs,
- then the process exits non-zero **within 30s**,
- and stderr matches `E_COOLIFY_UNREACHABLE` (per FT-002
  §Behaviour step 2),
- and zero parameter prompts are observed,
- and no Coolify resource is created.

### 3. Non-interactive + unset token via CI env-var detection → fail-fast preserved

- Given the test driver sets the env-var Aspire 13 uses to signal
  CI (or otherwise asserts non-interactive via the canonical
  Aspire signal — implementation chooses the appropriate sentinel
  at FT-012 implement time),
- and the token parameter is unset,
- when `aspire deploy` runs (no explicit `--non-interactive`
  flag, just CI signal),
- then behaviour matches scenario 1 (`E_AUTH_TOKEN_MISSING`,
  exits within 30s, no prompt, no Coolify side effect). This
  pins that the publisher consults the Aspire interactivity
  signal, not just the explicit CLI flag.

### 4. Non-interactive + unset registry-prefix → E_… symbol, no prompt, no hang

- Given the AppHost calls `WithImageRegistry(registryPrefix)` with
  the prefix parameter unset, and the deploy is non-interactive,
- when `aspire deploy` runs,
- then the process exits non-zero within 30s with the matching
  FT-004 fail-fast symbol on stderr,
- zero prompts are observed,
- no Coolify resource or registry record is created.

### 5. Non-interactive + set → unchanged happy path (regression guard)

- Given both parameters are set in user-secrets and the deploy is
  non-interactive (typical CI deploy),
- when `aspire deploy` runs,
- then configure proceeds without prompting, the combined probe
  succeeds, the deploy exits zero,
- and zero parameter prompts are observed (FT-012 I-4: prompt
  branch fires only when interactive AND unset).

### 6. Precedence preserved on the non-interactive path

- Given the token is unset (scenario 1's trigger for
  `E_AUTH_TOKEN_MISSING`) AND the configured url points at an
  unreachable host (scenario 4 of TC-004's trigger for
  `E_COOLIFY_UNREACHABLE`),
- and the deploy is non-interactive,
- when `aspire deploy` runs,
- then stderr matches `E_AUTH_TOKEN_MISSING` (highest-precedence
  failure per FT-002 §Behaviour "Precedence rule"); the
  publisher MUST NOT classify this as `E_COOLIFY_UNREACHABLE`
  just because the prompt branch was bypassed (FT-012 I-5: no
  symbol churn).

## Acceptance

All six scenarios pass. The load-bearing assertions are:
- the **`< 30s` wall-clock bound** in scenarios 1–4 (no
  prompt-hang on CI);
- the **literal `E_…` symbol on stderr** in scenarios 1, 2, 3, 4, 6
  (FT-012 I-1 + I-5 — non-interactive surface is identical to
  pre-FT-012);
- the **zero-prompt observation** in every scenario (FT-012 I-1 +
  I-4 — no prompt is ever requested in non-interactive mode,
  regardless of whether the value is set or unset).