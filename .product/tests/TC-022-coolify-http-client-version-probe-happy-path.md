---
id: TC-022
title: coolify_http_client_version_probe_happy_path
type: exit-criteria
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

Given a real (or test-double) Coolify v4 instance reachable at a known
url, a valid bearer token, and the FT-013 HTTP-backed `ICoolifyClient`
constructed from that `(url, token)` pair.

## When / then — version-probe happy path

**When** the publisher's configure phase invokes the client's version
probe method,
**Then**:

1. Exactly **one** outbound HTTP request is issued (asserted by
   intercepting outbound HTTP and counting requests).
2. The request is a GET (no body), carries an
   `Authorization: Bearer <token>` header, and targets the
   configured base url.
3. The response (200 OK with a Coolify version-shaped JSON payload)
   deserializes into a structured value carrying at least a
   non-empty `version` string field.
4. The method returns to the caller without throwing, and the
   caller (FT-002) observes a version string it can compare
   against `SupportedCoolifyVersions`.
5. No log line, no exception, and no return value contains the
   literal token string.

## Validates

- FT-013 — Concrete HTTP implementation of ICoolifyClient
- ADR-002 — Coolify API version and client strategy (v1)