using System.Text;

// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The four push-phase / configure-upsert fail-fast symbols (FT-004 §Outputs, I-4). These
/// literals are part of the publisher's observable CLI contract and may not change without
/// a new ADR.
/// </summary>
public enum PushSymbol
{
    CoolifyRegistryUpsertFailed,
    RegistryAuthFailed,
    ImagePushFailed,
    PushPhaseUnexpected,
}

internal static class PushSymbolExtensions
{
    public static string Literal(this PushSymbol s) => s switch
    {
        PushSymbol.CoolifyRegistryUpsertFailed => "E_COOLIFY_REGISTRY_UPSERT_FAILED",
        PushSymbol.RegistryAuthFailed => "E_REGISTRY_AUTH_FAILED",
        PushSymbol.ImagePushFailed => "E_IMAGE_PUSH_FAILED",
        PushSymbol.PushPhaseUnexpected => "E_PUSH_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown PushSymbol: {s}"),
    };
}

/// <summary>
/// Structured fail-fast diagnostic for FT-004's two failure sites (configure-phase upsert
/// step and the push phase body). The <see cref="Format"/> output is written verbatim to
/// stderr; its first whitespace-delimited token is the literal symbol that TC-008 greps for.
/// I-3: never includes the resolved registry password.
/// </summary>
public sealed class PushDiagnostic
{
    public required PushSymbol Symbol { get; init; }

    /// <summary>All failing (resource, tag) pairs for push-side aggregation (I-9).</summary>
    public IReadOnlyList<(string Resource, string Tag)> Failures { get; init; } = Array.Empty<(string, string)>();

    /// <summary>
    /// Additional non-auth failures listed under the auth-precedence diagnostic
    /// (FT-004 §"Error handling" — mixed-failure ordering).
    /// </summary>
    public IReadOnlyList<(string Resource, string Tag)> AdditionalNonAuthFailures { get; init; }
        = Array.Empty<(string, string)>();

    public string? Registry { get; init; }
    public string? Username { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    /// Optional remediation pointer the configure-upsert path uses when the cause is the
    /// "credentials travel as a pair" asymmetry (FT-004 §"Error handling").
    /// </summary>
    public string? RemediationHint { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        switch (Symbol)
        {
            case PushSymbol.CoolifyRegistryUpsertFailed:
                if (!string.IsNullOrEmpty(Registry))
                {
                    sb.AppendLine($"  registry:    {Registry}");
                }
                if (!string.IsNullOrEmpty(Username))
                {
                    sb.AppendLine($"  username:    {Username}");
                }
                sb.AppendLine("  see:         ADR-005 §D5");
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:      {Detail}");
                }
                sb.AppendLine("  remediation:");
                sb.AppendLine("    check Coolify's Private Registries view for stale or duplicate records");
                if (!string.IsNullOrEmpty(RemediationHint))
                {
                    sb.AppendLine($"    {RemediationHint}");
                }
                break;

            case PushSymbol.RegistryAuthFailed:
                if (Failures.Count > 0)
                {
                    sb.AppendLine($"  resource(s): {string.Join(", ", Failures.Select(f => f.Resource))}");
                    sb.AppendLine($"  tag(s):      {string.Join(", ", Failures.Select(f => f.Tag))}");
                }
                if (!string.IsNullOrEmpty(Registry))
                {
                    sb.AppendLine($"  registry:    {Registry}");
                }
                if (!string.IsNullOrEmpty(Username))
                {
                    sb.AppendLine($"  username:    {Username}");
                }
                if (AdditionalNonAuthFailures.Count > 0)
                {
                    sb.AppendLine(
                        $"  and {AdditionalNonAuthFailures.Count} additional non-auth failures: " +
                        string.Join(", ", AdditionalNonAuthFailures.Select(f => $"{f.Resource} ({f.Tag})")));
                }
                sb.AppendLine("  remediation:");
                sb.AppendLine("    verify the registry-username / registry-password Aspire parameters");
                break;

            case PushSymbol.ImagePushFailed:
                if (Failures.Count > 0)
                {
                    sb.AppendLine($"  resource(s): {string.Join(", ", Failures.Select(f => f.Resource))}");
                    sb.AppendLine($"  tag(s):      {string.Join(", ", Failures.Select(f => f.Tag))}");
                }
                if (!string.IsNullOrEmpty(Registry))
                {
                    sb.AppendLine($"  registry:    {Registry}");
                }
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:      {Detail}");
                }
                sb.AppendLine("  remediation:");
                sb.AppendLine("    verify network reach to the registry from the AppHost build machine");
                break;

            case PushSymbol.PushPhaseUnexpected:
                if (Failures.Count > 0)
                {
                    sb.AppendLine($"  resource(s): {string.Join(", ", Failures.Select(f => f.Resource))}");
                }
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:      {Detail}");
                }
                break;
        }

        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        PushSymbol.CoolifyRegistryUpsertFailed =>
            "Coolify Private Registry upsert failed.",
        PushSymbol.RegistryAuthFailed =>
            "Registry refused the push for one or more resources (401/403).",
        PushSymbol.ImagePushFailed =>
            "Image push failed for one or more resources.",
        PushSymbol.PushPhaseUnexpected =>
            Detail ?? "Unclassified failure in push phase.",
        _ => "",
    };
}

/// <summary>Outcome of the configure-phase registry-upsert step.</summary>
public sealed class RegistryUpsertOutcome
{
    private RegistryUpsertOutcome() { }

    public bool Succeeded { get; private init; }
    public bool Skipped { get; private init; }
    public PushDiagnostic? Diagnostic { get; private init; }

    public static RegistryUpsertOutcome Ok() => new() { Succeeded = true };
    public static RegistryUpsertOutcome SkippedAnonymous() => new() { Succeeded = true, Skipped = true };
    public static RegistryUpsertOutcome Fail(PushDiagnostic d) => new() { Succeeded = false, Diagnostic = d };
}

/// <summary>Outcome of the push phase.</summary>
public sealed class PushOutcome
{
    private PushOutcome() { }

    public bool Succeeded { get; private init; }
    public PushDiagnostic? Diagnostic { get; private init; }
    public IReadOnlyList<string> PushedTags { get; private init; } = Array.Empty<string>();

    public static PushOutcome Ok(IReadOnlyList<string> pushed) =>
        new() { Succeeded = true, PushedTags = pushed };

    public static PushOutcome Fail(PushDiagnostic diagnostic) =>
        new() { Succeeded = false, Diagnostic = diagnostic };
}
