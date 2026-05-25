// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-016 surface of the <c>coolify-api</c> Applications endpoint group, consumed by the new
/// <c>coolify-prereq</c> phase for the in-project registry's own Coolify Application
/// (create / upsert + deploy trigger + read-back of the assigned domain + private-registry
/// attachment UUID). The publisher does not introduce new endpoints; this surface is the
/// minimum subset of the existing Applications + Private-Registries endpoints the prereq
/// phase composes (FT-016 §Description: "no new endpoints").
/// </summary>
public interface IApplicationsApi
{
    /// <summary>
    /// Idempotently provisions the in-project registry's Coolify Application under the named
    /// project, returns the Application UUID and the Private-Registry UUID Coolify assigned
    /// for sibling-workload attachment, plus the auto-discovered domain (when populated).
    /// </summary>
    Task<ApplicationProvisionResult> ProvisionRegistryAsync(
        ApplicationProvisionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a deploy for the Application UUID returned by
    /// <see cref="ProvisionRegistryAsync"/> and polls until the deploy-action reaches a
    /// terminal state. Returns the deploy-action UUID and the terminal outcome.
    /// </summary>
    Task<ApplicationDeployResult> TriggerAndAwaitAsync(
        string applicationUuid,
        CancellationToken cancellationToken);

    /// <summary>FT-017: upserts environment variables on the application via Coolify's <c>/envs</c> endpoint.</summary>
    Task<ApplicationProvisionResult> UpsertEnvironmentVariablesAsync(
        string applicationUuid,
        IReadOnlyDictionary<string, string> envs,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicationProvisionResult(false, applicationUuid, null, null, null,
            "env-upsert not implemented on this client"));

    /// <summary>FT-017: creates an owner on a deployed pks-agent-registry via its mgmt API.</summary>
    Task<OwnerProvisionResult> ProvisionOwnerAsync(
        OwnerProvisionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new OwnerProvisionResult(false, "owner provisioning not implemented on this client"));
}

/// <summary>Input record for <see cref="IApplicationsApi.ProvisionRegistryAsync"/>.</summary>
public sealed record ApplicationProvisionRequest(
    string RegistryResourceName,
    string ApphostProjectName,
    string EnvironmentName,
    string Image,
    int Port,
    string? DestinationName = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

/// <summary>
/// FT-017: pks-agent-registry owner provisioning via <c>POST /_mgmt/owners</c>.
/// Returns owner credentials sibling workloads use as docker-push basic-auth.
/// </summary>
public sealed record OwnerProvisionRequest(string Fqdn, string AdminToken, string OwnerName, string OwnerPassword);

public sealed record OwnerProvisionResult(bool Succeeded, string? ErrorMessage);

/// <summary>Outcome of <see cref="IApplicationsApi.ProvisionRegistryAsync"/>.</summary>
public sealed record ApplicationProvisionResult(
    bool Succeeded,
    string? ApplicationUuid,
    string? PrivateRegistryUuid,
    string? ProjectUuid,
    string? AutoDiscoveredFqdn,
    string? ErrorMessage);

/// <summary>Outcome of <see cref="IApplicationsApi.TriggerAndAwaitAsync"/>.</summary>
public sealed record ApplicationDeployResult(
    bool Succeeded,
    string? DeployActionUuid,
    string? ErrorMessage);

internal sealed class UnconfiguredApplicationsApi : IApplicationsApi
{
    public static readonly UnconfiguredApplicationsApi Instance = new();

    public Task<ApplicationProvisionResult> ProvisionRegistryAsync(
        ApplicationProvisionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicationProvisionResult(
            false, null, null, null, null,
            "no Applications client registered on this ICoolifyClient"));

    public Task<ApplicationDeployResult> TriggerAndAwaitAsync(
        string applicationUuid,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicationDeployResult(
            false, null,
            "no Applications client registered on this ICoolifyClient"));
}

/// <summary>
/// FT-016 reachability-probe surface. Default implementation issues
/// <c>HEAD /v2/</c> (falling back to <c>GET /v2/</c>) against the supplied FQDN; tests
/// inject a deterministic fake.
/// </summary>
public interface IRegistryReachabilityProbe
{
    Task<RegistryProbeOutcome> ProbeAsync(string fqdn, CancellationToken cancellationToken);
}

public sealed record RegistryProbeOutcome(
    bool Succeeded,
    string ProbeUrl,
    TimeSpan Elapsed,
    string? LastNetworkError);

/// <summary>
/// Default reachability probe. Issues a single <c>HEAD /v2/</c> against
/// <c>https://{fqdn}/v2/</c>; any non-5xx, non-network-error response counts as success.
/// </summary>
internal sealed class DefaultRegistryReachabilityProbe : IRegistryReachabilityProbe
{
    public static readonly DefaultRegistryReachabilityProbe Instance = new();

    public async Task<RegistryProbeOutcome> ProbeAsync(string fqdn, CancellationToken cancellationToken)
    {
        // Coolify provisions Let's Encrypt asynchronously after deploy: HTTPS can fail with
        // "SSL connection could not be established" for ~30-60s after the container is running.
        // Try HTTP then HTTPS on each round; retry up to 2 minutes overall.
        var hasPort = fqdn.Contains(':') && !fqdn.StartsWith("[", StringComparison.Ordinal);
        string[] schemes = hasPort ? new[] { "http" } : new[] { "http", "https" };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        string lastUrl = $"https://{fqdn}/v2/";
        string lastErr = "no probe attempted";

        while (DateTime.UtcNow < deadline)
        {
            foreach (var scheme in schemes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probeUrl = $"{scheme}://{fqdn}/v2/";
                lastUrl = probeUrl;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var request = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                    using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status < 500)
                    {
                        return new RegistryProbeOutcome(true, probeUrl, swTotal.Elapsed, null);
                    }
                    lastErr = $"HTTP {status}";
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        return new RegistryProbeOutcome(false, lastUrl, swTotal.Elapsed, lastErr);
    }
}
