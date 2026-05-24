// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-007's service-scope env-var endpoint group (ADR-002 client surface). The publisher
/// upserts by name (GET-by-name → PATCH if present, POST if absent — ADR-001 / ADR-003 §D3)
/// at the service scope only. Project- and environment-scope env-vars are out of scope in v1.
/// </summary>
public interface IServiceEnvVarsApi
{
    /// <summary>
    /// GET an env-var by name on a service. Returns
    /// <see cref="EnvVarFetchResult.NotFound"/> when no such key exists,
    /// <see cref="EnvVarFetchResult.Found"/> with the current value / secret-flag otherwise.
    /// </summary>
    Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken cancellationToken);

    /// <summary>POST a new env-var on a service (used after a NotFound).</summary>
    Task<EnvVarWriteResult> CreateAsync(
        string serviceId, string key, string value, bool secret, CancellationToken cancellationToken);

    /// <summary>
    /// PATCH an existing env-var on a service (used after a Found with drift).
    /// Managed fields (v1, FT-007 §"Managed-field set"): <c>value</c> and <c>secret-flag</c> only.
    /// Unmanaged fields are absent from the payload.
    /// </summary>
    Task<EnvVarWriteResult> PatchAsync(
        string serviceId, string key, string value, bool secret, CancellationToken cancellationToken);
}

/// <summary>Outcome of a GET-by-name on a service env-var.</summary>
public sealed record EnvVarFetchResult(EnvVarFetchKind Kind, string? Value, bool Secret, string? ResponseExcerpt)
{
    public static EnvVarFetchResult FoundWith(string value, bool secret) =>
        new(EnvVarFetchKind.Found, value, secret, null);

    public static EnvVarFetchResult NotFoundResult() =>
        new(EnvVarFetchKind.NotFound, null, false, null);

    public static EnvVarFetchResult FailureWith(string? excerpt) =>
        new(EnvVarFetchKind.Failure, null, false, excerpt);
}

public enum EnvVarFetchKind { Found, NotFound, Failure }

/// <summary>Outcome of a POST/PATCH write on a service env-var.</summary>
public sealed record EnvVarWriteResult(bool Succeeded, string? ResponseExcerpt)
{
    public static EnvVarWriteResult Ok() => new(true, null);
    public static EnvVarWriteResult Failure(string? excerpt) => new(false, excerpt);
}

internal sealed class UnconfiguredServiceEnvVarsApi : IServiceEnvVarsApi
{
    public static readonly UnconfiguredServiceEnvVarsApi Instance = new();

    public Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken ct) =>
        Task.FromResult(EnvVarFetchResult.FailureWith("no ServiceEnvVars client registered on this ICoolifyClient"));

    public Task<EnvVarWriteResult> CreateAsync(string serviceId, string key, string value, bool secret, CancellationToken ct) =>
        Task.FromResult(EnvVarWriteResult.Failure("no ServiceEnvVars client registered on this ICoolifyClient"));

    public Task<EnvVarWriteResult> PatchAsync(string serviceId, string key, string value, bool secret, CancellationToken ct) =>
        Task.FromResult(EnvVarWriteResult.Failure("no ServiceEnvVars client registered on this ICoolifyClient"));
}
