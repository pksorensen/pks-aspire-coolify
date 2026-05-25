using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
// Implements ADR-005: Image registry strategy — explicit publisher-push to a developer-chosen registry (v1)
// Implements ADR-007: Adopt Aspire native ContainerRegistry primitives in the publisher's push-target read path (v1)
// Implements FT-012: Interactive parameter prompting in the configure phase (amends ADR-004 §5a)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The Coolify deploying publisher. Owns the (url, token) Aspire parameter handles for a single
/// <see cref="CoolifyBuilderExtensions.WithCoolifyDeploy"/> registration, exposes the five
/// fixed phase shells (<c>configure → build → push → deploy → verify</c>) per ADR-003, and —
/// from FT-002 — implements the configure-phase body: parameter resolution + combined
/// version + auth probe, with fail-fast diagnostics on every failure path (FT-002 §Behaviour).
/// </summary>
public sealed class CoolifyDeployingPublisher
{
    public CoolifyDeployingPublisher(
        IResourceBuilder<ParameterResource> url,
        IResourceBuilder<ParameterResource> token)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Token = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>Coolify base URL parameter handle (non-secret per ADR-004 §8).</summary>
    public IResourceBuilder<ParameterResource> Url { get; }

    /// <summary>Coolify bearer token parameter handle (declared <c>secret: true</c> per ADR-004 §1).</summary>
    public IResourceBuilder<ParameterResource> Token { get; }

    /// <summary>
    /// Factory that produces the <see cref="ICoolifyClient"/> used by the configure-phase probe
    /// (and by subsequent phases). Defaults to a stub that always reports the API client as
    /// unconfigured; tests and real wiring override this.
    /// </summary>
    public ICoolifyClientFactory ClientFactory { get; set; } = new NotConfiguredCoolifyClientFactory();

    /// <summary>
    /// Sink for fail-fast diagnostics (FT-002 §Outputs: "single diagnostic to stderr"). Defaults
    /// to <see cref="Console.Error"/>; tests inject a <see cref="StringWriter"/>.
    /// </summary>
    public TextWriter ErrorWriter { get; set; } = Console.Error;

    /// <summary>
    /// Per-deploy interactivity + prompt surface (FT-012). When a parameter handle resolves to
    /// an empty value, the publisher calls <see cref="IParameterPrompter.PromptAsync"/> iff
    /// <see cref="IParameterPrompter.IsInteractive"/> is true; otherwise the matching
    /// <c>E_…</c> fail-fast symbol fires verbatim (I-1). Defaults to the CI-safe
    /// <see cref="NonInteractivePrompter"/>; host wiring (or tests) substitutes a real
    /// Aspire-bound prompter.
    /// </summary>
    public IParameterPrompter Prompter { get; set; } = NonInteractivePrompter.Instance;

    // FT-012 I-3 cache: at most one prompt per parameter per deploy invocation. Keyed by
    // parameter name (the only stable handle across phases). A value of "" means "resolved to
    // empty and confirmed unset" — the matching E_… symbol is appropriate.
    private readonly Dictionary<string, string?> _resolvedParameterValues =
        new(StringComparer.Ordinal);

    /// <summary>
    /// FT-012 helper: resolve a parameter handle through Aspire's standard mechanism; if the
    /// value is null or empty AND <see cref="IParameterPrompter.IsInteractive"/> is true,
    /// request the canonical Aspire parameter prompt and consume its reply. Caches the result
    /// per parameter name for the duration of the deploy invocation (I-3).
    /// </summary>
    /// <returns>The resolved (or prompted) value, or <c>null</c> when the parameter remains
    /// unset — caller emits the matching <c>E_…</c> symbol.</returns>
    /// <remarks>
    /// FT-012 §"Error handling": a faulting prompt subsystem is treated as non-interactive
    /// (return null → matching E_…), so the CI contract is preserved over the
    /// degenerate-host case. <see cref="OperationCanceledException"/> propagates verbatim so
    /// the FT-001 cancellation diagnostic fires instead of an <c>E_…</c> symbol.
    /// </remarks>
    internal async Task<string?> ResolveOrPromptAsync(
        IResourceBuilder<ParameterResource> handle,
        bool isSecret,
        CancellationToken cancellationToken)
    {
        var name = handle.Resource.Name;

        // FT-012 I-3: re-use the value resolved earlier in this deploy invocation.
        if (_resolvedParameterValues.TryGetValue(name, out var cached))
        {
            return cached;
        }

        string? value = null;
        try
        {
            value = await handle.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Aspire parameter-resolution failure is "unset" — fall through to the
            // interactive branch (I-4) or to the matching E_… on the non-interactive branch.
            value = null;
        }

        value = value?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            _resolvedParameterValues[name] = value;
            return value;
        }

        // FT-012 I-4: prompt branch fires only when interactivity is true AND value is empty.
        var prompter = Prompter;
        if (!prompter.IsInteractive)
        {
            _resolvedParameterValues[name] = null;
            return null;
        }

        string? reply;
        try
        {
            reply = await prompter
                .PromptAsync(new ParameterPromptRequest(name, isSecret), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates per FT-012 §Behaviour: cancellation is the user
            // actively aborting, not "value is absent" — FT-001 cancellation diagnostic
            // fires, not an E_… symbol.
            throw;
        }
        catch
        {
            // Aspire prompt subsystem itself faulted — treat as non-interactive (CI-safe
            // default per FT-012 §"Error handling").
            _resolvedParameterValues[name] = null;
            return null;
        }

        reply = reply?.Trim();
        if (string.IsNullOrEmpty(reply))
        {
            // Empty reply: per FT-012 §Behaviour "user pressed Enter on a blank prompt" →
            // treat as unset → matching E_… symbol.
            _resolvedParameterValues[name] = null;
            return null;
        }

        _resolvedParameterValues[name] = reply;
        return reply;
    }

    /// <summary>
    /// On a successful configure phase, the <see cref="ICoolifyClient"/> handed to subsequent
    /// phases (FT-002 §Behaviour step 6, §State: "single owner of the bearer header"). Null
    /// until configure succeeds.
    /// </summary>
    public ICoolifyClient? ResolvedClient { get; private set; }

    /// <summary>Coolify version observed by the configure-phase probe; null until success.</summary>
    public string? ObservedVersion { get; private set; }

    /// <summary>Most recently emitted fail-fast diagnostic (null on success).</summary>
    public ConfigureDiagnostic? LastDiagnostic { get; private set; }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-014 — registry edge state. The publisher reads the push target from the
    // Aspire resource graph via ContainerRegistryReferenceAnnotation per ADR-007.
    // The deprecated WithImageRegistry(...) shim contributes by registering a
    // synthetic ContainerRegistryResource as the implicit fallback for workloads
    // without an explicit edge.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The synthetic registry resource registered by the most recent
    /// <c>WithImageRegistry(...)</c> shim call, used as the implicit fallback for
    /// containerisable workloads that have no explicit
    /// <see cref="ContainerRegistryResourceBuilderExtensions.WithContainerRegistry{TDestination,TContainerRegistry}"/>
    /// edge (FT-014 §0 step 4). Null when the shim was never called.
    /// </summary>
    public ContainerRegistryResource? ShimDefaultRegistry { get; internal set; }

    // Map of syntheticName → registry-builder, used by the shim to converge repeated calls
    // with the same prefix on a single resource (FT-014 I-8, ADR-007 §Decision §4.1).
    private readonly Dictionary<string, IResourceBuilder<ContainerRegistryResource>> _shimRegistries =
        new(StringComparer.Ordinal);

    internal bool TryGetShimRegistry(string syntheticName, out IResourceBuilder<ContainerRegistryResource>? registry)
    {
        if (_shimRegistries.TryGetValue(syntheticName, out var found))
        {
            registry = found;
            return true;
        }
        registry = null;
        return false;
    }

    internal void RegisterShimRegistry(string syntheticName, IResourceBuilder<ContainerRegistryResource> registry)
    {
        _shimRegistries[syntheticName] = registry;
    }

    /// <summary>Aspire image build pipeline binding. Defaults to a stub that throws; tests / host wire the real one.</summary>
    public IImageBuildPipeline ImagePipeline { get; set; } = new UnconfiguredImageBuildPipeline();

    /// <summary>Optional defensive verification surface (FT-003 §"Error handling").</summary>
    public ILocalImageStore? ImageStore { get; set; }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-009 — containerisability filter state.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Source of the full Aspire resource set FT-009 walks. The filter pass runs once at the
    /// configure/build boundary (after FT-002's auth probe / FT-004's registry upsert succeed,
    /// before FT-003 begins). When unset, the filter does not run automatically — tests / host
    /// wiring drive the filter explicitly via <see cref="ContainerisabilityFilter.Run"/>.
    /// </summary>
    public Func<IEnumerable<IResource>>? AllResourcesProvider { get; set; }

    /// <summary>
    /// Result of the most recent FT-009 filter pass. Null until <see cref="RunContainerisabilityFilterAsync"/>
    /// has been called at least once. Read by FT-003 / FT-004 / FT-005 as the single source of
    /// truth (I-7) when their per-phase resource delegates are unset.
    /// </summary>
    public FilterSummary? LastFilterSummary { get; private set; }

    /// <summary>
    /// The canonical filtered enumeration published by the most recent filter pass. Empty
    /// until <see cref="RunContainerisabilityFilterAsync"/> has been called.
    /// </summary>
    public IReadOnlyList<IResource> ContainerisableResources
        => LastFilterSummary?.Containerisable ?? Array.Empty<IResource>();

    /// <summary>
    /// Source of the resource set to iterate in the build phase. The build phase is
    /// agnostic to the graph walker that produces the set — FT-003 simply iterates whatever
    /// it receives (§3, "the publisher assumes every resource in this set is containerisable").
    /// </summary>
    public Func<IEnumerable<IResource>>? ResourcesToBuild { get; set; }

    /// <summary>
    /// AppHost info delegate. Returns (assembly simple name, informational version). Defaults
    /// to reading <see cref="Assembly.GetEntryAssembly"/> exactly once per build phase (I-3).
    /// Tests inject a counted delegate to assert the "exactly once" invariant.
    /// </summary>
    public Func<(string AssemblyName, string? Version)> AppHostInfoProvider { get; set; } =
        DefaultAppHostInfoProvider;

    private static (string AssemblyName, string? Version) DefaultAppHostInfoProvider()
    {
        var asm = Assembly.GetEntryAssembly();
        var name = asm?.GetName().Name ?? "(unknown-apphost)";
        var version = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (name, version);
    }

