using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Implements ADR-001: Aspire-graph to Coolify-hierarchy mapping (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-010 (managed Aspire dashboard opt-in —
// WithManagedDashboard(dashboardToken) deploy-time upsert). Covers TC-014 in full.
public sealed class ManagedDashboardExitCriteriaTests
{
    private const string DeploySentinel = "SENTINEL_DEPLOY_TOKEN_DO_NOT_LEAK_dashboard";
    private const string DashboardSentinel = "SENTINEL_DASHBOARD_TOKEN_DO_NOT_LEAK";
    private const string DashboardServiceName = "coolify-aspiredashboard";

    // ──────────────────────────────────────────────────────────────────────────
    // Fakes — extend the deploy-phase recorders with a ServiceEnvVars recorder.
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed class RecordingDestinationsApi : IDestinationsApi
    {
        public Dictionary<string, string> Existing { get; } = new();
        public Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken ct)
        {
            if (Existing.TryGetValue(name, out var id))
                return Task.FromResult(DestinationUpsertResult.Found(id));
            var newId = $"dest-{Existing.Count + 1}";
            Existing[name] = newId;
            return Task.FromResult(DestinationUpsertResult.Created(newId));
        }
    }

    private sealed class RecordingProjectsApi : IProjectsApi
    {
        public Dictionary<string, string> Existing { get; } = new();
        public Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken ct)
        {
            if (Existing.TryGetValue(name, out var id))
                return Task.FromResult(ProjectUpsertResult.Unchanged(id));
            var newId = $"proj-{Existing.Count + 1}";
            Existing[name] = newId;
            return Task.FromResult(ProjectUpsertResult.Created(newId));
        }
    }

    private sealed class RecordingEnvironmentsApi : IEnvironmentsApi
    {
        public Dictionary<(string Project, string Env), string> Existing { get; } = new();
        public Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken ct)
        {
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
        public List<(string Project, string Env, string Resource, ServiceSpec Spec)> UpsertCalls { get; } = new();
        public List<string> TriggerCalls { get; } = new();
        public Dictionary<string, string> Existing { get; } = new();
        public Func<string, ServiceUpsertResult?>? UpsertOverride { get; set; }
        public Func<string, DeployTriggerResult?>? TriggerOverride { get; set; }
        public string? DashboardFqdn { get; set; }

        public Task<ServiceUpsertResult> UpsertAsync(
            string projectId, string environmentId, string resourceName, ServiceSpec spec, CancellationToken ct)
        {
            UpsertCalls.Add((projectId, environmentId, resourceName, spec));
            var forced = UpsertOverride?.Invoke(resourceName);
            if (forced is not null) return Task.FromResult(forced);
            if (Existing.TryGetValue(resourceName, out var id))
            {
                var unchanged = ServiceUpsertResult.Unchanged(id);
                if (resourceName == DashboardServiceName && DashboardFqdn is not null)
                    unchanged = unchanged with { Fqdn = DashboardFqdn };
                return Task.FromResult(unchanged);
            }
            var newId = $"svc-{Existing.Count + 1}";
            Existing[resourceName] = newId;
            var created = ServiceUpsertResult.Created(newId);
            if (resourceName == DashboardServiceName && DashboardFqdn is not null)
                created = created with { Fqdn = DashboardFqdn };
            return Task.FromResult(created);
        }

        public Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken ct)
        {
            TriggerCalls.Add(serviceId);
            var forced = TriggerOverride?.Invoke(serviceId);
            if (forced is not null) return Task.FromResult(forced);
            return Task.FromResult(DeployTriggerResult.Ok($"job-{serviceId}"));
        }
    }

    private sealed class RecordingEnvVarsApi : IServiceEnvVarsApi
    {
        public List<(string ServiceId, string Key, string Value, bool Secret, string Op)> Writes { get; } = new();
        public List<(string ServiceId, string Key)> Fetches { get; } = new();
        public HashSet<string> FailKeys { get; } = new();

        public Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken ct)
        {
            Fetches.Add((serviceId, key));
            return Task.FromResult(EnvVarFetchResult.NotFoundResult());
        }

        public Task<EnvVarWriteResult> CreateAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            if (FailKeys.Contains(key))
            {
                // Don't record the value on failure (simulates Coolify error path).
                Writes.Add((serviceId, key, "(failed)", secret, "create-failed"));
                return Task.FromResult(EnvVarWriteResult.Failure($"forced failure on {key}"));
            }
            Writes.Add((serviceId, key, value, secret, "create"));
            return Task.FromResult(EnvVarWriteResult.Ok());
        }

        public Task<EnvVarWriteResult> PatchAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            if (FailKeys.Contains(key))
            {
                Writes.Add((serviceId, key, "(failed)", secret, "patch-failed"));
                return Task.FromResult(EnvVarWriteResult.Failure($"forced failure on {key}"));
            }
            Writes.Add((serviceId, key, value, secret, "patch"));
            return Task.FromResult(EnvVarWriteResult.Ok());
        }
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public RecordingDestinationsApi DestApi { get; } = new();
        public RecordingProjectsApi ProjectsApi { get; } = new();
        public RecordingEnvironmentsApi EnvironmentsApi { get; } = new();
        public RecordingServicesApi ServicesApi { get; } = new();
        public RecordingEnvVarsApi EnvVarsApi { get; } = new();

        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));

        public IDestinationsApi Destinations => DestApi;
        public IProjectsApi Projects => ProjectsApi;
        public IEnvironmentsApi Environments => EnvironmentsApi;
        public IServicesApi Services => ServicesApi;
        public IServiceEnvVarsApi ServiceEnvVars => EnvVarsApi;
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
        public CapturingLogger LogCapture { get; }
        private readonly DistributedApplication _app;

        public Harness(
            string apphostName = "SampleApp",
            string activeEnvironment = "Production",
            string? destinationValue = "homelab-prod",
            bool withDashboard = true,
            string dashboardTokenValue = DashboardSentinel,
            bool dashboardTokenUnset = false,
            string urlValue = "https://coolify.lan",
            IEnumerable<string>? resourceNames = null)
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
            var token = b.AddParameter("coolify-token", () => DeploySentinel, secret: true);
            var url = b.AddParameter("coolify-url", () => urlValue);
            b.WithCoolifyDeploy(url, token);

            var prefixParam = b.AddParameter("registry-prefix", () => "auth-reg.test:5000/myapp");
            var userParam = b.AddParameter("registry-username", () => "ci");
            var passParam = b.AddParameter("registry-password", () => "PASS", secret: true);
            b.WithImageRegistry(prefixParam, userParam, passParam);

            if (destinationValue is not null)
            {
                var destParam = b.AddParameter("coolify-dest", () => destinationValue);
                b.WithCoolifyDestination(destParam);
            }

            if (withDashboard)
            {
                IResourceBuilder<ParameterResource> dashTok;
                if (dashboardTokenUnset)
                {
                    dashTok = b.AddParameter("coolify-homelab-dashboard-token", () => "", secret: true);
                }
                else
                {
                    dashTok = b.AddParameter("coolify-homelab-dashboard-token",
                        () => dashboardTokenValue, secret: true);
                }
                b.WithManagedDashboard(dashTok);
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
            var tagDict = typeof(CoolifyDeployingPublisher)
                .GetField("_lastBuildTagsByResource",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Publisher) as Dictionary<string, string>;
            foreach (var n in names)
                tagDict![n] = $"auth-reg.test:5000/myapp/{n}:1.2.3-test";

            LogCapture = new CapturingLogger();
        }

        public Task<DeployOutcome> Run() =>
            Publisher.RunDeployAsync(Resources, LogCapture, CancellationToken.None);

        public void Dispose() => _app.Dispose();

        public string AllOutput() => Stderr.ToString() + "\n" + LogCapture.AllText();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _lines = new();
        public List<string> Lines => _lines;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Add($"[{logLevel}] {formatter(state, exception)}");
        }
        public string AllText() => string.Join("\n", _lines);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // A. Registration discipline (FT-010 §0)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithManagedDashboard_NullToken_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        Assert.Throws<ArgumentNullException>(() => b.WithManagedDashboard(null!));
    }

    [Fact]
    public void WithManagedDashboard_NullBuilder_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        var dashTok = b.AddParameter("dt", secret: true);
        Assert.Throws<ArgumentNullException>(
            () => CoolifyBuilderExtensions.WithManagedDashboard(null!, dashTok));
    }

    [Fact]
    public void WithManagedDashboard_BeforeDeploy_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var dashTok = b.AddParameter("dt", secret: true);
        Assert.Throws<InvalidOperationException>(() => b.WithManagedDashboard(dashTok));
    }

    [Fact]
    public void WithManagedDashboard_IsLastCallWins_HandleAndStickyOptInFlag()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);
        var first = b.AddParameter("dt1", secret: true);
        var second = b.AddParameter("dt2", secret: true);
        b.WithManagedDashboard(first);
        b.WithManagedDashboard(second);

        var publisher = b.GetRegisteredCoolifyPublisher()!;
        Assert.Same(second, publisher.DashboardToken);
        Assert.True(publisher.DashboardOptedIn);
    }

    [Fact]
    public void WithManagedDashboard_NotCalled_LeavesOptInFalse()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("t", secret: true);
        var url = b.AddParameter("u");
        b.WithCoolifyDeploy(url, token);

        var publisher = b.GetRegisteredCoolifyPublisher()!;
        Assert.False(publisher.DashboardOptedIn);
        Assert.Null(publisher.DashboardToken);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // B. Dashboard NEVER fails the workload (FT-010 I-1) — five warning paths.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenMissing_EmitsWarningAndWorkloadSucceeds()
    {
        using var h = new Harness(dashboardTokenUnset: true);
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.StartsWith("W_DASHBOARD_TOKEN_MISSING", h.Stderr.ToString());
        // Workload triggers ran; dashboard service was never upserted.
        Assert.DoesNotContain(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
    }

    [Fact]
    public async Task DashboardUpsertFailed_EmitsWarningAndWorkloadSucceeds()
    {
        using var h = new Harness();
        h.Client.ServicesApi.UpsertOverride = name =>
            name == DashboardServiceName ? ServiceUpsertResult.Failure("HTTP 500") : null;

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.StartsWith("W_DASHBOARD_UPSERT_FAILED", h.Stderr.ToString());
        // Env-var writes never attempted against dashboard service.
        Assert.Empty(h.Client.EnvVarsApi.Writes);
    }

    [Fact]
    public async Task EnvVarFailed_EmitsWarningAndWorkloadSucceeds_NamesFailingKey()
    {
        using var h = new Harness();
        h.Client.EnvVarsApi.FailKeys.Add("COOLIFY_API_TOKEN");

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        var stderr = h.Stderr.ToString();
        Assert.StartsWith("W_DASHBOARD_ENVVAR_FAILED", stderr);
        Assert.Contains("COOLIFY_API_TOKEN", stderr);
        // No dashboard trigger.
        Assert.DoesNotContain(h.Client.ServicesApi.TriggerCalls,
            id => h.Client.ServicesApi.Existing.GetValueOrDefault(DashboardServiceName) == id);
    }

    [Fact]
    public async Task TriggerFailed_EmitsWarningAndNoHandleAppended()
    {
        using var h = new Harness();
        h.Client.ServicesApi.TriggerOverride = id =>
        {
            // Force the dashboard service's trigger to fail; workload triggers (svc-1 / svc-2)
            // succeed via the default branch.
            var dashId = h.Client.ServicesApi.Existing.GetValueOrDefault(DashboardServiceName);
            return id == dashId ? DeployTriggerResult.Failed("HTTP 500") : null;
        };

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.StartsWith("W_DASHBOARD_TRIGGER_FAILED", h.Stderr.ToString());
        // Workload handles only — no dashboard-tagged handle.
        Assert.DoesNotContain(outcome.TriggeredHandles, h => h.Tag == "dashboard");
        Assert.Equal(2, outcome.TriggeredHandles.Count);
    }

    [Fact]
    public async Task Unexpected_EmitsCatchAllWarningAndWorkloadSucceeds()
    {
        using var h = new Harness();
        h.Client.ServicesApi.UpsertOverride = name =>
        {
            if (name == DashboardServiceName)
                throw new InvalidOperationException("synthetic dashboard kaboom");
            return null;
        };

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        // Service-upsert wrap → W_DASHBOARD_UPSERT_FAILED (specific path wins over catch-all).
        // The catch-all activates only for failures escaping the structured branches.
        var stderr = h.Stderr.ToString();
        Assert.True(
            stderr.StartsWith("W_DASHBOARD_UPSERT_FAILED") ||
            stderr.StartsWith("W_DASHBOARD_UNEXPECTED"),
            $"Expected W_DASHBOARD_UPSERT_FAILED or W_DASHBOARD_UNEXPECTED; got: {stderr}");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // C. Dashboard sub-phase runs only after clean workload deploy (FT-010 I-2)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkloadShortCircuit_SkipsDashboardSubPhase_NoCallsAtAll()
    {
        using var h = new Harness();
        h.Client.ServicesApi.UpsertOverride = name =>
            name == "api" ? ServiceUpsertResult.Failure("HTTP 500") : null;

        var outcome = await h.Run();
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_COOLIFY_SERVICE_UPSERT_FAILED", h.Stderr.ToString());
        // Zero dashboard-attributed calls.
        Assert.DoesNotContain(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
        Assert.DoesNotContain(h.Stderr.ToString(), "W_DASHBOARD");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // D. Same project + targeted environment (FT-010 I-3)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DashboardLandsInTargetedEnvironmentOfSameProject()
    {
        using var h = new Harness(activeEnvironment: "Production");
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);

        var dashCall = Assert.Single(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
        // Resolved IDs match the single project / environment upsert FT-005 issued.
        Assert.Equal("proj-1", dashCall.Project);
        Assert.Equal("env-1", dashCall.Env);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E. Audience separation honoured (FT-010 I-4)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DashboardEnvVarUsesDashboardSentinel_NotDeploySentinel()
    {
        using var h = new Harness();
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);

        var tokenWrite = Assert.Single(h.Client.EnvVarsApi.Writes,
            w => w.Key == "COOLIFY_API_TOKEN");
        Assert.Equal(DashboardSentinel, tokenWrite.Value);
        // The deploy sentinel must never appear on any dashboard-attributed env-var write.
        Assert.DoesNotContain(h.Client.EnvVarsApi.Writes, w => w.Value == DeploySentinel);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F. Image tag is a publisher-pinned constant (FT-010 I-8)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DashboardImageTag_IsPublisherPinnedConstant()
    {
        using var h = new Harness();
        await h.Run();
        var dashCall = Assert.Single(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
        Assert.Equal(CoolifyDeployingPublisher.DashboardImageTag, dashCall.Spec.Image);
        Assert.StartsWith("ghcr.io/", dashCall.Spec.Image);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G. Three required env-vars on the dashboard service (FT-010 §4)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DashboardServiceCarriesThreeRequiredEnvVars()
    {
        using var h = new Harness(urlValue: "https://coolify.example");
        await h.Run();

        var dashId = h.Client.ServicesApi.Existing[DashboardServiceName];
        var dashboardWrites = h.Client.EnvVarsApi.Writes.Where(w => w.ServiceId == dashId).ToList();
        Assert.Equal(3, dashboardWrites.Count);

        var byKey = dashboardWrites.ToDictionary(w => w.Key, w => w);
        Assert.Equal("https://coolify.example", byKey["COOLIFY_API_URL"].Value);
        Assert.False(byKey["COOLIFY_API_URL"].Secret);
        Assert.Equal(DashboardSentinel, byKey["COOLIFY_API_TOKEN"].Value);
        Assert.True(byKey["COOLIFY_API_TOKEN"].Secret);
        Assert.Equal("proj-1", byKey["COOLIFY_PROJECT_UUID"].Value);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H. Name-keyed upsert + managed-field discipline (FT-010 I-9)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DashboardUpsertCarriesOnlyImageAndDestinationBinding()
    {
        using var h = new Harness();
        await h.Run();
        var dashCall = Assert.Single(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
        Assert.Equal(CoolifyDeployingPublisher.DashboardImageTag, dashCall.Spec.Image);
        Assert.Equal("dest-1", dashCall.Spec.DestinationBinding);
        // RegistryReference is null on the dashboard service (public image, anonymous pull).
        Assert.Null(dashCall.Spec.RegistryReference);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I. Aggregated env-var failure attempts all three (FT-010 §4)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvVarFailure_AggregatesAcrossAllThree_NamesBothFailing()
    {
        using var h = new Harness();
        h.Client.EnvVarsApi.FailKeys.Add("COOLIFY_API_URL");
        h.Client.EnvVarsApi.FailKeys.Add("COOLIFY_PROJECT_UUID");

        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        var stderr = h.Stderr.ToString();
        Assert.StartsWith("W_DASHBOARD_ENVVAR_FAILED", stderr);
        Assert.Contains("COOLIFY_API_URL", stderr);
        Assert.Contains("COOLIFY_PROJECT_UUID", stderr);
        // All three were attempted before aggregation.
        var dashId = h.Client.ServicesApi.Existing[DashboardServiceName];
        Assert.Equal(3, h.Client.EnvVarsApi.Fetches.Count(f => f.ServiceId == dashId));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J. FQDN surfacing (FT-010 §Outputs)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FqdnAvailable_LogsManagedDashboardUrlLine()
    {
        using var h = new Harness();
        h.Client.ServicesApi.DashboardFqdn = "dashboard.coolify.lan";
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.Equal("dashboard.coolify.lan", h.Publisher.LastDashboardFqdn);
        Assert.Contains("managed-dashboard: url=https://dashboard.coolify.lan", h.LogCapture.AllText());
    }

    [Fact]
    public async Task FqdnEmpty_LogsPendingForm()
    {
        using var h = new Harness();
        h.Client.ServicesApi.DashboardFqdn = null;
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.Null(h.Publisher.LastDashboardFqdn);
        Assert.Contains("managed-dashboard: url=<pending — check Coolify UI for assigned domain>",
            h.LogCapture.AllText());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K. Dashboard handle appended with `dashboard` tag (FT-010 I-10)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulDashboardTrigger_AppendsHandleWithDashboardTag()
    {
        using var h = new Harness();
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.Equal(3, outcome.TriggeredHandles.Count); // 2 workload + 1 dashboard
        var dashHandle = Assert.Single(outcome.TriggeredHandles, x => x.Tag == "dashboard");
        Assert.Equal(DashboardServiceName, dashHandle.Resource);
        Assert.NotNull(dashHandle.JobHandle);
        // Workload handles carry no tag.
        Assert.Equal(2, outcome.TriggeredHandles.Count(x => x.Tag is null));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // M. Token redaction (FT-010 I-7) — sentinel never appears on stderr.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenSentinel_NeverAppearsOnStderr_HappyPath()
    {
        using var h = new Harness();
        await h.Run();
        Assert.DoesNotContain(DashboardSentinel, h.Stderr.ToString());
        Assert.DoesNotContain(DeploySentinel, h.Stderr.ToString());
    }

    [Fact]
    public async Task TokenSentinel_NeverAppearsOnStderr_WarningPaths()
    {
        // Token-missing path.
        using (var h = new Harness(dashboardTokenUnset: true))
        {
            await h.Run();
            Assert.DoesNotContain(DashboardSentinel, h.Stderr.ToString());
        }
        // Upsert-failed path.
        using (var h = new Harness())
        {
            h.Client.ServicesApi.UpsertOverride = name =>
                name == DashboardServiceName ? ServiceUpsertResult.Failure("HTTP 500") : null;
            await h.Run();
            Assert.DoesNotContain(DashboardSentinel, h.Stderr.ToString());
        }
        // EnvVar-failed path.
        using (var h = new Harness())
        {
            h.Client.EnvVarsApi.FailKeys.Add("COOLIFY_API_TOKEN");
            await h.Run();
            Assert.DoesNotContain(DashboardSentinel, h.Stderr.ToString());
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // N. Opt-out is silent (FT-010 I-6)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OptOut_NoDashboardCalls_NoWarnings()
    {
        using var h = new Harness(withDashboard: false);
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain(h.Client.ServicesApi.UpsertCalls,
            c => c.Resource == DashboardServiceName);
        Assert.Empty(h.Client.EnvVarsApi.Writes);
        var stderr = h.Stderr.ToString();
        Assert.DoesNotContain("W_DASHBOARD", stderr);
        Assert.DoesNotContain("managed-dashboard", h.LogCapture.AllText());
        // No dashboard-tagged handle.
        Assert.DoesNotContain(outcome.TriggeredHandles, x => x.Tag == "dashboard");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Q. Stable observable contract (FT-010 I-13)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllFiveSymbols_HaveTheExactObservableLiterals()
    {
        Assert.Equal("W_DASHBOARD_TOKEN_MISSING", DashboardSymbol.DashboardTokenMissing.Literal());
        Assert.Equal("W_DASHBOARD_UPSERT_FAILED", DashboardSymbol.DashboardUpsertFailed.Literal());
        Assert.Equal("W_DASHBOARD_ENVVAR_FAILED", DashboardSymbol.DashboardEnvVarFailed.Literal());
        Assert.Equal("W_DASHBOARD_TRIGGER_FAILED", DashboardSymbol.DashboardTriggerFailed.Literal());
        Assert.Equal("W_DASHBOARD_UNEXPECTED", DashboardSymbol.DashboardUnexpected.Literal());
    }

    [Fact]
    public async Task AllWarnings_CarrySeverityWarning_AsFirstStructuredField()
    {
        // Token-missing path.
        using var h = new Harness(dashboardTokenUnset: true);
        await h.Run();
        var stderr = h.Stderr.ToString();
        Assert.Contains("severity:    warning", stderr);
    }

    [Fact]
    public async Task LastDashboardDiagnostic_IsNullOnSuccess()
    {
        using var h = new Harness();
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded);
        Assert.Null(h.Publisher.LastDashboardDiagnostic);
    }
}
