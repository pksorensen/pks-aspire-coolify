---
id: TC-026
title: coolify_http_client_bearer_token_never_appears_in_body_logs_or_exceptions
type: scenario
status: passing
validates:
  features:
  - FT-013
  adrs: []
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~HttpCoolifyClientExitCriteria
last-run: 2026-05-25T08:53:21.713172861+00:00
last-run-duration: 2.3s
---

## Scenario

Given the FT-013 HTTP-backed `ICoolifyClient` constructed from a
`(url, token)` pair whose token is a unique sentinel string
(e.g. `SENTINEL_TOKEN_DO_NOT_LEAK_8a9c2e`), and a synthetic deploy
that exercises:

- a happy-path version probe (TC-022 shape),
- a transport failure (TC-023 shape),
- an auth failure (`401`),
- an upsert call against each endpoint group with a body that the
  publisher serializes,
- a deserialization failure (malformed response body).

Captured artefacts for the assertion:

- every outbound HTTP request body, as bytes,
- every log line emitted through the client's logger,
- every exception thrown by the client, including `Message`,
  `ToString()`, and the full inner-exception chain.

## When / then — sentinel token never leaks

**When** the synthetic deploy completes,
**Then** a literal substring search for the sentinel token value
across **all** captured artefacts returns **zero** matches in:

1. Any outbound HTTP **request body** (the bearer header is the only
   permitted location for the token — request bodies must never
   contain it). Asserted against every serialized request issued
   during the synthetic deploy.
2. Any **log line** emitted by the client or by code paths the
   client invokes (FT-013 §I-3). This includes structured log
   property values, not just message templates.
3. Any **exception text** produced by the client — `Message`,
   `ToString()`, and every `InnerException` in the chain (FT-013
   §I-3 and §Error handling).
4. Any **return value** returned from any client method (no DTO
   field carries the token back to the caller).

The bearer header itself **is** permitted to carry the token (that
is the entire point of bearer auth); the assertion explicitly
excludes the `Authorization` header from the request-body grep
target.

## Validates

- FT-013 — Concrete HTTP implementation of ICoolifyClient
- ADR-004 — Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)