    /// <summary>Most recent build-phase diagnostic (null on success).</summary>
    public BuildDiagnostic? LastBuildDiagnostic { get; private set; }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-016 — prereq phase state for in-project pks-agent-registry resources.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Default-implementation reachability probe (HEAD /v2/ → fallback GET /v2/). Tests
    /// inject a deterministic fake.
    /// </summary>
    public IRegistryReachabilityProbe ReachabilityProbe { get; set; } =
        DefaultRegistryReachabilityProbe.Instance;

    /// <summary>Most recent prereq-phase diagnostic (null on success / no-op).</summary>
    public PrereqDiagnostic? LastPrereqDiagnostic { get; private set; }

    /// <summary>
    /// Per-registry state collected during the prereq phase. Keyed by the
    /// <see cref="ContainerRegistryResource.Name"/> of the registry-target resource.
    /// Bounded by the AppHost process lifetime (FT-016 §State / I-10).
    /// </summary>
    internal readonly Dictionary<string, PksAgentRegistryState> _prereqState =
        new(StringComparer.Ordinal);

    /// <summary>Public read-only accessor for tests / downstream phases.</summary>
    public IReadOnlyDictionary<string, PksAgentRegistryState> PrereqStateByRegistry => _prereqState;

    /// <summary>
    /// Workload-name → in-project-registry-UUID attachment map (FT-016 §"Behaviour §5").
    /// Populated by the prereq phase; consumed by the deploy phase when emitting workload
    /// Application bodies (I-5).
    /// </summary>
    internal readonly Dictionary<string, string> _workloadPrivateRegistryAttachment =
        new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> WorkloadPrivateRegistryAttachment
        => _workloadPrivateRegistryAttachment;

    /// <summary>Number of in-project registries visited by the most recent prereq run (TC-032 / TC-034).</summary>
    public int LastPrereqVisitedCount { get; private set; }

    /// <summary>
    /// Source of in-project pks-agent-registry resources the prereq phase walks. Populated
    /// by <c>WithCoolifyDeploy</c> from the per-builder lookup that
    /// <see cref="PksAgentRegistryExtensions.AddPksAgentRegistry"/> maintains. Tests
    /// override directly.
    /// </summary>
    public Func<IEnumerable<ContainerRegistryResource>>? PksAgentRegistryProvider { get; set; }


    // ──────────────────────────────────────────────────────────────────────────
    // FT-004 — push phase state.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Aspire image push pipeline binding. Defaults to a stub that throws.</summary>
    public IImagePushPipeline ImagePushPipeline { get; set; } = new UnconfiguredImagePushPipeline();

    /// <summary>
    /// Source of the resource set to iterate in the push phase. FT-004 §Push §1: push
    /// receives the same resource set the build phase iterated; v1 reuses
    /// <see cref="ResourcesToBuild"/> when this is unset.
    /// </summary>
    public Func<IEnumerable<IResource>>? ResourcesToPush { get; set; }

    /// <summary>Most recent push-phase / registry-upsert diagnostic (null on success).</summary>
    public PushDiagnostic? LastPushDiagnostic { get; private set; }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-005 — deploy phase state.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Destination-name parameter handle (FT-005 §0). Captured by
    /// <see cref="CoolifyBuilderExtensions.WithCoolifyDestination"/> at registration time and
    /// resolved exactly once at the top of the deploy phase. <c>null</c> until the extension
    /// is called; an unset handle causes the deploy phase to fail with
    /// <c>E_COOLIFY_DESTINATION_UPSERT_FAILED</c> at step 1.
    /// </summary>
    public IResourceBuilder<ParameterResource>? DestinationName { get; internal set; }

    /// <summary>
    /// Literal destination name (FT-005 §0 string-overload amendment). When non-null this
    /// supersedes <see cref="DestinationName"/> in the deploy-phase resolution. Set by the
    /// <c>WithCoolifyDestination(string)</c> overload; the handle overload clears it.
    /// </summary>
    public string? DestinationLiteralName { get; internal set; }

    /// <summary>
    /// Source of the resource set FT-005 iterates in step 5. Defaults to
    /// <see cref="ResourcesToBuild"/> (same set as build / push, per FT-005 §Inputs).
    /// </summary>
    public Func<IEnumerable<IResource>>? ResourcesToDeploy { get; set; }

    /// <summary>
    /// Active Aspire environment name (e.g. <c>Production</c>) — the only environment
    /// FT-005 materialises on the Coolify side per ADR-001 D2. Defaults to
    /// <c>"Production"</c>; host wiring overrides per <c>aspire deploy --environment</c>.
    /// </summary>
    public Func<string> ActiveEnvironmentProvider { get; set; } = () => "Production";

    /// <summary>
    /// FT-007 (env-var sync) hook. Invoked between service upsert success and the per-service
    /// deploy-action trigger (FT-005 I-12). Null by default; FT-007 wires it.
    /// </summary>
    public ICoolifyDeployHook? EnvVarHook { get; set; }

    /// <summary>
    /// FT-008 (reference wiring) hook. Invoked after <see cref="EnvVarHook"/> and before the
    /// trigger, per FT-008 I-1. Null by default; FT-008 wires it.
    /// </summary>
    public ICoolifyDeployHook? ReferenceHook { get; set; }

    /// <summary>Most recent deploy-phase diagnostic (null on success).</summary>
    public DeployDiagnostic? LastDeployDiagnostic { get; private set; }

    /// <summary>
    /// Per-service deploy-action handles collected by the most recent successful deploy phase.
    /// Handed to <c>verify</c> (FT-006) on phase exit; not persisted (FT-005 §State).
    /// </summary>
    public IReadOnlyList<DeployActionHandle> LastDeployHandles { get; private set; }
        = Array.Empty<DeployActionHandle>();

    /// <summary>
    /// Per-resource image tags recorded by the most recent successful build phase. FT-004 I-5:
    /// push reads what build emitted rather than recomputing, so build/push drift is impossible
    /// by construction.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastBuildTagsByResource => _lastBuildTagsByResource;
    private readonly Dictionary<string, string> _lastBuildTagsByResource = new(StringComparer.Ordinal);

    // ──────────────────────────────────────────────────────────────────────────
    // FT-006 — verify phase state.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Initial per-handle poll interval (FT-006 §0). Default: 5s.</summary>
    public TimeSpan VerifyInterval { get; internal set; } = TimeSpan.FromSeconds(5);

    /// <summary>Overall phase-level wall-clock timeout (FT-006 §0). Default: 10min.</summary>
    public TimeSpan VerifyTimeout { get; internal set; } = TimeSpan.FromMinutes(10);

