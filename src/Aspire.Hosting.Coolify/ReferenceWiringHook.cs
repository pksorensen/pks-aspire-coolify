using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-008 fail-fast symbols (FT-008 §Outputs, I-14). Spellings are observable contract.
/// Sentinel leaks escalate to <c>E_ENVVAR_SECRET_LEAKED</c> (shared with FT-007, NOT
/// bifurcated). Precedence (FT-008 §"Sentinel-leak precedence"):
/// <c>SECRET_LEAKED &gt; TARGET_NOT_DEPLOYED &gt; UPSERT_FAILED &gt; PHASE_UNEXPECTED</c>.
/// </summary>
public enum ReferenceSymbol
{
    ReferenceTargetNotDeployed,
    ReferenceEnvVarUpsertFailed,
    ReferencePhaseUnexpected,
}

internal static class ReferenceSymbolExtensions
{
    public static string Literal(this ReferenceSymbol s) => s switch
    {
        ReferenceSymbol.ReferenceTargetNotDeployed => "E_REFERENCE_TARGET_NOT_DEPLOYED",
        ReferenceSymbol.ReferenceEnvVarUpsertFailed => "E_REFERENCE_ENVVAR_UPSERT_FAILED",
        ReferenceSymbol.ReferencePhaseUnexpected => "E_REFERENCE_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown ReferenceSymbol: {s}"),
    };
}

/// <summary>
/// One exposed endpoint on a deployed target. <see cref="Scheme"/> is lower-cased (TC-012 §D6),
/// <see cref="Index"/> is the non-negative ordinal of the endpoint among siblings sharing a
/// scheme, <see cref="Port"/> is the Coolify-internal-network port the target listens on.
/// </summary>
public sealed record EndpointDescriptor(string Scheme, int Index, int Port);

/// <summary>
/// FT-005's in-phase upserted-services map entry (FT-008 §Inputs, I-15). Identifies a target
/// by Aspire resource name; carries its Coolify <see cref="ServiceId"/>, its
/// Coolify-internal-network <see cref="Hostname"/>, and the endpoints it exposes.
/// </summary>
public sealed record DeployedTarget(
    string AspireResourceName,
    string ServiceId,
    string Hostname,
    IReadOnlyList<EndpointDescriptor> Endpoints);

/// <summary>An endpoint-typed <c>WithReference()</c> edge on the consuming resource.</summary>
public sealed record EndpointReference(string TargetResourceName);

/// <summary>A connection-string-typed <c>WithReference()</c> edge on the consuming resource.</summary>
public sealed record ConnectionStringReference(string TargetResourceName);

/// <summary>Composed value for a connection-string env-var (FT-008 §Behaviour §3).</summary>
public sealed record ConnectionStringWriteValue(string Value, bool Secret);

public delegate IReadOnlyList<EndpointReference> EndpointReferenceProvider(IResource resource);
public delegate IReadOnlyList<ConnectionStringReference> ConnectionStringReferenceProvider(IResource resource);
public delegate DeployedTarget? DeployedTargetLookup(string aspireResourceName);

/// <summary>
/// Composer hand-off: given the target Aspire resource name and the resolved deployed-target
/// (containing the Coolify-internal hostname), produce the connection-string env-var value
/// with its secret-flag set per the A3 rule. Returning <c>null</c> indicates the consumer
/// could not be composed (e.g. missing parameter) and short-circuits the write for that key
/// (no Coolify call is issued; FT-007 will have already reserved the key with
/// <c>envvar-skipped: …reason=awaiting-FT-008</c>).
/// </summary>
public delegate ConnectionStringWriteValue? ConnectionStringValueComposer(
    string targetResourceName, DeployedTarget target);

