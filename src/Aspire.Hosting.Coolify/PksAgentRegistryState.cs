// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-016 §State: per-deploy mutable state attached to an in-project pks-agent-registry
/// resource by the prereq phase. Lives only for the AppHost process lifetime (I-10).
/// </summary>
public sealed class PksAgentRegistryState
{
    /// <summary>Aspire resource name of the registry-target.</summary>
    public required string RegistryResourceName { get; init; }

    /// <summary>FQDN resolved by the prereq phase (auto-discovered or pre-set).</summary>
    public string? ResolvedFqdn { get; set; }

    /// <summary>Pre-set domain from <c>WithDomain(...)</c>, when present (FT-016 escape hatch).</summary>
    public string? PreSetDomain { get; set; }

    /// <summary>Coolify Application UUID returned by the prereq upsert (when reached).</summary>
    public string? ApplicationUuid { get; set; }

    /// <summary>Coolify deploy-action UUID returned by the prereq trigger (when reached).</summary>
    public string? DeployActionUuid { get; set; }

    /// <summary>
    /// Coolify Private-Registry UUID assigned for sibling-workload attachment (I-5).
    /// </summary>
    public string? PrivateRegistryUuid { get; set; }

    /// <summary>Coolify project UUID under which the registry Application lives.</summary>
    public string? ProjectUuid { get; set; }
}
