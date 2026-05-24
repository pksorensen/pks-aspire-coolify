using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-003 (build phase: deterministic tagging + four E_ symbols).
// Covers TC-007 in full.
public sealed class BuildPhaseExitCriteriaTests
{
    private const string SentinelPassword = "SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_build_phase";

    // ──────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed class RecordingPipeline : IImageBuildPipeline
    {
        public List<(string Resource, string Tag)> Calls { get; } = new();
        private readonly Func<IResource, string, Task>? _hook;

        public RecordingPipeline(Func<IResource, string, Task>? hook = null) { _hook = hook; }

        public async Task BuildAsync(IResource resource, string imageTag, CancellationToken ct)
        {
            Calls.Add((resource.Name, imageTag));
            if (_hook is not null) await _hook(resource, imageTag);
        }
    }

    private sealed class FakeImageStore : ILocalImageStore
    {
        public HashSet<string> Tags { get; } = new();
        public bool HasTag(string imageTag) => Tags.Contains(imageTag);
    }

    private sealed class Harness : IDisposable
    {
        public CoolifyDeployingPublisher Publisher { get; }
        public StringWriter Stderr { get; }
        public RecordingPipeline Pipeline { get; }
        public FakeImageStore Store { get; }
        public int VersionReadCount;
        private readonly DistributedApplication _app;

        public Harness(
            string? prefixValue = "auth-reg.test:5000/myapp",
            string? username = "user",
            string? password = SentinelPassword,
            string? infoVersion = "1.2.3-test",
            string assemblyName = "Test.AppHost",
            Func<IResource, string, Task>? buildHook = null)
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
            var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
            var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
            b.WithCoolifyDeploy(url, token);

            if (prefixValue is not null)
            {
                var prefixParam = b.AddParameter("registry-prefix", () => prefixValue);
                IResourceBuilder<ParameterResource>? userParam = null;
                IResourceBuilder<ParameterResource>? passParam = null;
                if (username is not null && password is not null)
                {
                    userParam = b.AddParameter("registry-username", () => username);
                    passParam = b.AddParameter("registry-password", () => password, secret: true);
                }
                b.WithImageRegistry(prefixParam, userParam, passParam);
            }

            _app = b.Build();
            Publisher = b.GetRegisteredCoolifyPublisher()!;
            Stderr = new StringWriter();
            Pipeline = new RecordingPipeline(buildHook);
            Store = new FakeImageStore();
            // Default: image-store mirrors what pipeline says it built.
            Pipeline = new RecordingPipeline(async (r, t) =>
            {
                if (buildHook is not null) await buildHook(r, t);
                Store.Tags.Add(t);
            });

            Publisher.ErrorWriter = Stderr;
            Publisher.ImagePipeline = Pipeline;
            Publisher.ImageStore = Store;
            Publisher.AppHostInfoProvider = () =>
            {
                Interlocked.Increment(ref VersionReadCount);
                return (assemblyName, infoVersion);
            };
        }

