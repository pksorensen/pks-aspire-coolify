// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-006's deploy-job-status endpoint group on the <c>coolify-api</c> client. The verify phase
/// polls <see cref="GetStatusAsync"/> until a terminal outcome and composes the human-followup
/// deploy-job URL via <see cref="GetHumanUrl"/>. FT-006 contacts no other endpoint group during
/// verify (I-7).
/// </summary>
public interface IDeployJobsApi
{
    Task<DeployJobStatusResult> GetStatusAsync(string handle, CancellationToken cancellationToken);

    /// <summary>
    /// Composes the human-followup deploy-job URL for the given handle. FT-006 surfaces this in
    /// diagnostics (FT-006 I-12); the path shape is owned by ADR-002 / the client implementation.
    /// </summary>
    string GetHumanUrl(string handle);
}

/// <summary>
/// Classification of a single <see cref="IDeployJobsApi.GetStatusAsync"/> outcome. Terminal
/// states are <see cref="DeployJobStatusKind.Succeeded"/>, <see cref="DeployJobStatusKind.Failed"/>,
/// and <see cref="DeployJobStatusKind.NotFound"/> (404 → treated as failure per FT-006
/// §Error handling). Non-terminal states are <see cref="DeployJobStatusKind.Queued"/> and
/// <see cref="DeployJobStatusKind.InProgress"/>. <see cref="DeployJobStatusKind.TransientFailure"/>
/// is a single classified transport hiccup; the verify loop treats it as "state unchanged,
/// sleep and retry" (FT-006 §Error handling).
/// </summary>
public enum DeployJobStatusKind
{
    Queued,
    InProgress,
    Succeeded,
    Failed,
    NotFound,
    TransientFailure,
}

public sealed record DeployJobStatusResult(
    DeployJobStatusKind Kind,
    string? RawState,
    string? ErrorMessage)
{
    public bool IsTerminal => Kind is DeployJobStatusKind.Succeeded
        or DeployJobStatusKind.Failed
        or DeployJobStatusKind.NotFound;

    public static DeployJobStatusResult Succeeded(string raw = "succeeded") =>
        new(DeployJobStatusKind.Succeeded, raw, null);
    public static DeployJobStatusResult Failed(string raw = "failed", string? error = null) =>
        new(DeployJobStatusKind.Failed, raw, error);
    public static DeployJobStatusResult Queued(string raw = "queued") =>
        new(DeployJobStatusKind.Queued, raw, null);
    public static DeployJobStatusResult InProgress(string raw = "in_progress") =>
        new(DeployJobStatusKind.InProgress, raw, null);
    public static DeployJobStatusResult NotFound() =>
        new(DeployJobStatusKind.NotFound, "404", null);
    public static DeployJobStatusResult Transient(string message) =>
        new(DeployJobStatusKind.TransientFailure, null, message);
}

internal sealed class UnconfiguredDeployJobsApi : IDeployJobsApi
{
    public static readonly UnconfiguredDeployJobsApi Instance = new();
    public Task<DeployJobStatusResult> GetStatusAsync(string handle, CancellationToken cancellationToken) =>
        Task.FromResult(DeployJobStatusResult.Transient("no DeployJobs client registered on this ICoolifyClient"));
    public string GetHumanUrl(string handle) => $"(no client) {handle}";
}
