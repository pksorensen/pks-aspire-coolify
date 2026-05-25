# pks-agent-registry — recon for in-project registry wiring

Scope: can `pks-agent-registry` act as the in-project Docker registry that
Coolify pulls workload images from in pks-aspire-coolify? Pure read-only
inspection of `/workspaces/agentic-live-www/projects/pks-agent-registry/`.

## 1. Project shape and language

- **Language**: Go (1.24). Single module at `src/agent-registry/` —
  `go.mod` declares `github.com/pksorensen/pks-agent-registry`.
- **Entry point**: `src/agent-registry/main.go` — one binary, two modes:
  - `agent-registry serve` (default) — runs the OCI registry HTTP server
  - `agent-registry owner|repo|tag|gc ...` — admin CLI against the local
    filesystem, or against a remote `/_mgmt/` API when `REGISTRY_REMOTE` is set
    (`main.go:23-44`).
- **Implements**: Docker Registry V2 / OCI Distribution Spec. Routes registered
  in `internal/server/server.go:44-58` (`/v2/{owner}/{name}/manifests|blobs|...`)
  plus a `/_mgmt/...` admin REST API (`server.go:62-70`) and `/healthz`.
- **Port**: `:5000` by default (`REGISTRY_ADDR`, see `main.go:46` and
  `Dockerfile:14` which `EXPOSE 5000`).
- **Auth**: HTTP Basic per owner — bcrypt-hashed passwords in
  `$USER_DATA_DIR/owners/<owner>.json`. Path's `{owner}` segment must match the
  authenticated user for writes (`server.go:94-102`). Admin API gated by
  `REGISTRY_ADMIN_TOKEN` bearer (`server.go:107-122`); disabled when env unset.
  No TLS — README states "terminate at a reverse proxy".
- **Persistence**: plain filesystem under `USER_DATA_DIR` (default `/data`
  in code, `/app/user-data` in the Dockerfile). Layout in README:
  `blobs/sha256/<aa>/<digest>`, `uploads/<id>`, `repos/<owner>/<name>/...`,
  `owners/<owner>.json`. tar-friendly, no DB.
- **Container-runnable**: yes. `Dockerfile` is a clean two-stage Go build →
  `alpine:3.21`, exposes 5000, declares `VOLUME /app/user-data`,
  `ENTRYPOINT ["./agent-registry"]`, `CMD ["serve"]`.

## 2. GitHub release / publish workflow

- **Workflows present**:
  - `.github/workflows/ci.yml` — `go vet`/`build`/`test` on PR + push to main;
    on push to main also deploys to staging via
    `pksorensen/pks-cli/.github/actions/coolify-deploy@main` (app
    `agent-registry`, env `staging`).
  - `.github/workflows/release.yml` — release-please cuts a release on push to
    main; on release-created, runs `build-push` and `deploy-production`.
- **Does it publish to ghcr.io?** **No.** The `build-push` job in
  `release.yml:28-46` pushes to **`registry.kjeldager.io/agent-registry:${VERSION}`**
  and `:latest` — the user's *other* self-hosted registry, not GHCR. Login via
  `pksorensen/pks-cli/.github/actions/registry-login@main`.
- **Triggers**: release-please tag (workflow_dispatch + push-to-main → if
  `release_created`).
- **Tags pushed**: `:${VERSION}` (semver from release-please) and `:latest`.
- **Visibility**: `registry.kjeldager.io` requires auth — image is not
  anonymously pullable. This is the **bootstrapping blocker** (see §4).

### What's missing for a ghcr.io publish

Add a job (or replace `build-push`) along these lines:

```yaml
  build-push-ghcr:
    needs: release-please
    if: needs.release-please.outputs.release_created == 'true'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          tags: |
            ghcr.io/pksorensen/pks-agent-registry:${{ needs.release-please.outputs.version }}
            ghcr.io/pksorensen/pks-agent-registry:latest
```

Then make the package public in repo Settings → Packages so anonymous
`docker pull` works (needed for the chicken-and-egg first deploy).

## 3. Aspire hosting package

- **None exists.** No `aspire/`, `hosting/`, or `Aspire.Hosting.*.csproj`
  anywhere in the tree. The repo is Go-only — `find . -name '*.csproj'`
  returns nothing; root holds only `src/agent-registry/` (Go), `.github/`,
  `Dockerfile`, README, release-please config.
