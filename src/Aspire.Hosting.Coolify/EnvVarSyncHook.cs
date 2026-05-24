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
/// The three FT-007 fail-fast symbols (FT-007 §Outputs, I-12). Their spellings are
/// observable contract; changing any literal is a breaking change. Precedence is
/// <c>SECRET_LEAKED &gt; UPSERT_FAILED &gt; PHASE_UNEXPECTED</c>.
/// </summary>
public enum EnvVarSymbol
{
    EnvVarUpsertFailed,
    EnvVarSecretLeaked,
    EnvVarPhaseUnexpected,
}

internal static class EnvVarSymbolExtensions
{
    public static string Literal(this EnvVarSymbol s) => s switch
    {
        EnvVarSymbol.EnvVarUpsertFailed => "E_ENVVAR_UPSERT_FAILED",
        EnvVarSymbol.EnvVarSecretLeaked => "E_ENVVAR_SECRET_LEAKED",
        EnvVarSymbol.EnvVarPhaseUnexpected => "E_ENVVAR_PHASE_UNEXPECTED",
        _ => throw new InvalidOperationException($"Unknown EnvVarSymbol: {s}"),
    };
}

/// <summary>
/// One Aspire parameter reference projected from a resource (FT-007 §Behaviour §1). The
/// <see cref="Key"/> is the env-var name the consuming container would see (Aspire-surfaced,
/// verbatim — I-4). <see cref="Value"/> is the resolved string; <see cref="Secret"/> mirrors
/// the parameter's <c>secret: true / false</c> flag.
/// </summary>
public sealed record ParameterReference(string Key, string? Value, bool Secret);

/// <summary>
/// Resolved value for a connection-string-named env-var (FT-007 §Behaviour §2). When
/// <see cref="ConnectionStringResolver"/> returns <c>null</c> for a name, FT-007 emits a
/// skip-with-log-line and makes no Coolify call.
/// </summary>
public sealed record ConnectionStringValue(string Value, bool Secret);

/// <summary>Resolver for connection-string-named env-vars. FT-008 supplies this in v1+.</summary>
public delegate ConnectionStringValue? ConnectionStringResolver(string name);

/// <summary>Provider that enumerates a resource's Aspire parameter references.</summary>
public delegate Task<IReadOnlyList<ParameterReference>> ParameterReferenceProvider(
    IResource resource, CancellationToken cancellationToken);

/// <summary>Provider that enumerates the connection-string names a resource consumes.</summary>
public delegate IReadOnlyList<string> ConnectionStringNameProvider(IResource resource);

/// <summary>
/// FT-007: secret / env-var sync hook. Wired into FT-005's per-resource loop between the
/// service upsert and the per-service deploy-action trigger (FT-005 I-12). Owns the
/// parameter-projection write path; FT-008 (when present) owns the reference-value write
/// path through a non-overlapping key set (FT-007 I-5 / FT-008 I-3).
/// </summary>
public sealed class EnvVarSyncHook : ICoolifyDeployHook
{
    private readonly Func<ICoolifyClient> _clientProvider;
    private readonly ParameterReferenceProvider _parameterRefsProvider;
    private readonly ConnectionStringNameProvider _connectionStringNamesProvider;
    private readonly ConnectionStringResolver? _connectionStringResolver;
    private readonly IReadOnlyList<string> _sentinels;

    /// <summary>Sink for the FT-007 fail-fast diagnostic (FT-007 §Outputs). Defaults to <see cref="Console.Error"/>.</summary>
    public TextWriter ErrorWriter { get; set; } = Console.Error;

    /// <summary>Structured-log sink. Defaults to a null logger; tests inject a recorder.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Most recent fail-fast diagnostic (null on success).</summary>
    public EnvVarSyncDiagnostic? LastDiagnostic { get; private set; }

    /// <summary>Total invocation count across the publisher's lifetime (asserted by TC-011 §A).</summary>
    public int InvocationCount { get; private set; }

