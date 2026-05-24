using Aspire.Hosting.ApplicationModel;

// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-005's named hook surface for FT-007 (env-var sync) and FT-008 (reference wiring).
/// Hooks are invoked exactly once per successfully-upserted service, between service upsert
/// success and the per-service deploy-action trigger (FT-005 I-12). Hooks observe the
/// in-phase Coolify-side IDs; FT-005 never dereferences env-vars or references itself.
/// </summary>
public interface ICoolifyDeployHook
{
    Task<DeployHookResult> InvokeAsync(DeployHookContext context, CancellationToken cancellationToken);
}

/// <summary>Context handed to each hook invocation.</summary>
public sealed record DeployHookContext(
    string ProjectId,
    string EnvironmentId,
    string ServiceId,
    IResource Resource);

/// <summary>
/// Hook outcome. On failure, <see cref="FailureSymbol"/> is the literal feature-owned
/// <c>E_…</c> symbol (e.g. <c>E_ENVVAR_UPSERT_FAILED</c>) FT-005 surfaces verbatim per
/// FT-005 §"Error handling".
/// </summary>
public sealed record DeployHookResult(bool Succeeded, string? FailureSymbol, string? DiagnosticText)
{
    public static DeployHookResult Ok() => new(true, null, null);

    public static DeployHookResult Fail(string failureSymbol, string? diagnosticText = null) =>
        new(false, failureSymbol, diagnosticText);
}

/// <summary>Wraps a hook-owned fail-fast so the deploy phase exits with its symbol verbatim.</summary>
public sealed class CoolifyDeployHookFailedException : Exception
{
    public CoolifyDeployHookFailedException(string symbol, string? diagnosticText)
        : base(symbol)
    {
        Symbol = symbol;
        DiagnosticText = diagnosticText;
    }

    public string Symbol { get; }
    public string? DiagnosticText { get; }
}