/// <summary>
/// FT-008: service-to-service network wiring hook. Wired into FT-005's per-resource loop AFTER
/// FT-007 returns successfully and BEFORE the per-service deploy-action trigger (FT-008 I-1).
/// Owns the reference-value write path; the keys it writes are partitioned from FT-007's
/// (FT-008 I-3): <c>services__&lt;target&gt;__&lt;scheme&gt;__&lt;i&gt;</c> for endpoint refs and
/// <c>ConnectionStrings__&lt;target&gt;</c> for connection-string refs.
/// </summary>
public sealed class ReferenceWiringHook : ICoolifyDeployHook
{
    private const string SharedLeakedSymbol = "E_ENVVAR_SECRET_LEAKED";

    private readonly Func<ICoolifyClient> _clientProvider;
    private readonly EndpointReferenceProvider _endpointRefsProvider;
    private readonly ConnectionStringReferenceProvider _connectionStringRefsProvider;
    private readonly DeployedTargetLookup _targetLookup;
    private readonly ConnectionStringValueComposer _connectionStringComposer;
    private readonly IReadOnlyList<string> _sentinels;

    public TextWriter ErrorWriter { get; set; } = Console.Error;
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Most recent fail-fast diagnostic (null on success).</summary>
    public ReferenceWiringDiagnostic? LastDiagnostic { get; private set; }

    /// <summary>Total invocation count.</summary>
    public int InvocationCount { get; private set; }

    public ReferenceWiringHook(
        Func<ICoolifyClient> clientProvider,
        EndpointReferenceProvider endpointRefsProvider,
        ConnectionStringReferenceProvider connectionStringRefsProvider,
        DeployedTargetLookup targetLookup,
        ConnectionStringValueComposer connectionStringComposer,
        IReadOnlyList<string>? sentinels = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _endpointRefsProvider = endpointRefsProvider ?? throw new ArgumentNullException(nameof(endpointRefsProvider));
        _connectionStringRefsProvider = connectionStringRefsProvider ?? throw new ArgumentNullException(nameof(connectionStringRefsProvider));
        _targetLookup = targetLookup ?? throw new ArgumentNullException(nameof(targetLookup));
        _connectionStringComposer = connectionStringComposer ?? throw new ArgumentNullException(nameof(connectionStringComposer));
        _sentinels = sentinels ?? Array.Empty<string>();
    }