    /// <summary>Hard per-poll cap invariant under <c>WithVerifyPolling</c> (FT-006 I-9). 60s.</summary>
    private static readonly TimeSpan VerifyPollCap = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sleep delegate used inside the verify loop. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Tests inject a recorder/no-op pairing with <see cref="VerifyElapsedProvider"/>.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> VerifySleeper { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>
    /// Elapsed-since-verify-enter accessor. <c>null</c> uses an internal <see cref="System.Diagnostics.Stopwatch"/>;
    /// tests inject a virtual clock that advances on <see cref="VerifySleeper"/> calls.
    /// </summary>
    public Func<TimeSpan>? VerifyElapsedProvider { get; set; }

    /// <summary>Most recent verify-phase diagnostic (null on success).</summary>
    public VerifyDiagnostic? LastVerifyDiagnostic { get; private set; }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-010 — managed Aspire dashboard sub-phase state. Captured by
    // WithManagedDashboard(...) at registration time and consumed at the tail of the
    // deploy phase, strictly after FT-005's per-service trigger loop succeeds.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dashboard-token parameter handle (FT-010 §0). Required when
    /// <see cref="DashboardOptedIn"/> is true; resolved at deploy time only. Distinct from
    /// <see cref="Token"/> — audience-separation per ADR-004 (I-4).
    /// </summary>
    public IResourceBuilder<ParameterResource>? DashboardToken { get; internal set; }

    /// <summary>
    /// True iff <see cref="CoolifyBuilderExtensions.WithManagedDashboard"/> was called at
    /// least once. When false, the dashboard sub-phase is a silent no-op (I-6).
    /// </summary>
    public bool DashboardOptedIn { get; internal set; }

    /// <summary>
    /// Publisher-defined fixed service name FT-010 upserts inside the targeted Coolify
    /// environment. Intentionally distinct from any workload resource name.
    /// </summary>
    public const string DashboardServiceName = "coolify-aspiredashboard";

    /// <summary>
    /// Publisher-pinned dashboard image tag (FT-010 I-8). No code path may read this from a
    /// parameter, env-var, config file, or any other runtime source — it changes only via a
    /// publisher release.
    /// </summary>
    public const string DashboardImageTag = "ghcr.io/pksorensen/coolify-aspiredashboard:0.1.0";

    /// <summary>Most recent dashboard-sub-phase warning diagnostic (null when sub-phase succeeded or was skipped).</summary>
    public DashboardDiagnostic? LastDashboardDiagnostic { get; private set; }

    /// <summary>
    /// FQDN observed on the dashboard service after a successful upsert+trigger. Null when
    /// the sub-phase did not run, when Coolify has not yet assigned a domain, or when the
    /// upsert failed before FQDN could be read.
    /// </summary>
    public string? LastDashboardFqdn { get; private set; }

    internal async Task RunPhaseAsync(CoolifyPhase phase, PipelineStepContext context)
    {
        var ct = context.CancellationToken;
        var log = context.Logger;
        var name = phase.PhaseName();

        // Plumb IServiceProvider into the default image-build/push pipelines so they can
        // resolve Aspire's IResourceContainerImageManager (docs:
        // /dotnet/api/aspire.hosting.publishing.iresourcecontainerimagemanager). Tests
        // that have already overridden ImagePipeline / ImagePushPipeline with mocks
        // are unaffected.
        if (ImagePipeline is UnconfiguredImageBuildPipeline buildPipeline)
        {
            buildPipeline.AspireServices = context.Services;
        }
        if (ImagePushPipeline is UnconfiguredImagePushPipeline pushPipeline)
        {
            pushPipeline.AspireServices = context.Services;
        }

        ct.ThrowIfCancellationRequested();

        log.LogInformation("coolify: {Phase}: enter", name);
        bool ok = false;
        try
        {
            switch (phase)
            {
                case CoolifyPhase.Configure:
                    var outcome = await RunConfigureAsync(ct).ConfigureAwait(false);
                    if (!outcome.Succeeded)
                    {
                        // FT-002 §Behaviour: fail-fast. Surface a phase-failing exception so the
                        // downstream `build` step never enters (I-7). The diagnostic text has
                        // already been written to ErrorWriter inside RunConfigureAsync.
                        throw new CoolifyConfigureFailedException(outcome.Diagnostic!);
                    }
                    // FT-004 §Configure: after FT-002's probe succeeds, run the registry-upsert
                    // contribution. On failure, configure does not advance to build (I-7).
                    var upsert = await RunConfigureRegistryUpsertAsync(ct).ConfigureAwait(false);
                    if (!upsert.Succeeded)
                    {
                        throw new CoolifyPushFailedException(upsert.Diagnostic!);
                    }
                    // FT-009: run the containerisability filter at the end of configure (per
                    // §"Where the pass runs"). The pass is pure-local, never fails the deploy
                    // (I-6), and publishes the filtered enumeration FT-003 / FT-004 / FT-005
                    // consume verbatim (I-7).
                    RunContainerisabilityFilter(log, ct);
                    ok = true;
                    break;

                case CoolifyPhase.Prereq:
                    var prereqOutcome = await RunPrereqAsync(ct).ConfigureAwait(false);
                    if (!prereqOutcome.Succeeded)
                    {
                        var d = prereqOutcome.Diagnostic!;
                        log.LogError("coolify: prereq: {Symbol} registry={Registry} project={Project} app={App} deploy={Deploy} fqdn={Fqdn} detail={Detail}",
                            d.Symbol, d.Registry, d.ProjectUuid, d.ApplicationUuid, d.DeployActionUuid, d.Fqdn, d.Detail);
                        throw new CoolifyPrereqFailedException(d);
                    }
                    ok = true;
                    break;

                case CoolifyPhase.Build:
                    var resources = ResourcesToBuild?.Invoke()
                        ?? (LastFilterSummary?.Containerisable as IEnumerable<IResource>)
                        ?? Array.Empty<IResource>();
                    var buildOutcome = await RunBuildAsync(resources, log, ct).ConfigureAwait(false);
                    if (!buildOutcome.Succeeded)
                    {
                        var bd = buildOutcome.Diagnostic!;
                        log.LogError("coolify: build: {Symbol} resource={Resource} detail={Detail}",
                            bd.Symbol, bd.Resource, bd.Detail);
                        throw new CoolifyBuildFailedException(bd);
                    }
                    ok = true;
                    break;

                case CoolifyPhase.Deploy:
                    var deployResources = ResourcesToDeploy?.Invoke()
                        ?? ResourcesToBuild?.Invoke()
                        ?? (LastFilterSummary?.Containerisable as IEnumerable<IResource>)
                        ?? Array.Empty<IResource>();
                    var deployOutcome = await RunDeployAsync(deployResources, log, ct).ConfigureAwait(false);
                    if (!deployOutcome.Succeeded)
                    {
                        throw new CoolifyDeployFailedException(deployOutcome.Diagnostic!);
                    }
                    ok = true;
                    break;

                case CoolifyPhase.Verify:
                    var verifyOutcome = await RunVerifyAsync(LastDeployHandles, log, ct).ConfigureAwait(false);
                    if (!verifyOutcome.Succeeded)
                    {
                        throw new CoolifyVerifyFailedException(verifyOutcome.Diagnostic!);
                    }
                    ok = true;
                    break;

                case CoolifyPhase.Push:
                    var pushResources = ResourcesToPush?.Invoke()
                        ?? ResourcesToBuild?.Invoke()
                        ?? (LastFilterSummary?.Containerisable as IEnumerable<IResource>)
                        ?? Array.Empty<IResource>();
                    var pushOutcome = await RunPushAsync(pushResources, log, ct).ConfigureAwait(false);
                    if (!pushOutcome.Succeeded)
                    {
                        throw new CoolifyPushFailedException(pushOutcome.Diagnostic!);
                    }
                    ok = true;
                    break;

                default:
                    // FT-001 skeleton: later features (FT-003+) fill these in. They remain no-ops
                    // so that successful configure → build → push → deploy → verify visibility is
                    // preserved end-to-end.
                    ok = true;
                    break;
            }
        }
        finally
        {
            log.LogInformation("coolify: {Phase}: exit ({Outcome})", name, ok ? "ok" : "failed");
        }
    }

    /// <summary>
    /// Runs FT-009's containerisability filter against <see cref="AllResourcesProvider"/>.
    /// Idempotent on graph equality (I-5): a second invocation against the same provider
    /// produces an identical <see cref="FilterSummary"/>. No I/O, no Coolify call (I-8).
    /// When <see cref="AllResourcesProvider"/> is unset, the pass is a structured no-op and
    /// <see cref="LastFilterSummary"/> is left untouched.
    /// </summary>
    public FilterSummary? RunContainerisabilityFilter(ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (AllResourcesProvider is null)
        {
            return LastFilterSummary;
        }
        var resources = AllResourcesProvider.Invoke();
        var summary = ContainerisabilityFilter.Run(resources, logger, cancellationToken);
        LastFilterSummary = summary;
        return summary;
    }

    /// <summary>
    /// Configure-phase body (FT-002 §Behaviour steps 1–6). Resolves parameters via Aspire's
    /// standard mechanism, constructs the <see cref="ICoolifyClient"/>, issues the single
    /// combined version + auth probe, and classifies the outcome into <see cref="ConfigureOutcome"/>.
    /// On any fail-fast path the structured diagnostic is written to <see cref="ErrorWriter"/>
    /// before this method returns.
    /// </summary>
    public async Task<ConfigureOutcome> RunConfigureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: resolve token (highest precedence — short-circuits before any network I/O).
        // FT-012: on the interactive-and-unset branch, ResolveOrPromptAsync fires the canonical
        // Aspire parameter prompt; on the non-interactive-and-unset branch, the value is null
        // and the matching E_AUTH_TOKEN_MISSING symbol fires verbatim (I-1).
        var tokenValue = await ResolveOrPromptAsync(Token, isSecret: true, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(tokenValue))
        {
            return EmitAndReturn(new ConfigureDiagnostic
            {
                Symbol = ConfigureSymbol.AuthTokenMissing,
                ParameterName = Token.Resource.Name,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: resolve url. Failure here is precedence-equivalent to a transport failure
        // (FT-002 §Behaviour step 2). FT-012: prompts on interactive-and-unset.
        var urlValue = await ResolveOrPromptAsync(Url, isSecret: false, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(urlValue))
        {
            return EmitAndReturn(new ConfigureDiagnostic
            {
                Symbol = ConfigureSymbol.CoolifyUnreachable,
                ParameterName = Token.Resource.Name,
                Url = "(unresolved)",
                Detail = $"url parameter '{Url.Resource.Name}' is unset or empty",
            });
        }

        // Steps 3–4: construct client and probe (single round-trip — I-1).
        var client = ClientFactory.Create(urlValue, tokenValue);
        CoolifyProbeResult result;
        try
        {
            result = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Conservative catch-all (FT-002 §"Error handling": treat unclassified client
            // exceptions as UNREACHABLE; no stack trace, no token).
            result = CoolifyProbeResult.TransportFailure(Sanitize(ex.Message, tokenValue) ?? "");
        }

        switch (result.Kind)
        {
            case CoolifyProbeKind.AuthRejected:
                return EmitAndReturn(new ConfigureDiagnostic
                {
                    Symbol = ConfigureSymbol.AuthTokenInvalid,
                    ParameterName = Token.Resource.Name,
                    Url = urlValue,
                });

            case CoolifyProbeKind.TransportFailure:
                return EmitAndReturn(new ConfigureDiagnostic
                {
                    Symbol = ConfigureSymbol.CoolifyUnreachable,
                    ParameterName = Token.Resource.Name,
                    Url = urlValue,
                    Detail = Sanitize(result.ErrorMessage, tokenValue),
                });

            case CoolifyProbeKind.UnparseableResponse:
                // FT-002 §"Error handling": 200 OK with unparseable version body → UNREACHABLE,
                // not VERSION_BELOW_FLOOR.
                return EmitAndReturn(new ConfigureDiagnostic
                {
                    Symbol = ConfigureSymbol.CoolifyUnreachable,
                    ParameterName = Token.Resource.Name,
                    Url = urlValue,
                    Detail = Sanitize(result.ErrorMessage, tokenValue),
                });

            case CoolifyProbeKind.Success:
                var observed = result.Version ?? "";
                // Step 5: floor comparison (FT-002 §Behaviour step 5).
                if (SemVerCompare.Compare(observed, SupportedCoolifyVersions.Floor) < 0)
                {
                    return EmitAndReturn(new ConfigureDiagnostic
                    {
                        Symbol = ConfigureSymbol.CoolifyVersionBelowFloor,
                        ParameterName = Token.Resource.Name,
                        Url = urlValue,
                        Observed = observed,
                        Required = SupportedCoolifyVersions.Floor,
                    });
                }

                // Step 6: hand off the client. The token is now scoped to the client only
                // (FT-002 §State).
                ResolvedClient = client;
                ObservedVersion = observed;
                LastDiagnostic = null;
                return ConfigureOutcome.Ok(client, observed);

            default:
                return EmitAndReturn(new ConfigureDiagnostic
                {
                    Symbol = ConfigureSymbol.CoolifyUnreachable,
                    ParameterName = Token.Resource.Name,
                    Url = urlValue,
                    Detail = "unclassified probe result",
                });
        }
    }

    /// <summary>
    /// Build-phase body (FT-003 §Behaviour). Walks the supplied resource set, drives the
    /// Aspire image pipeline once per resource, and tags each emitted image with
    /// <c>&lt;prefix&gt;/&lt;resource.Name&gt;:&lt;apphost-version&gt;</c>. On any fail-fast path
    /// the structured diagnostic is written to <see cref="ErrorWriter"/> before returning.
    /// </summary>
    public async Task<BuildOutcome> RunBuildAsync(
        IEnumerable<IResource> resources,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        _lastBuildTagsByResource.Clear();

        try
        {
            // Materialise the resource set once so we can pre-walk it for the
            // registry-edge gate without re-enumerating a stateful provider.
            // FT-016: skip pks-agent-registry containers — they're deployed by the prereq
            // phase, not built/pushed as workloads.
            var resourceList = resources
                .Where(r => !r.Annotations.OfType<PksAgentRegistryAnnotation>().Any())
                .ToList();

            // FT-014 §A: verify every workload has a registry edge (explicit annotation
            // or shim default). FT-016 relaxes nothing here — the pks-agent in-project
            // path still requires a workload→registry edge; ResolveRegistryFor returns
            // that registry and ResolveRegistryAddressAsync substitutes the
            // prereq-resolved FQDN at iteration time (I-3 / TC-035).
            var unattached = new List<string>();
            foreach (var resource in resourceList)
            {
                if (ResolveRegistryFor(resource) is null)
                {
                    unattached.Add(resource.Name);
                }
            }
            if (unattached.Count > 0)
            {
                return EmitAndReturnBuild(new BuildDiagnostic
                {
                    Symbol = BuildSymbol.RegistryNotConfigured,
                    Resource = string.Join(",", unattached),
                });
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Step 2: resolve the AppHost version exactly once (I-3).
            var (assemblyName, rawVersion) = AppHostInfoProvider();
            var version = rawVersion?.Trim();
            if (string.IsNullOrEmpty(version))
            {
                return EmitAndReturnBuild(new BuildDiagnostic
                {
                    Symbol = BuildSymbol.ApphostVersionMissing,
                    ApphostAssembly = assemblyName,
                });
            }

            // Step 3 + 4: iterate, build, tag (FT-014 §B: address comes from the registry
            // resource attached to *this* workload).
            var emitted = new List<string>();
            foreach (var resource in resourceList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var registry = ResolveRegistryFor(resource)!;
                var address = await ResolveRegistryAddressAsync(registry, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(address))
                {
                    return EmitAndReturnBuild(new BuildDiagnostic
                    {
                        Symbol = BuildSymbol.RegistryNotConfigured,
                        Resource = resource.Name,
                    });
                }

                var tag = $"{address}/{resource.Name}:{version}";

                try
                {
                    await ImagePipeline.BuildAsync(resource, tag, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return EmitAndReturnBuild(new BuildDiagnostic
                    {
                        Symbol = BuildSymbol.ImageBuildFailed,
                        Resource = resource.Name,
                        Tag = tag,
                        Detail = ex.Message,
                    });
                }

                // Defensive verification: pipeline reported success but no image landed.
                if (ImageStore is not null && !ImageStore.HasTag(tag))
                {
                    return EmitAndReturnBuild(new BuildDiagnostic
                    {
                        Symbol = BuildSymbol.ImageBuildFailed,
                        Resource = resource.Name,
                        Tag = tag,
                        Detail = "pipeline reported success but no image is present in the local cache for the computed tag",
                    });
                }

                emitted.Add(tag);
                _lastBuildTagsByResource[resource.Name] = tag;
                // Structured per-resource log line (FT-003 §4.4). No credentials.
                logger.LogInformation("coolify: build: resource {Resource} → {Tag}", resource.Name, tag);
            }

            LastBuildDiagnostic = null;
            return BuildOutcome.Ok(emitted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoolifyBuildFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmitAndReturnBuild(new BuildDiagnostic
            {
                Symbol = BuildSymbol.BuildPhaseUnexpected,
                Detail = ex.Message,
            });
        }
    }

    /// <summary>
    /// FT-004 configure-phase contribution (§Configure §1–§5). Runs after
    /// <see cref="RunConfigureAsync"/> succeeds. When registry credentials are absent (or
    /// <see cref="WithImageRegistry"/> was never called), this is a structured no-op
    /// (anonymous-push path — I-1). When present, derives the registry host from
    /// the registry edge attached to each containerisable workload and calls
    /// <see cref="IPrivateRegistriesApi.UpsertAsync"/>. On any failure emits
    /// <c>E_COOLIFY_REGISTRY_UPSERT_FAILED</c> to <see cref="ErrorWriter"/>.
    /// </summary>
    public async Task<RegistryUpsertOutcome> RunConfigureRegistryUpsertAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // FT-014 §C: enumerate distinct (host, username) pairs across the resource set's
        // registry edges + the shim default. The shim's synthetic registry is included even
        // when no workloads have been wired through ResourcesToBuild — its credentials are
        // the developer's stated intent for the configure-phase upsert.
        var registries = CollectDistinctRegistries(
            ResourcesToBuild?.Invoke() ?? Array.Empty<IResource>());

        if (registries.Count == 0)
        {
            return RegistryUpsertOutcome.SkippedAnonymous();
        }

        var client = ResolvedClient
            ?? ClientFactory.Create(string.Empty, string.Empty); // defence: shouldn't happen post-probe

        // Dedupe by (host, username); skip registries without credentials (anonymous).
        var seen = new HashSet<(string Host, string User)>();
        var anyCredentialled = false;

        foreach (var registry in registries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var address = await ResolveRegistryAddressAsync(registry, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }
            var host = DeriveHost(address);

            var credAnn = registry.Annotations
                .OfType<CoolifyRegistryCredentialsAnnotation>()
                .FirstOrDefault();
            if (credAnn is null)
            {
                continue; // anonymous registry — no upsert per FT-014 §C.
            }

            string? usernameValue;
            string? passwordValue;
            try
            {
                usernameValue = await credAnn.Username.Resource.GetValueAsync(cancellationToken)
                    .ConfigureAwait(false);
                passwordValue = await credAnn.Password.Resource.GetValueAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnUpsert(new PushDiagnostic
                {
                    Symbol = PushSymbol.CoolifyRegistryUpsertFailed,
                    Registry = host,
                    Detail = RedactPassword(ex.Message, passwordValue: null),
                });
            }

            var userPresent = !string.IsNullOrWhiteSpace(usernameValue);
            var passPresent = !string.IsNullOrWhiteSpace(passwordValue);
            if (userPresent ^ passPresent)
            {
                return EmitAndReturnUpsert(new PushDiagnostic
                {
                    Symbol = PushSymbol.CoolifyRegistryUpsertFailed,
                    Registry = host,
                    Username = userPresent ? usernameValue : null,
                    Detail = "registry-username and registry-password resolved asymmetrically",
                    RemediationHint = "credentials travel as a pair — set both or neither (ADR-005 §1)",
                });
            }
            if (!userPresent && !passPresent)
            {
                continue;
            }

            if (!seen.Add((host, usernameValue!)))
            {
                continue;
            }
            anyCredentialled = true;

            PrivateRegistryUpsertResult upsertResult;
            try
            {
                upsertResult = await client.PrivateRegistries
                    .UpsertAsync(host, usernameValue!, passwordValue!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnUpsert(new PushDiagnostic
                {
                    Symbol = PushSymbol.CoolifyRegistryUpsertFailed,
                    Registry = host,
                    Username = usernameValue,
                    Detail = RedactPassword(ex.Message, passwordValue),
                });
            }

            if (upsertResult.Kind != PrivateRegistryUpsertKind.Success)
            {
                return EmitAndReturnUpsert(new PushDiagnostic
                {
                    Symbol = PushSymbol.CoolifyRegistryUpsertFailed,
                    Registry = host,
                    Username = usernameValue,
                    Detail = RedactPassword(upsertResult.ErrorMessage, passwordValue),
                });
            }
        }

        LastPushDiagnostic = null;
        return anyCredentialled ? RegistryUpsertOutcome.Ok() : RegistryUpsertOutcome.SkippedAnonymous();
    }

    /// <summary>
    /// FT-016 prereq-phase body. Enumerates every in-project pks-agent-registry resource
    /// reachable from the AppHost resource graph (those carrying a
    /// <see cref="PksAgentRegistryAnnotation"/>) and, for each, upserts the Coolify
    /// Application, triggers its deploy, resolves the FQDN (auto-discovery or
    /// <c>WithDomain(...)</c> escape hatch), probes <c>/v2/</c> for reachability, and
    /// records the assigned Private-Registry UUID for sibling-workload attachment.
    /// </summary>
    /// <remarks>
    /// I-2: when zero registries carry the annotation, the phase is a structured no-op
    /// (zero Coolify calls, zero state mutation). I-1 / I-6 / I-7: any failure throws via
    /// <see cref="CoolifyPrereqFailedException"/> so <c>coolify-build</c> never runs.
    /// </remarks>
    public async Task<PrereqOutcome> RunPrereqAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _prereqState.Clear();
        _workloadPrivateRegistryAttachment.Clear();
        LastPrereqDiagnostic = null;
        LastPrereqVisitedCount = 0;

        // I-2: enumerate in-project registries via the sidecar lookup maintained by
        // AddPksAgentRegistry (Aspire's AddContainerRegistry doesn't register the resource
        // on builder.Resources, so we keep a separate per-builder table).
        var allResources = AllResourcesProvider?.Invoke() ?? Array.Empty<IResource>();
        var registryCandidates = PksAgentRegistryProvider?.Invoke()
            ?? Array.Empty<ContainerRegistryResource>();
        var registries = new List<(ContainerRegistryResource Registry, PksAgentRegistryAnnotation Marker)>();
        foreach (var registry in registryCandidates)
        {
            var marker = registry.Annotations.OfType<PksAgentRegistryAnnotation>().FirstOrDefault();
            if (marker is null) continue;
            registries.Add((registry, marker));
        }

        if (registries.Count == 0)
        {
            return PrereqOutcome.SkippedNoRegistries();
        }

        var client = ResolvedClient
            ?? ClientFactory.Create(string.Empty, string.Empty);

        var apphostName = AppHostInfoProvider().AssemblyName;
        var environmentName = ActiveEnvironmentProvider();

        foreach (var (registry, marker) in registries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPrereqVisitedCount++;

            var state = new PksAgentRegistryState
            {
                RegistryResourceName = registry.Name,
            };
            _prereqState[registry.Name] = state;

            // §3 escape hatch: pre-set domain present?
            var preSet = registry.Annotations.OfType<PksAgentRegistryDomainAnnotation>().FirstOrDefault();
            if (preSet is not null)
            {
                state.PreSetDomain = preSet.Domain;
                // Emit the W_… warning verbatim (I-8). The phase still proceeds.
                EmitPrereqWarning(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.RegistryFqdnFallback,
                    Registry = registry.Name,
                    Fqdn = preSet.Domain,
                });
            }

            // §1 — upsert the Coolify Application for the registry.
            ApplicationProvisionResult provision;
            try
            {
                provision = await client.Applications.ProvisionRegistryAsync(
                    new ApplicationProvisionRequest(
                        RegistryResourceName: registry.Name,
                        ApphostProjectName: apphostName,
                        EnvironmentName: environmentName,
                        Image: marker.Image,
                        Port: marker.Port,
                        DestinationName: DestinationLiteralName),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryDeployFailed,
                    Registry = registry.Name,
                    Detail = ex.Message,
                });
            }

            state.ApplicationUuid = provision.ApplicationUuid;
            state.PrivateRegistryUuid = provision.PrivateRegistryUuid;
            state.ProjectUuid = provision.ProjectUuid;

            if (!provision.Succeeded)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryDeployFailed,
                    Registry = registry.Name,
                    ProjectUuid = provision.ProjectUuid,
                    ApplicationUuid = provision.ApplicationUuid,
                    Detail = provision.ErrorMessage,
                });
            }

            // §2 — trigger and await the deploy-action.
            ApplicationDeployResult deploy;
            try
            {
                deploy = await client.Applications
                    .TriggerAndAwaitAsync(provision.ApplicationUuid!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryDeployFailed,
                    Registry = registry.Name,
                    ProjectUuid = provision.ProjectUuid,
                    ApplicationUuid = provision.ApplicationUuid,
                    Detail = ex.Message,
                });
            }

            state.DeployActionUuid = deploy.DeployActionUuid;
            if (!deploy.Succeeded)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryDeployFailed,
                    Registry = registry.Name,
                    ProjectUuid = provision.ProjectUuid,
                    ApplicationUuid = provision.ApplicationUuid,
                    DeployActionUuid = deploy.DeployActionUuid,
                    Detail = deploy.ErrorMessage,
                });
            }

            // §3 — resolve FQDN: pre-set escape hatch wins, otherwise auto-discovered.
            var fqdn = state.PreSetDomain ?? provision.AutoDiscoveredFqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                // Auto-discovery yielded no domain; surface as unreachable so the user
                // sees a concrete diagnostic naming the registry.
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryUnreachable,
                    Registry = registry.Name,
                    ProjectUuid = provision.ProjectUuid,
                    ApplicationUuid = provision.ApplicationUuid,
                    Fqdn = "(none)",
                    Detail = "Coolify did not report an assigned domain for this Application",
                });
            }
            state.ResolvedFqdn = fqdn;

            // §4 — probe /v2/ for reachability.
            RegistryProbeOutcome probe;
            try
            {
                probe = await ReachabilityProbe.ProbeAsync(fqdn, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryUnreachable,
                    Registry = registry.Name,
                    Fqdn = fqdn,
                    ProbeUrl = $"https://{fqdn}/v2/",
                    Detail = ex.Message,
                });
            }
            if (!probe.Succeeded)
            {
                return EmitAndReturnPrereq(new PrereqDiagnostic
                {
                    Symbol = PrereqSymbol.PrereqRegistryUnreachable,
                    Registry = registry.Name,
                    Fqdn = fqdn,
                    ProbeUrl = probe.ProbeUrl,
                    Elapsed = probe.Elapsed,
                    Detail = probe.LastNetworkError,
                });
            }
        }

        // §5 — record per-workload attachment map from the resource graph's
        // workload→registry edges. Use both the standard resource graph and any
        // resources supplied via ResourcesToBuild (which may include test-only
        // FakeProject resources that aren't enumerated in builder.Resources).
        var attachmentSources = (ResourcesToBuild?.Invoke() ?? Array.Empty<IResource>())
            .Concat(allResources);
        foreach (var resource in attachmentSources)
        {
            var edge = resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().FirstOrDefault();
            if (edge?.Registry is not ContainerRegistryResource registry) continue;
            if (!_prereqState.TryGetValue(registry.Name, out var state)) continue;
            if (string.IsNullOrEmpty(state.PrivateRegistryUuid)) continue;
            _workloadPrivateRegistryAttachment[resource.Name] = state.PrivateRegistryUuid!;
        }

        return PrereqOutcome.Ok();
    }

    private PrereqOutcome EmitAndReturnPrereq(PrereqDiagnostic diagnostic)
    {
        LastPrereqDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return PrereqOutcome.Fail(diagnostic);
    }

    private void EmitPrereqWarning(PrereqDiagnostic diagnostic)
    {
        // Warning: written to stderr but does not halt the phase. Does not update
        // LastPrereqDiagnostic (which tracks fail-fast diagnostics only).
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
    }

    /// <summary>
    /// FT-014: collect the distinct set of <see cref="ContainerRegistryResource"/>s reachable
    /// from the supplied workloads' registry edges, plus the shim default registry if the
    /// <c>WithImageRegistry(...)</c> shim was called. Workloads contribute their explicit edge
    /// or — when absent — the shim default.
    /// </summary>
    internal List<ContainerRegistryResource> CollectDistinctRegistries(IEnumerable<IResource> resources)
    {
        var distinct = new List<ContainerRegistryResource>();
        var byName = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            var registry = ResolveRegistryFor(resource);
            if (registry is not null && byName.Add(registry.Name))
            {
                distinct.Add(registry);
            }
        }

        // The shim's synthetic registry is always part of the distinct set when the shim
        // was called — its presence is the developer's intent, even when no workload was
        // explicitly attached (e.g. the resource set is empty / unwired in tests).
        if (ShimDefaultRegistry is not null && byName.Add(ShimDefaultRegistry.Name))
        {
            distinct.Add(ShimDefaultRegistry);
        }

        return distinct;
    }

    /// <summary>
    /// FT-014: locate the <see cref="ContainerRegistryResource"/> for a workload via the
    /// explicit <c>WithContainerRegistry(...)</c> edge, falling back to the shim default
    /// when no edge is present. Returns null when neither is available.
    /// </summary>
    internal ContainerRegistryResource? ResolveRegistryFor(IResource resource)
    {
        var explicitAnn = resource.Annotations
            .OfType<ContainerRegistryReferenceAnnotation>()
            .FirstOrDefault();
        if (explicitAnn?.Registry is ContainerRegistryResource explicitResource)
        {
            return explicitResource;
        }
        return ShimDefaultRegistry;
    }

    /// <summary>
    /// FT-014: resolve a registry resource's address — endpoint + optional repository path.
    /// FT-016: for in-project pks-agent-registry resources whose prereq phase has resolved
    /// an FQDN, that FQDN takes precedence so workload tags carry the real Coolify-assigned
    /// host (I-3 / TC-035).
    /// </summary>
    internal async Task<string> ResolveRegistryAddressAsync(
        ContainerRegistryResource registry,
        CancellationToken cancellationToken)
    {
        if (_prereqState.TryGetValue(registry.Name, out var state)
            && !string.IsNullOrEmpty(state.ResolvedFqdn))
        {
            return state.ResolvedFqdn!;
        }
        return await ResolveRegistryAddressStaticAsync(registry, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ResolveRegistryAddressStaticAsync(
        ContainerRegistryResource registry,
        CancellationToken cancellationToken)
    {
        var icr = (Aspire.Hosting.ApplicationModel.IContainerRegistry)registry;
        var endpoint = (await icr.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            return string.Empty;
        }
        string? repository = null;
        try
        {
            if (icr.Repository is not null)
            {
                repository = (await icr.Repository.GetValueAsync(cancellationToken).ConfigureAwait(false))?.Trim();
            }
        }
        catch
        {
            repository = null;
        }
        return string.IsNullOrEmpty(repository)
            ? endpoint
            : endpoint.TrimEnd('/') + "/" + repository.TrimStart('/');
    }

    /// <summary>
    /// FT-004 push-phase body (§Push §1–§5). Iterates the resource set, resolves credentials
    /// (or confirms anonymous), invokes <see cref="IImagePushPipeline"/> per resource using
    /// the deterministic tag FT-003 emitted (I-5), aggregates failures, and surfaces one of
    /// three symbols (<c>E_REGISTRY_AUTH_FAILED</c> wins over <c>E_IMAGE_PUSH_FAILED</c>;
    /// any escape becomes <c>E_PUSH_PHASE_UNEXPECTED</c>).
    /// </summary>
    public async Task<PushOutcome> RunPushAsync(
        IEnumerable<IResource> resources,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        string? passwordValue = null;
        try
        {
            // FT-014 §D: per-resource push. Credentials follow the workload's registry edge
            // (explicit annotation or shim default).
            var authFailures = new List<(string Resource, string Tag)>();
            var otherFailures = new List<(string Resource, string Tag)>();
            var pushed = new List<string>();

            foreach (var resource in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // FT-016: skip pks-agent-registry containers — deployed by the prereq phase.
                if (resource.Annotations.OfType<PksAgentRegistryAnnotation>().Any())
                {
                    continue;
                }

                var registry = ResolveRegistryFor(resource);
                string? host = null;
                RegistryCredentials? credentials = null;
                if (registry is not null)
                {
                    var address = await ResolveRegistryAddressAsync(registry, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(address))
                    {
                        host = DeriveHost(address);
                    }

                    var credAnn = registry.Annotations
                        .OfType<CoolifyRegistryCredentialsAnnotation>()
                        .FirstOrDefault();
                    if (credAnn is not null)
                    {
                        var usernameValue = await credAnn.Username.Resource.GetValueAsync(cancellationToken)
                            .ConfigureAwait(false);
                        passwordValue = await credAnn.Password.Resource.GetValueAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var userPresent = !string.IsNullOrWhiteSpace(usernameValue);
                        var passPresent = !string.IsNullOrWhiteSpace(passwordValue);
                        if (userPresent && passPresent)
                        {
                            credentials = new RegistryCredentials(usernameValue!, passwordValue!);
                        }
                        else if (userPresent ^ passPresent)
                        {
                            return EmitAndReturnPush(new PushDiagnostic
                            {
                                Symbol = PushSymbol.PushPhaseUnexpected,
                                Detail = "registry-username and registry-password resolved asymmetrically",
                            }, passwordValue);
                        }
                    }
                }

                // I-5: read FT-003's tag, falling back to recomputation only if absent.
                if (!_lastBuildTagsByResource.TryGetValue(resource.Name, out var tag))
                {
                    if (registry is null)
                    {
                        return EmitAndReturnPush(new PushDiagnostic
                        {
                            Symbol = PushSymbol.PushPhaseUnexpected,
                            Detail = "no image tag from build phase and no registry edge to recompute from",
                        }, passwordValue);
                    }
                    var address = await ResolveRegistryAddressAsync(registry, cancellationToken)
                        .ConfigureAwait(false);
                    var (_, ver) = AppHostInfoProvider();
                    tag = $"{address}/{resource.Name}:{ver?.Trim()}";
                }

                // The default push pipeline routes through Aspire's IResourceContainerImageManager
                // which pushes by IResource (not by tag string), so tell it which resource we're
                // pushing in this step. Test-injected mocks ignore this property.
                if (ImagePushPipeline is UnconfiguredImagePushPipeline upp)
                {
                    upp.CurrentResource = resource;
                }
                ImagePushResult result;
                try
                {
                    result = await ImagePushPipeline
                        .PushAsync(tag, credentials, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    otherFailures.Add((resource.Name, tag));
                    logger.LogWarning("coolify: push: {Resource} → {Tag} failed: {Detail}",
                        resource.Name, tag, RedactPassword(ex.Message, passwordValue));
                    continue;
                }

                switch (result.Kind)
                {
                    case ImagePushResultKind.Success:
                        pushed.Add(tag);
                        logger.LogInformation(
                            "coolify: push: resource {Resource} → {Tag} (registry {Host})",
                            resource.Name, tag, host ?? "(unknown)");
                        break;
                    case ImagePushResultKind.AuthRejected:
                        authFailures.Add((resource.Name, tag));
                        break;
                    case ImagePushResultKind.Failed:
                    default:
                        otherFailures.Add((resource.Name, tag));
                        break;
                }
            }

            // Step 4: aggregate.
            if (authFailures.Count > 0)
            {
                return EmitAndReturnPush(new PushDiagnostic
                {
                    Symbol = PushSymbol.RegistryAuthFailed,
                    Failures = authFailures,
                    AdditionalNonAuthFailures = otherFailures,
                    Registry = DeriveHostFromFailures(authFailures),
                }, passwordValue);
            }
            if (otherFailures.Count > 0)
            {
                return EmitAndReturnPush(new PushDiagnostic
                {
                    Symbol = PushSymbol.ImagePushFailed,
                    Failures = otherFailures,
                    Registry = DeriveHostFromFailures(otherFailures),
                }, passwordValue);
            }

            LastPushDiagnostic = null;
            return PushOutcome.Ok(pushed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmitAndReturnPush(new PushDiagnostic
            {
                Symbol = PushSymbol.PushPhaseUnexpected,
                Detail = RedactPassword(ex.Message, passwordValue),
            }, passwordValue);
        }
    }

    /// <summary>
    /// Deploy-phase body (FT-005 §Behaviour steps 1–8). Walks
    /// destination → project → environment → per-resource service upsert → hooks → trigger,
    /// in the fixed ADR-003 D2 order, with name-keyed upserts and aggregation discipline.
    /// On any fail-fast path the structured diagnostic is written to <see cref="ErrorWriter"/>
    /// before this method returns.
    /// </summary>
    public async Task<DeployOutcome> RunDeployAsync(
        IEnumerable<IResource> resources,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        LastDeployHandles = Array.Empty<DeployActionHandle>();

        // Step 1: resolve destination (FT-005 §0 string-overload amendment — literal wins,
        // else fall back to parameter handle resolution).
        string? destinationValue;
        if (DestinationLiteralName is not null)
        {
            destinationValue = DestinationLiteralName.Trim();
            if (string.IsNullOrEmpty(destinationValue))
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyDestinationUpsertFailed,
                    Detail = "WithCoolifyDestination(literalName): literal value is empty or whitespace",
                });
            }
        }
        else if (DestinationName is null)
        {
            return EmitAndReturnDeploy(new DeployDiagnostic
            {
                Symbol = DeploySymbol.CoolifyDestinationUpsertFailed,
                Detail = "WithCoolifyDestination(...) was not called on this builder",
            });
        }
        else
        {
            // FT-012: prompt for the destination-name value when interactive and unset.
            destinationValue = await ResolveOrPromptAsync(DestinationName, isSecret: false, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(destinationValue))
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyDestinationUpsertFailed,
                    Detail = $"destination parameter '{DestinationName.Resource.Name}' is unset or empty",
                });
            }
        }

        var client = ResolvedClient
            ?? ClientFactory.Create(string.Empty, string.Empty);

        var apphostName = AppHostInfoProvider().AssemblyName;
        var environmentName = ActiveEnvironmentProvider();

        try
        {
            // Step 2: destination lookup-or-upsert.
            cancellationToken.ThrowIfCancellationRequested();
            DestinationUpsertResult destResult;
            try
            {
                destResult = await client.Destinations
                    .LookupOrUpsertAsync(destinationValue, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyDestinationUpsertFailed,
                    Destination = destinationValue,
                    Detail = ex.Message,
                });
            }
            if (destResult.Kind == UpsertKind.Failure || destResult.Id is null)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyDestinationUpsertFailed,
                    Destination = destinationValue,
                    Detail = destResult.ErrorMessage,
                });
            }
            var destinationId = destResult.Id;
            logger.LogInformation(
                "coolify: deploy: destination {Name} → {Outcome}",
                destinationValue, destResult.Kind.ToString().ToLowerInvariant());

            // Step 3: project upsert.
            cancellationToken.ThrowIfCancellationRequested();
            ProjectUpsertResult projResult;
            try
            {
                projResult = await client.Projects.UpsertAsync(apphostName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyProjectUpsertFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Detail = ex.Message,
                });
            }
            if (projResult.Kind == UpsertKind.Failure || projResult.Id is null)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyProjectUpsertFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Detail = projResult.ErrorMessage,
                });
            }
            var projectId = projResult.Id;
            EmitDriftWarnings(logger, apphostName, projResult.Drifts);
            logger.LogInformation(
                "coolify: deploy: project {Name} → {Outcome}",
                apphostName, projResult.Kind.ToString().ToLowerInvariant());

            // Step 4: environment upsert (targeted only).
            cancellationToken.ThrowIfCancellationRequested();
            EnvironmentUpsertResult envResult;
            try
            {
                envResult = await client.Environments
                    .UpsertAsync(projectId, environmentName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyEnvironmentUpsertFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Environment = environmentName,
                    Detail = ex.Message,
                });
            }
            if (envResult.Kind == UpsertKind.Failure || envResult.Id is null)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyEnvironmentUpsertFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Environment = environmentName,
                    Detail = envResult.ErrorMessage,
                });
            }
            var environmentId = envResult.Id;
            EmitDriftWarnings(logger, environmentName, envResult.Drifts);
            logger.LogInformation(
                "coolify: deploy: environment {Name} → {Outcome}",
                environmentName, envResult.Kind.ToString().ToLowerInvariant());

            // Step 5: per-resource service upsert + hooks. Step 7 (triggers) runs after the
            // full loop so aggregation discipline (I-10) holds on both buckets.
            var serviceFailures = new List<(string Resource, string? Tag, string? Detail)>();
            var upserted = new List<(IResource Resource, string ServiceId, string Tag)>();
            // Hook failures short-circuit with the hook's own symbol per FT-005 I-12.
            CoolifyDeployHookFailedException? hookFailure = null;

            foreach (var resource in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // FT-016: skip pks-agent-registry containers — deployed by the prereq phase.
                if (resource.Annotations.OfType<PksAgentRegistryAnnotation>().Any())
                {
                    continue;
                }

                _lastBuildTagsByResource.TryGetValue(resource.Name, out var tag);
                if (string.IsNullOrEmpty(tag))
                {
                    // Pre-existing-image resource (AddContainer without IProjectMetadata)
                    // — no build, no push, but the image annotation already names a real,
                    // pullable image. Use it verbatim as the tag for the Coolify service.
                    var imageAnn = resource.Annotations
                        .OfType<ContainerImageAnnotation>()
                        .FirstOrDefault();
                    if (imageAnn is not null && !string.IsNullOrEmpty(imageAnn.Image))
                    {
                        tag = string.IsNullOrEmpty(imageAnn.Tag)
                            ? imageAnn.Image
                            : $"{imageAnn.Image}:{imageAnn.Tag}";
                    }
                }
                tag ??= ""; // No tag available — surface as failure detail via the upsert.

                // FT-014: derive registry reference from the workload's registry edge.
                var workloadRegistry = ResolveRegistryFor(resource);
                var hasRegistryCreds = workloadRegistry is not null
                    && workloadRegistry.Annotations.OfType<CoolifyRegistryCredentialsAnnotation>().Any();
                var registryReference = hasRegistryCreds
                    ? $"{ResolveHostFromTag(tag)}|registry"
                    : null;
                // FT-016 I-5: workloads attached to an in-project pks-agent-registry carry
                // the Private-Registry UUID assigned by the prereq phase.
                _workloadPrivateRegistryAttachment.TryGetValue(resource.Name, out var prereqAttachmentUuid);
                var spec = new ServiceSpec(
                    Image: tag,
                    RegistryReference: registryReference,
                    DestinationBinding: destinationId)
                {
                    PrivateRegistryAttachmentUuid = prereqAttachmentUuid,
                };

                ServiceUpsertResult svcResult;
                try
                {
                    svcResult = await client.Services
                        .UpsertAsync(projectId, environmentId, resource.Name, spec, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    serviceFailures.Add((resource.Name, tag, ex.Message));
                    continue;
                }

                if (svcResult.Kind == UpsertKind.Failure || svcResult.Id is null)
                {
                    serviceFailures.Add((resource.Name, tag, svcResult.ErrorMessage));
                    continue;
                }

                EmitDriftWarnings(logger, resource.Name, svcResult.Drifts);
                logger.LogInformation(
                    "coolify: deploy: service {Name} → {Outcome} (tag {Tag})",
                    resource.Name, svcResult.Kind.ToString().ToLowerInvariant(), tag);

                // Step 5.3: FT-007 / FT-008 hooks.
                if (hookFailure is null)
                {
                    var hookContext = new DeployHookContext(projectId, environmentId, svcResult.Id, resource);
                    foreach (var hook in new[] { EnvVarHook, ReferenceHook })
                    {
                        if (hook is null) continue;
                        DeployHookResult hookResult;
                        try
                        {
                            hookResult = await hook.InvokeAsync(hookContext, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            hookFailure = new CoolifyDeployHookFailedException(
                                "E_DEPLOY_PHASE_UNEXPECTED", ex.Message);
                            break;
                        }
                        if (!hookResult.Succeeded)
                        {
                            hookFailure = new CoolifyDeployHookFailedException(
                                hookResult.FailureSymbol ?? "E_DEPLOY_PHASE_UNEXPECTED",
                                hookResult.DiagnosticText);
                            break;
                        }
                    }
                }

                upserted.Add((resource, svcResult.Id, tag));
            }

            // Step 6: aggregate service-upsert failures (I-10).
            if (serviceFailures.Count > 0)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyServiceUpsertFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Environment = environmentName,
                    Failures = serviceFailures,
                });
            }

            // If any hook fail-fasted, surface its symbol verbatim (not E_DEPLOY_PHASE_UNEXPECTED
            // unless the hook itself crashed unexpectedly per FT-005 §"Error handling").
            if (hookFailure is not null)
            {
                if (hookFailure.Symbol == "E_DEPLOY_PHASE_UNEXPECTED")
                {
                    return EmitAndReturnDeploy(new DeployDiagnostic
                    {
                        Symbol = DeploySymbol.DeployPhaseUnexpected,
                        Destination = destinationValue,
                        Project = apphostName,
                        Environment = environmentName,
                        Detail = hookFailure.DiagnosticText,
                    });
                }
                LastDeployDiagnostic = null;
                ErrorWriter.Write(hookFailure.Symbol);
                ErrorWriter.WriteLine(": " + (hookFailure.DiagnosticText ?? "feature-owned fail-fast from deploy hook"));
                ErrorWriter.Flush();
                throw hookFailure;
            }

            // Step 7: per-service deploy-action trigger with aggregation.
            var triggerFailures = new List<(string Resource, string? Tag, string? Detail)>();
            var handles = new List<DeployActionHandle>();
            foreach (var (resource, serviceId, tag) in upserted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DeployTriggerResult trigResult;
                try
                {
                    trigResult = await client.Services
                        .TriggerDeployAsync(serviceId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    triggerFailures.Add((resource.Name, tag, ex.Message));
                    continue;
                }

                if (!trigResult.Accepted)
                {
                    triggerFailures.Add((resource.Name, tag, trigResult.ErrorMessage));
                    continue;
                }

                handles.Add(new DeployActionHandle(resource.Name, serviceId, trigResult.Handle));
                logger.LogInformation(
                    "coolify: deploy: trigger {Name} → accepted (handle {Handle})",
                    resource.Name, trigResult.Handle ?? "(none)");
            }

            if (triggerFailures.Count > 0)
            {
                return EmitAndReturnDeploy(new DeployDiagnostic
                {
                    Symbol = DeploySymbol.CoolifyDeployTriggerFailed,
                    Destination = destinationValue,
                    Project = apphostName,
                    Environment = environmentName,
                    Failures = triggerFailures,
                });
            }

            // Step 7.5 (FT-010): dashboard sub-phase. Runs strictly after the workload trigger
            // loop succeeded (I-2). Every failure inside is a W_… warning that leaves the
            // workload deploy's exit code unchanged (I-1). On opt-out (I-6), this is a silent
            // no-op — zero Coolify calls, zero log lines, zero W_… symbols.
            if (DashboardOptedIn)
            {
                await RunDashboardSubPhaseAsync(
                    client,
                    apphostName,
                    environmentName,
                    projectId,
                    environmentId,
                    destinationId,
                    handles,
                    logger,
                    cancellationToken).ConfigureAwait(false);
            }

            // Step 8: success.
            LastDeployDiagnostic = null;
            LastDeployHandles = handles;
            return DeployOutcome.Ok(handles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoolifyDeployHookFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmitAndReturnDeploy(new DeployDiagnostic
            {
                Symbol = DeploySymbol.DeployPhaseUnexpected,
                Destination = destinationValue,
                Project = apphostName,
                Environment = environmentName,
                Detail = ex.Message,
            });
        }
    }

    /// <summary>
    /// Verify-phase body (FT-006 §Behaviour steps 1–5). Polls each handle in
    /// <paramref name="handles"/> through <c>client.DeployJobs.GetStatusAsync</c> until each
    /// reaches a terminal state (<c>succeeded</c> / <c>failed</c> / <c>404</c>) or the configured
    /// overall timeout elapses. Per-handle interval grows under exponential backoff capped at
    /// 60s (I-9). All handles are driven before failure is surfaced (I-4); on mixed timeout
    /// + failure outcomes, <c>E_VERIFY_TIMEOUT</c> wins (I-11). Cancellation propagates
    /// FT-001's cancellation diagnostic without an <c>E_…</c> symbol.
    /// </summary>
    public async Task<VerifyOutcome> RunVerifyAsync(
        IReadOnlyList<DeployActionHandle> handles,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: empty handle list short-circuits with zero outbound calls (I-8).
        if (handles.Count == 0)
        {
            LastVerifyDiagnostic = null;
            return VerifyOutcome.Ok();
        }

        var client = ResolvedClient
            ?? ClientFactory.Create(string.Empty, string.Empty);

        var apphostName = AppHostInfoProvider().AssemblyName;
        var environmentName = ActiveEnvironmentProvider();

        // Step 1: snapshot polling configuration and start wall-clock.
        var configuredInterval = VerifyInterval;
        var configuredTimeout = VerifyTimeout;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan Elapsed() => VerifyElapsedProvider?.Invoke() ?? stopwatch.Elapsed;

        var states = handles
            .Select(h => new VerifyHandleState(h, configuredInterval))
            .ToList();

        try
        {
            // Step 3: per-handle polling loop, sequential in input order.
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Phase-level timeout pre-empts polling this handle.
                if (Elapsed() >= configuredTimeout)
                {
                    state.MarkTimeout(state.LastObservedState ?? "unobserved");
                    continue;
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Issue one GetStatusAsync call (step 3.i).
                    DeployJobStatusResult status;
                    try
                    {
                        status = await client.DeployJobs
                            .GetStatusAsync(state.Handle.JobHandle ?? state.Handle.ServiceId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Unclassifiable client exception → E_VERIFY_PHASE_UNEXPECTED
                        // (FT-006 §Error handling).
                        return EmitAndReturnVerify(new VerifyDiagnostic
                        {
                            Symbol = VerifySymbol.VerifyPhaseUnexpected,
                            Project = apphostName,
                            Environment = environmentName,
                            Detail = ex.Message,
                        });
                    }

                    // Step 3.ii: emit verify-progress on every observed state change.
                    var observedState = status.RawState ?? status.Kind.ToString().ToLowerInvariant();
                    if (!string.Equals(state.LastObservedState, observedState, StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "verify-progress: resource={Resource} state={State} handle={Handle} elapsed={Elapsed}",
                            state.Handle.Resource,
                            observedState,
                            state.Handle.JobHandle ?? state.Handle.ServiceId,
                            Elapsed());
                        state.LastObservedState = observedState;
                    }

                    // Steps 3.iii / 3.iv: terminal states.
                    if (status.Kind == DeployJobStatusKind.Succeeded)
                    {
                        state.MarkSucceeded(observedState);
                        break;
                    }
                    if (status.Kind == DeployJobStatusKind.Failed
                        || status.Kind == DeployJobStatusKind.NotFound)
                    {
                        state.MarkFailed(observedState);
                        break;
                    }

                    // Steps 3.v / 3.vi: non-terminal — sleep with backoff, check timeout.
                    // TransientFailure is treated as "state unchanged, sleep, retry" per
                    // FT-006 §Error handling.
                    var remaining = configuredTimeout - Elapsed();
                    if (remaining <= TimeSpan.Zero)
                    {
                        state.MarkTimeout(state.LastObservedState ?? observedState);
                        break;
                    }

                    var sleepFor = state.CurrentInterval < remaining ? state.CurrentInterval : remaining;
                    try
                    {
                        await VerifySleeper(sleepFor, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }

                    // Double the per-handle interval, clamp to the 60s cap (I-9).
                    var doubledTicks = state.CurrentInterval.Ticks * 2;
                    var doubled = doubledTicks < 0 || doubledTicks > VerifyPollCap.Ticks
                        ? VerifyPollCap
                        : TimeSpan.FromTicks(doubledTicks);
                    state.CurrentInterval = doubled;

                    if (Elapsed() >= configuredTimeout)
                    {
                        state.MarkTimeout(state.LastObservedState ?? observedState);
                        break;
                    }
                }
            }

            // Step 4: aggregate.
            var failed = states.Where(s => s.Outcome == VerifyHandleOutcome.Failed).ToList();
            var timedOut = states.Where(s => s.Outcome == VerifyHandleOutcome.Timeout).ToList();

            if (timedOut.Count > 0)
            {
                // I-11 precedence: timeout wins over failure.
                var tuples = states
                    .Where(s => s.Outcome != VerifyHandleOutcome.Succeeded)
                    .Select(s => new VerifyFailureTuple(
                        s.Handle.Resource,
                        s.Handle.JobHandle ?? s.Handle.ServiceId,
                        client.DeployJobs.GetHumanUrl(s.Handle.JobHandle ?? s.Handle.ServiceId),
                        s.LastObservedState ?? "unobserved"))
                    .ToList();
                return EmitAndReturnVerify(new VerifyDiagnostic
                {
                    Symbol = VerifySymbol.VerifyTimeout,
                    Project = apphostName,
                    Environment = environmentName,
                    Failures = tuples,
                    Elapsed = Elapsed(),
                });
            }

            if (failed.Count > 0)
            {
                var tuples = failed
                    .Select(s => new VerifyFailureTuple(
                        s.Handle.Resource,
                        s.Handle.JobHandle ?? s.Handle.ServiceId,
                        client.DeployJobs.GetHumanUrl(s.Handle.JobHandle ?? s.Handle.ServiceId),
                        s.LastObservedState ?? "failed"))
                    .ToList();
                return EmitAndReturnVerify(new VerifyDiagnostic
                {
                    Symbol = VerifySymbol.VerifyFailed,
                    Project = apphostName,
                    Environment = environmentName,
                    Failures = tuples,
                });
            }

            // Step 5: every handle reached succeeded.
            LastVerifyDiagnostic = null;
            return VerifyOutcome.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoolifyVerifyFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmitAndReturnVerify(new VerifyDiagnostic
            {
                Symbol = VerifySymbol.VerifyPhaseUnexpected,
                Project = apphostName,
                Environment = environmentName,
                Detail = ex.Message,
            });
        }
    }

    /// <summary>
    /// FT-010 dashboard sub-phase body (§Behaviour §1–§7). Runs inside the deploy phase,
    /// strictly after FT-005's per-service trigger loop succeeded. Every failure is folded
    /// into a <c>W_…</c> warning (I-1) — never throws to the caller except on cooperative
    /// cancellation. On a successful trigger, appends a tagged <see cref="DeployActionHandle"/>
    /// to <paramref name="handles"/> so <c>verify</c> (FT-006) polls the dashboard alongside
    /// workload services (I-10).
    /// </summary>
    internal async Task RunDashboardSubPhaseAsync(
        ICoolifyClient client,
        string projectName,
        string environmentName,
        string projectId,
        string environmentId,
        string destinationId,
        List<DeployActionHandle> handles,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        LastDashboardDiagnostic = null;
        LastDashboardFqdn = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // §1 — opt-in gate already checked at the call site, but defence in depth.
            if (!DashboardOptedIn || DashboardToken is null)
            {
                return;
            }

            // §2 — resolve the dashboard token. FT-012: prompts on interactive-and-unset
            // for the dashboard-token secret parameter. Failure → W_DASHBOARD_TOKEN_MISSING.
            var dashboardTokenValue = await ResolveOrPromptAsync(
                DashboardToken, isSecret: true, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(dashboardTokenValue))
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardTokenMissing,
                    Project = projectName,
                    Environment = environmentName,
                    ParameterName = DashboardToken.Resource.Name,
                });
                return;
            }
            // (already trimmed by ResolveOrPromptAsync)

            // Resolve the Coolify base URL (env-var COOLIFY_API_URL value). Best-effort: a
            // resolution failure here is unusual (configure-phase already resolved it) and
            // is treated as a transport-style failure later in the env-var write step.
            string? urlValue = null;
            try
            {
                urlValue = (await Url.Resource.GetValueAsync(cancellationToken)
                    .ConfigureAwait(false))?.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { /* surfaced as W_DASHBOARD_ENVVAR_FAILED on the COOLIFY_API_URL write */ }

            // §3 — dashboard service upsert (name-keyed, idempotent).
            var spec = new ServiceSpec(
                Image: DashboardImageTag,
                RegistryReference: null,
                DestinationBinding: destinationId);

            ServiceUpsertResult upsertResult;
            try
            {
                upsertResult = await client.Services
                    .UpsertAsync(projectId, environmentId, DashboardServiceName, spec, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardUpsertFailed,
                    Project = projectName,
                    Environment = environmentName,
                    Coolify = Sanitize(ex.Message, dashboardTokenValue),
                });
                return;
            }

            if (upsertResult.Kind == UpsertKind.Failure || upsertResult.Id is null)
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardUpsertFailed,
                    Project = projectName,
                    Environment = environmentName,
                    Coolify = Sanitize(upsertResult.ErrorMessage, dashboardTokenValue),
                });
                return;
            }

            var dashboardServiceId = upsertResult.Id;
            EmitDriftWarnings(logger, DashboardServiceName, upsertResult.Drifts);
            LastDashboardFqdn = string.IsNullOrWhiteSpace(upsertResult.Fqdn) ? null : upsertResult.Fqdn;
            logger.LogInformation(
                "coolify: deploy: dashboard {Name} → {Outcome}",
                DashboardServiceName, upsertResult.Kind.ToString().ToLowerInvariant());

            // §4 — env-var writes (the three required vars). Aggregated per FT-010 §4.
            var envVars = client.ServiceEnvVars;
            var pending = new List<(string Key, string Value)>
            {
                ("COOLIFY_API_URL", urlValue ?? ""),
                ("COOLIFY_API_TOKEN", dashboardTokenValue),
                ("COOLIFY_PROJECT_UUID", projectId),
            };
            // I-4: never substitute the deploy token. Defence in depth — sanity-check the
            // value we are about to send for COOLIFY_API_TOKEN before issuing the write.
            // (The substitution would require code to mistakenly pick Token over DashboardToken.)

            var envVarFailures = new List<string>();
            foreach (var (key, value) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var secretFlag = key != "COOLIFY_API_URL"; // URL is non-secret; token + UUID are sensitive

                if (string.IsNullOrEmpty(value))
                {
                    envVarFailures.Add(key);
                    continue;
                }

                EnvVarFetchResult fetch;
                try
                {
                    fetch = await envVars
                        .GetByNameAsync(dashboardServiceId, key, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    envVarFailures.Add(key);
                    continue;
                }

                EnvVarWriteResult write;
                try
                {
                    if (fetch.Kind == EnvVarFetchKind.NotFound)
                    {
                        write = await envVars
                            .CreateAsync(dashboardServiceId, key, value, secretFlag, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (fetch.Kind == EnvVarFetchKind.Found)
                    {
                        write = await envVars
                            .PatchAsync(dashboardServiceId, key, value, secretFlag, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        envVarFailures.Add(key);
                        continue;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    envVarFailures.Add(key);
                    continue;
                }

                if (!write.Succeeded)
                {
                    envVarFailures.Add(key);
                }
            }

            if (envVarFailures.Count > 0)
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardEnvVarFailed,
                    Project = projectName,
                    Environment = environmentName,
                    FailedEnvVars = envVarFailures,
                });
                return;
            }

            // §5 — dashboard deploy-action trigger.
            DeployTriggerResult triggerResult;
            try
            {
                triggerResult = await client.Services
                    .TriggerDeployAsync(dashboardServiceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardTriggerFailed,
                    Project = projectName,
                    Environment = environmentName,
                    Coolify = Sanitize(ex.Message, dashboardTokenValue),
                });
                return;
            }

            if (!triggerResult.Accepted)
            {
                EmitDashboardWarning(new DashboardDiagnostic
                {
                    Symbol = DashboardSymbol.DashboardTriggerFailed,
                    Project = projectName,
                    Environment = environmentName,
                    Coolify = Sanitize(triggerResult.ErrorMessage, dashboardTokenValue),
                });
                return;
            }

            // I-10: append the dashboard handle with the `dashboard` tag.
            handles.Add(new DeployActionHandle(
                DashboardServiceName, dashboardServiceId, triggerResult.Handle)
            {
                Tag = "dashboard",
            });
            logger.LogInformation(
                "coolify: deploy: dashboard trigger → accepted (handle {Handle})",
                triggerResult.Handle ?? "(none)");

            // §6 — FQDN surfacing (best-effort; never a W_…).
            var fqdn = LastDashboardFqdn;
            if (!string.IsNullOrWhiteSpace(fqdn))
            {
                logger.LogInformation("managed-dashboard: url=https://{Fqdn}", fqdn);
            }
            else
            {
                logger.LogInformation(
                    "managed-dashboard: url=<pending — check Coolify UI for assigned domain>");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates per FT-010 §Cancellation; not folded into a W_….
            throw;
        }
        catch (Exception ex)
        {
            // Catch-all: W_DASHBOARD_UNEXPECTED with inner Message appended; no stack trace.
            EmitDashboardWarning(new DashboardDiagnostic
            {
                Symbol = DashboardSymbol.DashboardUnexpected,
                Project = projectName,
                Environment = environmentName,
                Detail = ex.Message,
            });
        }
    }

    private void EmitDashboardWarning(DashboardDiagnostic diagnostic)
    {
        LastDashboardDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
    }

    private VerifyOutcome EmitAndReturnVerify(VerifyDiagnostic diagnostic)
    {
        LastVerifyDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return VerifyOutcome.Fail(diagnostic);
    }

    private enum VerifyHandleOutcome { Pending, Succeeded, Failed, Timeout }

    private sealed class VerifyHandleState
    {
        public VerifyHandleState(DeployActionHandle handle, TimeSpan initialInterval)
        {
            Handle = handle;
            CurrentInterval = initialInterval;
        }

        public DeployActionHandle Handle { get; }
        public TimeSpan CurrentInterval { get; set; }
        public string? LastObservedState { get; set; }
        public VerifyHandleOutcome Outcome { get; private set; } = VerifyHandleOutcome.Pending;

        public void MarkSucceeded(string state)
        {
            LastObservedState = state;
            Outcome = VerifyHandleOutcome.Succeeded;
        }
        public void MarkFailed(string state)
        {
            LastObservedState = state;
            Outcome = VerifyHandleOutcome.Failed;
        }
        public void MarkTimeout(string state)
        {
            LastObservedState = state;
            Outcome = VerifyHandleOutcome.Timeout;
        }
    }

    private static void EmitDriftWarnings(ILogger logger, string resource, IReadOnlyList<DriftEntry> drifts)
    {
        foreach (var d in drifts)
        {
            var prev = d.IsSecret ? "REDACTED" : (d.Previous ?? "(absent)");
            logger.LogWarning(
                "drift-overwritten: resource={Resource} field={Field} previous={Previous} new={New}",
                resource, d.Field, prev, d.New);
        }
    }

    private static string ResolveHostFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        var slash = tag.IndexOf('/');
        return slash < 0 ? tag : tag.Substring(0, slash);
    }

    private DeployOutcome EmitAndReturnDeploy(DeployDiagnostic diagnostic)
    {
        LastDeployDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return DeployOutcome.Fail(diagnostic);
    }

    private static string DeriveHost(string prefix)
    {
        var slash = prefix.IndexOf('/');
        return slash < 0 ? prefix : prefix.Substring(0, slash);
    }

    private static string? DeriveHostFromFailures(IReadOnlyList<(string Resource, string Tag)> failures)
    {
        if (failures.Count == 0) return null;
        var tag = failures[0].Tag;
        var slash = tag.IndexOf('/');
        return slash < 0 ? tag : tag.Substring(0, slash);
    }

    private RegistryUpsertOutcome EmitAndReturnUpsert(PushDiagnostic diagnostic)
    {
        LastPushDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return RegistryUpsertOutcome.Fail(diagnostic);
    }

    private PushOutcome EmitAndReturnPush(PushDiagnostic diagnostic, string? passwordValue)
    {
        LastPushDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return PushOutcome.Fail(diagnostic);
    }

    private static string? RedactPassword(string? message, string? passwordValue)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(passwordValue))
        {
            return message;
        }
        return message.Replace(passwordValue, "***", StringComparison.Ordinal);
    }

    private BuildOutcome EmitAndReturnBuild(BuildDiagnostic diagnostic)
    {
        LastBuildDiagnostic = diagnostic;
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return BuildOutcome.Fail(diagnostic);
    }

    private ConfigureOutcome EmitAndReturn(ConfigureDiagnostic diagnostic)
    {
        LastDiagnostic = diagnostic;
        // Single diagnostic, written verbatim to the configured stderr sink. I-3: never
        // includes the token value; I-5: first whitespace-delimited token is the literal symbol.
        ErrorWriter.Write(diagnostic.Format());
        ErrorWriter.Flush();
        return ConfigureOutcome.Fail(diagnostic);
    }

    private static string? Sanitize(string? message, string token)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(token))
        {
            return message;
        }
        return message.Replace(token, "***", StringComparison.Ordinal);
    }
}

