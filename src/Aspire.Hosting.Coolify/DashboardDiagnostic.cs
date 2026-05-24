using System.Text;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The five FT-010 managed-dashboard warning symbols (FT-010 §Outputs, I-13). These literals
/// are part of the publisher's observable CLI contract and may not change without a new ADR.
/// All five are <b>warnings, not errors</b>: the dashboard never fails the workload (I-1).
/// </summary>
public enum DashboardSymbol
{
    DashboardTokenMissing,
    DashboardUpsertFailed,
    DashboardEnvVarFailed,
    DashboardTriggerFailed,
    DashboardUnexpected,
}

internal static class DashboardSymbolExtensions
{
    public static string Literal(this DashboardSymbol s) => s switch
    {
        DashboardSymbol.DashboardTokenMissing => "W_DASHBOARD_TOKEN_MISSING",
        DashboardSymbol.DashboardUpsertFailed => "W_DASHBOARD_UPSERT_FAILED",
        DashboardSymbol.DashboardEnvVarFailed => "W_DASHBOARD_ENVVAR_FAILED",
        DashboardSymbol.DashboardTriggerFailed => "W_DASHBOARD_TRIGGER_FAILED",
        DashboardSymbol.DashboardUnexpected => "W_DASHBOARD_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown DashboardSymbol: {s}"),
    };
}

/// <summary>
/// Structured warning diagnostic produced by the FT-010 dashboard sub-phase (FT-010 §Outputs).
/// The <see cref="Format"/> output is written verbatim to stderr; its first whitespace-
/// delimited token is the literal <c>W_…</c> symbol TC-014 greps for. All diagnostics carry
/// <c>severity: warning</c> per FT-010 §Outputs.
/// </summary>
public sealed class DashboardDiagnostic
{
    public required DashboardSymbol Symbol { get; init; }

    public string? Project { get; init; }
    public string? Environment { get; init; }
    public string? Coolify { get; init; }
    public string? ParameterName { get; init; }

    /// <summary>Names of the env-vars that failed (W_DASHBOARD_ENVVAR_FAILED only). Values never logged.</summary>
    public IReadOnlyList<string> FailedEnvVars { get; init; } = Array.Empty<string>();

    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());
        sb.AppendLine("  severity:    warning");
        if (!string.IsNullOrEmpty(Project))
        {
            sb.AppendLine($"  project:     {Project}");
        }
        if (!string.IsNullOrEmpty(Environment))
        {
            sb.AppendLine($"  environment: {Environment}");
        }
        if (Symbol == DashboardSymbol.DashboardEnvVarFailed && FailedEnvVars.Count > 0)
        {
            sb.AppendLine($"  env-vars:    {string.Join(", ", FailedEnvVars)}");
        }
        if (!string.IsNullOrEmpty(Coolify))
        {
            sb.AppendLine($"  coolify:     {Coolify}");
        }
        if (!string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  detail:      {Detail}");
        }
        sb.AppendLine("  see:         FT-010, ADR-004 §\"audience separation\"");
        sb.AppendLine("  remediation:");
        sb.AppendLine($"    {RemediationLine()}");
        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        DashboardSymbol.DashboardTokenMissing =>
            "managed Aspire dashboard opt-in: dashboard-token parameter is unset; workload deploy continues.",
        DashboardSymbol.DashboardUpsertFailed =>
            "managed Aspire dashboard upsert failed; workload deploy continues.",
        DashboardSymbol.DashboardEnvVarFailed =>
            "managed Aspire dashboard env-var write failed for one or more variables; workload deploy continues.",
        DashboardSymbol.DashboardTriggerFailed =>
            "managed Aspire dashboard deploy-action trigger failed; workload deploy continues.",
        DashboardSymbol.DashboardUnexpected =>
            "managed Aspire dashboard sub-phase encountered an unclassified failure; workload deploy continues.",
        _ => "",
    };

    private string RemediationLine() => Symbol switch
    {
        DashboardSymbol.DashboardTokenMissing =>
            $"set parameter '{ParameterName ?? "<dashboard-token>"}' via " +
            $"`dotnet user-secrets set Parameters:{ParameterName ?? "<dashboard-token>"} <value>` " +
            $"or the Parameters__{(ParameterName ?? "<dashboard-token>").Replace('-', '_')} env-var, then re-run `aspire deploy`.",
        DashboardSymbol.DashboardUpsertFailed =>
            "inspect the Coolify-side application list for the `coolify-aspiredashboard` entry, then re-run `aspire deploy` to retry.",
        DashboardSymbol.DashboardEnvVarFailed =>
            "inspect the Coolify-side `coolify-aspiredashboard` env-var panel for the failing key(s), then re-run `aspire deploy`.",
        DashboardSymbol.DashboardTriggerFailed =>
            "inspect the Coolify-side dashboard application's deploy history, then re-run `aspire deploy` to retry the trigger.",
        DashboardSymbol.DashboardUnexpected =>
            "re-run `aspire deploy` to retry the dashboard sub-phase; workload is already deployed.",
        _ => "see FT-010 §\"Error handling\"",
    };
}
