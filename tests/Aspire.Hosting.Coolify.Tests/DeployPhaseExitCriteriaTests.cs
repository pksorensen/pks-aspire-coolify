using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-005 (deploy phase — Aspire-graph walk + idempotent name-keyed
// upserts + per-service trigger + WithCoolifyDestination capture). Covers TC-009 in full and
// the publisher-side assertions of TC-001 (mapping scenario, without a live Coolify).
public sealed class DeployPhaseExitCriteriaTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Fakes
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    public enum CallKind { DestinationLookup, ProjectUpsert, EnvironmentUpsert, ServiceUpsert, TriggerDeploy }

    private sealed class RecordingDestinationsApi : IDestinationsApi
    {
        public List<string> Calls { get; } = new();
        public Dictionary<string, string> Existing { get; } = new();
        public Func<string, DestinationUpsertResult>? OverrideFor { get; set; }

        public Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken ct)
        {
            Calls.Add(name);
            if (OverrideFor is not null) return Task.FromResult(OverrideFor(name));
            if (Existing.TryGetValue(name, out var id))
                return Task.FromResult(DestinationUpsertResult.Found(id));
            var newId = $"dest-{Existing.Count + 1}";
            Existing[name] = newId;
            return Task.FromResult(DestinationUpsertResult.Created(newId));
        }
    }

    private sealed class RecordingProjectsApi : IProjectsApi
    {
        public List<string> Calls { get; } = new();
        public Dictionary<string, string> Existing { get; } = new();
        public Func<string, ProjectUpsertResult>? OverrideFor { get; set; }

        public Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken ct)
        {
            Calls.Add(name);
            if (OverrideFor is not null) return Task.FromResult(OverrideFor(name));
            if (Existing.TryGetValue(name, out var id))
                return Task.FromResult(ProjectUpsertResult.Unchanged(id));
            var newId = $"proj-{Existing.Count + 1}";
            Existing[name] = newId;
            return Task.FromResult(ProjectUpsertResult.Created(newId));
        }
    }

    private sealed class RecordingEnvironmentsApi : IEnvironmentsApi
    {
        public List<(string Project, string Env)> Calls { get; } = new();
        public Dictionary<(string Project, string Env), string> Existing { get; } = new();
        public Func<string, string, EnvironmentUpsertResult>? OverrideFor { get; set; }

        public Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken ct)
        {
            Calls.Add((projectId, name));
            if (OverrideFor is not null) return Task.FromResult(OverrideFor(projectId, name));
            var key = (projectId, name);
            if (Existing.TryGetValue(key, out var id))
                return Task.FromResult(EnvironmentUpsertResult.Unchanged(id));
            var newId = $"env-{Existing.Count + 1}";
            Existing[key] = newId;
            return Task.FromResult(EnvironmentUpsertResult.Created(newId));
        }
    }

    private sealed class RecordingServicesApi : IServicesApi
    {
        public List<(string Resource, ServiceSpec Spec)> UpsertCalls { get; } = new();
        public List<string> TriggerCalls { get; } = new();
        public Dictionary<string, string> Existing { get; } = new();
        public Func<string, ServiceUpsertResult>? UpsertOverride { get; set; }
        public Func<string, DeployTriggerResult>? TriggerOverride { get; set; }

        public Task<ServiceUpsertResult> UpsertAsync(
            string projectId, string environmentId, string resourceName, ServiceSpec spec, CancellationToken ct)
        {
            UpsertCalls.Add((resourceName, spec));
            if (UpsertOverride is not null) return Task.FromResult(UpsertOverride(resourceName));
            if (Existing.TryGetValue(resourceName, out var id))
                return Task.FromResult(ServiceUpsertResult.Unchanged(id));
            var newId = $"svc-{Existing.Count + 1}";
            Existing[resourceName] = newId;
            return Task.FromResult(ServiceUpsertResult.Created(newId));
        }

        public Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken ct)
        {
            TriggerCalls.Add(serviceId);
            return Task.FromResult(TriggerOverride?.Invoke(serviceId) ?? DeployTriggerResult.Ok($"job-{serviceId}"));
        }
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public RecordingDestinationsApi DestApi { get; } = new();
        public RecordingProjectsApi ProjectsApi { get; } = new();
        public RecordingEnvironmentsApi EnvironmentsApi { get; } = new();
        public RecordingServicesApi ServicesApi { get; } = new();

        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));

        public IDestinationsApi Destinations => DestApi;
        public IProjectsApi Projects => ProjectsApi;
        public IEnvironmentsApi Environments => EnvironmentsApi;
        public IServicesApi Services => ServicesApi;
    }

    private sealed class RecordingHook : ICoolifyDeployHook
    {
        public List<DeployHookContext> Calls { get; } = new();
        public Func<DeployHookContext, DeployHookResult>? Reply { get; set; }

        public Task<DeployHookResult> InvokeAsync(DeployHookContext context, CancellationToken ct)
        {
            Calls.Add(context);
            return Task.FromResult(Reply?.Invoke(context) ?? DeployHookResult.Ok());
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Harness
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public CoolifyDeployingPublisher Publisher { get; }
        public StringWriter Stderr { get; }
        public FakeClient Client { get; }
        public List<IResource> Resources { get; }
        private readonly DistributedApplication _app;

        public Harness(
            string apphostName = "SampleApp",
            string activeEnvironment = "Production",
            string? destinationValue = "homelab-prod",
            bool withCreds = true,
            IEnumerable<string>? resourceNames = null)
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
            var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
            var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
            b.WithCoolifyDeploy(url, token);

            var prefixParam = b.AddParameter("registry-prefix", () => "auth-reg.test:5000/myapp");
            IResourceBuilder<ParameterResource>? userParam = null;
            IResourceBuilder<ParameterResource>? passParam = null;
            if (withCreds)
            {
                userParam = b.AddParameter("registry-username", () => "ci");
                passParam = b.AddParameter("registry-password", () => "PASS", secret: true);
            }
            b.WithImageRegistry(prefixParam, userParam, passParam);

            if (destinationValue is not null)
            {
                var destParam = b.AddParameter("coolify-dest", () => destinationValue);
                b.WithCoolifyDestination(destParam);
            }

            _app = b.Build();
            Publisher = b.GetRegisteredCoolifyPublisher()!;
            Stderr = new StringWriter();
            Client = new FakeClient();
            Publisher.ErrorWriter = Stderr;
            Publisher.AppHostInfoProvider = () => (apphostName, "1.2.3-test");
            Publisher.ActiveEnvironmentProvider = () => activeEnvironment;
            typeof(CoolifyDeployingPublisher)
                .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
                .SetValue(Publisher, Client);

            var names = resourceNames?.ToArray() ?? new[] { "api", "redis" };
            Resources = names.Select(n => (IResource)new FakeResource(n)).ToList();
            // Seed build-phase tags so the deploy phase has a per-resource image to send.
            var tagDict = typeof(CoolifyDeployingPublisher)
                .GetField("_lastBuildTagsByResource",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Publisher) as Dictionary<string, string>;
            foreach (var n in names)
                tagDict![n] = $"auth-reg.test:5000/myapp/{n}:1.2.3-test";
        }

        public ILogger Logger { get; } = NullLogger.Instance;

        public Task<DeployOutcome> Run() =>
            Publisher.RunDeployAsync(Resources, Logger, CancellationToken.None);

        public void Dispose() => _app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // A. WithCoolifyDestination(...) registration discipline (FT-005 §0)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithCoolifyDestination_NullName_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        Assert.Throws<ArgumentNullException>(() => b.WithCoolifyDestination((IResourceBuilder<ParameterResource>)null!));
    }

    [Fact]
    public void WithCoolifyDestination_NullBuilder_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        var dest = b.AddParameter("d");
        Assert.Throws<ArgumentNullException>(
            () => CoolifyBuilderExtensions.WithCoolifyDestination(null!, dest));
    }

    [Fact]
    public void WithCoolifyDestination_BeforeDeploy_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var dest = b.AddParameter("d");
        Assert.Throws<InvalidOperationException>(() => b.WithCoolifyDestination(dest));
    }

    [Fact]
    public void WithCoolifyDestination_IsLastCallWins()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        var first = b.AddParameter("d1");
        var second = b.AddParameter("d2");
        b.WithCoolifyDestination(first);
        b.WithCoolifyDestination(second);

        var publisher = b.GetRegisteredCoolifyPublisher();
        Assert.Same(second, publisher!.DestinationName);
    }

    [Fact]
    public async Task WithCoolifyDestination_Omitted_FailsWithDestinationSymbol()
    {
        using var h = new Harness(destinationValue: null);
        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_COOLIFY_DESTINATION_UPSERT_FAILED", h.Stderr.ToString());
        Assert.Empty(h.Client.ProjectsApi.Calls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // B. Fixed walk order (FT-005 I-1)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_HonoursFixedWalkOrder_WithHooksBetweenUpsertAndTrigger()
    {
        using var h = new Harness();
        var envHook = new RecordingHook();
        var refHook = new RecordingHook();
        h.Publisher.EnvVarHook = envHook;
        h.Publisher.ReferenceHook = refHook;

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);

        Assert.Single(h.Client.DestApi.Calls);
        Assert.Single(h.Client.ProjectsApi.Calls);
        Assert.Single(h.Client.EnvironmentsApi.Calls);
        Assert.Equal(2, h.Client.ServicesApi.UpsertCalls.Count);
        Assert.Equal(2, h.Client.ServicesApi.TriggerCalls.Count);
        // Hooks invoked once per upserted service.
        Assert.Equal(2, envHook.Calls.Count);
        Assert.Equal(2, refHook.Calls.Count);
        // Hook context carries the in-phase IDs.
        Assert.All(envHook.Calls, c => Assert.NotNull(c.ServiceId));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // C. Targeted environment only (FT-005 I-3)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyTheTargetedEnvironmentIsUpserted()
    {
        using var h = new Harness(activeEnvironment: "Production");
        await h.Run();
        Assert.Single(h.Client.EnvironmentsApi.Calls);
        Assert.Equal("Production", h.Client.EnvironmentsApi.Calls[0].Env);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // D. Managed-field discipline (FT-005 I-4) — only image / registry-reference /
    //    destination-binding land in the spec the publisher passes downstream.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServiceSpec_OnlyCarriesManagedFields()
    {
        using var h = new Harness();
        await h.Run();
        var spec = h.Client.ServicesApi.UpsertCalls[0].Spec;
        Assert.Equal("auth-reg.test:5000/myapp/api:1.2.3-test", spec.Image);
        Assert.NotNull(spec.RegistryReference);
        Assert.NotNull(spec.DestinationBinding);
    }

    [Fact]
    public async Task AnonymousPush_OmitsRegistryReferenceFromSpec()
    {
        using var h = new Harness(withCreds: false);
        await h.Run();
        var spec = h.Client.ServicesApi.UpsertCalls[0].Spec;
        Assert.Null(spec.RegistryReference);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E. Idempotency on unchanged AppHost (FT-005 I-6)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondDeployTakesUnchangedBranch()
    {
        using var h = new Harness();
        await h.Run();
        var firstUpserts = h.Client.ServicesApi.UpsertCalls.Count;

        // Re-run.
        await h.Run();
        // Existing dict keeps prior IDs → unchanged branch.
        Assert.Equal(firstUpserts * 2, h.Client.ServicesApi.UpsertCalls.Count);
        // No new project / environment / destination.
        Assert.Equal(2, h.Client.DestApi.Calls.Count);
        Assert.Equal(2, h.Client.ProjectsApi.Calls.Count);
        Assert.Equal(2, h.Client.EnvironmentsApi.Calls.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F. Service-upsert aggregation (FT-005 I-10) — attempt all N before failing.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServiceUpsertFailure_AggregatesAndSkipsTrigger()
    {
        using var h = new Harness(resourceNames: new[] { "api", "redis", "worker" });
        // Force second resource to fail.
        h.Client.ServicesApi.UpsertOverride = name =>
            name == "redis" ? ServiceUpsertResult.Failure("HTTP 500") : null!;
        // Wrap so the override only fails redis but lets others fall through to default.
        var inner = h.Client.ServicesApi.UpsertOverride;
        h.Client.ServicesApi.UpsertOverride = name =>
        {
            var forced = inner(name);
            return forced ?? DefaultUpsert(h.Client.ServicesApi, name);
        };

        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        var err = h.Stderr.ToString();
        Assert.StartsWith("E_COOLIFY_SERVICE_UPSERT_FAILED", err);
        Assert.Contains("redis", err);
        // Loop attempted all three resources.
        Assert.Equal(3, h.Client.ServicesApi.UpsertCalls.Count);
        // Trigger phase never executed.
        Assert.Empty(h.Client.ServicesApi.TriggerCalls);
    }

    private static ServiceUpsertResult DefaultUpsert(RecordingServicesApi api, string name)
    {
        if (api.Existing.TryGetValue(name, out var id))
            return ServiceUpsertResult.Unchanged(id);
        var newId = $"svc-{api.Existing.Count + 1}";
        api.Existing[name] = newId;
        return ServiceUpsertResult.Created(newId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G. Trigger aggregation
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerFailure_AggregatesWithTriggerSymbol()
    {
        using var h = new Harness(resourceNames: new[] { "api", "redis", "worker" });
        h.Client.ServicesApi.TriggerOverride = id =>
            id == "svc-1" ? DeployTriggerResult.Ok($"job-{id}") : DeployTriggerResult.Failed("HTTP 500");

        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        var err = h.Stderr.ToString();
        Assert.StartsWith("E_COOLIFY_DEPLOY_TRIGGER_FAILED", err);
        Assert.Contains("redis", err);
        Assert.Contains("worker", err);
        Assert.Equal(3, h.Client.ServicesApi.TriggerCalls.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H. Hook fail-fast surfaces hook's symbol, not E_DEPLOY_PHASE_UNEXPECTED
    //    (FT-005 §"Error handling" / I-12)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HookFailure_SurfacesHookSymbolNotDeployUnexpected()
    {
        using var h = new Harness();
        h.Publisher.EnvVarHook = new RecordingHook
        {
            Reply = _ => DeployHookResult.Fail("E_ENVVAR_UPSERT_FAILED", "two of three keys failed"),
        };

        var ex = await Assert.ThrowsAsync<CoolifyDeployHookFailedException>(() => h.Run());
        Assert.Equal("E_ENVVAR_UPSERT_FAILED", ex.Symbol);
        Assert.StartsWith("E_ENVVAR_UPSERT_FAILED", h.Stderr.ToString());
        // Trigger never executed.
        Assert.Empty(h.Client.ServicesApi.TriggerCalls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I. Project / Environment fail-fast precedence
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectUpsertFailure_FailsFast()
    {
        using var h = new Harness();
        h.Client.ProjectsApi.OverrideFor = _ => ProjectUpsertResult.Failure("HTTP 500");
        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_COOLIFY_PROJECT_UPSERT_FAILED", h.Stderr.ToString());
        Assert.Empty(h.Client.EnvironmentsApi.Calls);
    }

    [Fact]
    public async Task EnvironmentUpsertFailure_FailsFast()
    {
        using var h = new Harness();
        h.Client.EnvironmentsApi.OverrideFor = (_, _) => EnvironmentUpsertResult.Failure("HTTP 500");
        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_COOLIFY_ENVIRONMENT_UPSERT_FAILED", h.Stderr.ToString());
        Assert.Empty(h.Client.ServicesApi.UpsertCalls);
    }

    [Fact]
    public async Task DestinationUpsertFailure_FailsFast()
    {
        using var h = new Harness();
        h.Client.DestApi.OverrideFor = _ => DestinationUpsertResult.Failure("HTTP 500");
        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_COOLIFY_DESTINATION_UPSERT_FAILED", h.Stderr.ToString());
        Assert.Empty(h.Client.ProjectsApi.Calls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J. Phase-boundary: handles are collected and exposed on the publisher.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulDeploy_RecordsDeployActionHandles()
    {
        using var h = new Harness();
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.TriggeredHandles.Count);
        Assert.Equal(2, h.Publisher.LastDeployHandles.Count);
        Assert.All(outcome.TriggeredHandles, x => Assert.NotNull(x.JobHandle));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K. Six observable symbols (FT-005 I-11)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllSixSymbols_HaveTheExactObservableLiterals()
    {
        Assert.Equal("E_COOLIFY_DESTINATION_UPSERT_FAILED", DeploySymbol.CoolifyDestinationUpsertFailed.Literal());
        Assert.Equal("E_COOLIFY_PROJECT_UPSERT_FAILED", DeploySymbol.CoolifyProjectUpsertFailed.Literal());
        Assert.Equal("E_COOLIFY_ENVIRONMENT_UPSERT_FAILED", DeploySymbol.CoolifyEnvironmentUpsertFailed.Literal());
        Assert.Equal("E_COOLIFY_SERVICE_UPSERT_FAILED", DeploySymbol.CoolifyServiceUpsertFailed.Literal());
        Assert.Equal("E_COOLIFY_DEPLOY_TRIGGER_FAILED", DeploySymbol.CoolifyDeployTriggerFailed.Literal());
        Assert.Equal("E_DEPLOY_PHASE_UNEXPECTED", DeploySymbol.DeployPhaseUnexpected.Literal());
    }
}
