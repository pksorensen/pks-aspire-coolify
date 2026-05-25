using System.Text;

// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-016 fail-fast / warning symbols for the new <c>coolify-prereq</c> phase.
/// Literals are part of the publisher's observable CLI contract.
/// </summary>
public enum PrereqSymbol
{
    PrereqRegistryDeployFailed,
    PrereqRegistryUnreachable,
    RegistryFqdnFallback,
}

internal static class PrereqSymbolExtensions
{
    public static string Literal(this PrereqSymbol s) => s switch
    {
        PrereqSymbol.PrereqRegistryDeployFailed => "E_PREREQ_REGISTRY_DEPLOY_FAILED",
        PrereqSymbol.PrereqRegistryUnreachable => "E_PREREQ_REGISTRY_UNREACHABLE",
        PrereqSymbol.RegistryFqdnFallback => "W_REGISTRY_FQDN_FALLBACK",
        _ => throw new InvalidOperationException($"Unknown PrereqSymbol: {s}"),
    };
}

/// <summary>
/// Structured fail-fast diagnostic for FT-016. Written verbatim to stderr; the first
/// whitespace-delimited token is the literal symbol that the TC suite greps for.
/// </summary>
public sealed class PrereqDiagnostic
{
    public required PrereqSymbol Symbol { get; init; }
    public string? Registry { get; init; }
    public string? ProjectUuid { get; init; }
    public string? ApplicationUuid { get; init; }
    public string? DeployActionUuid { get; init; }
    public string? Fqdn { get; init; }
    public string? ProbeUrl { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        if (!string.IsNullOrEmpty(Registry))
        {
            sb.AppendLine($"  registry:    {Registry}");
        }
        if (!string.IsNullOrEmpty(ProjectUuid))
        {
            sb.AppendLine($"  project:     {ProjectUuid}");
        }
        if (!string.IsNullOrEmpty(ApplicationUuid))
        {
            sb.AppendLine($"  application: {ApplicationUuid}");
        }
        if (!string.IsNullOrEmpty(DeployActionUuid))
        {
            sb.AppendLine($"  deploy:      {DeployActionUuid}");
        }
        if (!string.IsNullOrEmpty(Fqdn))
        {
            sb.AppendLine($"  fqdn:        {Fqdn}");
        }
        if (!string.IsNullOrEmpty(ProbeUrl))
        {
            sb.AppendLine($"  probe:       {ProbeUrl}");
        }
        if (Elapsed is not null)
        {
            sb.AppendLine($"  elapsed:     {Elapsed.Value}");
        }
        if (!string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  detail:      {Detail}");
        }
        sb.AppendLine("  see:         FT-016");
        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        PrereqSymbol.PrereqRegistryDeployFailed =>
            "In-project registry Coolify Application deploy failed before workload build.",
        PrereqSymbol.PrereqRegistryUnreachable =>
            "Resolved in-project registry FQDN was not reachable within the probe budget.",
        PrereqSymbol.RegistryFqdnFallback =>
            "Using pre-set domain from WithDomain(...) instead of Coolify auto-discovery.",
        _ => "",
    };
}

/// <summary>Outcome of <see cref="CoolifyDeployingPublisher.RunPrereqAsync"/>.</summary>
public sealed class PrereqOutcome
{
    private PrereqOutcome() { }

    public bool Succeeded { get; private init; }
    public bool Skipped { get; private init; }
    public PrereqDiagnostic? Diagnostic { get; private init; }

    public static PrereqOutcome Ok() => new() { Succeeded = true };
    public static PrereqOutcome SkippedNoRegistries() => new() { Succeeded = true, Skipped = true };
    public static PrereqOutcome Fail(PrereqDiagnostic d) => new() { Succeeded = false, Diagnostic = d };
}

/// <summary>
/// Wraps a <see cref="PrereqDiagnostic"/> so a fail-fast prereq phase aborts the
/// pipeline step before <c>coolify-build</c> runs (FT-016 I-1 / I-6 / I-7).
/// </summary>
public sealed class CoolifyPrereqFailedException : Exception
{
    public CoolifyPrereqFailedException(PrereqDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public PrereqDiagnostic Diagnostic { get; }
}
