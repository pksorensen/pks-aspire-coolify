// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-004 boundary onto Aspire's container-image push infrastructure (ADR-005 §D3). The push
/// phase reads the deterministic per-resource tag emitted by FT-003 and delegates the actual
/// upload to this pipeline. <see cref="RegistryCredentials"/> may be null for anonymous push.
/// Implementations never throw for HTTP-level failures: those are folded into
/// <see cref="ImagePushResult"/>.
/// </summary>
public interface IImagePushPipeline
{
    Task<ImagePushResult> PushAsync(
        string imageTag,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken);
}

/// <summary>Resolved registry credentials handed to the push pipeline (FT-004 §Push §2).</summary>
public sealed record RegistryCredentials(string Username, string Password);

/// <summary>Classified push outcome. Exactly one kind per call.</summary>
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

internal sealed class UnconfiguredImagePushPipeline : IImagePushPipeline
{
    public Task<ImagePushResult> PushAsync(
        string imageTag,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No image push pipeline is registered. FT-004 ships the orchestration only; " +
            "the live Aspire push-pipeline binding is wired by host code or by tests.");
}
