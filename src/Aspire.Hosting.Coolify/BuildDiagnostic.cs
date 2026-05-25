using System.Text;

// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The four build-phase fail-fast symbols (FT-003 §Outputs, I-10). These literals are part
/// of the publisher's observable CLI contract and may not change without a new ADR.
/// </summary>
public enum BuildSymbol
{
    RegistryNotConfigured,
    ApphostVersionMissing,
    ImageBuildFailed,
    BuildPhaseUnexpected,
}

internal static class BuildSymbolExtensions
{
    public static string Literal(this BuildSymbol s) => s switch
    {
        BuildSymbol.RegistryNotConfigured => "E_REGISTRY_NOT_CONFIGURED",
        BuildSymbol.ApphostVersionMissing => "E_APPHOST_VERSION_MISSING",
        BuildSymbol.ImageBuildFailed => "E_IMAGE_BUILD_FAILED",
        BuildSymbol.BuildPhaseUnexpected => "E_BUILD_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown BuildSymbol: {s}"),
    };
}

/// <summary>
/// Structured fail-fast diagnostic produced by the build phase (FT-003 §Outputs). The
/// <see cref="Format"/> output is written verbatim to stderr; its first whitespace-delimited
/// token is the literal symbol that TC-007 greps for.
/// </summary>
public sealed class BuildDiagnostic
{
    public required BuildSymbol Symbol { get; init; }
    public string? Resource { get; init; }
    public string? Tag { get; init; }
    public string? ApphostAssembly { get; init; }
    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        switch (Symbol)
        {
            case BuildSymbol.RegistryNotConfigured:
                if (!string.IsNullOrEmpty(Resource))
                {
                    sb.AppendLine($"  resource: {Resource}");
                }
                sb.AppendLine("  see:      ADR-005 §1; ADR-007");
                sb.AppendLine("  remediation:");
                sb.AppendLine("    var reg = builder.AddContainerRegistry(\"name\", \"host/prefix\");");
                sb.AppendLine("    builder.AddProject<...>(\"<name>\").WithContainerRegistry(reg);");
                sb.AppendLine("    // or (deprecated, v1.x source-compat only):");
                sb.AppendLine("    builder.WithCoolifyDeploy(url, token)");
                sb.AppendLine("           .WithImageRegistry(prefix, [user, pass]);");
                break;

            case BuildSymbol.ApphostVersionMissing:
                if (!string.IsNullOrEmpty(ApphostAssembly))
                {
                    sb.AppendLine($"  apphost:  {ApphostAssembly}");
                }
                sb.AppendLine("  remediation:");
                sb.AppendLine("    [<Assembly: AssemblyInformationalVersion(\"1.0.0\")>]");
                break;

            case BuildSymbol.ImageBuildFailed:
                if (!string.IsNullOrEmpty(Resource))
                {
                    sb.AppendLine($"  resource: {Resource}");
                }
                if (!string.IsNullOrEmpty(Tag))
                {
                    sb.AppendLine($"  tag:      {Tag}");
                }
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:   {Detail}");
                }
                break;

            case BuildSymbol.BuildPhaseUnexpected:
                if (!string.IsNullOrEmpty(Resource))
                {
                    sb.AppendLine($"  resource: {Resource}");
                }
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:   {Detail}");
                }
                break;
        }

        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        BuildSymbol.RegistryNotConfigured =>
            "No image registry configured. Call WithImageRegistry(...) on the Coolify deploy.",
        BuildSymbol.ApphostVersionMissing =>
            "AppHost assembly is missing AssemblyInformationalVersionAttribute.",
        BuildSymbol.ImageBuildFailed =>
            "Aspire container-image pipeline failed for a resource.",
        BuildSymbol.BuildPhaseUnexpected =>
            Detail ?? "Unclassified failure in build phase.",
        _ => "",
    };
}

/// <summary>
/// Outcome of the build phase. On success the build phase has populated the local image
/// store with one deterministic tag per resource (FT-003 §Outputs).
/// </summary>
public sealed class BuildOutcome
{
    private BuildOutcome() { }

    public bool Succeeded { get; private init; }
    public BuildDiagnostic? Diagnostic { get; private init; }

    /// <summary>The full image tags emitted on a successful build, in iteration order.</summary>
    public IReadOnlyList<string> EmittedTags { get; private init; } = Array.Empty<string>();

    public static BuildOutcome Ok(IReadOnlyList<string> tags) =>
        new() { Succeeded = true, EmittedTags = tags };

    public static BuildOutcome Fail(BuildDiagnostic diagnostic) =>
        new() { Succeeded = false, Diagnostic = diagnostic };
}
