// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-004 surface of the <c>coolify-api</c> Private Registries endpoint group (ADR-005 §D5).
/// Owned by the <c>coolify-api</c> domain (ADR-002): the underlying <c>/api/v1/...</c> paths,
/// the GET → POST/PATCH discipline, the <c>managed-by: pks-aspire-coolify</c> marker
/// placement, and the password-change detection (presence/hash vs always-PATCH) are all the
/// implementation's concern.
/// </summary>
public interface IPrivateRegistriesApi
{
    Task<PrivateRegistryUpsertResult> UpsertAsync(
        string host,
        string username,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>Classified outcome of a Private Registry upsert call.</summary>
public sealed record PrivateRegistryUpsertResult
{
    private PrivateRegistryUpsertResult() { }

    public PrivateRegistryUpsertKind Kind { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static PrivateRegistryUpsertResult Success() =>
        new() { Kind = PrivateRegistryUpsertKind.Success };

    public static PrivateRegistryUpsertResult Failure(string message) =>
        new() { Kind = PrivateRegistryUpsertKind.Failure, ErrorMessage = message };
}

public enum PrivateRegistryUpsertKind
{
    Success,
    Failure,
}

internal sealed class UnconfiguredPrivateRegistriesApi : IPrivateRegistriesApi
{
    public static readonly UnconfiguredPrivateRegistriesApi Instance = new();

    public Task<PrivateRegistryUpsertResult> UpsertAsync(
        string host,
        string username,
        string password,
        CancellationToken cancellationToken) =>
        Task.FromResult(PrivateRegistryUpsertResult.Failure(
            "no PrivateRegistries client registered on this ICoolifyClient"));
}
