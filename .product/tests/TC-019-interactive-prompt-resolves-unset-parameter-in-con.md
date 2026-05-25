---
id: TC-019
title: interactive_prompt_resolves_unset_parameter_in_configure
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
last-run-duration: 2.7s
---

# TC-019 — Interactive prompt resolves unset parameter in configure

Validates **FT-012** (interactive parameter prompting in the configure
phase) on the **interactive-and-unset** path. Composes with TC-004
(ADR-004 redaction and fail-fast scenarios) and TC-002 (combined
version + auth probe). Asserts that when the deploy is interactive AND
a parameter handle resolves to an empty value, the canonical Aspire 13
parameter prompt fires, the user's reply is consumed, and configure
proceeds to the combined probe exactly as if the value had been set in
`dotnet user-secrets` from the start — with full redaction preserved.

## Setup

Representative AppHost (re-used from TC-001/TC-004), declaring the
canonical pair:

```csharp
var url   = builder.AddParameter("coolify-homelab-url");
var token = builder.AddParameter("coolify-homelab-token", secret: true);

builder.AddDockerComposeEnvironment("homelab")
       .WithCoolifyDeploy(url, token);
```

Coolify test instance `coolify-homelab` meeting ADR-002's
`SupportedCoolifyVersions` floor.

Test driver runs `aspire deploy --environment homelab` in **interactive
mode** (whatever signal the Aspire 13 SDK exposes for "this is an
interactive deploy" — the test consults the same signal the publisher
does so the scenarios stay API-agnostic).

Sentinel token literal `SENTINEL_TOKEN_INTERACTIVE_a91f3` is fed as
the prompt reply in scenarios 1 and 4 so any redaction leak is
greppable.

## Scenarios

### 1. Interactive + unset token → prompt fires, configure proceeds

- Given `Parameters:coolify-homelab-url` is set in user-secrets
  and `Parameters:coolify-homelab-token` is **unset** (no
  user-secret, no `Parameters__coolify_homelab_token` env-var),
- and the deploy is interactive (TTY available, no
  `--non-interactive`),
- when `aspire deploy --environment homelab` runs,
- then the canonical Aspire 13 parameter prompt is observed for
  parameter `coolify-homelab-token` (exactly one prompt; test
  intercepts via the same Aspire surface the publisher uses),
- and the test driver replies with
  `SENTINEL_TOKEN_INTERACTIVE_a91f3`,
- and the configure phase advances to the combined version + auth
  probe carrying the prompted token in the
  `Authorization: Bearer …` header (asserted on the captured
  request),
- and the deploy proceeds through configure → build → push →
  deploy → verify and exits zero,
- and **no `E_AUTH_TOKEN_MISSING` symbol** appears anywhere in
  stdout / stderr / Aspire logs (FT-012 I-5: no diagnostic on the
  interactive happy path).

### 2. Interactive + unset url → prompt fires, configure proceeds

- Given the token parameter is set but `coolify-homelab-url` is
  unset,
- and the deploy is interactive,
- when `aspire deploy` runs,
- then exactly one prompt fires for parameter `coolify-homelab-url`,
- the test replies with the homelab base URL,
- configure constructs the client against that URL and the
  combined probe succeeds,
- the deploy exits zero with no `E_COOLIFY_UNREACHABLE` symbol
  emitted.

### 3. Interactive + unset registry-prefix → prompt fires, configure proceeds

- Given the AppHost additionally calls
  `WithImageRegistry(registryPrefix)` with the prefix parameter
  unset,
- and the deploy is interactive,
- when `aspire deploy` runs,
- then exactly one prompt fires for the registry-prefix
  parameter (FT-004's resolution step),
- the test replies with `ghcr.io/test/app`,
- the registry-upsert and subsequent build/push phases consume
  the prompted value as their image-prefix.

### 4. Redaction — prompted secret value never appears in any sink

- Given scenario 1 has just completed with token value
  `SENTINEL_TOKEN_INTERACTIVE_a91f3`,
- grepping captured stdout, captured stderr, captured Aspire
  structured logs, the Aspire dashboard parameter-display
  snapshot, and any thrown-and-handled exception messages for
  the literal `SENTINEL_TOKEN_INTERACTIVE_a91f3` returns **zero
  matches** (FT-012 I-2; inherits ADR-004 redaction).

### 5. At most one prompt per parameter per deploy

- Given scenario 1 (token initially unset, prompt fires once and
  succeeds),
- during the rest of the deploy (build / push / deploy / verify
  phases that all need the token via the `ICoolifyClient`),
- exactly **one** prompt is observed for `coolify-homelab-token`
  across the entire `aspire deploy` invocation (FT-012 I-3).

### 6. Interactive + set → no prompt fires (regression guard)

- Given both `coolify-homelab-url` and `coolify-homelab-token`
  are set in user-secrets,
- and the deploy is interactive,
- when `aspire deploy` runs,
- then **zero** prompts fire (FT-012 I-4: the interactive branch
  must fire only when interactive AND unset),
- and behaviour is identical to today's FT-002 happy path
  (TC-004 §1).

### 7. Prompt cancellation → FT-001 cancellation diagnostic, not E_…

- Given the token parameter is unset and the deploy is interactive,
- when the prompt fires and the test driver cancels it (closes
  stdin / triggers the deploy `CancellationToken` while the
  prompt is awaiting),
- then configure exits with FT-001's cancellation diagnostic,
- and **no `E_AUTH_TOKEN_MISSING`** symbol is emitted (FT-012
  §Behaviour: cancellation is not "value absent"),
- and no Coolify resource has been created (FT-012 I-8).

## Acceptance

All seven scenarios pass. Specifically:
- scenario 1 is the load-bearing "prompt-then-proceed" assertion;
- scenario 4 is the load-bearing redaction assertion (FT-012 I-2);
- scenario 5 is the load-bearing single-prompt assertion (FT-012 I-3);
- scenario 6 is the no-regression guard against accidental prompting
  on the existing happy path (FT-012 I-4).