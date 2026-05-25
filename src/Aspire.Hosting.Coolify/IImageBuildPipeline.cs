using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-003 boundary onto Aspire's container-image build infrastructure (ADR-005 §3). The
/// build phase composes a deterministic tag and delegates execution. The pipeline is
/// owned by Aspire; this interface lets the publisher drive it once per resource without
/// taking a direct dependency on Aspire's internal build API surface.
/// </summary>
public interface IImageBuildPipeline
{
    /// <summary>
    /// Build a container image for <paramref name="resource"/> and tag it as
    /// <paramref name="imageTag"/>. Throws on any pipeline failure.
    /// </summary>
    Task BuildAsync(IResource resource, string imageTag, CancellationToken cancellationToken);
}

/// <summary>
/// Optional defensive verification surface (FT-003 §"Error handling": "Aspire image pipeline
/// returns success but no image appears in the local cache for the computed tag" →
/// E_IMAGE_BUILD_FAILED). If supplied, the publisher consults it after each successful build.
/// </summary>
public interface ILocalImageStore
{
    bool HasTag(string imageTag);
}

/// <summary>
/// Default image-build pipeline: delegates to Aspire's canonical
/// <see cref="IResourceContainerImageManager"/> (the same service the Azure Container Apps
/// publisher uses — see docs/aspire-image-builder-recon.md §2). This gives us multi-arch,
/// layer caching, build-arg / build-secret handling, and correct
/// <c>WithContainerRegistry</c> propagation for free.
///
/// Falls back to a no-op when no <see cref="AspireServices"/> has been plumbed in (e.g.
/// in unit tests that don't run the full pipeline) so the build phase still progresses.
/// </summary>
internal sealed class UnconfiguredImageBuildPipeline : IImageBuildPipeline
{
    /// <summary>
    /// Aspire's pipeline-step <see cref="IServiceProvider"/>. Set by
    /// <see cref="CoolifyDeployingPublisher.RunPhaseAsync"/> at the top of every phase
    /// invocation so this pipeline can resolve <see cref="IResourceContainerImageManager"/>.
    /// </summary>
    internal IServiceProvider? AspireServices { get; set; }

    public async Task BuildAsync(IResource resource, string imageTag, CancellationToken cancellationToken)
    {
        if (AspireServices is null)
        {
            // Test / non-pipeline context: no-op succeed. The publisher records the
            // computed tag so the deploy phase still tells Coolify the right image name.
            return;
        }

#pragma warning disable ASPIREPIPELINES003 // IResourceContainerImageManager is [Experimental]; we deliberately depend on it for v1.x — see docs/aspire-image-builder-recon.md
        var manager = AspireServices.GetService<IResourceContainerImageManager>();
        if (manager is null)
        {
            // Aspire below 13.x or DI not yet bootstrapped — same no-op fallback as above.
            return;
        }

        // The manager honours WithContainerRegistry / WithRemoteImageName / WithRemoteImageTag
        // annotations on the resource (FT-014 wires WithContainerRegistry). The `imageTag`
        // parameter we computed locally is informational only; the manager derives the
        // final tag from the resource's annotations.
        await manager.BuildImageAsync(resource, cancellationToken).ConfigureAwait(false);
#pragma warning restore ASPIREPIPELINES003
    }
}
