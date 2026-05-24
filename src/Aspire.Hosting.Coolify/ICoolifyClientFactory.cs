// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-002 boundary onto the <c>coolify-api</c> domain (ADR-002). The configure phase resolves
/// the <c>(url, token)</c> Aspire parameter pair and hands the resolved strings to this factory
/// to obtain the <see cref="ICoolifyClient"/> that owns the bearer header for the rest of the
/// deploy (ADR-004 §6, FT-002 §State).
/// </summary>
public interface ICoolifyClientFactory
{
    ICoolifyClient Create(string baseUrl, string bearerToken);
}

/// <summary>
/// Default factory used when no concrete <c>coolify-api</c> client has been registered.
/// FT-002 ships only the configure-phase contract; the live HTTP client is owned by the
/// <c>coolify-api</c> domain (ADR-002) and a later feature wires the real factory.
/// </summary>
internal sealed class NotConfiguredCoolifyClientFactory : ICoolifyClientFactory
{
    public ICoolifyClient Create(string baseUrl, string bearerToken) =>
        new Stub();

    private sealed class Stub : ICoolifyClient
    {
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CoolifyProbeResult.TransportFailure(
                "no coolify-api client registered"));
    }
}
