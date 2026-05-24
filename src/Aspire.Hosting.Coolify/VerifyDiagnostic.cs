using System.Text;

// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The three verify-phase fail-fast symbols (FT-006 §Outputs, I-5). These literals are part
/// of the publisher's observable CLI contract and may not change without a new ADR.
/// </summary>
public enum VerifySymbol
{
    VerifyFailed,
    VerifyTimeout,
    VerifyPhaseUnexpected,
}

internal static class VerifySymbolExtensions
{
    public static string Literal(this VerifySymbol s) => s switch
    {
        VerifySymbol.VerifyFailed => "E_VERIFY_FAILED",
        VerifySymbol.VerifyTimeout => "E_VERIFY_TIMEOUT",
        VerifySymbol.VerifyPhaseUnexpected => "E_VERIFY_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown VerifySymbol: {s}"),
    };
}

/// <summary>
/// Per-offending-handle tuple surfaced in the verify-phase diagnostic field block (FT-006
/// §Outputs).
/// </summary>
public sealed record VerifyFailureTuple(
    string Resource,
    string JobHandle,
    string DeployJobUrl,
    string LastObservedState);

/// <summary>
/// Structured fail-fast diagnostic produced by the verify phase (FT-006 §Outputs). The
/// <see cref="Format"/> output is written verbatim to stderr; its first whitespace-delimited
/// token is the literal symbol TC-010 greps for (FT-006 I-5).
/// </summary>
public sealed class VerifyDiagnostic
{
    public required VerifySymbol Symbol { get; init; }

    public string? Project { get; init; }
    public string? Environment { get; init; }

    /// <summary>Offending handles only — never names successfully-completed siblings.</summary>
    public IReadOnlyList<VerifyFailureTuple> Failures { get; init; } = Array.Empty<VerifyFailureTuple>();

    /// <summary>Wall-clock elapsed from <c>verify: enter</c>, included on timeout.</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>Free-form detail on <see cref="VerifySymbol.VerifyPhaseUnexpected"/>.</summary>
    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        if (!string.IsNullOrEmpty(Project))
        {
            sb.AppendLine($"  project:       {Project}");
        }
        if (!string.IsNullOrEmpty(Environment))
        {
            sb.AppendLine($"  environment:   {Environment}");
        }

        if (Failures.Count > 0)
        {
            sb.AppendLine($"  resource(s):   {string.Join(", ", Failures.Select(f => f.Resource))}");
            sb.AppendLine($"  deploy-job:    {string.Join(", ", Failures.Select(f => f.DeployJobUrl))}");
            sb.AppendLine($"  coolify:       {string.Join(" | ", Failures.Select(f => $"{f.Resource}={f.LastObservedState}"))}");
        }

        if (Symbol == VerifySymbol.VerifyTimeout && Elapsed is { } el)
        {
            sb.AppendLine($"  elapsed:       {el}");
        }

        if (Symbol == VerifySymbol.VerifyPhaseUnexpected && !string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  coolify:       {Detail}");
        }

        sb.AppendLine("  see:           ADR-003 §D6");
        sb.AppendLine("  remediation:");
        foreach (var line in RemediationLines())
        {
            sb.AppendLine($"    {line}");
        }

        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        VerifySymbol.VerifyFailed =>
            "One or more Coolify deploy-actions reached a terminal failure state.",
        VerifySymbol.VerifyTimeout =>
            "Verify phase timed out before all Coolify deploy-actions reached a terminal state.",
        VerifySymbol.VerifyPhaseUnexpected =>
            Detail ?? "Unclassified failure in verify phase.",
        _ => "",
    };

    private IEnumerable<string> RemediationLines() => Symbol switch
    {
        VerifySymbol.VerifyFailed => new[]
        {
            "inspect the Coolify deploy-job log at the URL above",
            "check Coolify-side resource limits or pull failures",
        },
        VerifySymbol.VerifyTimeout => new[]
        {
            "raise WithVerifyPolling(timeout) or inspect the job log",
        },
        _ => new[] { "see ADR-003 §D6" },
    };
}

/// <summary>Outcome of the verify phase.</summary>
public sealed class VerifyOutcome
{
    private VerifyOutcome() { }

    public bool Succeeded { get; private init; }
    public VerifyDiagnostic? Diagnostic { get; private init; }

    public static VerifyOutcome Ok() => new() { Succeeded = true };
    public static VerifyOutcome Fail(VerifyDiagnostic diagnostic) =>
        new() { Succeeded = false, Diagnostic = diagnostic };
}

/// <summary>
/// Wraps a <see cref="VerifyDiagnostic"/> so a fail-fast verify phase aborts the pipeline step.
/// The diagnostic text has already been written to stderr by the time this exception is thrown.
/// </summary>
public sealed class CoolifyVerifyFailedException : Exception
{
    public CoolifyVerifyFailedException(VerifyDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public VerifyDiagnostic Diagnostic { get; }
}
