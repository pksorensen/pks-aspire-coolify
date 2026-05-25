using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-004 boundary onto Aspire's container-image push infrastructure. Push happens after
/// the build phase has produced a local image, before the deploy phase tells Coolify
/// about it. Returns a structured result so the publisher can distinguish auth failures
/// from transport failures and emit the right diagnostic.
/// </summary>
public interface IImagePushPipeline
{
    Task<ImagePushResult> PushAsync(
        string imageTag,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken);
}

/// <summary>
/// Credentials for a registry push. Either both <see cref="Username"/> and
/// <see cref="Password"/> are set (authenticated push) or neither is (anonymous push).
/// </summary>
public sealed record RegistryCredentials(string Username, string Password);

public sealed record ImagePushResult
{
    private ImagePushResult() { }

    public ImagePushResultKind Kind { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ImagePushResult Success() =>
        new() { Kind = ImagePushResultKind.Success };

    public static ImagePushResult AuthRejected(string? message = null) =>
        new() { Kind = ImagePushResultKind.AuthRejected, ErrorMessage = message };

    public static ImagePushResult Failed(string message) =>
        new() { Kind = ImagePushResultKind.Failed, ErrorMessage = message };
}

public enum ImagePushResultKind
{
    Success,
    AuthRejected,
    Failed,
}

/// <summary>
/// Default image-push pipeline: delegates to Aspire's canonical
/// <see cref="IResourceContainerImageManager"/>, which honours the
/// <c>WithContainerRegistry</c> + <c>WithImagePushOptions</c> annotations FT-014 wires.
/// Falls back to no-op-succeed when no <see cref="IServiceProvider"/> has been plumbed
/// in (test / non-pipeline context).
/// </summary>
internal sealed class UnconfiguredImagePushPipeline : IImagePushPipeline
{
    internal IServiceProvider? AspireServices { get; set; }

    /// <summary>
    /// The resource whose image is being pushed in this step. Set by
    /// <see cref="CoolifyDeployingPublisher.RunPushAsync"/> right before invocation so
    /// the manager has the right resource handle (Aspire pushes by resource, not by tag).
    /// </summary>
    internal IResource? CurrentResource { get; set; }

    public async Task<ImagePushResult> PushAsync(
        string imageTag,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken)
    {
        if (AspireServices is null || CurrentResource is null)
        {
            // No pipeline context — no-op (test / fallback).
            return ImagePushResult.Success();
        }

#pragma warning disable ASPIREPIPELINES003 // IResourceContainerImageManager is [Experimental]; we deliberately depend on it for v1.x — see docs/aspire-image-builder-recon.md
        var manager = AspireServices.GetService<IResourceContainerImageManager>();
        if (manager is null)
        {
            return ImagePushResult.Success();
        }

        try
        {
            // The manager reads WithContainerRegistry / WithImagePushOptions and pushes
            // to the configured registry. credentials are surfaced by Aspire through
            // ContainerImagePushOptions and don't need to be re-passed here.
            await manager.PushImageAsync(CurrentResource, cancellationToken).ConfigureAwait(false);
            return ImagePushResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("unauthor", StringComparison.OrdinalIgnoreCase))
        {
            return ImagePushResult.AuthRejected(ex.Message);
        }
        catch (Exception ex)
        {
            return ImagePushResult.Failed(ex.Message);
        }
#pragma warning restore ASPIREPIPELINES003
    }
}