/// <summary>
/// Wraps a <see cref="ConfigureDiagnostic"/> so a fail-fast configure phase aborts the
/// pipeline step. The diagnostic text has already been written to stderr by the time this
/// exception is thrown.
/// </summary>
public sealed class CoolifyConfigureFailedException : Exception
{
    public CoolifyConfigureFailedException(ConfigureDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public ConfigureDiagnostic Diagnostic { get; }
}

/// <summary>
/// Wraps a <see cref="BuildDiagnostic"/> so a fail-fast build phase aborts the pipeline step
/// (FT-003 I-7). The diagnostic text has already been written to stderr by the time this
/// exception is thrown.
/// </summary>
public sealed class CoolifyBuildFailedException : Exception
{
    public CoolifyBuildFailedException(BuildDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public BuildDiagnostic Diagnostic { get; }
}

/// <summary>
/// Wraps a <see cref="PushDiagnostic"/> so a fail-fast push phase (or configure-phase
/// registry-upsert step) aborts the pipeline step (FT-004 I-6, I-7). The diagnostic text
/// has already been written to stderr by the time this exception is thrown.
/// </summary>
public sealed class CoolifyPushFailedException : Exception
{
    public CoolifyPushFailedException(PushDiagnostic diagnostic)
        : base(diagnostic.Symbol.Literal())
    {
        Diagnostic = diagnostic;
    }

    public PushDiagnostic Diagnostic { get; }
}
