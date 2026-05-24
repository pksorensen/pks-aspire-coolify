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
    public Task BuildAsync(IResource resource, string imageTag, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No image build pipeline is registered. FT-003 ships the orchestration only; " +
            "the live Aspire image-pipeline binding is wired by host code or by tests.");
}
