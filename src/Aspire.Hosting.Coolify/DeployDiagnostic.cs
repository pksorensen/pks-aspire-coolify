using System.Text;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The six deploy-phase fail-fast symbols (FT-005 §Outputs, I-11). These literals are
/// part of the publisher's observable CLI contract and may not change without a new ADR.
/// </summary>
public enum DeploySymbol
{
    CoolifyDestinationUpsertFailed,
    CoolifyProjectUpsertFailed,
    CoolifyEnvironmentUpsertFailed,
    CoolifyServiceUpsertFailed,
    CoolifyDeployTriggerFailed,
    DeployPhaseUnexpected,
}

internal static class DeploySymbolExtensions
{
    public static string Literal(this DeploySymbol s) => s switch
    {
        DeploySymbol.CoolifyDestinationUpsertFailed => "E_COOLIFY_DESTINATION_UPSERT_FAILED",
        DeploySymbol.CoolifyProjectUpsertFailed => "E_COOLIFY_PROJECT_UPSERT_FAILED",
        DeploySymbol.CoolifyEnvironmentUpsertFailed => "E_COOLIFY_ENVIRONMENT_UPSERT_FAILED",
        DeploySymbol.CoolifyServiceUpsertFailed => "E_COOLIFY_SERVICE_UPSERT_FAILED",
        DeploySymbol.CoolifyDeployTriggerFailed => "E_COOLIFY_DEPLOY_TRIGGER_FAILED",
        DeploySymbol.DeployPhaseUnexpected => "E_DEPLOY_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown DeploySymbol: {s}"),
    };
}

/// <summary>
/// Structured fail-fast diagnostic produced by the deploy phase (FT-005 §Outputs). The
/// <see cref="Format"/> output is written verbatim to stderr; its first whitespace-
/// delimited token is the literal symbol TC-009 greps for.
/// </summary>
public sealed class DeployDiagnostic
{
    public required DeploySymbol Symbol { get; init; }

    public string? Destination { get; init; }
    public string? Project { get; init; }
    public string? Environment { get; init; }

    /// <summary>Failing (resource, tag, response-excerpt) tuples for SERVICE / TRIGGER aggregation.</summary>
    public IReadOnlyList<(string Resource, string? Tag, string? Detail)> Failures { get; init; }
        = Array.Empty<(string, string?, string?)>();

    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        if (!string.IsNullOrEmpty(Destination))
        {
            sb.AppendLine($"  destination: {Destination}");
        }
        if (!string.IsNullOrEmpty(Project) && Symbol != DeploySymbol.CoolifyDestinationUpsertFailed)
        {
            sb.AppendLine($"  project:     {Project}");
        }
        if (!string.IsNullOrEmpty(Environment) &&
            (Symbol == DeploySymbol.CoolifyEnvironmentUpsertFailed
             || Symbol == DeploySymbol.CoolifyServiceUpsertFailed
             || Symbol == DeploySymbol.CoolifyDeployTriggerFailed))
        {
            sb.AppendLine($"  environment: {Environment}");
        }

        if (Failures.Count > 0)
        {
            sb.AppendLine($"  resource(s): {string.Join(", ", Failures.Select(f => f.Resource))}");
            var tags = Failures.Where(f => !string.IsNullOrEmpty(f.Tag)).Select(f => f.Tag!).ToList();
            if (tags.Count > 0)
            {
                sb.AppendLine($"  tag(s):      {string.Join(", ", tags)}");
            }
            var details = Failures.Where(f => !string.IsNullOrEmpty(f.Detail))
                .Select(f => $"{f.Resource}: {f.Detail}").ToList();
            if (details.Count > 0)
            {
                sb.AppendLine($"  coolify:     {string.Join(" | ", details)}");
            }
        }
        else if (!string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  coolify:     {Detail}");
        }

        sb.AppendLine("  see:         ADR-003 §D2");
        sb.AppendLine("  remediation:");
        sb.AppendLine($"    {RemediationLine()}");

        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        DeploySymbol.CoolifyDestinationUpsertFailed =>
            "Coolify destination lookup-or-upsert failed.",
        DeploySymbol.CoolifyProjectUpsertFailed =>
            "Coolify project upsert failed.",
        DeploySymbol.CoolifyEnvironmentUpsertFailed =>
            "Coolify environment upsert failed.",
        DeploySymbol.CoolifyServiceUpsertFailed =>
            "Coolify application/service/database upsert failed for one or more resources.",
        DeploySymbol.CoolifyDeployTriggerFailed =>
            "Coolify deploy-action trigger failed for one or more services.",
        DeploySymbol.DeployPhaseUnexpected =>
            Detail ?? "Unclassified failure in deploy phase.",
        _ => "",
    };

    private string RemediationLine() => Symbol switch
    {
        DeploySymbol.CoolifyDestinationUpsertFailed =>
            $"verify the destination \"{Destination}\" exists in Coolify or can be auto-upserted",
        DeploySymbol.CoolifyProjectUpsertFailed =>
            "check Coolify project list for a stale or name-colliding project",
        DeploySymbol.CoolifyEnvironmentUpsertFailed =>
            "check Coolify project's environment list",
        DeploySymbol.CoolifyServiceUpsertFailed =>
            "inspect the Coolify-side service for a previously-failed deploy / drift",
        DeploySymbol.CoolifyDeployTriggerFailed =>
            "inspect the Coolify-side deploy-job log for the failing service",
        _ => "see ADR-003 §D2",
    };
}

/// <summary>
/// Per-service deploy-action handle the deploy phase hands to <c>verify</c> (FT-006).
/// Identifies the originating Aspire resource for diagnostic surfacing. The optional
/// <see cref="Tag"/> (FT-010 I-10) distinguishes workload handles (default <c>null</c>) from
/// the managed-dashboard handle (<c>"dashboard"</c>), enabling FT-006 to apply different
/// policies per category without re-resolving the originating resource.
/// </summary>
public sealed record DeployActionHandle(string Resource, string ServiceId, string? JobHandle)
{
    public string? Tag { get; init; }
}

/// <summary>Outcome of the deploy phase.</summary>
public sealed class DeployOutcome
{
    private DeployOutcome() { }

    public bool Succeeded { get; private init; }
    public DeployDiagnostic? Diagnostic { get; private init; }
    public IReadOnlyList<DeployActionHandle> TriggeredHandles { get; private init; }
        = Array.Empty<DeployActionHandle>();

    public static DeployOutcome Ok(IReadOnlyList<DeployActionHandle> handles) =>
        new() { Succeeded = true, TriggeredHandles = handles };

    public static DeployOutcome Fail(DeployDiagnostic diagnostic) =>
        new() { Succeeded = false, Diagnostic = diagnostic };
}

/// <summary>
/// Wraps a <see cref="DeployDiagnostic"/> so a fail-fast deploy phase aborts the pipeline
/// step. The diagnostic text has already been written to stderr by the time this exception
/// is thrown.
/// </summary>
public sealed class CoolifyDeployFailedException : Exception
{
    public CoolifyDeployFailedException(DeployDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public DeployDiagnostic Diagnostic { get; }
}
