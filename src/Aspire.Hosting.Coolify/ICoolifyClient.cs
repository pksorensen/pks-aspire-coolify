using System.Net;

namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-002 surface of the <c>coolify-api</c> client (ADR-002). Exposes only the combined
/// version + auth probe used by the configure phase — later features extend with the rest
/// of the endpoint surface.
///
/// I-1: the probe is the single configure-phase round-trip; implementations must not issue
/// a second "double-check" call.
/// </summary>
public interface ICoolifyClient
{
    /// <summary>
    /// Performs the combined version + auth probe — "one authenticated GET against a
    /// low-cost authenticated endpoint" (FT-002 §Behaviour step 4). Returns a classified
    /// <see cref="CoolifyProbeResult"/>; never throws for HTTP- or transport-level
    /// failures (those are folded into the result classification).
    /// </summary>
    Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Private Registries endpoint group (ADR-005 §D5), consumed by FT-004's configure-phase
    /// credential upsert step. Default implementation returns a failure so an unwired client
    /// surfaces as <c>E_COOLIFY_REGISTRY_UPSERT_FAILED</c> if the credentialled path is
    /// exercised against it.
    /// </summary>
    IPrivateRegistriesApi PrivateRegistries => UnconfiguredPrivateRegistriesApi.Instance;

    /// <summary>Destinations endpoint group (ADR-001 D4), consumed by FT-005 step 2.</summary>
    IDestinationsApi Destinations => UnconfiguredDestinationsApi.Instance;

    /// <summary>Projects endpoint group (ADR-001 D1), consumed by FT-005 step 3.</summary>
    IProjectsApi Projects => UnconfiguredProjectsApi.Instance;

    /// <summary>Environments endpoint group (ADR-001 D2), consumed by FT-005 step 4.</summary>
    IEnvironmentsApi Environments => UnconfiguredEnvironmentsApi.Instance;

    /// <summary>Per-resource service endpoint group, consumed by FT-005 step 5 / 7.</summary>
    IServicesApi Services => UnconfiguredServicesApi.Instance;

    /// <summary>Deploy-job-status endpoint group (FT-006 I-7), consumed by the verify phase.</summary>
    IDeployJobsApi DeployJobs => UnconfiguredDeployJobsApi.Instance;

    /// <summary>Service-scope env-var endpoint group (FT-007 I-2), consumed by the env-var sync hook.</summary>
    IServiceEnvVarsApi ServiceEnvVars => UnconfiguredServiceEnvVarsApi.Instance;
}

/// <summary>
/// Classified outcome of <see cref="ICoolifyClient.ProbeAsync"/>. Exactly one of the
/// member kinds is populated.
/// </summary>
public sealed record CoolifyProbeResult
{
    private CoolifyProbeResult() { }

    public CoolifyProbeKind Kind { get; private init; }

    /// <summary>Observed Coolify version string on success.</summary>
    public string? Version { get; private init; }

    /// <summary>HTTP status code on <see cref="CoolifyProbeKind.AuthRejected"/> /
    /// <see cref="CoolifyProbeKind.TransportFailure"/> (when classification has one).</summary>
    public HttpStatusCode? StatusCode { get; private init; }

    /// <summary>Underlying error message (already redacted by the caller) on transport failures.</summary>
    public string? ErrorMessage { get; private init; }

    public static CoolifyProbeResult Success(string version) =>
        new() { Kind = CoolifyProbeKind.Success, Version = version };

    public static CoolifyProbeResult AuthRejected(HttpStatusCode status) =>
        new() { Kind = CoolifyProbeKind.AuthRejected, StatusCode = status };

    public static CoolifyProbeResult UnparseableResponse(string message) =>
        new() { Kind = CoolifyProbeKind.UnparseableResponse, ErrorMessage = message };

    public static CoolifyProbeResult TransportFailure(string message, HttpStatusCode? status = null) =>
        new() { Kind = CoolifyProbeKind.TransportFailure, ErrorMessage = message, StatusCode = status };
}

public enum CoolifyProbeKind
{
    Success,
    AuthRejected,
    UnparseableResponse,
    TransportFailure,
}
