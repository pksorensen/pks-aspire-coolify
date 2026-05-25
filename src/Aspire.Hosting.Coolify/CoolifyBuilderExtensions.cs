using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Aspire.Hosting.Coolify.Http;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
// Implements ADR-007: Adopt Aspire native ContainerRegistry primitives in the publisher's push-target read path (v1)
// FT-001 §B: extension methods live in the Aspire.Hosting namespace (NOT
// Aspire.Hosting.Coolify) so consumers — whose AppHost projects already import
// Aspire.Hosting via implicit usings — get WithCoolifyDeploy / WithImageRegistry /
// WithCoolifyDestination / WithVerifyPolling / WithManagedDashboard with no extra
// using directive. Matches the Aspire.Hosting.Redis -> WithRedis() pattern.
namespace Aspire.Hosting;

/// <summary>
/// Public surface for the Coolify hosting extension. The signature is fixed by ADR-004:
/// <c>WithCoolifyDeploy(url, token)</c> takes two required
/// <see cref="IResourceBuilder{ParameterResource}"/> handles, in that order, no overloads.
/// </summary>
public static class CoolifyBuilderExtensions
{
    // Idempotency map (I-4): one publisher per builder instance; second call wins-no-op.
    // ConditionalWeakTable keeps registrations tied to the builder lifetime without leaking.
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CoolifyDeployingPublisher> s_registry = new();

