using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-006 (verify phase — poll Coolify deploy-action handles to
// terminal outcome or overall timeout). Covers TC-010 in full.
public sealed class VerifyPhaseExitCriteriaTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Fakes
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScriptedDeployJobsApi : IDeployJobsApi
    {
        public Dictionary<string, Queue<DeployJobStatusResult>> Script { get; } = new();
        public List<string> StatusCalls { get; } = new();
        public Func<string, string>? UrlOverride { get; set; }

        public Task<DeployJobStatusResult> GetStatusAsync(string handle, CancellationToken cancellationToken)
        {
            StatusCalls.Add(handle);
            if (Script.TryGetValue(handle, out var q) && q.Count > 0)
            {
                var item = q.Count == 1 ? q.Peek() : q.Dequeue();
                return Task.FromResult(item);
            }
            return Task.FromResult(DeployJobStatusResult.InProgress());
        }

        public string GetHumanUrl(string handle) =>
            UrlOverride?.Invoke(handle) ?? $"https://coolify.example/deploy-job/{handle}";
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public ScriptedDeployJobsApi DeployJobsApi { get; } = new();
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));
        public IDeployJobsApi DeployJobs => DeployJobsApi;
    }

    private sealed class Harness : IDisposable
    {
        public CoolifyDeployingPublisher Publisher { get; }
        public StringWriter Stderr { get; }
        public FakeClient Client { get; }
        public List<(TimeSpan Delay, TimeSpan ElapsedAfter)> Sleeps { get; } = new();
        public TimeSpan VirtualElapsed { get; private set; }
        private readonly DistributedApplication _app;

        public Harness()
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
            var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
            var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
            b.WithCoolifyDeploy(url, token);

            _app = b.Build();
            Publisher = b.GetRegisteredCoolifyPublisher()!;
            Stderr = new StringWriter();
            Client = new FakeClient();
            Publisher.ErrorWriter = Stderr;
            Publisher.AppHostInfoProvider = () => ("SampleApp", "1.2.3-test");
            Publisher.ActiveEnvironmentProvider = () => "Production";
            typeof(CoolifyDeployingPublisher)
                .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
                .SetValue(Publisher, Client);

            Publisher.VerifySleeper = (t, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                VirtualElapsed += t;
                Sleeps.Add((t, VirtualElapsed));
                return Task.CompletedTask;
            };
            Publisher.VerifyElapsedProvider = () => VirtualElapsed;
        }

        public void AdvanceElapsedTo(TimeSpan t) => VirtualElapsed = t;

        public Task<VerifyOutcome> Run(IEnumerable<DeployActionHandle> handles, CancellationToken ct = default) =>
            Publisher.RunVerifyAsync(handles.ToList(), NullLogger.Instance, ct);

        public void Dispose() => _app.Dispose();
    }

    private static DeployActionHandle H(string resource, string? jobId = null) =>
        new(resource, $"svc-{resource}", jobId ?? $"job-{resource}");

    // ──────────────────────────────────────────────────────────────────────────
    // A. WithVerifyPolling(...) registration discipline (FT-006 §0)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithVerifyPolling_NullInterval_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var t = b.AddParameter("t", secret: true);
        var u = b.AddParameter("u");
        b.WithCoolifyDeploy(u, t);
        var ex = Assert.Throws<ArgumentNullException>(
            () => b.WithVerifyPolling((TimeSpan?)null, TimeSpan.FromSeconds(10)));
        Assert.Equal("interval", ex.ParamName);
    }

    [Fact]
    public void WithVerifyPolling_NullTimeout_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var t = b.AddParameter("t", secret: true);
        var u = b.AddParameter("u");
        b.WithCoolifyDeploy(u, t);
        var ex = Assert.Throws<ArgumentNullException>(
            () => b.WithVerifyPolling(TimeSpan.FromSeconds(1), (TimeSpan?)null));
        Assert.Equal("timeout", ex.ParamName);
    }

    [Fact]
    public void WithVerifyPolling_ZeroOrNegative_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var t = b.AddParameter("t", secret: true);
        var u = b.AddParameter("u");
        b.WithCoolifyDeploy(u, t);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => b.WithVerifyPolling(TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => b.WithVerifyPolling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void WithVerifyPolling_IsLastCallWins()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var t = b.AddParameter("t", secret: true);
        var u = b.AddParameter("u");
        b.WithCoolifyDeploy(u, t);
        b.WithVerifyPolling(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2));
        b.WithVerifyPolling(TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(5));
        var p = b.GetRegisteredCoolifyPublisher()!;
        Assert.Equal(TimeSpan.FromSeconds(7), p.VerifyInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), p.VerifyTimeout);
    }

    [Fact]
    public void WithVerifyPolling_DefaultsLeftInPlace()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var t = b.AddParameter("t", secret: true);
        var u = b.AddParameter("u");
        b.WithCoolifyDeploy(u, t);
        var p = b.GetRegisteredCoolifyPublisher()!;
        Assert.Equal(TimeSpan.FromSeconds(5), p.VerifyInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), p.VerifyTimeout);
    }

    [Fact]
    public void WithVerifyPolling_BeforeDeploy_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(
            () => b.WithVerifyPolling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // B. Happy path — all handles succeed (FT-006 I-1)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_AllHandlesSucceedOnFirstPoll()
    {
        using var h = new Harness();
        var handles = new[] { H("api"), H("redis"), H("worker") };
        foreach (var hand in handles)
            h.Client.DeployJobsApi.Script[hand.JobHandle!] =
                new Queue<DeployJobStatusResult>(new[] { DeployJobStatusResult.Succeeded() });

        var outcome = await h.Run(handles);
        Assert.True(outcome.Succeeded);
        Assert.Equal(3, h.Client.DeployJobsApi.StatusCalls.Count);
    }

    [Fact]
    public async Task HappyPath_DefaultIntervalIsObservedBeforeRePoll()
    {
        using var h = new Harness();
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] = new Queue<DeployJobStatusResult>(new[]
        {
            DeployJobStatusResult.Queued(),
            DeployJobStatusResult.InProgress(),
            DeployJobStatusResult.Succeeded(),
        });
        var outcome = await h.Run(new[] { hand });
        Assert.True(outcome.Succeeded);
        // First sleep should be the configured 5s default; subsequent sleeps may be larger but
        // capped at 60s.
        Assert.NotEmpty(h.Sleeps);
        Assert.Equal(TimeSpan.FromSeconds(5), h.Sleeps[0].Delay);
        Assert.All(h.Sleeps, s => Assert.True(s.Delay <= TimeSpan.FromSeconds(60)));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // C. Empty handle list short-circuits (FT-006 I-8)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyHandleList_ZeroOutboundCalls_ExitsOk()
    {
        using var h = new Harness();
        var outcome = await h.Run(Array.Empty<DeployActionHandle>());
        Assert.True(outcome.Succeeded);
        Assert.Empty(h.Client.DeployJobsApi.StatusCalls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // D. Per-handle terminal-failure aggregation (FT-006 I-4)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoOfThreeFail_AggregatesAndNamesBoth()
    {
        using var h = new Harness();
        var ha = H("api"); var hr = H("redis"); var hw = H("worker");
        h.Client.DeployJobsApi.Script[ha.JobHandle!] = new(new[] { DeployJobStatusResult.Failed() });
        h.Client.DeployJobsApi.Script[hr.JobHandle!] = new(new[] { DeployJobStatusResult.Succeeded() });
        h.Client.DeployJobsApi.Script[hw.JobHandle!] = new(new[] { DeployJobStatusResult.Failed() });

        var outcome = await h.Run(new[] { ha, hr, hw });
        Assert.False(outcome.Succeeded);
        var err = h.Stderr.ToString();
        Assert.StartsWith("E_VERIFY_FAILED", err);
        Assert.Contains("api", err);
        Assert.Contains("worker", err);
        // Successful sibling not named in the field block (FT-006 §Outputs).
        var failBlock = err.Substring(err.IndexOf("resource(s):"));
        Assert.DoesNotContain("redis", failBlock);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E. Timeout (FT-006 I-3)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllHandlesStuck_ProducesVerifyTimeout()
    {
        using var h = new Harness();
        h.Publisher.VerifyInterval = TimeSpan.FromSeconds(1);
        h.Publisher.VerifyTimeout = TimeSpan.FromSeconds(10);
        var handles = new[] { H("api"), H("redis") };
        foreach (var hand in handles)
            h.Client.DeployJobsApi.Script[hand.JobHandle!] =
                new Queue<DeployJobStatusResult>(new[] { DeployJobStatusResult.InProgress() });

        var outcome = await h.Run(handles);
        Assert.False(outcome.Succeeded);
        var err = h.Stderr.ToString();
        Assert.StartsWith("E_VERIFY_TIMEOUT", err);
        Assert.Contains("elapsed:", err);
        Assert.Contains("api", err);
        Assert.Contains("redis", err);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F. Mixed-outcome precedence — TIMEOUT wins over FAILED (FT-006 I-11)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MixedFailureAndTimeout_TimeoutWins()
    {
        using var h = new Harness();
        h.Publisher.VerifyInterval = TimeSpan.FromSeconds(1);
        h.Publisher.VerifyTimeout = TimeSpan.FromSeconds(5);
        var ha = H("api"); var hr = H("redis");
        // api fails on first poll, redis stays non-terminal until timeout.
        h.Client.DeployJobsApi.Script[ha.JobHandle!] = new(new[] { DeployJobStatusResult.Failed() });
        h.Client.DeployJobsApi.Script[hr.JobHandle!] =
            new(new[] { DeployJobStatusResult.InProgress() });

        var outcome = await h.Run(new[] { ha, hr });
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_VERIFY_TIMEOUT", h.Stderr.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G. 60-second cap invariant under WithVerifyPolling (FT-006 I-9)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SixtySecondCap_HoldsForLargeConfiguredInterval()
    {
        using var h = new Harness();
        h.Publisher.VerifyInterval = TimeSpan.FromMinutes(5);
        h.Publisher.VerifyTimeout = TimeSpan.FromMinutes(30);
        var hand = H("api");
        // 5 polls all non-terminal, then succeed.
        h.Client.DeployJobsApi.Script[hand.JobHandle!] = new(new[]
        {
            DeployJobStatusResult.Queued(),
            DeployJobStatusResult.InProgress(),
            DeployJobStatusResult.InProgress(),
            DeployJobStatusResult.InProgress(),
            DeployJobStatusResult.Succeeded(),
        });

        var outcome = await h.Run(new[] { hand });
        Assert.True(outcome.Succeeded);
        // The first sleep is allowed to be the configured 5min (initial). Subsequent sleeps
        // must be capped at 60s.
        Assert.True(h.Sleeps.Count >= 2);
        Assert.Equal(TimeSpan.FromMinutes(5), h.Sleeps[0].Delay);
        foreach (var s in h.Sleeps.Skip(1))
        {
            Assert.True(s.Delay <= TimeSpan.FromSeconds(60),
                $"sleep {s.Delay} exceeded 60s cap");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H. Transient transport failure tolerance (FT-006 §Error handling)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransientTransportFailure_TreatedAsRetry()
    {
        using var h = new Harness();
        h.Publisher.VerifyInterval = TimeSpan.FromSeconds(1);
        h.Publisher.VerifyTimeout = TimeSpan.FromSeconds(60);
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] = new(new[]
        {
            DeployJobStatusResult.Transient("502 Bad Gateway"),
            DeployJobStatusResult.Succeeded(),
        });

        var outcome = await h.Run(new[] { hand });
        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain("E_VERIFY_PHASE_UNEXPECTED", h.Stderr.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I. 404 → FAILED (FT-006 §Error handling)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotFound_IsTreatedAsFailed()
    {
        using var h = new Harness();
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] =
            new(new[] { DeployJobStatusResult.NotFound() });

        var outcome = await h.Run(new[] { hand });
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_VERIFY_FAILED", h.Stderr.ToString());
        Assert.Contains("404", h.Stderr.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J. Read-only — verify never mutates Coolify (FT-006 I-2)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyOnlyContactsDeployJobsApi()
    {
        // ICoolifyClient on FakeClient exposes ONLY DeployJobs — every other endpoint group
        // falls back to the Unconfigured* default. If FT-006 ever calls anything else, those
        // defaults return failures the test would observe via outcome surface.
        using var h = new Harness();
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] =
            new(new[] { DeployJobStatusResult.Succeeded() });

        var outcome = await h.Run(new[] { hand });
        Assert.True(outcome.Succeeded);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K. Deploy-job URL composition via the client (FT-006 I-12)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployJobUrl_IsComposedByClient_NotHardCoded()
    {
        using var h = new Harness();
        h.Client.DeployJobsApi.UrlOverride = handle => $"client-url-for://{handle}";
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] =
            new(new[] { DeployJobStatusResult.Failed() });

        await h.Run(new[] { hand });
        var err = h.Stderr.ToString();
        Assert.Contains("client-url-for://job-api", err);
    }

    [Fact]
    public void NoHardCodedDeployJobPaths_InSourceFiles()
    {
        // FT-006 I-12: the human-followup URL is composed via client.DeployJobs.GetHumanUrl
        // — no FT-006-authored hard-coded "/projects/" / "/deployments/" path fragments.
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Aspire.Hosting.Coolify"));
        var files = new[] { "VerifyDiagnostic.cs", "IDeployJobsApi.cs" }
            .Select(n => Path.Combine(dir, n));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("/projects/", text);
            Assert.DoesNotContain("/deployments/", text);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // M. Cancellation pre-empts sleep and timeout (FT-006 §Cancellation)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_DuringSleep_ThrowsOperationCanceled()
    {
        using var h = new Harness();
        h.Publisher.VerifyInterval = TimeSpan.FromSeconds(1);
        h.Publisher.VerifyTimeout = TimeSpan.FromMinutes(10);
        var hand = H("api");
        h.Client.DeployJobsApi.Script[hand.JobHandle!] =
            new(new[] { DeployJobStatusResult.InProgress() });

        using var cts = new CancellationTokenSource();
        // Override the sleeper to cancel mid-sleep on first invocation.
        h.Publisher.VerifySleeper = (t, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Publisher.RunVerifyAsync(new[] { hand }, NullLogger.Instance, cts.Token));
        // No E_ symbol surfaced.
        Assert.DoesNotContain("E_VERIFY", h.Stderr.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // O. Three E_… symbols are exact (FT-006 I-5)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeSymbols_HaveExactObservableLiterals()
    {
        Assert.Equal("E_VERIFY_FAILED", VerifySymbol.VerifyFailed.Literal());
        Assert.Equal("E_VERIFY_TIMEOUT", VerifySymbol.VerifyTimeout.Literal());
        Assert.Equal("E_VERIFY_PHASE_UNEXPECTED", VerifySymbol.VerifyPhaseUnexpected.Literal());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // N. Phase-boundary log: verify: exit (failed) on fail-fast, (ok) on success.
    //    Exercised at the RunPhaseAsync layer (CoolifyDeployTests covers boundary
    //    emission across phases).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientException_SurfacesPhaseUnexpected()
    {
        using var h = new Harness();
        var hand = H("api");
        var altClient = new ThrowingClient(new ThrowingDeployJobsApi());
        typeof(CoolifyDeployingPublisher)
            .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
            .SetValue(h.Publisher, altClient);

        var outcome = await h.Publisher.RunVerifyAsync(new[] { hand }, NullLogger.Instance, default);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("E_VERIFY_PHASE_UNEXPECTED", h.Stderr.ToString());
    }

    private sealed class ThrowingDeployJobsApi : IDeployJobsApi
    {
        public Task<DeployJobStatusResult> GetStatusAsync(string handle, CancellationToken ct) =>
            throw new InvalidOperationException("TLS handshake failure");
        public string GetHumanUrl(string handle) => $"https://x/{handle}";
    }

    private sealed class ThrowingClient : ICoolifyClient
    {
        public ThrowingClient(IDeployJobsApi api) => DeployJobs = api;
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));
        public IDeployJobsApi DeployJobs { get; }
    }
}
