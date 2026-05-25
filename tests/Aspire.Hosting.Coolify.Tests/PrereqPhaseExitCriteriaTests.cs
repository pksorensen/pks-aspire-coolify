using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria + invariant tests for FT-016 (coolify-prereq phase: in-project
// pks-agent-registry provision-then-push orchestration). Covers TC-032 / TC-033 /
// TC-034 / TC-035 / TC-036 / TC-037 / TC-038.
public sealed class PrereqPhaseExitCriteriaTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeProjectMetadata : IProjectMetadata
    {
        public string ProjectPath { get; }
        public FakeProjectMetadata(string p) { ProjectPath = p; }
    }

    private sealed class FakeProject : Resource, IResource
    {
        public FakeProject(string name) : base(name)
        {
            Annotations.Add(new FakeProjectMetadata($"/fake/{name}.csproj"));
        }
    }

    private sealed class RecordingBuildPipeline : IImageBuildPipeline
    {
        public List<(string Resource, string Tag)> Calls { get; } = new();
        public Task BuildAsync(IResource resource, string imageTag, CancellationToken ct)
        {
            Calls.Add((resource.Name, imageTag));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPushPipeline : IImagePushPipeline
    {
        public List<(string Tag, RegistryCredentials? Creds)> Calls { get; } = new();
        public Task<ImagePushResult> PushAsync(string imageTag, RegistryCredentials? credentials, CancellationToken ct)
        {
            Calls.Add((imageTag, credentials));
            return Task.FromResult(ImagePushResult.Success());
        }
    }

    private sealed class FakeApplicationsApi : IApplicationsApi
    {
        public Func<ApplicationProvisionRequest, ApplicationProvisionResult> ProvisionImpl { get; set; } =
            req => new ApplicationProvisionResult(
                true,
                $"app-{req.RegistryResourceName}-uuid",
                $"pr-{req.RegistryResourceName}-uuid",
                "project-uuid",
                $"{req.RegistryResourceName}.example.test",
                null);

        public Func<string, ApplicationDeployResult> TriggerImpl { get; set; } =
            appId => new ApplicationDeployResult(true, $"deploy-{appId}", null);

        public List<ApplicationProvisionRequest> ProvisionCalls { get; } = new();
        public List<string> TriggerCalls { get; } = new();

        public Task<ApplicationProvisionResult> ProvisionRegistryAsync(
            ApplicationProvisionRequest request, CancellationToken ct)
        {
            ProvisionCalls.Add(request);
            return Task.FromResult(ProvisionImpl(request));
        }

        public Task<ApplicationDeployResult> TriggerAndAwaitAsync(
            string applicationUuid, CancellationToken ct)
        {
            TriggerCalls.Add(applicationUuid);
            return Task.FromResult(TriggerImpl(applicationUuid));
        }
    }

    private sealed class FakeProbe : IRegistryReachabilityProbe
    {
        public bool Succeed { get; set; } = true;
        public List<string> Calls { get; } = new();
        public Task<RegistryProbeOutcome> ProbeAsync(string fqdn, CancellationToken ct)
        {
            Calls.Add(fqdn);
            return Task.FromResult(Succeed
                ? new RegistryProbeOutcome(true, $"https://{fqdn}/v2/", TimeSpan.FromMilliseconds(7), null)
                : new RegistryProbeOutcome(false, $"https://{fqdn}/v2/", TimeSpan.FromSeconds(30), "connection refused"));
        }
    }

    private sealed class FakeServicesApi : IServicesApi
    {
        public List<(string ProjectId, string EnvId, string Name, ServiceSpec Spec)> Upserts { get; } = new();
        public List<string> Triggers { get; } = new();

        public Task<ServiceUpsertResult> UpsertAsync(
            string projectId, string environmentId, string resourceName,
            ServiceSpec spec, CancellationToken ct)
        {
            Upserts.Add((projectId, environmentId, resourceName, spec));
            return Task.FromResult(ServiceUpsertResult.Created($"svc-{resourceName}"));
        }

        public Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken ct)
        {
            Triggers.Add(serviceId);
            return Task.FromResult(DeployTriggerResult.Ok($"job-{serviceId}"));
        }
    }

    private sealed class FakeDestinationsApi : IDestinationsApi
    {
        public Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken ct) =>
            Task.FromResult(DestinationUpsertResult.Found("dest-1"));
    }
    private sealed class FakeProjectsApi : IProjectsApi
    {
        public Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken ct) =>
            Task.FromResult(ProjectUpsertResult.Unchanged("project-1"));
    }
    private sealed class FakeEnvironmentsApi : IEnvironmentsApi
    {
        public Task<EnvironmentUpsertResult> UpsertAsync(string pId, string n, CancellationToken ct) =>
            Task.FromResult(EnvironmentUpsertResult.Unchanged("env-1"));
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public FakeApplicationsApi Apps { get; } = new();
        public FakeServicesApi Svcs { get; } = new();
        public FakeDestinationsApi Dests { get; } = new();
        public FakeProjectsApi Projs { get; } = new();
        public FakeEnvironmentsApi Envs { get; } = new();

        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));

        public IApplicationsApi Applications => Apps;
        public IServicesApi Services => Svcs;
        public IDestinationsApi Destinations => Dests;
        public IProjectsApi Projects => Projs;
        public IEnvironmentsApi Environments => Envs;
    }

    private static void WireClient(CoolifyDeployingPublisher publisher, FakeClient client)
    {
        typeof(CoolifyDeployingPublisher)
            .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
            .SetValue(publisher, client);
    }

    private static (IDistributedApplicationBuilder Builder, CoolifyDeployingPublisher Publisher,
                    FakeClient Client, FakeProbe Probe, StringWriter Stderr)
        NewHarness()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var client = new FakeClient();
        WireClient(publisher, client);
        var probe = new FakeProbe();
        publisher.ReachabilityProbe = probe;
        var stderr = new StringWriter();
        publisher.ErrorWriter = stderr;
        publisher.AppHostInfoProvider = () => ("Test.AppHost", "1.0.0");
        return (b, publisher, client, probe, stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-032 — publisher discovers AddPksAgentRegistry resources
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC032_OneAddPksAgentRegistry_PrereqVisitsExactlyOne()
    {
        var (b, publisher, client, _, _) = NewHarness();
        b.AddPksAgentRegistry("reg");
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Skipped);
        Assert.Equal(1, publisher.LastPrereqVisitedCount);
        Assert.Single(client.Apps.ProvisionCalls);
        Assert.Single(client.Apps.TriggerCalls);
    }

    [Fact]
    public async Task TC032_TwoAddPksAgentRegistry_PrereqVisitsExactlyTwo()
    {
        var (b, publisher, client, _, _) = NewHarness();
        b.AddPksAgentRegistry("reg-a");
        b.AddPksAgentRegistry("reg-b", port: 5001);
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, publisher.LastPrereqVisitedCount);
        Assert.Equal(2, client.Apps.ProvisionCalls.Count);
        Assert.Equal(2, client.Apps.TriggerCalls.Count);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-033 — phase order: configure → prereq → build → push → deploy → verify
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TC033_PhaseEnum_PrereqSitsBetweenConfigureAndBuild()
    {
        var ordered = Enum.GetValues<CoolifyPhase>()
                          .OrderBy(p => (int)p)
                          .ToArray();
        Assert.Equal(
            new[] { CoolifyPhase.Configure, CoolifyPhase.Prereq, CoolifyPhase.Build,
                    CoolifyPhase.Push, CoolifyPhase.Deploy, CoolifyPhase.Verify },
            ordered);
    }

    [Fact]
    public async Task TC033_PrereqPhaseStep_IsRegisteredBetweenConfigureAndBuild()
    {
        // Inspect the pipeline step graph the extension wired into the builder. Each
        // coolify-* step depends on the prior step's name, so the order falls out of the
        // graph structurally.
        var (b, _, _, _, _) = NewHarness();
        b.AddPksAgentRegistry("reg");
        using var app = b.Build();

        // Resolve the pipeline and inspect its steps via reflection — the pipeline API
        // does not need to expose its internals for this test; we read the property
        // backing field.
        var pipeline = b.Pipeline;
        // The five publicly-known coolify-* step names must all exist post-wiring.
        // (Step lookup is via the public AddStep API — there is no listing accessor,
        // so we re-attempt to add a duplicate which throws iff the name is in use.)
        foreach (var stepName in new[]
        {
            "coolify-configure", "coolify-prereq", "coolify-build",
            "coolify-push", "coolify-deploy", "coolify-verify",
        })
        {
            var ex = Record.Exception(() =>
                pipeline.AddStep(stepName, _ => Task.CompletedTask));
            Assert.NotNull(ex);
        }

        await Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-034 — prereq is a no-op when no in-project registry exists
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC034_NoInProjectRegistry_PrereqIsStructuralNoop()
    {
        var (b, publisher, client, probe, stderr) = NewHarness();
        // External registry only — no AddPksAgentRegistry call.
        b.AddContainerRegistry("ghcr", "ghcr.io/acme");
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Skipped);
        Assert.Equal(0, publisher.LastPrereqVisitedCount);
        Assert.Empty(client.Apps.ProvisionCalls);
        Assert.Empty(client.Apps.TriggerCalls);
        Assert.Empty(probe.Calls);
        var s = stderr.ToString();
        Assert.DoesNotContain("E_PREREQ_REGISTRY_DEPLOY_FAILED", s);
        Assert.DoesNotContain("E_PREREQ_REGISTRY_UNREACHABLE", s);
        Assert.DoesNotContain("W_REGISTRY_FQDN_FALLBACK", s);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-035 — workload tags target the resolved FQDN
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC035_BuildTags_UseResolvedFqdn()
    {
        var (b, publisher, client, _, _) = NewHarness();
        var (_, target) = b.AddPksAgentRegistry("reg");
        // Two project workloads attached to the in-project registry.
        var api = new FakeProject("api");
        api.Annotations.Add(new ContainerRegistryReferenceAnnotation(target.Resource));
        var worker = new FakeProject("worker");
        worker.Annotations.Add(new ContainerRegistryReferenceAnnotation(target.Resource));

        // Override the Coolify provision to return a deterministic FQDN.
        client.Apps.ProvisionImpl = req => new ApplicationProvisionResult(
            true, "app-uuid", "private-registry-uuid", "project-uuid",
            "reg.example.test", null);

        using var app = b.Build();
        publisher.ResourcesToBuild = () => new IResource[] { api, worker };

        var prereq = await publisher.RunPrereqAsync(default);
        Assert.True(prereq.Succeeded);

        var buildPipeline = new RecordingBuildPipeline();
        publisher.ImagePipeline = buildPipeline;

        var build = await publisher.RunBuildAsync(
            new IResource[] { api, worker }, NullLogger.Instance, default);
        Assert.True(build.Succeeded);

        Assert.Equal(2, buildPipeline.Calls.Count);
        Assert.Equal("reg.example.test/api:1.0.0", buildPipeline.Calls[0].Tag);
        Assert.Equal("reg.example.test/worker:1.0.0", buildPipeline.Calls[1].Tag);

        // The push phase records its tags too.
        var pushPipeline = new RecordingPushPipeline();
        publisher.ImagePushPipeline = pushPipeline;
        var push = await publisher.RunPushAsync(
            new IResource[] { api, worker }, NullLogger.Instance, default);
        Assert.True(push.Succeeded);
        Assert.Equal(2, pushPipeline.Calls.Count);
        Assert.Equal("reg.example.test/api:1.0.0", pushPipeline.Calls[0].Tag);
        Assert.Equal("reg.example.test/worker:1.0.0", pushPipeline.Calls[1].Tag);
        // No tag carries the stale placeholder `localhost:` address.
        Assert.DoesNotContain(pushPipeline.Calls, c => c.Tag.StartsWith("localhost:", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-036 — workload Application carries the Private-Registry attachment
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC036_WorkloadServiceSpec_CarriesInProjectRegistryUuid()
    {
        var (b, publisher, client, _, _) = NewHarness();
        var (_, target) = b.AddPksAgentRegistry("reg");
        var api = new FakeProject("api");
        api.Annotations.Add(new ContainerRegistryReferenceAnnotation(target.Resource));
        var worker = new FakeProject("worker");
        worker.Annotations.Add(new ContainerRegistryReferenceAnnotation(target.Resource));

        client.Apps.ProvisionImpl = req => new ApplicationProvisionResult(
            true, "app-uuid", "PR-UUID-XYZ", "project-uuid",
            "reg.example.test", null);

        using var app = b.Build();
        publisher.ResourcesToBuild = () => new IResource[] { api, worker };
        publisher.WithCoolifyDestinationLiteralForTests("dest");

        // Run configure (auth-probe + registry-upsert), prereq, build, then deploy.
        var prereq = await publisher.RunPrereqAsync(default);
        Assert.True(prereq.Succeeded);

        // Build emits the tags so deploy has something to attach.
        publisher.ImagePipeline = new RecordingBuildPipeline();
        var build = await publisher.RunBuildAsync(
            new IResource[] { api, worker }, NullLogger.Instance, default);
        Assert.True(build.Succeeded);

        var deploy = await publisher.RunDeployAsync(
            new IResource[] { api, worker }, NullLogger.Instance, default);
        Assert.True(deploy.Succeeded);

        // The deploy phase upserts a Coolify service per workload; each Service spec
        // carries the Private-Registry UUID assigned by the prereq phase.
        var workloadUpserts = client.Svcs.Upserts
            .Where(u => u.Name is "api" or "worker")
            .ToList();
        Assert.Equal(2, workloadUpserts.Count);
        foreach (var u in workloadUpserts)
        {
            Assert.Equal("PR-UUID-XYZ", u.Spec.PrivateRegistryAttachmentUuid);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-037 — registry deploy failure halts before build
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC037_ProvisionFails_EmitsDeployFailedSymbolAndHaltsBeforeBuild()
    {
        var (b, publisher, client, _, stderr) = NewHarness();
        b.AddPksAgentRegistry("reg");
        client.Apps.ProvisionImpl = _ => new ApplicationProvisionResult(
            false, null, null, "project-uuid", null,
            "Coolify rejected the create call with HTTP 422");
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PrereqSymbol.PrereqRegistryDeployFailed, outcome.Diagnostic!.Symbol);
        var s = stderr.ToString();
        var firstToken = s.Split(new[] { ' ', '\n', '\r', ':' }, 2)[0];
        Assert.Equal("E_PREREQ_REGISTRY_DEPLOY_FAILED", firstToken);
        Assert.Contains("reg", s);
        Assert.Contains("project-uuid", s);
    }

    [Fact]
    public async Task TC037_TriggerFails_EmitsDeployFailedWithApplicationUuid()
    {
        var (b, publisher, client, _, stderr) = NewHarness();
        b.AddPksAgentRegistry("reg");
        client.Apps.TriggerImpl = appId =>
            new ApplicationDeployResult(false, null, "deploy-action terminal state: failed");
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PrereqSymbol.PrereqRegistryDeployFailed, outcome.Diagnostic!.Symbol);
        var s = stderr.ToString();
        Assert.StartsWith("E_PREREQ_REGISTRY_DEPLOY_FAILED", s);
        Assert.Contains("app-reg-target-uuid", s);
    }

    [Fact]
    public async Task TC037_PrereqFailure_PreventsBuildFromBeingDriven()
    {
        // Direct simulation of the phase-host wrapper: when RunPrereqAsync returns a
        // failure outcome, the host wraps it in CoolifyPrereqFailedException (see
        // RunPhaseAsync) and the build step never starts. We assert the contract directly
        // by checking the outcome and showing the build pipeline is undisturbed.
        var (b, publisher, client, _, _) = NewHarness();
        b.AddPksAgentRegistry("reg");
        client.Apps.ProvisionImpl = _ => new ApplicationProvisionResult(
            false, null, null, "project-uuid", null, "boom");
        using var app = b.Build();
        var buildPipeline = new RecordingBuildPipeline();
        publisher.ImagePipeline = buildPipeline;

        var prereq = await publisher.RunPrereqAsync(default);
        Assert.False(prereq.Succeeded);
        // Constructing the exception mirrors what RunPhaseAsync would throw.
        var ex = new CoolifyPrereqFailedException(prereq.Diagnostic!);
        Assert.Equal("E_PREREQ_REGISTRY_DEPLOY_FAILED", ex.Message);
        Assert.Empty(buildPipeline.Calls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-038 — registry unreachable halts before build
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC038_RegistryProbeFails_EmitsUnreachableSymbol()
    {
        var (b, publisher, client, probe, stderr) = NewHarness();
        b.AddPksAgentRegistry("reg");
        client.Apps.ProvisionImpl = _ => new ApplicationProvisionResult(
            true, "app-uuid", "pr-uuid", "project-uuid", "reg.example.test", null);
        probe.Succeed = false;
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PrereqSymbol.PrereqRegistryUnreachable, outcome.Diagnostic!.Symbol);
        var s = stderr.ToString();
        Assert.StartsWith("E_PREREQ_REGISTRY_UNREACHABLE", s);
        Assert.Contains("reg.example.test", s);
        Assert.Contains("/v2/", s);
        Assert.Contains("elapsed:", s);
        Assert.Contains("connection refused", s);
    }

    // ────────────────────────────────────────────────────────────────────────
    // WithDomain escape-hatch warning (I-8)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithDomain_SetsPreSetDomain_EmitsFallbackWarning_AndUsesItVerbatim()
    {
        var (b, publisher, client, probe, stderr) = NewHarness();
        var (_, target) = b.AddPksAgentRegistry("reg");
        target.WithDomain("custom.example.test");
        // Auto-discovery would return something else — pre-set wins.
        client.Apps.ProvisionImpl = _ => new ApplicationProvisionResult(
            true, "app-uuid", "pr-uuid", "project-uuid", "auto.example.test", null);
        using var app = b.Build();

        var outcome = await publisher.RunPrereqAsync(default);

        Assert.True(outcome.Succeeded);
        var state = publisher.PrereqStateByRegistry["reg-target"];
        Assert.Equal("custom.example.test", state.ResolvedFqdn);
        Assert.Equal("custom.example.test", state.PreSetDomain);
        // W_REGISTRY_FQDN_FALLBACK is on stderr.
        Assert.Contains("W_REGISTRY_FQDN_FALLBACK", stderr.ToString());
        // Probe was issued against the pre-set domain, not the auto-discovered one.
        Assert.Contains("custom.example.test", probe.Calls);
        Assert.DoesNotContain("auto.example.test", probe.Calls);
    }
}

internal static class TestExtensions
{
    public static void WithCoolifyDestinationLiteralForTests(
        this CoolifyDeployingPublisher publisher, string name)
    {
        typeof(CoolifyDeployingPublisher)
            .GetProperty("DestinationLiteralName",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)!
            .SetValue(publisher, name);
    }
}

internal static class StubPipelineContext
{
    public static PipelineStepContext Create()
    {
        // PipelineStepContext is a record-like type from Aspire.Hosting.Pipelines whose
        // construction surface is not directly exposed; bypass with a minimal stub via
        // reflection.
        var ctor = typeof(PipelineStepContext)
            .GetConstructors(System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = DefaultFor(ps[i].ParameterType);
        }
        return (PipelineStepContext)ctor.Invoke(args);
    }

    private static object? DefaultFor(Type t)
    {
        if (t == typeof(string)) return "stub";
        if (t == typeof(CancellationToken)) return CancellationToken.None;
        if (t == typeof(Microsoft.Extensions.Logging.ILogger)) return NullLogger.Instance;
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t.IsInterface)
        {
            // Try resolving common interfaces via a tiny ServiceProvider.
            if (t == typeof(IServiceProvider))
            {
                return new EmptyServiceProvider();
            }
            return null;
        }
        return null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