    /// <summary>
    /// Registers the Coolify deploying publisher on this Aspire AppHost and wires the five
    /// fixed phases (<c>configure → build → push → deploy → verify</c>) into the deploy pipeline.
    /// </summary>
    /// <param name="builder">The Aspire distributed application builder.</param>
    /// <param name="url">An Aspire parameter resource holding the Coolify base URL.</param>
    /// <param name="token">An Aspire secret parameter resource holding the Coolify bearer token.
    /// The skeleton never reads the underlying value (I-7).</param>
    /// <returns>The same <paramref name="builder"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public static IDistributedApplicationBuilder WithCoolifyDeploy(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> url,
        IResourceBuilder<ParameterResource> token)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(token);

        // I-4: first call wins. Second and subsequent calls (with any (url, token) pair) no-op.
        if (s_registry.TryGetValue(builder, out _))
        {
            return builder;
        }

        var publisher = new CoolifyDeployingPublisher(url, token)
        {
            // FT-013 §"DI wiring": once WithCoolifyDeploy has run, the publisher resolves the
            // live HTTP-backed client instead of the NotConfiguredCoolifyClientFactory stub.
            // Tests still substitute their own ICoolifyClientFactory by re-assigning
            // publisher.ClientFactory after registration (last-call-wins).
            ClientFactory = new HttpCoolifyClientFactory(),
            // FT-009 wiring: capture the builder so the containerisability filter (run at end
            // of configure) can enumerate the AppHost's actual resource graph. Without this
            // the filter returns null, LastFilterSummary stays unset, and the deploy phase
            // iterates zero resources — Coolify gets a project + env created and nothing
            // else. Resolved lazily at filter-time so resources added after WithCoolifyDeploy()
            // are still included.
            AllResourcesProvider = () => builder.Resources,
        };
        s_registry.Add(builder, publisher);

        // Register publisher in DI so phase bodies / tests can resolve it.
        builder.Services.AddSingleton(publisher);
        // FT-013 §"DI wiring": expose the live factory through the DI container as well, so
        // anything resolving ICoolifyClientFactory after WithCoolifyDeploy sees the real one
        // (I-9: stub supersession is total).
        builder.Services.AddSingleton<ICoolifyClientFactory>(publisher.ClientFactory);

        // Wire the five named phase steps into Aspire's deploy pipeline, chained in fixed order
        // per ADR-003 §1, §7. The last step is required by the well-known Deploy aggregator so
        // `aspire deploy` discovers and runs the chain.
        var pipeline = builder.Pipeline;
        string? previousStep = null;
        foreach (CoolifyPhase phase in Enum.GetValues<CoolifyPhase>())
        {
            var stepName = phase.StepName();
            var current = phase;
            object? requiredBy = current == CoolifyPhase.Verify ? WellKnownPipelineSteps.Deploy : null;

            // FT-012: every coolify-* step must wait for Aspire's built-in
            // process-parameters step to resolve unset AddParameter values (it does
            // the interactive prompting). DeployPrereq depends on ProcessParameters
            // in Aspire 13.1+, so depending on DeployPrereq is the canonical way to
            // pull the prompt step in. The first coolify-* step (configure) needs
            // this explicitly; later steps inherit it transitively via previousStep,
            // but listing it on every step is defensive against re-ordering.
            object dependsOn = previousStep is null
                ? new[] { WellKnownPipelineSteps.DeployPrereq }
                : new[] { previousStep, WellKnownPipelineSteps.DeployPrereq };

            pipeline.AddStep(
                name: stepName,
                action: ctx => publisher.RunPhaseAsync(current, ctx),
                dependsOn: dependsOn,
                requiredBy: requiredBy);

            previousStep = stepName;
        }

        return builder;
    }

    /// <summary>
    /// Deprecated v1.x source-compat shim per ADR-007. Synthesises a
    /// <see cref="ContainerRegistryResource"/> via
    /// <see cref="ContainerRegistryResourceBuilderExtensions.AddContainerRegistry(IDistributedApplicationBuilder,string,IResourceBuilder{ParameterResource},IResourceBuilder{ParameterResource})"/>
    /// from the supplied prefix parameter, attaches optional credentials, and registers it as
    /// the implicit fallback registry that the FT-014 publisher read path uses for every
    /// containerisable workload that has no explicit <c>WithContainerRegistry(...)</c> edge.
    /// Prefer <c>AddContainerRegistry(...)</c> + <c>WithContainerRegistry(...)</c> directly
    /// for new AppHosts.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or
    /// <paramref name="prefix"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when exactly one of <paramref name="username"/>
    /// / <paramref name="password"/> is non-null (they travel as a pair per ADR-005 §1).</exception>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="WithCoolifyDeploy"/> has not been called first on <paramref name="builder"/>.</exception>
    [Obsolete("Use AddContainerRegistry + WithContainerRegistry — see ADR-007.", error: false)]
    public static IDistributedApplicationBuilder WithImageRegistry(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> prefix,
        IResourceBuilder<ParameterResource>? username = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(prefix);

        if (username is null && password is not null)
        {
            throw new ArgumentException(
                "username must be provided when password is provided; credentials travel as a pair (ADR-005 §1).",
                nameof(username));
        }
        if (password is null && username is not null)
        {
            throw new ArgumentException(
                "password must be provided when username is provided; credentials travel as a pair (ADR-005 §1).",
                nameof(password));
        }

        var publisher = builder.GetRegisteredCoolifyPublisher()
            ?? throw new InvalidOperationException(
                "WithImageRegistry(...) requires a prior WithCoolifyDeploy(...) call on the same builder.");

        // ADR-007 §Decision §4.1: deterministic synthetic name so repeated calls with the
        // same prefix converge on the same resource (FT-014 I-8).
        var syntheticName = "coolify-legacy-" + StableHash(prefix.Resource.Name);

        IResourceBuilder<ContainerRegistryResource> registryBuilder;
        if (publisher.TryGetShimRegistry(syntheticName, out var existing))
        {
            registryBuilder = existing!;
        }
        else
        {
            registryBuilder = builder.AddContainerRegistry(syntheticName, prefix);
            publisher.RegisterShimRegistry(syntheticName, registryBuilder);
        }

        // Attach credentials annotation when both supplied. Repeated calls overwrite per
        // FT-003 I-8 last-call-wins semantics.
        if (username is not null && password is not null)
        {
            registryBuilder.WithAnnotation(
                new CoolifyRegistryCredentialsAnnotation(username, password),
                ResourceAnnotationMutationBehavior.Replace);
        }

        // FT-014 §0 step 4: the latest shim call provides the implicit default for workloads
        // without an explicit edge. Last-call-wins matches FT-003 I-8.
        publisher.ShimDefaultRegistry = registryBuilder.Resource;

        return builder;
    }

    /// <summary>
    /// Stable, deterministic, ASCII-safe 16-hex-character hash for use in synthesised
    /// resource names. Independent of <see cref="object.GetHashCode"/> (which is
    /// process-local) so the same prefix produces the same synthetic name across builds.
    /// </summary>
    private static string StableHash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Registers the Coolify destination handle the deploy phase resolves at step 1 (FT-005 §0).
    /// Fixed signature per ADR-001 D4 / FT-005 §0: <paramref name="name"/> is a required
    /// <see cref="IResourceBuilder{ParameterResource}"/>; the value is resolved exactly once at
    /// the top of the deploy phase. Calling this method twice on the same builder is
    /// **last-call-wins** (FT-005 §0), unlike <see cref="WithCoolifyDeploy"/> which is
    /// first-call-wins.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="WithCoolifyDeploy"/> has not been called first on <paramref name="builder"/>.</exception>
    public static IDistributedApplicationBuilder WithCoolifyDestination(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var publisher = builder.GetRegisteredCoolifyPublisher()
            ?? throw new InvalidOperationException(
                "WithCoolifyDestination(...) requires a prior WithCoolifyDeploy(...) call on the same builder.");

        // FT-005 §0: last-call-wins. Setting the handle clears any literal previously set
        // by the string overload so resolution prefers exactly one source.
        publisher.DestinationName = name;
        publisher.DestinationLiteralName = null;
        return builder;
    }

    /// <summary>
    /// String overload of <see cref="WithCoolifyDestination(IDistributedApplicationBuilder, IResourceBuilder{ParameterResource})"/>
    /// per FT-005 §0 amendment. Destination names rarely vary per environment and are
    /// never secret, so the literal-string call site is the smoother default for the
    /// homelab single-Coolify case. Last-call-wins across both overloads: the literal
    /// supersedes any previously-set handle and vice versa, so the deploy phase
    /// resolves exactly one source.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null,
    /// empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="WithCoolifyDeploy"/> has not been called first on <paramref name="builder"/>.</exception>
    public static IDistributedApplicationBuilder WithCoolifyDestination(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var publisher = builder.GetRegisteredCoolifyPublisher()
            ?? throw new InvalidOperationException(
                "WithCoolifyDestination(...) requires a prior WithCoolifyDeploy(...) call on the same builder.");

        publisher.DestinationLiteralName = name;
        publisher.DestinationName = null;
        return builder;
    }

    /// <summary>
    /// Registers the verify-phase polling configuration (FT-006 §0). Both arguments are
    /// optional; calling the method zero times leaves the v1 defaults in place (5s initial
    /// interval, 10min total timeout). Calling it twice is **last-call-wins**.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null,
    /// or when an explicitly-passed argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either <paramref name="interval"/>
    /// or <paramref name="timeout"/> is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="WithCoolifyDeploy"/> has not been called first on <paramref name="builder"/>.</exception>
    public static IDistributedApplicationBuilder WithVerifyPolling(
        this IDistributedApplicationBuilder builder,
        TimeSpan? interval,
        TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (interval is null)
        {
            throw new ArgumentNullException(nameof(interval));
        }
        if (timeout is null)
        {
            throw new ArgumentNullException(nameof(timeout));
        }
        if (interval.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval),
                "interval must be a positive TimeSpan (FT-006 §0).");
        }
        if (timeout.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout),
                "timeout must be a positive TimeSpan (FT-006 §0).");
        }

        var publisher = builder.GetRegisteredCoolifyPublisher()
            ?? throw new InvalidOperationException(
                "WithVerifyPolling(...) requires a prior WithCoolifyDeploy(...) call on the same builder.");

        // FT-006 §0: last-call-wins.
        publisher.VerifyInterval = interval.Value;
        publisher.VerifyTimeout = timeout.Value;
        return builder;
    }

    /// <summary>
    /// Opts in to the managed Aspire dashboard sub-phase (FT-010). When called, the deploy
    /// phase upserts a <c>coolify-aspiredashboard</c> Coolify application alongside the
    /// workload services inside the same project + targeted environment, wires the three
    /// required env-vars (<c>COOLIFY_API_URL</c>, <c>COOLIFY_API_TOKEN</c>,
    /// <c>COOLIFY_PROJECT_UUID</c>), triggers its own deploy action, and surfaces the
    /// Coolify-assigned FQDN to the developer.
    /// <para>
    /// The dashboard sub-phase is observability, not workload contract: every failure is a
    /// <c>W_…</c> warning that leaves the workload deploy exit code unchanged (I-1).
    /// </para>
    /// <para>
    /// Calling this method twice is <b>last-call-wins</b> on the handle; the opt-in flag
    /// stays true (I-5). Calling it zero times leaves the flag false and the dashboard
    /// sub-phase silently skips (I-6).
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="WithCoolifyDeploy"/> has not been called first on <paramref name="builder"/>.</exception>
    public static IDistributedApplicationBuilder WithManagedDashboard(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> dashboardToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dashboardToken);

        var publisher = builder.GetRegisteredCoolifyPublisher()
            ?? throw new InvalidOperationException(
                "WithManagedDashboard(...) requires a prior WithCoolifyDeploy(...) call on the same builder.");

        // FT-010 I-5: last-call-wins on the handle; the opt-in flag is sticky once set.
        publisher.DashboardToken = dashboardToken;
        publisher.DashboardOptedIn = true;
        return builder;
    }

    // Test seam: surface the publisher registered for a given builder, if any.
    internal static CoolifyDeployingPublisher? GetRegisteredCoolifyPublisher(
        this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return s_registry.TryGetValue(builder, out var p) ? p : null;
    }
}
