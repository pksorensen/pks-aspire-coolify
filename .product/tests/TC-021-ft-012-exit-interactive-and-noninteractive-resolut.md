---
id: TC-021
title: ft_012_exit_interactive_and_noninteractive_resolution
type: exit-criteria
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
last-run-duration: 2.5s
---

# TC-021 — FT-012 exit-criteria: interactive prompt + non-interactive fail-fast

Exit-criteria roll-up for **FT-012** (interactive parameter prompting
in the configure phase). Compresses the two property pairs that, taken
together, certify the feature is shippable:

1. **Interactive-and-unset → prompt-then-proceed.** TC-019 §1, §2, §3
   each demonstrate the canonical Aspire 13 parameter prompt firing for
   an unset parameter in interactive mode and configure proceeding to
   the combined version + auth probe with the prompted value. TC-019
   §4 demonstrates redaction (FT-012 I-2); TC-019 §5 demonstrates the
   at-most-one-prompt-per-parameter invariant (FT-012 I-3); TC-019 §6
   demonstrates the interactive-but-set regression guard (FT-012 I-4);
   TC-019 §7 demonstrates the cancellation diagnostic (FT-012 §Behaviour).
2. **Non-interactive-and-unset → existing fail-fast preserved.** TC-020
   §1, §2, §4 each demonstrate the matching `E_…` symbol firing in
   non-interactive mode within a 30-second wall-clock bound, with zero
   prompts observed (FT-012 I-1). TC-020 §3 demonstrates that the
   non-interactive signal is consulted (not just the explicit CLI
   flag). TC-020 §5 demonstrates the non-interactive-but-set regression
   guard. TC-020 §6 demonstrates that precedence among the four `E_…`
   symbols is unchanged (FT-012 I-5).

## Acceptance

FT-012 is **complete** when:

- **TC-019** passes end-to-end (all seven scenarios) against an Aspire
  13 deploy host configured as interactive.
- **TC-020** passes end-to-end (all six scenarios) against an Aspire
  13 deploy host configured as non-interactive, with every scenario
  exiting **within 30 seconds wall-clock** (no prompt-hang).
- **ADR-004 has been amended** to record the chosen Aspire 13 prompt
  API and the chosen Aspire 13 interactivity-signal surface (FT-012
  I-6: property-only spec, API recorded in the amendment at implement
  time).
- **No regression on TC-004**: the seven ADR-004 scenarios still pass
  unchanged. Specifically TC-004 §2 (`E_AUTH_TOKEN_MISSING`) and §3
  (`E_AUTH_TOKEN_INVALID`) — historically the canonical missing/invalid
  fail-fast checks — keep their behaviour when the deploy is
  non-interactive.
- **No regression on TC-002 / TC-006**: the combined version + auth
  probe still runs as one round-trip in both branches; the prompted
  token (when one is prompted) is the one carried in the
  `Authorization` header on that single round-trip.

This TC has no separate executable body — it is the conjunction of
TC-019 + TC-020 passing, plus the ADR-004 amendment landing.