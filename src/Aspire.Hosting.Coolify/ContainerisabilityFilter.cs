using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1) — §5 skip-with-warning.
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1).
namespace Aspire.Hosting.Coolify;

/// <summary>
/// FT-009: the containerisability filter. A single, deterministic pass that walks the Aspire
/// resource graph once at the configure/build boundary and partitions every resource into
/// either <em>containerisable</em> (passes through to FT-003 / FT-004 / FT-005) or
/// <em>non-containerisable</em> with one of the four fixed reason categories
/// (<c>parameter</c>, <c>azure-native</c>, <c>dev-only</c>, <c>unknown</c>).
/// </summary>
/// <remarks>
/// The classifier is total (I-1), the filter is stable (I-2), the reason vocabulary is the
/// four fixed literals only (I-3), every skip line matches a uniform regex (I-4), and the
/// pass never fails the deploy (I-6). FT-009 does not call Coolify (I-8), does not write to
/// disk, and is purely local.
/// </remarks>
public static class ContainerisabilityFilter
{
    /// <summary>
    /// The fixed v1 reason vocabulary (I-3). Adding a category requires a feature amendment.
    /// </summary>
    public enum Reason
    {
        Parameter,
        AzureNative,
        DevOnly,
        Unknown,
    }

    public static string ReasonLiteral(Reason r) => r switch
    {
        Reason.Parameter => "parameter",
        Reason.AzureNative => "azure-native",
        Reason.DevOnly => "dev-only",
        Reason.Unknown => "unknown",
        _ => "unknown",
    };

    /// <summary>
    /// Outcome of classifying a single resource. Either <see cref="Containerisable"/> (the
    /// resource passes through to FT-003 / FT-004 / FT-005) or non-containerisable with one
    /// of the four fixed <see cref="Reason"/> values.
    /// </summary>
    public readonly record struct Classification(bool Containerisable, Reason Reason);

    /// <summary>
    /// Apply the v1 first-match rules to <paramref name="resource"/>. The classifier is total:
    /// every resource produces exactly one outcome.
    /// </summary>
    public static Classification Classify(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Rule 1 (highest priority): container / project resource passthrough. Wins over
        // every non-containerisable rule, including a synthetic resource that ALSO carries
        // an Azure-resource annotation (the "first-match" property — assertion L).
        if (resource is ContainerResource || resource is ProjectResource || HasContainerImageAnnotation(resource))
        {
            return new Classification(true, default);
        }

        // Rule 2: ParameterResource → parameter.
        if (resource is ParameterResource)
        {
            return new Classification(false, Reason.Parameter);
        }

        // Rule 3: Azure-native — CLR type namespace or annotation namespace begins with
        // Aspire.Hosting.Azure. Detection is namespace-based, not type-allowlist, so any
        // Azure SDK resource Aspire ships in v1 is picked up automatically.
        if (IsAzureNative(resource))
        {
            return new Classification(false, Reason.AzureNative);
        }

        // Rule 4: dev-only — annotation-based per I-10. Detects Aspire's
        // EmulatorResourceAnnotation, any annotation whose name contains "Emulator" or
        // "RunMode", future emulator annotations are picked up without amending FT-009.
        if (IsDevOnly(resource))
        {
            return new Classification(false, Reason.DevOnly);
        }

        // Rule 5: catch-all — keeps the classifier total.
        return new Classification(false, Reason.Unknown);
    }

    /// <summary>
    /// Run the filter pass over <paramref name="resources"/>. Logs one warning line per
    /// skipped resource in the uniform shape <c>skipped: &lt;name&gt; (reason: &lt;category&gt;)</c>
    /// (I-4) and one structured info-level filter-summary line at the end. Cancellation is
    /// honoured at the per-resource boundary (§Behaviour §2.i).
    /// </summary>
    public static FilterSummary Run(
        IEnumerable<IResource> resources,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        var containerisable = new List<IResource>();
        var skipLines = new List<string>();
        int parameter = 0, azureNative = 0, devOnly = 0, unknown = 0;
        int walked = 0;

        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            walked++;

            var classification = Classify(resource);
            if (classification.Containerisable)
            {
                containerisable.Add(resource);
                continue;
            }

            switch (classification.Reason)
            {
                case Reason.Parameter: parameter++; break;
                case Reason.AzureNative: azureNative++; break;
                case Reason.DevOnly: devOnly++; break;
                default: unknown++; break;
            }

            var line = $"skipped: {resource.Name} (reason: {ReasonLiteral(classification.Reason)})";
            skipLines.Add(line);
            // Uniform warn-level skip line. The literal text matches I-4 verbatim; structured
            // {Resource}/{Reason} args let log sinks pivot without re-parsing.
            logger.LogWarning("skipped: {Resource} (reason: {Reason})",
                resource.Name, ReasonLiteral(classification.Reason));
        }

        var summary = new FilterSummary(
            Walked: walked,
            Containerisable: containerisable,
            Parameter: parameter,
            AzureNative: azureNative,
            DevOnly: devOnly,
            Unknown: unknown,
            SkipLines: skipLines);

        // Structured filter-summary entry. The five named counts let assertions E and F pin
        // exact-tally and idempotency without log-line parsing.
        logger.LogInformation(
            "filter-summary: walked={Walked} containerisable={Containerisable} parameter={Parameter} azure-native={AzureNative} dev-only={DevOnly} unknown={Unknown}",
            walked, containerisable.Count, parameter, azureNative, devOnly, unknown);

        return summary;
    }

    private static bool HasContainerImageAnnotation(IResource resource)
    {
        foreach (var annotation in resource.Annotations)
        {
            var name = annotation.GetType().Name;
            if (name.Equals("ContainerImageAnnotation", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAzureNative(IResource resource)
    {
        var ns = resource.GetType().Namespace;
        if (ns is not null && ns.StartsWith("Aspire.Hosting.Azure", StringComparison.Ordinal))
        {
            return true;
        }
        foreach (var annotation in resource.Annotations)
        {
            var annNs = annotation.GetType().Namespace;
            if (annNs is not null && annNs.StartsWith("Aspire.Hosting.Azure", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDevOnly(IResource resource)
    {
        foreach (var annotation in resource.Annotations)
        {
            var typeName = annotation.GetType().Name;
            if (typeName.Contains("Emulator", StringComparison.Ordinal)
                || typeName.Contains("RunMode", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Result of one FT-009 filter pass. The <see cref="Containerisable"/> list is the canonical
/// filtered enumeration FT-003 / FT-004 / FT-005 read verbatim (I-7); the counts feed the
/// structured filter-summary log entry and the idempotency assertion (I-5).
/// </summary>
public sealed record FilterSummary(
    int Walked,
    IReadOnlyList<IResource> Containerisable,
    int Parameter,
    int AzureNative,
    int DevOnly,
    int Unknown,
    IReadOnlyList<string> SkipLines);
