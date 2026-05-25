// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The fixed v1 phase set for a Coolify deploy. Order is part of the contract per ADR-003 §1, §7.
/// FT-016 inserts <see cref="Prereq"/> between <see cref="Configure"/> and <see cref="Build"/>:
/// per-deploy provision of any in-project registries declared via
/// <c>AddPksAgentRegistry(...)</c> before any sibling workload's image is built or pushed.
/// </summary>
public enum CoolifyPhase
{
    Configure = 0,
    Prereq = 1,
    Build = 2,
    Push = 3,
    Deploy = 4,
    Verify = 5,
}

internal static class CoolifyPhaseExtensions
{
    public static string PhaseName(this CoolifyPhase phase) => phase switch
    {
        CoolifyPhase.Configure => "configure",
        CoolifyPhase.Prereq => "prereq",
        CoolifyPhase.Build => "build",
        CoolifyPhase.Push => "push",
        CoolifyPhase.Deploy => "deploy",
        CoolifyPhase.Verify => "verify",
        _ => phase.ToString().ToLowerInvariant(),
    };

    public static string StepName(this CoolifyPhase phase) => "coolify-" + phase.PhaseName();
}
