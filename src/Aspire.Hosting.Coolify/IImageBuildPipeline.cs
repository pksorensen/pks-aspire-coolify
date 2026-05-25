using Aspire.Hosting.ApplicationModel;

// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-003 boundary onto Aspire's container-image build infrastructure (ADR-005 §3). The
/// build phase composes a deterministic tag and delegates execution. The pipeline is owned
/// by Aspire; this interface lets the publisher drive it once per resource without taking
/// a direct dependency on Aspire's internal build API surface (which the eventual
/// implementation wires through).
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

internal sealed class UnconfiguredImageBuildPipeline : IImageBuildPipeline
{
    // Smoke-test fallback: no-op succeed. FT-003 only spec'd the orchestration; the
    // real Aspire-build integration (`dotnet publish /t:PublishContainer` + container
    // image annotation reading) is a separate follow-up FT. Until it lands, the publisher
    // records the computed tag and trusts a real image to exist at that tag — which
    // holds for AddContainer(name, image) resources (Aspire already annotates the
    // pre-existing image) but NOT for AddProject (needs real build glue).
    public Task BuildAsync(IResource resource, string imageTag, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