    public EnvVarSyncHook(
        Func<ICoolifyClient> clientProvider,
        ParameterReferenceProvider parameterRefsProvider,
        ConnectionStringNameProvider connectionStringNamesProvider,
        ConnectionStringResolver? connectionStringResolver = null,
        IReadOnlyList<string>? sentinels = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _parameterRefsProvider = parameterRefsProvider ?? throw new ArgumentNullException(nameof(parameterRefsProvider));
        _connectionStringNamesProvider = connectionStringNamesProvider ?? throw new ArgumentNullException(nameof(connectionStringNamesProvider));
        _connectionStringResolver = connectionStringResolver;
        _sentinels = sentinels ?? Array.Empty<string>();
    }

    public async Task<DeployHookResult> InvokeAsync(DeployHookContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        InvocationCount++;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // §1 — enumerate parameter references.
            IReadOnlyList<ParameterReference> parameterRefs;
            try
            {
                parameterRefs = await _parameterRefsProvider(context.Resource, cancellationToken).ConfigureAwait(false)
                    ?? Array.Empty<ParameterReference>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var sanitised = ScanOrFlag(ex.Message, out var leak);
                return EmitFailure(new EnvVarSyncDiagnostic
                {
                    Symbol = leak ? EnvVarSymbol.EnvVarSecretLeaked : EnvVarSymbol.EnvVarPhaseUnexpected,
                    Resource = context.Resource.Name,
                    Service = context.ServiceId,
                    Detail = leak ? null : sanitised,
                    SentinelLeak = leak,
                }, context);
            }

            // §3 — pre-flight sentinel scan on resolved values for non-secret parameters.
            foreach (var p in parameterRefs)
            {
                if (!p.Secret && p.Value is not null && ContainsSentinel(p.Value))
                {
                    return EmitFailure(new EnvVarSyncDiagnostic
                    {
                        Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                        Resource = context.Resource.Name,
                        Service = context.ServiceId,
                        Detail = $"sentinel detected on non-secret parameter '{p.Key}' before any Coolify call",
                    }, context);
                }
            }

            // §2 — enumerate connection-string names; resolve via FT-008 or skip-with-log.
            IReadOnlyList<string> connectionStringNames;
            try
            {
                connectionStringNames = _connectionStringNamesProvider(context.Resource)
                    ?? Array.Empty<string>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var sanitised = ScanOrFlag(ex.Message, out var leak);
                return EmitFailure(new EnvVarSyncDiagnostic
                {
                    Symbol = leak ? EnvVarSymbol.EnvVarSecretLeaked : EnvVarSymbol.EnvVarPhaseUnexpected,
                    Resource = context.Resource.Name,
                    Service = context.ServiceId,
                    Detail = leak ? null : sanitised,
                    SentinelLeak = leak,
                }, context);
            }

            // Build the unified (key, value, secret) tuple list (§Behaviour §1–2).
            var toWrite = new List<(string Key, string Value, bool Secret)>();

            foreach (var p in parameterRefs)
            {
                var v = p.Value;
                if (string.IsNullOrWhiteSpace(v)) continue;
                toWrite.Add((p.Key, v.Trim(), p.Secret));
            }

            foreach (var name in connectionStringNames)
            {
                ConnectionStringValue? resolved = _connectionStringResolver?.Invoke(name);
                if (resolved is null || string.IsNullOrWhiteSpace(resolved.Value))
                {
                    EmitSkipLine(context.Resource.Name, name);
                    continue;
                }
                toWrite.Add((name, resolved.Value.Trim(), resolved.Secret));
            }

            // §4 — per env-var upsert loop with aggregation.
            var client = _clientProvider();
            var envVars = client.ServiceEnvVars;
            var failures = new List<(string Key, string? Excerpt)>();

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
                    var excerpt = ScanOrFlag(ex.Message, out var leak);
                    if (leak)
                    {
                        return EmitFailure(new EnvVarSyncDiagnostic
                        {
                            Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                            Resource = context.Resource.Name,
                            Service = context.ServiceId,
                            Keys = new[] { key },
                        }, context);
                    }
                    failures.Add((key, excerpt));
                    continue;
                }