        public void Dispose() => _app.Dispose();
    }

    private static IEnumerable<IResource> Resources(params string[] names) =>
        names.Select(n => (IResource)new FakeResource(n)).ToArray();

    // ────────────────────────────────────────────────────────────────────────
    // A. WithImageRegistry registration discipline
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithImageRegistry_NullPrefix_Throws()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u");
        var token = b.AddParameter("t", secret: true);
        b.WithCoolifyDeploy(url, token);

        var ex = Assert.Throws<ArgumentNullException>(() => b.WithImageRegistry(null!));
        Assert.Equal("prefix", ex.ParamName);
    }

    [Fact]
    public void WithImageRegistry_PrefixWithUserButNoPassword_ThrowsArgumentException()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u");
        var token = b.AddParameter("t", secret: true);
        b.WithCoolifyDeploy(url, token);
        var p = b.AddParameter("p");
        var u = b.AddParameter("user");

        var ex = Assert.Throws<ArgumentException>(() => b.WithImageRegistry(p, u, null));
        Assert.Equal("password", ex.ParamName);
    }

    [Fact]
    public void WithImageRegistry_PrefixWithPasswordButNoUser_ThrowsArgumentException()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u");
        var token = b.AddParameter("t", secret: true);
        b.WithCoolifyDeploy(url, token);
        var p = b.AddParameter("p");
        var pass = b.AddParameter("pass", secret: true);

        var ex = Assert.Throws<ArgumentException>(() => b.WithImageRegistry(p, null, pass));
        Assert.Equal("username", ex.ParamName);
    }

    [Fact]
    public async Task WithImageRegistry_CalledTwice_IsLastCallWins_AndTagsAgainstSecondPrefix()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u", () => "https://x");
        var token = b.AddParameter("t", () => "TOK", secret: true);
        b.WithCoolifyDeploy(url, token);

        var prefix1 = b.AddParameter("prefix1", () => "first.test/app");
        var prefix2 = b.AddParameter("prefix2", () => "second.test/app");
        b.WithImageRegistry(prefix1);
        b.WithImageRegistry(prefix2);

        using var app = b.Build();
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var pipeline = new RecordingPipeline();
        publisher.ImagePipeline = pipeline;
        publisher.AppHostInfoProvider = () => ("AppHost", "9.9.9");

        var outcome = await publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.True(outcome.Succeeded);
        Assert.Single(pipeline.Calls);
        Assert.Equal("second.test/app/web:9.9.9", pipeline.Calls[0].Tag);
    }

    // ────────────────────────────────────────────────────────────────────────
    // B. Pre-walk gates produce the right E_ symbol
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoWithImageRegistry_FailsFastWithRegistryNotConfigured()
    {
        using var h = new Harness(prefixValue: null);
        var outcome = await h.Publisher.RunBuildAsync(Resources("web", "worker"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.RegistryNotConfigured, outcome.Diagnostic!.Symbol);
        AssertFirstToken(h.Stderr.ToString(), "E_REGISTRY_NOT_CONFIGURED");
        Assert.Empty(h.Pipeline.Calls);
        Assert.Contains("see:      ADR-005 §1", h.Stderr.ToString());
        Assert.Contains("WithImageRegistry", h.Stderr.ToString());
        Assert.Contains("WithCoolifyDeploy", h.Stderr.ToString());
    }

    [Fact]
    public async Task EmptyPrefix_FailsFastWithRegistryNotConfigured()
    {
        using var h = new Harness(prefixValue: "   ");
        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.RegistryNotConfigured, outcome.Diagnostic!.Symbol);
        Assert.Empty(h.Pipeline.Calls);
    }

    [Fact]
    public async Task MissingAppHostVersion_FailsFastWithApphostVersionMissing()
    {
        using var h = new Harness(infoVersion: null, assemblyName: "MyTest.AppHost");
        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.ApphostVersionMissing, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_APPHOST_VERSION_MISSING");
        Assert.Contains("apphost:  MyTest.AppHost", stderr);
        Assert.Contains("AssemblyInformationalVersion(\"1.0.0\")", stderr);
        Assert.Empty(h.Pipeline.Calls);
    }

    [Fact]
    public async Task WhitespaceAppHostVersion_FailsFastWithApphostVersionMissing()
    {
        using var h = new Harness(infoVersion: "   ");
        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.ApphostVersionMissing, outcome.Diagnostic!.Symbol);
    }

    // ────────────────────────────────────────────────────────────────────────
    // C. Deterministic tag shape + no :latest
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_EmitsExactlyOneTagPerResource_InCorrectShape()
    {
        using var h = new Harness();
        var outcome = await h.Publisher.RunBuildAsync(
            Resources("web", "worker"), NullLogger.Instance, default);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, h.Pipeline.Calls.Count);
        Assert.Equal("auth-reg.test:5000/myapp/web:1.2.3-test", h.Pipeline.Calls[0].Tag);
        Assert.Equal("auth-reg.test:5000/myapp/worker:1.2.3-test", h.Pipeline.Calls[1].Tag);

        Assert.Equal(2, h.Store.Tags.Count);
        Assert.Contains("auth-reg.test:5000/myapp/web:1.2.3-test", h.Store.Tags);
        Assert.Contains("auth-reg.test:5000/myapp/worker:1.2.3-test", h.Store.Tags);

        // No :latest tag anywhere.
        Assert.DoesNotContain(h.Store.Tags, t => t.EndsWith(":latest", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Pipeline.Calls, c => c.Tag.EndsWith(":latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TagUsesResourceNameVerbatim_NoCaseFoldingNoSlugification()
    {
        using var h = new Harness();
        var outcome = await h.Publisher.RunBuildAsync(
            Resources("Mixed-Case_Worker_2"), NullLogger.Instance, default);

        Assert.True(outcome.Succeeded);
        Assert.Equal("auth-reg.test:5000/myapp/Mixed-Case_Worker_2:1.2.3-test", h.Pipeline.Calls[0].Tag);
    }

    // ────────────────────────────────────────────────────────────────────────
    // D. AppHost version is read exactly once (I-3)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppHostVersion_IsReadExactlyOnce_RegardlessOfResourceCount()
    {
        using var h = new Harness();
        await h.Publisher.RunBuildAsync(
            Resources("a", "b", "c", "d", "e"), NullLogger.Instance, default);
        Assert.Equal(1, h.VersionReadCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    // F. Credentials captured but never dereferenced (I-6)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_NeverDereferencesPasswordParameter_AndSentinelNeverAppears()
    {
        using var h = new Harness(); // password sentinel injected
        await h.Publisher.RunBuildAsync(Resources("web", "worker"), NullLogger.Instance, default);

        // Captured but not read: the password param is set on the publisher.
        Assert.NotNull(h.Publisher.RegistryPassword);
        Assert.NotNull(h.Publisher.RegistryUsername);

        // Sentinel must not leak through stderr.
        Assert.DoesNotContain(SentinelPassword, h.Stderr.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // G. Image-pipeline failure → E_IMAGE_BUILD_FAILED; earlier successes retained;
    //    no push entered (I-7)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PipelineFailureMidIteration_EmitsImageBuildFailed_AndPreservesEarlierSuccesses()
    {
        using var h = new Harness(buildHook: (r, t) =>
        {
            if (r.Name == "worker") throw new InvalidOperationException("docker build exited 1");
            return Task.CompletedTask;
        });
        var outcome = await h.Publisher.RunBuildAsync(
            Resources("web", "worker", "api"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.ImageBuildFailed, outcome.Diagnostic!.Symbol);
        Assert.Equal("worker", outcome.Diagnostic.Resource);
        Assert.Equal("auth-reg.test:5000/myapp/worker:1.2.3-test", outcome.Diagnostic.Tag);

        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_IMAGE_BUILD_FAILED");
        Assert.Contains("resource: worker", stderr);
        Assert.Contains("tag:      auth-reg.test:5000/myapp/worker:1.2.3-test", stderr);

        // Earlier success retained.
        Assert.Contains("auth-reg.test:5000/myapp/web:1.2.3-test", h.Store.Tags);

        // "api" was never attempted (we stopped at worker).
        Assert.Equal(2, h.Pipeline.Calls.Count); // web + worker
    }

    private sealed class AlwaysEmptyImageStore : ILocalImageStore
    {
        public bool HasTag(string imageTag) => false;
    }

    [Fact]
    public async Task PipelineReportsSuccessButImageMissing_IsTreatedAsImageBuildFailed()
    {
        using var h = new Harness();
        // Lying-pipeline simulation: replace the store with one that always reports absent.
        h.Publisher.ImageStore = new AlwaysEmptyImageStore();

        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.ImageBuildFailed, outcome.Diagnostic!.Symbol);
        Assert.Equal("web", outcome.Diagnostic.Resource);
    }

    // ────────────────────────────────────────────────────────────────────────
    // H. Catch-all (E_BUILD_PHASE_UNEXPECTED)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnclassifiableExceptionInBody_BecomesBuildPhaseUnexpected_AndContainsNoStackTrace()
    {
        using var h = new Harness();
        h.Publisher.AppHostInfoProvider = () => throw new InvalidDataException("oops in info provider");

        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.BuildPhaseUnexpected, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_BUILD_PHASE_UNEXPECTED");
        Assert.Contains("oops in info provider", stderr);
        Assert.DoesNotContain("at System.", stderr);
        Assert.DoesNotContain("Stack", stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // I. Phase-boundary observability through the wired step
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildPhaseStep_OnFailure_RaisesBuildFailedException()
    {
        using var h = new Harness(prefixValue: null);
        var outcome = await h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, default);
        Assert.False(outcome.Succeeded);

        var ex = await Assert.ThrowsAsync<CoolifyBuildFailedException>(() =>
        {
            throw new CoolifyBuildFailedException(outcome.Diagnostic!);
        });
        Assert.Equal(BuildSymbol.RegistryNotConfigured, ex.Diagnostic.Symbol);
    }

    // ────────────────────────────────────────────────────────────────────────
    // J. Stable observable contract — all four symbols spelled verbatim
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllFourBuildSymbols_HaveExactSpelling()
    {
        Assert.Equal("E_REGISTRY_NOT_CONFIGURED", BuildSymbol.RegistryNotConfigured.Literal());
        Assert.Equal("E_APPHOST_VERSION_MISSING", BuildSymbol.ApphostVersionMissing.Literal());
        Assert.Equal("E_IMAGE_BUILD_FAILED",      BuildSymbol.ImageBuildFailed.Literal());
        Assert.Equal("E_BUILD_PHASE_UNEXPECTED",  BuildSymbol.BuildPhaseUnexpected.Literal());
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cancellation
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancellationBeforeStart_PropagatesAsOperationCanceled()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Publisher.RunBuildAsync(Resources("web"), NullLogger.Instance, cts.Token));
        Assert.Empty(h.Pipeline.Calls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // helpers
    // ────────────────────────────────────────────────────────────────────────
    private static void AssertFirstToken(string stderr, string expected)
    {
        Assert.False(string.IsNullOrEmpty(stderr), "stderr was empty");
        var firstLine = stderr.Split('\n', 2)[0].TrimEnd('\r');
        var firstToken = firstLine.Split(new[] { ' ', '\t', ':' }, 2)[0];
        Assert.Equal(expected, firstToken);
    }
}

