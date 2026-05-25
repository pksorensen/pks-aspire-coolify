// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// Classification of a per-resource Coolify upsert outcome. The walk emits structured
/// log lines using these kinds; PATCH-with-no-drift collapses to <see cref="Unchanged"/>.
/// </summary>
public enum UpsertKind
{
    Created,
    Updated,
    Unchanged,
    Failure,
}

/// <summary>One managed-field drift observed during an upsert (FT-005 §Outputs).</summary>
public sealed record DriftEntry(string Field, string? Previous, string New, bool IsSecret);

/// <summary>
/// FT-005's destination endpoint group (ADR-001 D4). v1 supports lookup-or-upsert by name;
/// the destination identity returned scopes downstream service upserts.
/// </summary>
public interface IDestinationsApi
{
    Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken cancellationToken);
}

public sealed record DestinationUpsertResult(UpsertKind Kind, string? Id, string? ErrorMessage)
{
    public static DestinationUpsertResult Found(string id) => new(UpsertKind.Unchanged, id, null);
    public static DestinationUpsertResult Created(string id) => new(UpsertKind.Created, id, null);
    public static DestinationUpsertResult Failure(string message) => new(UpsertKind.Failure, null, message);
}

/// <summary>
/// FT-005's project endpoint group (ADR-001 D1). v1 keys by AppHost name; the project
/// identity returned scopes environment upserts.
/// </summary>
public interface IProjectsApi
{
    Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken cancellationToken);
}

public sealed record ProjectUpsertResult(
    UpsertKind Kind,
    string? Id,
    IReadOnlyList<DriftEntry> Drifts,
    string? ErrorMessage)
{
    public static ProjectUpsertResult Created(string id) =>
        new(UpsertKind.Created, id, Array.Empty<DriftEntry>(), null);
    public static ProjectUpsertResult Unchanged(string id) =>
        new(UpsertKind.Unchanged, id, Array.Empty<DriftEntry>(), null);
    public static ProjectUpsertResult Updated(string id, IReadOnlyList<DriftEntry> drifts) =>
        new(UpsertKind.Updated, id, drifts, null);
    public static ProjectUpsertResult Failure(string message) =>
        new(UpsertKind.Failure, null, Array.Empty<DriftEntry>(), message);
}

/// <summary>
/// FT-005's environment endpoint group (ADR-001 D2). v1 keys by Aspire environment name
/// within the parent project.
/// </summary>
public interface IEnvironmentsApi
{
    Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken cancellationToken);
}

public sealed record EnvironmentUpsertResult(
    UpsertKind Kind,
    string? Id,
    IReadOnlyList<DriftEntry> Drifts,
    string? ErrorMessage)
{
    public static EnvironmentUpsertResult Created(string id) =>
        new(UpsertKind.Created, id, Array.Empty<DriftEntry>(), null);
    public static EnvironmentUpsertResult Unchanged(string id) =>
        new(UpsertKind.Unchanged, id, Array.Empty<DriftEntry>(), null);
    public static EnvironmentUpsertResult Updated(string id, IReadOnlyList<DriftEntry> drifts) =>
        new(UpsertKind.Updated, id, drifts, null);
    public static EnvironmentUpsertResult Failure(string message) =>
        new(UpsertKind.Failure, null, Array.Empty<DriftEntry>(), message);
}

/// <summary>
/// Managed-field spec the deploy phase sends on every application/service/database upsert
/// (FT-005 §"Managed-field set"). Only <c>image</c>, <c>registry-reference</c>,
/// <c>destination-binding</c> are managed in v1; unmanaged fields are absent from PATCH
/// payloads entirely (I-4).
/// </summary>
public sealed record ServiceSpec(string Image, string? RegistryReference, string DestinationBinding)
{
    /// <summary>
    /// FT-016 I-5: Coolify Private-Registry UUID attachment for sibling workloads whose
    /// <c>WithContainerRegistry(...)</c> edge points at an in-project pks-agent-registry.
    /// Null for workloads attached to external registries; set by the deploy phase based on
    /// the UUID recorded by the prereq phase.
    /// </summary>
    public string? PrivateRegistryAttachmentUuid { get; init; }
}

/// <summary>
/// FT-005's per-resource service endpoint group. The Aspire-resource-kind → Coolify-resource-
/// group classifier (application / service / database) is the implementation's concern;
/// FT-005 consumes a single uniform surface.
/// </summary>
public interface IServicesApi
{
    Task<ServiceUpsertResult> UpsertAsync(
        string projectId,
        string environmentId,
        string resourceName,
        ServiceSpec spec,
        CancellationToken cancellationToken);

    Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken cancellationToken);
}

public sealed record ServiceUpsertResult(
    UpsertKind Kind,
    string? Id,
    IReadOnlyList<DriftEntry> Drifts,
    string? ErrorMessage)
{
    /// <summary>
    /// Coolify-side FQDN reported on the upsert response (FT-010 §Outputs / I-9 unmanaged).
    /// Null/empty when Coolify has not yet auto-assigned a domain. Surfaced by the dashboard
    /// sub-phase as the <c>managed-dashboard: url=…</c> info line; never managed by PATCH.
    /// </summary>
    public string? Fqdn { get; init; }

    public static ServiceUpsertResult Created(string id) =>
        new(UpsertKind.Created, id, Array.Empty<DriftEntry>(), null);
    public static ServiceUpsertResult Unchanged(string id) =>
        new(UpsertKind.Unchanged, id, Array.Empty<DriftEntry>(), null);
    public static ServiceUpsertResult Updated(string id, IReadOnlyList<DriftEntry> drifts) =>
        new(UpsertKind.Updated, id, drifts, null);
    public static ServiceUpsertResult Failure(string message) =>
        new(UpsertKind.Failure, null, Array.Empty<DriftEntry>(), message);
}

public sealed record DeployTriggerResult(bool Accepted, string? Handle, string? ErrorMessage)
{
    public static DeployTriggerResult Ok(string? handle) => new(true, handle, null);
    public static DeployTriggerResult Failed(string message) => new(false, null, message);
}

internal sealed class UnconfiguredDestinationsApi : IDestinationsApi
{
    public static readonly UnconfiguredDestinationsApi Instance = new();
    public Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(DestinationUpsertResult.Failure("no Destinations client registered on this ICoolifyClient"));
}

internal sealed class UnconfiguredProjectsApi : IProjectsApi
{
    public static readonly UnconfiguredProjectsApi Instance = new();
    public Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(ProjectUpsertResult.Failure("no Projects client registered on this ICoolifyClient"));
}

internal sealed class UnconfiguredEnvironmentsApi : IEnvironmentsApi
{
    public static readonly UnconfiguredEnvironmentsApi Instance = new();
    public Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(EnvironmentUpsertResult.Failure("no Environments client registered on this ICoolifyClient"));
}

internal sealed class UnconfiguredServicesApi : IServicesApi
{
    public static readonly UnconfiguredServicesApi Instance = new();
    public Task<ServiceUpsertResult> UpsertAsync(
        string projectId,
        string environmentId,
        string resourceName,
        ServiceSpec spec,
        CancellationToken cancellationToken) =>
        Task.FromResult(ServiceUpsertResult.Failure("no Services client registered on this ICoolifyClient"));

    public Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken cancellationToken) =>
        Task.FromResult(DeployTriggerResult.Failed("no Services client registered on this ICoolifyClient"));
}
