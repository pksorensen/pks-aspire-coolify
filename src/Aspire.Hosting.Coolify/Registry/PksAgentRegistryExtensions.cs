// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
// Implements ADR-007: Native Aspire AddContainerRegistry adoption (v1)
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;

namespace Aspire.Hosting;

/// <summary>
/// FT-015 / FT-016 — thin helper that wraps Aspire's native <c>AddContainer</c> +
/// <c>AddContainerRegistry</c> primitives to register a pks-agent-registry
/// (https://github.com/pksorensen/pks-agent-registry) instance alongside the
/// workload. Mirrors Azure's <c>AddAzureContainerRegistry</c> for the homelab
/// / Coolify use case where the registry lives in the same project as the
/// workloads that push to it.
///
/// FT-016: the registry target carries a <see cref="PksAgentRegistryAnnotation"/>
/// so the new <c>coolify-prereq</c> phase can discover it, deploy the registry
/// Application to Coolify first, resolve its FQDN, and rewrite the address the
/// FT-014 read path returns when sibling workloads ask "where does my image
/// push go."
/// </summary>
public static class PksAgentRegistryExtensions
{
    /// <summary>Image published from pksorensen/pks-agent-registry to GHCR. Public.</summary>
    public const string DefaultImage = "ghcr.io/pksorensen/pks-agent-registry";

    /// <summary>Tag pinned for reproducibility. Bump explicitly via the optional <c>tag</c> parameter.</summary>
    public const string DefaultTag = "latest";

    /// <summary>Container port pks-agent-registry listens on by default.</summary>
    public const int DefaultPort = 5000;

    /// <summary>
    /// Per-builder registry table: the set of in-project pks-agent-registry resources the
    /// FT-016 prereq phase enumerates. <c>ContainerRegistryResource</c>s are not necessarily
    /// added to <c>IDistributedApplicationBuilder.Resources</c> by Aspire's
    /// <see cref="ContainerRegistryResourceBuilderExtensions.AddContainerRegistry"/>, so we
    /// keep a sidecar lookup the publisher reads at prereq time.
    /// </summary>
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, List<ContainerRegistryResource>>
        s_registriesByBuilder = new();

    internal static IReadOnlyList<ContainerRegistryResource> GetPksAgentRegistries(
        IDistributedApplicationBuilder builder)
    {
        return s_registriesByBuilder.TryGetValue(builder, out var list)
            ? list
            : (IReadOnlyList<ContainerRegistryResource>)Array.Empty<ContainerRegistryResource>();
    }

    /// <summary>
    /// Adds a pks-agent-registry container resource plus a
    /// <see cref="ContainerRegistryResource"/> targeting it. The returned target carries
    /// a <see cref="PksAgentRegistryAnnotation"/> so the FT-016 prereq phase will deploy
    /// the registry to Coolify before any sibling workload pushes to it.
    /// </summary>
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

        var targetName = $"{name}-target";
        // FT-015 I-4: idempotent registration. If a (container, target) pair with the same
        // logical name has already been added, return the existing handles rather than
        // creating duplicates.
        var existingContainer = builder.Resources.OfType<ContainerResource>()
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        var existingTarget = GetPksAgentRegistries(builder)
            .FirstOrDefault(r => string.Equals(r.Name, targetName, StringComparison.Ordinal));
        if (existingContainer is not null && existingTarget is not null)
        {
            return (builder.CreateResourceBuilder(existingContainer),
                    builder.CreateResourceBuilder(existingTarget));
        }

        var container = builder.AddContainer(name, DefaultImage, tag)
            .WithHttpEndpoint(port: port, targetPort: DefaultPort, name: "http")
            .WithEnvironment("REGISTRY_STORAGE_FILESYSTEM_ROOTDIRECTORY", "/var/lib/registry")
            .WithEnvironment("REGISTRY_HTTP_ADDR", ":" + DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithBindMount($"./.data/{name}", "/var/lib/registry")
            // FT-016: mark the container itself so the publisher's build/push/deploy phases skip
            // it (the prereq phase already deploys this registry to Coolify).
            .WithAnnotation(
                new PksAgentRegistryAnnotation(
                    RegistryName: name,
                    Image: $"{DefaultImage}:{tag}",
                    Port: port),
                ResourceAnnotationMutationBehavior.Replace);

        var target = builder.AddContainerRegistry(
            targetName,
            $"localhost:{port}");

        // FT-016 marker — drives prereq-phase discovery (TC-032). Carries enough
        // identity for the prereq phase to upsert the registry Application
        // without re-walking the resource graph.
        target.WithAnnotation(
            new PksAgentRegistryAnnotation(
                RegistryName: name,
                Image: $"{DefaultImage}:{tag}",
                Port: port),
            ResourceAnnotationMutationBehavior.Replace);

        if (!s_registriesByBuilder.TryGetValue(builder, out var list))
        {
            list = new List<ContainerRegistryResource>();
            s_registriesByBuilder.Add(builder, list);
        }
        list.Add(target.Resource);

        return (container, target);
    }

    /// <summary>
    /// FT-016 §"Behaviour §3 — Pre-set escape hatch": attaches a verbatim domain to an
    /// in-project registry, bypassing Coolify auto-discovery. The prereq phase emits
    /// <c>W_REGISTRY_FQDN_FALLBACK</c> when this branch fires so the bypass is visible
    /// in deploy logs.
    /// </summary>
    public static IResourceBuilder<ContainerRegistryResource> WithDomain(
        this IResourceBuilder<ContainerRegistryResource> registry,
        string domain)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        registry.WithAnnotation(
            new PksAgentRegistryDomainAnnotation(domain.Trim()),
            ResourceAnnotationMutationBehavior.Replace);
        return registry;
    }
}
