// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
// Implements ADR-007: Native Aspire AddContainerRegistry adoption (v1)
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// FT-015 — thin helper that wraps Aspire's native <c>AddContainer</c> +
/// <c>AddContainerRegistry</c> primitives to register a pks-agent-registry
/// (https://github.com/pksorensen/pks-agent-registry) instance alongside the
/// workload. Mirrors Azure's <c>AddAzureContainerRegistry</c> for the homelab
/// / Coolify use case where the registry lives in the same project as the
/// workloads that push to it.
///
/// v1 limitation: the workload-push-to-in-project-registry orchestration
/// (deploy registry first, discover its FQDN, then build+push workloads to
/// that FQDN) is FT-016 work. v1 just gives you the registry as a Coolify
/// service; workloads still push wherever <c>registry-prefix</c> says.
/// </summary>
public static class PksAgentRegistryExtensions
{
    /// <summary>
    /// Image published from pksorensen/pks-agent-registry to GHCR. Public.
    /// </summary>
    private const string DefaultImage = "ghcr.io/pksorensen/pks-agent-registry";

    /// <summary>
    /// Tag pinned for reproducibility. Bump explicitly via the optional
    /// <c>tag</c> parameter when a new registry release ships.
    /// </summary>
    private const string DefaultTag = "latest";

    /// <summary>
    /// Container port pks-agent-registry listens on by default.
    /// </summary>
    private const int DefaultPort = 5000;

    /// <summary>
    /// Adds a pks-agent-registry container resource plus a
    /// <see cref="ContainerRegistryResource"/> targeting it.
    /// </summary>
    /// <param name="builder">The Aspire distributed-application builder.</param>
    /// <param name="name">Resource name (defaults to <c>pks-registry</c>). Used as
    /// the Coolify service name and the Docker bind-mount folder name.</param>
    /// <param name="tag">Image tag (defaults to <c>latest</c>). Bump
    /// explicitly to pin a published release.</param>
    /// <param name="port">Host-side HTTP port. Defaults to 5000. Pick a free
    /// port if you have collisions on the Coolify host.</param>
    /// <returns>A tuple of (registry container resource builder, registry
    /// target for use with <c>WithContainerRegistry</c>) so the caller can
    /// chain further annotations (env vars, mounts) on the container and
    /// point workloads at the target.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null, empty, or whitespace.</exception>
    public static (IResourceBuilder<ContainerResource> Container,
                   IResourceBuilder<ContainerRegistryResource> Target)
        AddPksAgentRegistry(
            this IDistributedApplicationBuilder builder,
            string name = "pks-registry",
            string tag = DefaultTag,
            int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var container = builder.AddContainer(name, DefaultImage, tag)
            .WithHttpEndpoint(port: port, targetPort: DefaultPort, name: "http")
            .WithEnvironment("REGISTRY_STORAGE_FILESYSTEM_ROOTDIRECTORY", "/var/lib/registry")
            .WithEnvironment("REGISTRY_HTTP_ADDR", ":" + DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            // Persist images across container restarts. Bind-mount because Coolify
            // (v1 of this helper) doesn't manage named docker volumes for us.
            .WithBindMount($"./.data/{name}", "/var/lib/registry");

        // The ContainerRegistryResource the caller passes into
        // workload.WithContainerRegistry(target). FT-014 made the Coolify publisher
        // read this annotation; FT-016 will make it deploy this resource first
        // and rewrite the address to the Coolify-assigned FQDN.
        //
        // v1 default address: localhost:<port> — valid for local-dev testing where
        // the AppHost and the registry share a host. In Coolify deploys today this
        // address isn't used (workload push still targets the registry-prefix
        // parameter); FT-016 fixes that.
        var target = builder.AddContainerRegistry(
            $"{name}-target",
            $"localhost:{port}");

        return (container, target);
    }
}
