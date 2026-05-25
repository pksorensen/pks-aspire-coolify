// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-016 marker attached to <see cref="ContainerRegistryResource"/>s produced by
/// <see cref="PksAgentRegistryExtensions.AddPksAgentRegistry"/>. The publisher's prereq
/// phase enumerates resources carrying this annotation, deploys the registry to Coolify,
/// and writes the resolved FQDN onto the resource's address sink so subsequent phases
/// observe the real, reachable host (FT-016 I-3).
/// </summary>
/// <param name="RegistryName">Aspire resource name of the registry container.</param>
/// <param name="Image">Fully-qualified image tag (<c>repo:tag</c>) the registry Application uses.</param>
/// <param name="Port">HTTP port the registry listens on inside the container.</param>
public sealed record PksAgentRegistryAnnotation(
    string RegistryName,
    string Image,
    int Port) : IResourceAnnotation;

/// <summary>
/// FT-016 §"Pre-set escape hatch": carries an explicit FQDN the prereq phase uses verbatim
/// instead of polling Coolify for the auto-discovered domain. Presence triggers a
/// <c>W_REGISTRY_FQDN_FALLBACK</c> warning.
/// </summary>
public sealed record PksAgentRegistryDomainAnnotation(string Domain) : IResourceAnnotation;
