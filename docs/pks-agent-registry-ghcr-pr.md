# pks-agent-registry — GHCR publish PR

PR: https://github.com/pksorensen/pks-agent-registry/pull/2

Adds `.github/workflows/publish-ghcr.yml` which publishes multi-arch images
to `ghcr.io/pksorensen/pks-agent-registry` on every push to `main`, on
semver tags, and via `workflow_dispatch`, authenticating with the built-in
`GITHUB_TOKEN`.

## Manual step (likely required once)

The workflow attempts to flip the package to public via the GitHub API,
but the call is wrapped in `continue-on-error` because it commonly fails
the first time (the package doesn't exist yet until the first push, and
the API path differs for org-owned vs user-owned packages). Expect to
flip it manually once:

1. Wait for the first successful `Publish to GHCR` run after merge.
2. Open https://github.com/users/pksorensen/packages/container/package/pks-agent-registry
3. Package settings → "Change visibility" → **Public**.

After that the auto-public step will be a no-op on subsequent runs.