                // Sentinel scan the response excerpt before it would be logged or attached
                // to a diagnostic (FT-007 §Behaviour §4.ii).
                if (fetch.ResponseExcerpt is not null && ContainsSentinel(fetch.ResponseExcerpt))
                {
                    return EmitFailure(new EnvVarSyncDiagnostic
                    {
                        Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                        Resource = context.Resource.Name,
                        Service = context.ServiceId,
                        Keys = new[] { key },
                    }, context);
                }

                switch (fetch.Kind)
                {
                    case EnvVarFetchKind.NotFound:
                    {
                        EnvVarWriteResult write;
                        try
                        {
                            write = await envVars.CreateAsync(context.ServiceId, key, value, secret, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            var excerpt = ScanOrFlag(ex.Message, out var leak);
                            if (leak)
                            {
                                return EmitFailure(new EnvVarSyncDiagnostic
                                {
                                    Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                                    Resource = context.Resource.Name,
                                    Service = context.ServiceId,
                                    Keys = new[] { key },
                                }, context);
                            }
                            failures.Add((key, excerpt));
                            continue;
                        }
                        if (!write.Succeeded)
                        {
                            var excerpt = write.ResponseExcerpt;
                            if (excerpt is not null && ContainsSentinel(excerpt))
                            {
                                return EmitFailure(new EnvVarSyncDiagnostic
                                {
                                    Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                                    Resource = context.Resource.Name,
                                    Service = context.ServiceId,
                                    Keys = new[] { key },
                                }, context);
                            }
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

                        // Drift overwrite — managed-field set is {value, secret-flag}.
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
                            patch = await envVars.PatchAsync(context.ServiceId, key, value, secret, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            var excerpt = ScanOrFlag(ex.Message, out var leak);
                            if (leak)
                            {
                                return EmitFailure(new EnvVarSyncDiagnostic
                                {
                                    Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                                    Resource = context.Resource.Name,
                                    Service = context.ServiceId,
                                    Keys = new[] { key },
                                }, context);
                            }
                            failures.Add((key, excerpt));
                            continue;
                        }
                        if (!patch.Succeeded)
                        {
                            var excerpt = patch.ResponseExcerpt;
                            if (excerpt is not null && ContainsSentinel(excerpt))
                            {
                                return EmitFailure(new EnvVarSyncDiagnostic
                                {
                                    Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                                    Resource = context.Resource.Name,
                                    Service = context.ServiceId,
                                    Keys = new[] { key },
                                }, context);
                            }
                            failures.Add((key, excerpt));
                            continue;
                        }
                        EmitLog("updated", context.Resource.Name, key, secret);
                        break;
                    }
                    case EnvVarFetchKind.Failure:
                    default:
                    {
                        var excerpt = fetch.ResponseExcerpt;
                        // The fetch-excerpt sentinel scan above already handled the leak case.
                        failures.Add((key, excerpt));
                        break;
                    }
                }
            }

            // §5 — aggregate per-key failures.
            if (failures.Count > 0)
            {
                return EmitFailure(new EnvVarSyncDiagnostic
                {
                    Symbol = EnvVarSymbol.EnvVarUpsertFailed,
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
            var sanitised = ScanOrFlag(ex.Message, out var leak);
            return EmitFailure(new EnvVarSyncDiagnostic
            {
                Symbol = leak ? EnvVarSymbol.EnvVarSecretLeaked : EnvVarSymbol.EnvVarPhaseUnexpected,
                Resource = context.Resource.Name,
                Service = context.ServiceId,
                Detail = leak ? null : sanitised,
                SentinelLeak = leak,
            }, context);
        }
    }

    private void EmitSkipLine(string resource, string key)
    {
        Logger.LogInformation(
            "envvar-skipped: resource={Resource} key={Key} reason=awaiting-FT-008",
            resource, key);
    }

    private void EmitLog(string outcome, string resource, string key, bool secret)
    {
        // Per FT-007 §Outputs: log record carries the key only, never the value.
        Logger.LogInformation(
            "envvar-{Outcome}: resource={Resource} key={Key} secret={Secret}",
            outcome, resource, key, secret);
    }

    private DeployHookResult EmitFailure(EnvVarSyncDiagnostic diagnostic, DeployHookContext context)
    {
        // §6 — sentinel scan FT-007's own outgoing diagnostic line before stderr write. If the
        // assembled text contains a sentinel substring, escalate to SECRET_LEAKED and replace
        // the planned output with the fixed diagnostic.
        var planned = diagnostic.Format();
        if (diagnostic.Symbol != EnvVarSymbol.EnvVarSecretLeaked && ContainsSentinel(planned))
        {
            diagnostic = new EnvVarSyncDiagnostic
            {
                Symbol = EnvVarSymbol.EnvVarSecretLeaked,
                Resource = context.Resource.Name,
                Service = context.ServiceId,
                Keys = diagnostic.Keys,
            };
            planned = diagnostic.Format();
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

    private string? ScanOrFlag(string? text, out bool leaked)
    {
        leaked = ContainsSentinel(text);
        return leaked ? null : text;
    }
}

/// <summary>
/// Structured fail-fast diagnostic produced by FT-007 (FT-007 §Outputs). The
/// <see cref="Format"/> output is written verbatim to stderr; its first whitespace-delimited
/// token is the literal symbol TC-011 greps for.
/// </summary>
public sealed class EnvVarSyncDiagnostic
{
    public required EnvVarSymbol Symbol { get; init; }
    public string? Resource { get; init; }
    public string? Service { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
    public string? CoolifyExcerpt { get; init; }
    public string? Detail { get; init; }
    /// <summary>True when the SECRET_LEAKED path was reached via sentinel detection on an inner message.</summary>
    public bool SentinelLeak { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        if (!string.IsNullOrEmpty(Resource))
        {
            sb.AppendLine($"  resource:    {Resource}");
        }
        if (!string.IsNullOrEmpty(Service))
        {
            sb.AppendLine($"  service:     {Service}");
        }
        if (Symbol == EnvVarSymbol.EnvVarUpsertFailed && Keys.Count > 0)
        {
            sb.AppendLine($"  keys:        {string.Join(", ", Keys)}");
            if (!string.IsNullOrEmpty(CoolifyExcerpt))
            {
                sb.AppendLine($"  coolify:     {CoolifyExcerpt}");
            }
        }
        if (Symbol == EnvVarSymbol.EnvVarSecretLeaked)
        {
            sb.AppendLine("  see:         ADR-004 §\"Test coverage\", FT-007");
        }
        if (Symbol == EnvVarSymbol.EnvVarPhaseUnexpected && !string.IsNullOrEmpty(Detail))
        {
            sb.AppendLine($"  detail:      {Detail}");
        }
        sb.AppendLine("  remediation:");
        sb.AppendLine($"    {RemediationLine()}");
        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        EnvVarSymbol.EnvVarUpsertFailed =>
            "Coolify service env-var upsert failed for one or more keys.",
        EnvVarSymbol.EnvVarSecretLeaked =>
            "Aspire redaction-sentinel detected in env-var sync output; line suppressed.",
        EnvVarSymbol.EnvVarPhaseUnexpected =>
            "Unclassified failure in env-var sync hook.",
        _ => "",
    };

    private string RemediationLine() => Symbol switch
    {
        EnvVarSymbol.EnvVarUpsertFailed =>
            "inspect the Coolify-side service's environment-variables panel for the failing key(s)",
        EnvVarSymbol.EnvVarSecretLeaked =>
            "file a bug — a redaction discipline regression was caught",
        _ => "see FT-007 §\"Error handling\"",
    };
}
