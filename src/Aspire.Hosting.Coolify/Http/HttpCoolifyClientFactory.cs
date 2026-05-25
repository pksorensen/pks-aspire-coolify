// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify.Http;

/// <summary>
/// FT-013 factory shipping the live HTTP-backed <see cref="ICoolifyClient"/>. Wired by
/// <see cref="Aspire.Hosting.CoolifyBuilderExtensions.WithCoolifyDeploy"/> so the FT-002
/// configure phase resolves to a real client instead of the
/// <see cref="NotConfiguredCoolifyClientFactory"/> stub once the publisher is registered.
/// </summary>
internal sealed class HttpCoolifyClientFactory : ICoolifyClientFactory
{
    public ICoolifyClient Create(string baseUrl, string bearerToken) =>
        new HttpCoolifyClient(baseUrl, bearerToken);
}