- **Sketch of what it would look like** — single C# project
  `aspire/Aspire.Hosting.PksAgentRegistry/`:

  ```xml
  <!-- Aspire.Hosting.PksAgentRegistry.csproj -->
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <IsAspireHostingPackage>true</IsAspireHostingPackage>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Aspire.Hosting" Version="9.*" />
    </ItemGroup>
  </Project>
  ```

  ```csharp
  // PksAgentRegistryHostingExtensions.cs
  using Aspire.Hosting.ApplicationModel;

  public static class PksAgentRegistryHostingExtensions
  {
      public static IResourceBuilder<ContainerResource> AddPksAgentRegistry(
          this IDistributedApplicationBuilder builder,
          string name = "registry",
          string adminToken = "dev-admin",
          string imageTag = "latest")
      {
          return builder.AddContainer(name, "ghcr.io/pksorensen/pks-agent-registry", imageTag)
              .WithHttpEndpoint(targetPort: 5000, name: "oci")
              .WithEnvironment("REGISTRY_ADDR", ":5000")
              .WithEnvironment("REGISTRY_ADMIN_TOKEN", adminToken)
              .WithVolume("pks-registry-data", "/app/user-data");
      }
  }
  ```

  Owner credentials would have to be seeded post-start — either by a
  `WithLifecycleHook` that POSTs to `/_mgmt/owners` once the endpoint is
  healthy, or by `docker exec`ing `agent-registry owner add <name>` with
  `REGISTRY_PASSWORD` env.

## 4. Viability for in-project registry in pks-aspire-coolify

- **First-resource declaration**: yes, this is the natural shape. In the
  example AppHost, `AddPksAgentRegistry()` first; downstream resources read
  its endpoint via `GetEndpoint("oci")` and use it as their push target.
- **Push-target discovery**: Aspire-side, a workload resource that publishes
  an image would need to know `registry.GetEndpoint("oci").Url` (intra-network
  hostname like `registry:5000`). Today the registry itself only exposes the
  OCI API; there is no Aspire convention plumbing a "push here" URL into
  arbitrary `ProjectResource`s. The pks-aspire-coolify push-phase code in
  FT-004 would need to be told the registry endpoint explicitly (env var or
  builder method on its own resource type).
- **Coolify-side**: yes — Coolify needs a Private Registry record pointing at
  the in-project registry's *external* hostname (whatever Coolify resolves it
  as — typically the container/service name on Coolify's Docker network, not
  Aspire's). FT-004 already implements the Private Registry upsert pattern
  (`.product/checklist.md:### FT-004`), so this is "feed it different
  credentials/URL", not new code.
- **Bootstrapping chicken-and-egg**: the registry's own image cannot live in
  itself on first deploy. With the current release.yml it lives in
  `registry.kjeldager.io` (auth-gated, fine for the user's own
  infrastructure but not for an open example). For pks-aspire-coolify's
  example AppHost to work for anyone, the registry image must be available
  from a public location → **GHCR publish is the prerequisite** (§2).
- **No other blocker**: filesystem volume is straightforward, no TLS needed
  inside the Aspire network (Coolify on the same Docker network can pull
  `registry:5000` plain HTTP — registry V2 supports this when configured as
  an "insecure registry" on the daemon, which Coolify's deploy host must
  allow).

## 5. Recommendation

**Needs work — small, well-scoped.** Two concrete follow-ups before
pks-aspire-coolify can wire it in:

1. **Add a GHCR publish job** to `pks-agent-registry/.github/workflows/release.yml`
   (~25 lines as sketched in §2) and make the package public. Without this,
   first-deploy bootstrap requires `registry.kjeldager.io` creds which are
   not portable.
2. **Add an `Aspire.Hosting.PksAgentRegistry` package** (csproj + one
   `*HostingExtensions.cs`, ~40 lines as sketched in §3). Could live in this
   repo under `aspire/`, or be vendored into pks-aspire-coolify's
   `examples/` if we don't want a NuGet round-trip yet.

Coolify-side: no new code — FT-004's Private Registry upsert already handles
the credentials registration; it just needs to be parameterised by registry
host/port/admin-token coming from the Aspire resource.

Once those two land, the example AppHost wiring is:

```csharp
var registry = builder.AddPksAgentRegistry("registry", adminToken: "dev-admin");

var app = builder.AddProject<Projects.MyApp>("app")
    .WithReference(registry);     // app picks up registry endpoint to push to

builder.AddCoolify("coolify")
    .WithPrivateRegistry(registry); // FT-004-style upsert
```

**Verdict: Needs work (~1 short PR per repo) — not ready today, no
significant blockers.**
