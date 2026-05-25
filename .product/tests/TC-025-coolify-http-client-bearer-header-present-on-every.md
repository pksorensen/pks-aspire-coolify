---
id: TC-025
title: coolify_http_client_bearer_header_present_on_every_request
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
`(url, token)` pair where the token has a sentinel value
(e.g. `SENTINEL_TOKEN_DO_NOT_LEAK_xxxx`), and an interceptor that
captures every outbound HTTP request made by the client during a
synthetic deploy that exercises **every endpoint group** declared by
the client interface (version, destinations, projects, environments,
services, private-registries, deploy-jobs, env-vars, applications).

## When / then — bearer header on every request

**When** the synthetic deploy runs to completion (or to the first
mocked failure),
**Then**:

1. The interceptor captures one or more outbound HTTP requests per
   endpoint group.
2. **Every** captured request carries an `Authorization` header.
3. **Every** `Authorization` header has the form
   `Bearer <sentinel-token>`.
4. No captured request omits the header, and no captured request
   carries a malformed header (e.g. `Bearer` with no value, or a
   different scheme such as `Basic`).
5. The bearer value matches the token supplied to the factory
   exactly — the client does not transform, truncate, or re-encode
   it.

## Validates

- FT-013 — Concrete HTTP implementation of ICoolifyClient
- ADR-004 — Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)