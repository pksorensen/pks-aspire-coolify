---
id: TC-024
title: coolify_http_client_tolerant_deserialization_ignores_unknown_fields
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
last-run-duration: 2.2s
---

## Scenario

Given the FT-013 HTTP-backed `ICoolifyClient` and a Coolify endpoint
(test double) configured to return JSON response bodies containing
**additional fields** not modelled on the publisher's response DTO —
simulating an additive, non-breaking upstream change as ADR-002 §5
anticipates.

The extra fields exercised cover:

- a new top-level scalar field,
- a new top-level object field with nested members,
- a new array of objects,
- a known field whose value is a newly-introduced enum variant the
  publisher does not yet model.

## When / then — tolerant deserialization

**When** the client deserializes such a response,
**Then**:

1. Deserialization **succeeds**: no `JsonException` is thrown.
2. The unknown fields are silently ignored — they do not appear on
   the returned DTO and they are not echoed back into any subsequent
   request (preserves FT-002's conservative-serialization assertion
   already covered by TC-002).
3. The caller proceeds with the deploy and produces the same
   Coolify-side state it would have produced against a response
   without the extra fields.

## Validates

- FT-013 — Concrete HTTP implementation of ICoolifyClient
- ADR-002 — Coolify API version and client strategy (v1)