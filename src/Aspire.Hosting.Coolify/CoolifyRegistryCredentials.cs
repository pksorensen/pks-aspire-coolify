using Aspire.Hosting.ApplicationModel;

// Implements ADR-007: Adopt Aspire native ContainerRegistry primitives in the publisher's push-target read path (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// Annotation that attaches a (username, password) parameter-handle pair to a
/// <see cref="ContainerRegistryResource"/>. The Coolify publisher reads this surface at
/// configure-phase to compose the Coolify Private Registry upsert and at push-phase to drive
/// the image-push pipeline. Aspire 13's stock <see cref="ContainerRegistryResource"/> exposes
/// only endpoint/repository — credentials live on this annotation per FT-014's
/// "whatever credential surface the resource exposes" contract.
/// </summary>
public sealed class CoolifyRegistryCredentialsAnnotation : IResourceAnnotation
{
    public CoolifyRegistryCredentialsAnnotation(
        IResourceBuilder<ParameterResource> username,
        IResourceBuilder<ParameterResource> password)
    {
        Username = username ?? throw new ArgumentNullException(nameof(username));
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public IResourceBuilder<ParameterResource> Username { get; }
    public IResourceBuilder<ParameterResource> Password { get; }
}
