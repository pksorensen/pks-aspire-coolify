---
id: TC-004
title: coolify_auth_token_per_instance_via_aspire_parameter
type: scenario
status: passing
validates:
  features:
  - FT-002
  adrs:
  - ADR-004
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ConfigurePhaseExitCriteria
last-run: 2026-05-24T16:40:27.564681418+00:00
last-run-duration: 2.6s
---

# TC-004 — Coolify auth token resolved per-instance via Aspire secret parameter

Validates **ADR-004** (Coolify auth model). Composes with TC-001 (mapping),
TC-002 (version probe), TC-003 (imperative deploy). Asserts every property
ADR-004 commits to: per-instance scoping via Aspire parameters,
`configure`-phase fail-fast on missing/invalid token, redaction, no
persistent on-disk state, and rotation by parameter edit.

## Setup

Two Coolify test instances (or one instance addressable under two URLs
with two distinct tokens — sufficient to prove isolation):

- `coolify-homelab` — meets ADR-002's `SupportedCoolifyVersions` floor.
- `coolify-vps`     — meets ADR-002's `SupportedCoolifyVersions` floor.

Representative AppHost (re-used from TC-001/TC-003) extended with:

```csharp
var homelabUrl   = builder.AddParameter("coolify-homelab-url");
var homelabToken = builder.AddParameter("coolify-homelab-token", secret: true);
var vpsUrl       = builder.AddParameter("coolify-vps-url");
var vpsToken     = builder.AddParameter("coolify-vps-token",     secret: true);

builder.AddDockerComposeEnvironment("homelab")
       .WithCoolifyDeploy(homelabUrl, homelabToken);

builder.AddDockerComposeEnvironment("vps")
       .WithCoolifyDeploy(vpsUrl, vpsToken);
```

Sentinel token literal `SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9` is used in
redaction assertions so any leak is greppable.

## Scenarios

### 1. Happy path — user-secrets resolved, configure proceeds

- Given `Parameters:coolify-homelab-url` and `Parameters:coolify-homelab-token`
  are both set via `dotnet user-secrets`,
- when `aspire deploy --environment Production` runs targeting the
  homelab environment,
- then `configure` completes without interactive prompt,
- and **exactly one** authenticated `GET /api/v1/version` request is
  observed against the homelab url with the resolved token in the
  `Authorization: Bearer …` header (this is the same round-trip that
  satisfies ADR-002's version probe),
- and the AppHost directory has no new file created by the publisher.

### 2. Missing token — fail-fast in configure with E_AUTH_TOKEN_MISSING

- Given the url parameter is set but the token parameter is unset
  (no user-secret, no `Parameters__coolify_homelab_token` env-var),
- when `aspire deploy` runs,
- then the process exits non-zero **before** any Coolify resource is
  created (no project, no environment, no service exists in Coolify),
- and stderr matches `E_AUTH_TOKEN_MISSING`,
- and the error message names the parameter `coolify-homelab-token`,
- and the error message shows both remediation forms:
  - `dotnet user-secrets set Parameters:coolify-homelab-token <value>`
  - `Parameters__coolify_homelab_token=<value>` env-var.

### 3. Invalid token — fail-fast in configure with E_AUTH_TOKEN_INVALID

- Given both parameters are set and Coolify returns `401` (or `403`)
  to `GET /api/v1/version`,
- when `aspire deploy` runs,
- then the process exits non-zero **before** any Coolify resource is
  created,
- and stderr matches `E_AUTH_TOKEN_INVALID`,
- and the error message names the configured url and the parameter
  name (`coolify-homelab-token`),
- and **no portion** of the token value (including the sentinel) appears
  anywhere in stdout, stderr, Aspire structured logs, or the captured
  exception trace.

### 4. Redaction — no log/dashboard/exception path leaks the token

- Given a happy-path deploy with token value
  `SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9`,
- when the deploy completes,
- then grepping captured Aspire logs, the Aspire-dashboard parameter
  display snapshot, and any thrown-and-handled exception messages for
  the literal `SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9` returns zero matches.

### 5. No persistent publisher state for auth

- After a successful deploy,
- the AppHost directory contains no new file matching any of:
  `coolify-token.cache`, `.coolify/credentials`, `.coolify/token`,
  `coolify.lock`,
- and the user-secrets store contains only the values the developer
  set (the publisher did not write back).

### 6. Multi-instance isolation

- Given both `(homelab-url, homelab-token)` and `(vps-url, vps-token)`
  pairs are set,
- when `aspire deploy` targets the homelab environment first, then the
  vps environment,
- each deploy's authenticated `GET /api/v1/version` carries the token
  from its own pair (asserted on the captured request headers),
- and rotating `coolify-homelab-token` to a value Coolify rejects does
  **not** cause a subsequent vps-targeted deploy to fail (vps remains
  authenticated against its own token).

### 7. Rotation — edit parameter, next deploy picks up the new value

- After scenario 1,
- update `Parameters:coolify-homelab-token` via
  `dotnet user-secrets set` to a freshly-issued valid token,
- re-run the same `aspire deploy --environment Production`,
- it succeeds with the new token (asserted on the captured
  `Authorization` header),
- and no publisher-side cache or file had to be invalidated by hand.

## Acceptance

All seven scenarios pass. Specifically:
- scenarios 2 and 3 must fail **in `configure`** (assert phase boundary
  in the deploy log) and leave Coolify side-effect-free;
- scenario 4's grep is the load-bearing redaction assertion;
- scenario 5's filesystem snapshot is the load-bearing
  no-persistent-state assertion that ties this TC to ADR-003.