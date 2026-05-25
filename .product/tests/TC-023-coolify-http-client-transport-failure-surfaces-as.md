---
id: TC-023
title: coolify_http_client_transport_failure_surfaces_as_typed_exception
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
`(url, token)` pair, and an environment in which the Coolify endpoint
exhibits one of the following transport-level fault modes:

- TCP connection refused,
- DNS resolution failure for the configured host,
- TLS handshake failure,
- request timeout exceeding `HttpClient.Timeout`,
- HTTP `5xx` response,
- HTTP `404` on the version-probe path.

## When / then — transport failure surfaces uniformly

**When** the configure phase invokes the version probe (or any other
client method) against such an instance,
**Then**:

1. The client method throws an exception of the **same well-known
   shape** (transport-failure shape) for every fault mode listed
   above — i.e. FT-002's classifier can map all of them to
   `E_COOLIFY_UNREACHABLE` with a single catch (FT-013 §I-6).
2. The exception shape is **distinct** from the auth-failure shape
   produced by `401` / `403` (FT-013 §I-7). A `401` does not present
   as a transport failure, and a connection refused does not present
   as an auth failure.
3. The exception's `Message` and inner-exception chain do **not**
   contain the literal token string (FT-013 §I-3, asserted again in
   TC-026 with sentinel-grep).
4. No partial Coolify state has been created (the client made the
   request, but no upsert / trigger method has run side-effecting
   work successfully).

## When / then — cancellation is not a transport failure

**When** the caller cancels the `CancellationToken` mid-request,
**Then** the client surfaces `OperationCanceledException` /
`TaskCanceledException`, **not** the transport-failure shape. This
preserves FT-002's distinction between the cancellation diagnostic
and `E_COOLIFY_UNREACHABLE`.

## Validates

- FT-013 — Concrete HTTP implementation of ICoolifyClient
- ADR-002 — Coolify API version and client strategy (v1)