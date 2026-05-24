---
id: TC-005
title: image_registry_push_and_coolify_pull_credential_upsert
type: scenario
status: unimplemented
validates:
  features:
  - FT-004
  adrs:
  - ADR-005
phase: 1
---

## Intent

Exit-criteria scenario for ADR-005 (image registry strategy). Given
the representative AppHost from TC-001 (two environments —
`Development`, `Production` — and a mix of resource types) and the
auth setup from TC-004, asserts that v1's registry behaviour holds:
the publisher pushes per-resource versioned tags to a developer-
declared registry, optionally upserts a Coolify-side Private Registry
record by `(host, username)`, fails fast in `configure` when registry
config is missing, fails fast in `push` when push errors occur, and
never leaks credentials or emits a `latest` tag.

## Setup

- A Coolify instance meeting ADR-002's version floor, reachable at
  a parameterised URL with a valid bearer token (per ADR-004).
- Two registry harnesses available to the test:
  - **Authenticated registry:** a `registry:2` instance with basic-
    auth (htpasswd), reachable from both the test runner (for push)
    and the Coolify destination (for pull).
  - **Anonymous registry:** a `registry:2` instance configured to
    accept anonymous writes and reads, on a distinct port.
- An AppHost project with two containerisable resources
  (`web` and `worker`), one Azure-native non-containerisable
  resource, an informational assembly version of `1.2.3-test`, and
  user-secrets populated for `coolify-*` parameters.
- A sentinel password value (`SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK`)
  used in the redaction sub-scenario.

## Sub-scenarios

### S1. Push happy path, authenticated

1. Set parameters: `registry-prefix=auth-reg.test:5000/myapp`,
   `registry-username=ci`, `registry-password=<htpasswd-value>`.
2. Run `aspire deploy --environment Production`.
3. Assert push-phase log contains push completions for exactly two
   tags: `auth-reg.test:5000/myapp/web:1.2.3-test` and
   `auth-reg.test:5000/myapp/worker:1.2.3-test`.
4. Query the registry's `_catalog` and per-repo `tags/list`: the
   set equals exactly the two tags above. No `latest` tag exists
   under either repo.

### S2. Push happy path, anonymous

1. Set parameters: `registry-prefix=anon-reg.test:5001/myapp`,
   leave `registry-username` and `registry-password` unset.
2. Run `aspire deploy --environment Production`.
3. Assert push-phase succeeds with the same two-tag shape as S1
   against the anonymous registry.
4. Assert the Coolify Private Registries list contains **no**
   record with host `anon-reg.test:5001` (no record created when
   creds are absent).

### S3. Coolify-side registry upsert — create

1. Pre-condition: the Coolify Private Registries list contains no
   record matching `(auth-reg.test:5000, ci)`.
2. With S1's parameters, run `aspire deploy --environment Production`.
3. Assert `configure` phase issued exactly one `POST` to Coolify's
   private-registries endpoint, with `host=auth-reg.test:5000`,
   `username=ci`, and a name carrying the `managed-by:
   pks-aspire-coolify` marker.
4. Post-condition: the Coolify list contains exactly one matching
   record.

### S4. Coolify-side registry upsert — idempotent

1. Pre-condition: S3 has run and the record exists.
2. Re-run the same deploy with the same parameter values.
3. Assert zero `POST` calls and zero `PATCH` calls against the
   private-registries endpoint (password unchanged → no-op).
4. Update `registry-password` via `dotnet user-secrets set` to a new
   htpasswd-valid value; re-run the deploy.
5. Assert exactly one `PATCH` against the existing record and zero
   `POST`. Post-condition: still exactly one matching record.

### S5. Missing registry config (E_REGISTRY_NOT_CONFIGURED)

1. AppHost calls `WithCoolifyDeploy(...)` but omits
   `WithImageRegistry(...)`.
2. Run `aspire deploy --environment Production`.
3. Assert exit code is non-zero.
4. Assert no `build` work has run, no image has been pushed, and
   no Coolify project / environment / service / private-registry
   record has been created or modified.
5. Assert stderr contains the error code `E_REGISTRY_NOT_CONFIGURED`,
   names the `WithImageRegistry` extension method, and shows both
   canonical recipes (one containing `ghcr.io/` and one containing
   `registry.lan:5000/`).

### S6. Push failure does not advance to deploy

1. Set `registry-prefix` to point at an unreachable host (e.g.
   `127.0.0.1:1` — connection refused) or a registry that returns
   401 on push.
2. Run `aspire deploy --environment Production`.
3. Assert exit code is non-zero, returned during the `push` phase.
4. Assert stderr names the failing resource (`web` or `worker`),
   the image tag, the registry host, and the underlying error
   class (connection refused / 401 / 403).
5. Assert no Coolify application/service for either resource was
   created or updated in this run (the `deploy` phase was never
   entered).

### S7. Redaction

1. Use S1's parameters but with `registry-password` set to
   `SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK`.
2. Run `aspire deploy --environment Production` with full log
   capture (stdout, stderr, Aspire structured logs, OTEL export).
3. Grep all captured output for the sentinel string.
4. Assert zero matches across every captured stream.

### S8. No `latest` tag, ever

1. Run S1, S2, S3, and S4.
2. After each run, query each registry's per-repo `tags/list`.
3. Assert no `:latest` tag exists under any repo at any point.
4. Grep all push-phase log output across all runs for `:latest`.
5. Assert zero matches.

### S9. Reused Aspire build pipeline

1. During S1's deploy, capture the process tree spawned by
   `aspire deploy` and the set of Aspire build-hook callbacks
   invoked.
2. Assert no direct `docker build` or `docker push` invocation is
   spawned by the publisher itself.
3. Assert Aspire's container-image-build hook was invoked for each
   of the two containerisable resources, mirroring the integration
   point used by `aspire-ssh-deploy`.

## Pass criteria

All nine sub-scenarios pass deterministically across at least three
consecutive runs against a freshly reset Coolify instance and freshly
reset registry harnesses. Any redaction failure (S7) is a hard fail
regardless of other results.
