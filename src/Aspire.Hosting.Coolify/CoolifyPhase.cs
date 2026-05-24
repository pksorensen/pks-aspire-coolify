// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The fixed v1 phase set for a Coolify deploy. Order is part of the contract per ADR-003 §1, §7.
/// </summary>
public enum CoolifyPhase
{
    Configure = 0,
    Build = 1,
    Push = 2,
    Deploy = 3,
    Verify = 4,
}

internal static class CoolifyPhaseExtensions
{
    public static string PhaseName(this CoolifyPhase phase) => phase switch
    {
        CoolifyPhase.Configure => "configure",
        CoolifyPhase.Build => "build",
        CoolifyPhase.Push => "push",
        CoolifyPhase.Deploy => "deploy",
        CoolifyPhase.Verify => "verify",
        _ => phase.ToString().ToLowerInvariant(),
    };

    public static string StepName(this CoolifyPhase phase) => "coolify-" + phase.PhaseName();
}
