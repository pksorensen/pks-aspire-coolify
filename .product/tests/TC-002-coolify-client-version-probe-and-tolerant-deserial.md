---
id: TC-002
title: coolify_client_version_probe_and_tolerant_deserialization
type: scenario
status: passing
validates:
  features:
  - FT-002
  adrs:
  - ADR-002
phase: 1
runner: bash
runner-args: dotnet test tests/Aspire.Hosting.Coolify.Tests/Aspire.Hosting.Coolify.Tests.csproj --filter FullyQualifiedName~ConfigurePhaseExitCriteria
last-run: 2026-05-24T16:40:27.564681418+00:00
last-run-duration: 2.5s
---

## Scenario

Given a configured `WithCoolifyDeploy(destination: "homelab-prod")` on
an Aspire AppHost named `SampleApp` (the same shape used by TC-001),
and a `CoolifyClient` whose `SupportedCoolifyVersions` floor constant
is fixed at the value compiled into the publisher release under test.

The Coolify endpoint that reports the running Coolify version is
treated as the **probe endpoint**. The probe runs in the publisher's
`configure` phase, before any project / environment / application
upsert.

## When / then — version-probe happy path

**When** the configured Coolify instance reports a version equal to or
above `SupportedCoolifyVersions`,
**Then** `aspire deploy --environment Production` proceeds past
`configure` without surfacing the version probe to the user, and no
version-related warning appears in the deploy log.

## When / then — version-probe below floor

**When** the configured Coolify instance reports a version *below*
`SupportedCoolifyVersions`,
**Then**:

1. `aspire deploy --environment Production` exits with a **non-zero**
   exit code.
2. **Zero** Coolify projects, environments, applications, services,
   databases, or environment variables are created or modified as a
   result of the invocation (verified by snapshotting Coolify state
   before and after).
3. The error message printed to the deploy log:
   - names the **observed** Coolify version,
   - names the **required floor** version,
   - references the `SUPPORTED_COOLIFY_VERSIONS.md` table by path,
   - does **not** print a misleading HTTP status code from a
     downstream endpoint (the failure is attributed to the probe, not
     to a 4xx mid-deploy).

## When / then — version-probe unreachable

**When** the probe endpoint returns 404, returns 5xx, times out, or
the connection is refused,
**Then**:

1. `aspire deploy --environment Production` exits with a **non-zero**
   exit code.
2. **Zero** Coolify resources are created or modified.
3. The error message distinguishes "could not determine Coolify
   version" from "version too old" — i.e. it does not falsely claim
   the version is below floor when the probe simply failed.

## When / then — tolerant deserialization (additive upstream change)

**When** the Coolify instance returns a response that contains
additional JSON fields not represented on the corresponding publisher
DTO (simulating an additive, non-breaking upstream change),
**Then**:

1. Deserialization **succeeds** with no `JsonException`.
2. The unknown fields are silently ignored.
3. The publisher proceeds with the deploy and produces the same
   Coolify-side state it would have produced without the extra fields.

## When / then — conservative serialization

**When** the publisher reads an existing Coolify resource via GET
(picking up whatever fields Coolify chose to return, including any
fields the publisher does not model) and then issues a subsequent
PATCH or POST for that resource,
**Then** the outgoing request body contains **only** the fields the
publisher explicitly set. Fields read from the GET response that the
publisher did not write are **not** echoed back. This is asserted by
intercepting the outbound HTTP request.

## When / then — no build-time codegen

**When** the repository is checked out fresh into a network-isolated
build environment and `dotnet build` is invoked,
**Then**:

1. The build succeeds with no outbound network calls (asserted by the
   CI sandbox).
2. The repository tree contains no `*.g.cs` file and no `Generated/`
   directory whose contents are derived from a Coolify OpenAPI
   document (asserted by a `find` / repo-grep check at CI time).

## Validates

- ADR-002 — Coolify API version and client strategy (v1)