    public async Task<DeployHookResult> InvokeAsync(DeployHookContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        InvocationCount++;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // §1 — enumerate reference edges (parameter refs are out of scope: FT-007 owns them).
            IReadOnlyList<EndpointReference> endpointRefs;
            IReadOnlyList<ConnectionStringReference> connectionStringRefs;
            try
            {
                endpointRefs = _endpointRefsProvider(context.Resource) ?? Array.Empty<EndpointReference>();
                connectionStringRefs = _connectionStringRefsProvider(context.Resource) ?? Array.Empty<ConnectionStringReference>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return FailUnexpected(context, ex);
            }

            // §2 — resolve each reference target against FT-005's upserted-services map.
            var missingTargets = new List<string>();
            var resolvedEndpointTuples = new List<(string Key, string Value, bool Secret)>();
            var resolvedConnectionStringTuples = new List<(string Key, string Value, bool Secret)>();

            foreach (var er in endpointRefs)
            {
                var target = _targetLookup(er.TargetResourceName);
                if (target is null)
                {
                    if (!missingTargets.Contains(er.TargetResourceName, StringComparer.Ordinal))
                    {
                        missingTargets.Add(er.TargetResourceName);
                    }
                    continue;
                }

                // §3 — compose endpoint tuples per (scheme, index). Value carries the
                // Coolify-internal hostname (FT-008 I-5). Secret-flag is always false for
                // plain endpoint URLs (A3 default).
                foreach (var ep in target.Endpoints)
                {
                    var key = $"services__{target.AspireResourceName}__{ep.Scheme}__{ep.Index}";
                    var value = $"{ep.Scheme}://{target.Hostname}:{ep.Port}";
                    resolvedEndpointTuples.Add((key, value, Secret: false));
                }
            }

            foreach (var cr in connectionStringRefs)
            {
                var target = _targetLookup(cr.TargetResourceName);
                if (target is null)
                {
                    if (!missingTargets.Contains(cr.TargetResourceName, StringComparer.Ordinal))
                    {
                        missingTargets.Add(cr.TargetResourceName);
                    }
                    continue;
                }

                ConnectionStringWriteValue? composed;
                try
                {
                    composed = _connectionStringComposer(cr.TargetResourceName, target);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return FailUnexpected(context, ex);
                }
                if (composed is null || string.IsNullOrWhiteSpace(composed.Value)) continue;

                var key = $"ConnectionStrings__{cr.TargetResourceName}";
                resolvedConnectionStringTuples.Add((key, composed.Value.Trim(), composed.Secret));
            }

            // §6 — precedence: TARGET_NOT_DEPLOYED dominates UPSERT_FAILED. If any target is
            // missing, we fail before issuing any env-var work for the missing-target refs.
            // Endpoint/CS tuples for resolved targets are also NOT written in this case
            // (matches FT-008 §6: deploy-action trigger is skipped; partial keys from this
            // hook on the same service are not desirable when any target is missing).
            // The spec says aggregate before symbol-selection — we already aggregated above.
            if (missingTargets.Count > 0)
            {
                return EmitFailure(new ReferenceWiringDiagnostic
                {
                    Symbol = ReferenceSymbol.ReferenceTargetNotDeployed,
                    Resource = context.Resource.Name,
                    Service = context.ServiceId,
                    Targets = missingTargets.ToArray(),
                }, context);
            }

            // §4 — pre-flight sentinel scan on resolved values for non-secret tuples
            // (defends against a redaction-pipeline bug: a non-secret value carrying a sentinel).
            foreach (var (key, value, secret) in resolvedEndpointTuples.Concat(resolvedConnectionStringTuples))
            {
                if (!secret && ContainsSentinel(value))
                {
                    return EmitLeak(context, key);
                }
            }

            // §5 — per env-var upsert loop. Combined endpoint + connection-string tuples.
            var client = _clientProvider();
            var envVars = client.ServiceEnvVars;
            var failures = new List<(string Key, string? Excerpt)>();
            var toWrite = resolvedEndpointTuples.Concat(resolvedConnectionStringTuples).ToList();

            foreach (var (key, value, secret) in toWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnvVarFetchResult fetch;
                try
                {
                    fetch = await envVars.GetByNameAsync(context.ServiceId, key, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (ContainsSentinel(ex.Message)) return EmitLeak(context, key);
                    failures.Add((key, ex.Message));
                    continue;
                }

                if (fetch.ResponseExcerpt is not null && ContainsSentinel(fetch.ResponseExcerpt))
                {
                    return EmitLeak(context, key);
                }

                switch (fetch.Kind)
                {
                    case EnvVarFetchKind.NotFound:
                    {
                        EnvVarWriteResult write;
                        try
                        {
                            write = await envVars.CreateAsync(context.ServiceId, key, value, secret, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (ContainsSentinel(ex.Message)) return EmitLeak(context, key);
                            failures.Add((key, ex.Message));
                            continue;
                        }
                        if (!write.Succeeded)
                        {
                            var excerpt = write.ResponseExcerpt;
                            if (excerpt is not null && ContainsSentinel(excerpt)) return EmitLeak(context, key);
                            failures.Add((key, excerpt));
                            continue;
                        }
                        EmitLog("created", context.Resource.Name, key, secret);
                        break;
                    }
                    case EnvVarFetchKind.Found:
                    {
                        var sameValue = string.Equals(fetch.Value, value, StringComparison.Ordinal);
                        var sameFlag = fetch.Secret == secret;
                        if (sameValue && sameFlag)
                        {
                            EmitLog("unchanged", context.Resource.Name, key, secret);
                            break;
                        }

                        if (!sameValue)
                        {
                            var prev = secret || fetch.Secret ? "REDACTED" : (fetch.Value ?? "(absent)");
                            var nextOut = secret ? "REDACTED" : value;
                            Logger.LogWarning(
                                "drift-overwritten: resource={Resource} field=envvar:{Key} previous={Previous} new={New}",
                                context.Resource.Name, key, prev, nextOut);
                        }
                        if (!sameFlag)
                        {
                            Logger.LogWarning(
                                "drift-overwritten: resource={Resource} field=envvar:{Key}:secret-flag previous={Previous} new={New}",
                                context.Resource.Name, key, fetch.Secret, secret);
                        }

                        EnvVarWriteResult patch;
                        try
                        {
                            patch = await envVars.PatchAsync(context.ServiceId, key, value, secret, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (ContainsSentinel(ex.Message)) return EmitLeak(context, key);
                            failures.Add((key, ex.Message));
                            continue;
                        }
                        if (!patch.Succeeded)
                        {
                            var excerpt = patch.ResponseExcerpt;
                            if (excerpt is not null && ContainsSentinel(excerpt)) return EmitLeak(context, key);
                            failures.Add((key, excerpt));
                            continue;
                        }
                        EmitLog("updated", context.Resource.Name, key, secret);
                        break;
                    }
                    case EnvVarFetchKind.Failure:
                    default:
                        failures.Add((key, fetch.ResponseExcerpt));
                        break;
                }
            }

            if (failures.Count > 0)
            {
                return EmitFailure(new ReferenceWiringDiagnostic
                {
                    Symbol = ReferenceSymbol.ReferenceEnvVarUpsertFailed,
                    Resource = context.Resource.Name,
                    Service = context.ServiceId,
                    Keys = failures.Select(f => f.Key).ToArray(),
                    CoolifyExcerpt = string.Join(" | ",
                        failures.Where(f => !string.IsNullOrEmpty(f.Excerpt))
                                .Select(f => $"{f.Key}: {f.Excerpt}")),
                }, context);
            }

            LastDiagnostic = null;
            return DeployHookResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailUnexpected(context, ex);
        }
    }

    private DeployHookResult FailUnexpected(DeployHookContext context, Exception ex)
    {
        if (ContainsSentinel(ex.Message))
        {
            return EmitLeak(context, key: null);
        }
        return EmitFailure(new ReferenceWiringDiagnostic
        {
            Symbol = ReferenceSymbol.ReferencePhaseUnexpected,
            Resource = context.Resource.Name,
            Service = context.ServiceId,
            Detail = ex.Message,
        }, context);
    }

    private DeployHookResult EmitLeak(DeployHookContext context, string? key)
    {
        var diagnostic = new ReferenceWiringDiagnostic
        {
            Symbol = ReferenceSymbol.ReferencePhaseUnexpected, // overridden below
            Resource = context.Resource.Name,
            Service = context.ServiceId,
            Keys = key is null ? Array.Empty<string>() : new[] { key },
            IsSharedLeak = true,
        };
        var text = diagnostic.Format();
        LastDiagnostic = diagnostic;
        ErrorWriter.Write(text);
        ErrorWriter.Flush();
        return DeployHookResult.Fail(SharedLeakedSymbol, text);
    }

    private void EmitLog(string outcome, string resource, string key, bool secret)
    {
        // FT-008 §Outputs: log carries the key only; value is never included.
        Logger.LogInformation(
            "envvar-{Outcome}: resource={Resource} key={Key} secret={Secret}",
            outcome, resource, key, secret);
    }

    private DeployHookResult EmitFailure(ReferenceWiringDiagnostic diagnostic, DeployHookContext context)
    {
        // §7 — sentinel-scan FT-008's own outgoing diagnostic line before stderr write.
        var planned = diagnostic.Format();
        if (ContainsSentinel(planned))
        {
            return EmitLeak(context, key: diagnostic.Keys.FirstOrDefault());
        }

        LastDiagnostic = diagnostic;
        ErrorWriter.Write(planned);
        ErrorWriter.Flush();
        return DeployHookResult.Fail(diagnostic.Symbol.Literal(), planned);
    }

    private bool ContainsSentinel(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var s in _sentinels)
        {
            if (!string.IsNullOrEmpty(s) && text.Contains(s, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// FT-008 fail-fast diagnostic. <see cref="Format"/>'s first whitespace-delimited token is
/// the literal <c>E_…</c> symbol the deploy phase surfaces verbatim.
/// </summary>
public sealed class ReferenceWiringDiagnostic
{
    public required ReferenceSymbol Symbol { get; init; }
    public string? Resource { get; init; }
    public string? Service { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
    public string? CoolifyExcerpt { get; init; }
    public string? Detail { get; init; }
    /// <summary>True when this diagnostic was reached via the shared FT-007 leak symbol.</summary>
    public bool IsSharedLeak { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        if (IsSharedLeak)
        {
            sb.Append("E_ENVVAR_SECRET_LEAKED");
            sb.Append(": ");
            sb.AppendLine("Aspire redaction-sentinel detected in reference-wiring output; line suppressed.");
            if (!string.IsNullOrEmpty(Resource)) sb.AppendLine($"  resource:    {Resource}");
            if (!string.IsNullOrEmpty(Service)) sb.AppendLine($"  service:     {Service}");
            sb.AppendLine("  see:         ADR-004, FT-007 leak-detector (shared)");
            sb.AppendLine("  remediation:");
            sb.AppendLine("    file a bug — a redaction discipline regression was caught");
            return sb.ToString();
        }

        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        if (!string.IsNullOrEmpty(Resource)) sb.AppendLine($"  resource:    {Resource}");
        if (!string.IsNullOrEmpty(Service)) sb.AppendLine($"  service:     {Service}");

        if (Symbol == ReferenceSymbol.ReferenceTargetNotDeployed && Targets.Count > 0)
        {
            sb.AppendLine($"  target(s):   {string.Join(", ", Targets)}");
            sb.AppendLine("  see:         ADR-001 §D2, FT-005 walk-order");
        }
        if (Symbol == ReferenceSymbol.ReferenceEnvVarUpsertFailed && Keys.Count > 0)
        {
            sb.AppendLine($"  keys:        {string.Join(", ", Keys)}");
            if (!string.IsNullOrEmpty(CoolifyExcerpt))
            {
                sb.AppendLine($"  coolify:     {CoolifyExcerpt}");
            }
            sb.AppendLine("  see:         ADR-002, FT-007 upsert mechanism");
        }
        if (Symbol == ReferenceSymbol.ReferencePhaseUnexpected && !string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  detail:      {Detail}");
        }
        sb.AppendLine("  remediation:");
        sb.AppendLine($"    {RemediationLine()}");
        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        ReferenceSymbol.ReferenceTargetNotDeployed =>
            "Reference target Aspire resource is not present in FT-005's in-phase upserted-services map.",
        ReferenceSymbol.ReferenceEnvVarUpsertFailed =>
            "Coolify service env-var upsert failed for one or more FT-008-owned reference keys.",
        ReferenceSymbol.ReferencePhaseUnexpected =>
            "Unclassified failure in reference-wiring hook.",
        _ => "",
    };

    private string RemediationLine() => Symbol switch
    {
        ReferenceSymbol.ReferenceTargetNotDeployed =>
            "verify the target resource is reachable in the AppHost graph and that FT-005's walk has not been re-ordered",
        ReferenceSymbol.ReferenceEnvVarUpsertFailed =>
            "inspect the Coolify-side service's environment-variables panel for the failing key(s)",
        _ => "see FT-008 §\"Error handling\"",
    };
}
